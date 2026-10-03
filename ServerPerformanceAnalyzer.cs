using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Server Performance Analyzer", "SeesAll", "2.3.1")]
    [Description("Low-impact server performance reports and repeatable plugin benchmark windows")]
    public class ServerPerformanceAnalyzer : RustPlugin
    {
        private const string ReportCommand = "monitor.report";
        private const string LegacyReportCommand = "monitor.createreport";
        private const string BenchmarkCommand = "monitor.benchmark";
        private const string StatusCommand = "monitor.status";
        private const string DataRoot = "ServerPerformanceAnalyzer";
        private const string LegacyDataRoot = "PerformanceMonitorEnhanced";
        private const string CurrentConfigFileName = "ServerPerformanceAnalyzer.json";
        private const string LegacyConfigFileName = "PerformanceMonitorEnhanced.json";

        private Configuration _config;
        private Timer _reportTimer;
        private Timer _sampleTimer;
        private Coroutine _activeCoroutine;
        private bool _reportRunning;
        private string _activeLabel;
        private DateTime _activeStartedUtc;
        private PerformanceAccumulator _rollingPerformance;
        private Dictionary<string, PluginCounter> _previousPluginCounters = new Dictionary<string, PluginCounter>(StringComparer.OrdinalIgnoreCase);
        private DateTime? _previousPluginCaptureUtc;
        private HashSet<string> _excludedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _excludedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<EnvironmentEvent> _environmentEvents = new List<EnvironmentEvent>();

        private static readonly MetricDefinition[] PerformanceMetrics =
        {
            new MetricDefinition("Frame Rate", "frameRate", "fps"),
            new MetricDefinition("Frame Time", "frameTime", "ms"),
            new MetricDefinition("Average Frame Rate", "frameRateAverage", "fps"),
            new MetricDefinition("Average Frame Time", "frameTimeAverage", "ms"),
            new MetricDefinition("System Memory", "memoryUsageSystem", "game value"),
            new MetricDefinition("Memory Allocations", "memoryAllocations", "count"),
            new MetricDefinition("Memory Collections", "memoryCollections", "count"),
            new MetricDefinition("Load Balancer Tasks", "loadBalancerTasks", "count"),
            new MetricDefinition("Invoke Handler Tasks", "invokeHandlerTasks", "count"),
            new MetricDefinition("Workshop Skins Queued", "workshopSkinsQueued", "count"),
            new MetricDefinition("Ping", "ping", "ms"),
            new MetricDefinition("GC Triggered", "gcTriggered", "boolean fraction")
        };

        #region Lifecycle

        private void Init()
        {
            CopyLegacyDataIfNeeded();

            cmd.AddConsoleCommand(ReportCommand, this, nameof(HandleReportCommand));
            cmd.AddConsoleCommand(LegacyReportCommand, this, nameof(HandleReportCommand));
            cmd.AddConsoleCommand(BenchmarkCommand, this, nameof(HandleBenchmarkCommand));
            cmd.AddConsoleCommand(StatusCommand, this, nameof(HandleStatusCommand));

            RebuildExclusionCaches();
            _rollingPerformance = new PerformanceAccumulator(DateTime.UtcNow, PerformanceMetrics);
        }

        private void OnServerInitialized()
        {
            CaptureRollingPerformanceSample();

            _sampleTimer = timer.Every(_config.PerformanceSampleIntervalSeconds, CaptureRollingPerformanceSample);

            if (_config.ReportIntervalSeconds > 0)
            {
                _reportTimer = timer.Every(_config.ReportIntervalSeconds, delegate
                {
                    TryStartReport("scheduled", 0, 0, null, false, null);
                });
            }
        }

        private void Unload()
        {
            if (_reportTimer != null)
            {
                _reportTimer.Destroy();
                _reportTimer = null;
            }

            if (_sampleTimer != null)
            {
                _sampleTimer.Destroy();
                _sampleTimer = null;
            }

            if (_activeCoroutine != null && ServerMgr.Instance != null)
            {
                ServerMgr.Instance.StopCoroutine(_activeCoroutine);
                _activeCoroutine = null;
            }

            _reportRunning = false;
        }

        private void OnServerSave()
        {
            RecordEnvironmentEvent("ServerSave", "A server save started.");
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name != Name)
            {
                RecordEnvironmentEvent("PluginLoaded", plugin.Name + " " + plugin.Version);
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name != Name)
            {
                RecordEnvironmentEvent("PluginUnloaded", plugin.Name + " " + plugin.Version);
            }
        }

        #endregion

        #region Commands

        private void HandleReportCommand(ConsoleSystem.Arg arg)
        {
            if (!IsAuthorized(arg))
            {
                Reply(arg, "You do not have permission to create performance reports.");
                return;
            }

            string label = GetArgument(arg, 0, "manual");
            if (TryStartReport(label, 0, 0, null, false, arg))
            {
                Reply(arg, "Performance report started.");
            }
        }

        private void HandleBenchmarkCommand(ConsoleSystem.Arg arg)
        {
            if (!IsAuthorized(arg))
            {
                Reply(arg, "You do not have permission to run performance benchmarks.");
                return;
            }

            string label = GetArgument(arg, 0, null);
            if (string.IsNullOrWhiteSpace(label))
            {
                Reply(arg, "Usage: monitor.benchmark <label> [duration seconds] [warm-up seconds] [baseline label]");
                return;
            }

            int duration = GetIntArgument(arg, 1, _config.DefaultBenchmarkDurationSeconds);
            duration = Math.Max(_config.MinimumBenchmarkDurationSeconds, Math.Min(_config.MaximumBenchmarkDurationSeconds, duration));
            int warmup = GetIntArgument(arg, 2, _config.DefaultBenchmarkWarmupSeconds);
            warmup = Math.Max(0, Math.Min(_config.MaximumBenchmarkWarmupSeconds, warmup));
            string baselineLabel = GetArgument(arg, 3, null);
            baselineLabel = string.IsNullOrWhiteSpace(baselineLabel) ? null : NormalizeLabel(baselineLabel);

            if (TryStartReport(label, duration, warmup, baselineLabel, true, arg))
            {
                Reply(arg, string.Format(CultureInfo.InvariantCulture,
                    "Benchmark '{0}' started with a {1}s warm-up and {2}s observation window{3}.",
                    NormalizeLabel(label), warmup, duration,
                    baselineLabel == null ? string.Empty : " against baseline '" + baselineLabel + "'"));
            }
        }

        private void HandleStatusCommand(ConsoleSystem.Arg arg)
        {
            if (!IsAuthorized(arg))
            {
                Reply(arg, "You do not have permission to view monitor status.");
                return;
            }

            if (!_reportRunning)
            {
                Reply(arg, "Server Performance Analyzer is idle.");
                return;
            }

            Reply(arg, string.Format(CultureInfo.InvariantCulture,
                "A report for '{0}' has been running for {1:F1} seconds.",
                _activeLabel,
                (DateTime.UtcNow - _activeStartedUtc).TotalSeconds));
        }

        private bool IsAuthorized(ConsoleSystem.Arg arg)
        {
            return arg != null && arg.IsAdmin;
        }

        private static void Reply(ConsoleSystem.Arg arg, string message)
        {
            if (arg != null)
            {
                arg.ReplyWith(message);
            }
        }

        private static string GetArgument(ConsoleSystem.Arg arg, int index, string fallback)
        {
            if (arg == null || arg.Args == null || index < 0 || index >= arg.Args.Length)
            {
                return fallback;
            }

            return arg.Args[index].ToString();
        }

        private static int GetIntArgument(ConsoleSystem.Arg arg, int index, int fallback)
        {
            string value = GetArgument(arg, index, null);
            int parsed;
            return value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        #endregion

        #region Report orchestration

        private bool TryStartReport(string requestedLabel, int benchmarkDurationSeconds, int warmupSeconds, string baselineLabel, bool benchmark, ConsoleSystem.Arg requester)
        {
            if (_reportRunning)
            {
                Reply(requester, string.Format(CultureInfo.InvariantCulture,
                    "A report for '{0}' is already running.", _activeLabel));
                return false;
            }

            _reportRunning = true;
            _activeLabel = NormalizeLabel(requestedLabel);
            _activeStartedUtc = DateTime.UtcNow;
            _activeCoroutine = ServerMgr.Instance.StartCoroutine(
                CreateReport(_activeLabel, benchmarkDurationSeconds, warmupSeconds, baselineLabel, benchmark));
            return true;
        }

        private IEnumerator CreateReport(string label, int benchmarkDurationSeconds, int warmupSeconds, string baselineLabel, bool benchmark)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            DateTime startedUtc = DateTime.UtcNow;
            PerformanceReport report = new PerformanceReport
            {
                SchemaVersion = 3,
                ReportId = Guid.NewGuid().ToString("N"),
                Label = label,
                Mode = benchmark ? "benchmark" : "snapshot",
                StartedUtc = startedUtc,
                ServerIdentity = ConVar.Server.identity,
                OnlinePlayers = BasePlayer.activePlayerList.Count,
                SleepingPlayers = BasePlayer.sleepingPlayerList.Count
            };

            Exception failure = null;
            Dictionary<string, PluginCounter> benchmarkStartCounters = null;
            PerformanceAccumulator completedPerformanceWindow = null;

            try
            {
                if (benchmark)
                {
                    if (warmupSeconds > 0)
                    {
                        yield return new WaitForSecondsRealtime(warmupSeconds);
                    }

                    report.WarmupDurationSeconds = warmupSeconds;
                    report.ObservationStartedUtc = DateTime.UtcNow;
                    report.OnlinePlayers = BasePlayer.activePlayerList.Count;
                    report.SleepingPlayers = BasePlayer.sleepingPlayerList.Count;
                    benchmarkStartCounters = TryCapturePluginCounters(out failure);
                    if (failure == null)
                    {
                        completedPerformanceWindow = new PerformanceAccumulator(DateTime.UtcNow, PerformanceMetrics);
                        Stopwatch benchmarkStopwatch = Stopwatch.StartNew();

                        while (benchmarkStopwatch.Elapsed.TotalSeconds < benchmarkDurationSeconds)
                        {
                            completedPerformanceWindow.Capture(Performance.current);
                            float remaining = (float)(benchmarkDurationSeconds - benchmarkStopwatch.Elapsed.TotalSeconds);
                            float delay = Mathf.Min(_config.BenchmarkSampleIntervalSeconds, Mathf.Max(0.05f, remaining));
                            yield return new WaitForSecondsRealtime(delay);
                        }

                        completedPerformanceWindow.Capture(Performance.current);
                        completedPerformanceWindow.Close(DateTime.UtcNow);
                        report.ObservationCompletedUtc = DateTime.UtcNow;
                        report.ObservationDurationSeconds = benchmarkStopwatch.Elapsed.TotalSeconds;
                    }
                }
                else
                {
                    CaptureRollingPerformanceSample();
                    completedPerformanceWindow = _rollingPerformance;
                    completedPerformanceWindow.Close(DateTime.UtcNow);
                    _rollingPerformance = new PerformanceAccumulator(DateTime.UtcNow, PerformanceMetrics);
                    report.ObservationStartedUtc = completedPerformanceWindow.StartedUtc;
                    report.ObservationCompletedUtc = completedPerformanceWindow.CompletedUtc;
                    report.ObservationDurationSeconds = completedPerformanceWindow.DurationSeconds;
                }

                // Capture comparison context at the observation boundary. The entity scan that follows
                // is intentionally outside the measured window and can take several server frames.
                report.CompletedOnlinePlayers = BasePlayer.activePlayerList.Count;
                report.CompletedSleepingPlayers = BasePlayer.sleepingPlayerList.Count;

                Dictionary<string, PluginCounter> currentCounters = null;
                if (failure == null && _config.IncludePluginReport)
                {
                    currentCounters = TryCapturePluginCounters(out failure);
                }

                if (failure == null && _config.IncludePluginReport)
                {
                    Dictionary<string, PluginCounter> baselineCounters = benchmark
                        ? benchmarkStartCounters
                        : _previousPluginCounters;
                    DateTime? baselineCapturedUtc = benchmark
                        ? (DateTime?)startedUtc
                        : _previousPluginCaptureUtc;

                    report.Plugins = BuildPluginReport(currentCounters, baselineCounters, baselineCapturedUtc, DateTime.UtcNow);

                    if (!benchmark)
                    {
                        _previousPluginCounters = currentCounters;
                        _previousPluginCaptureUtc = DateTime.UtcNow;
                    }
                }

                if (completedPerformanceWindow != null)
                {
                    report.PerformanceWindow = completedPerformanceWindow.CreateReport();
                }

                if (failure == null && _config.IncludeEntityReport)
                {
                    IEnumerator entityEnumerator = BuildEntityReport(report);
                    try
                    {
                        while (true)
                        {
                            bool hasNext = false;
                            object yielded = null;
                            try
                            {
                                hasNext = entityEnumerator.MoveNext();
                                if (hasNext)
                                {
                                    yielded = entityEnumerator.Current;
                                }
                            }
                            catch (Exception ex)
                            {
                                failure = ex;
                            }

                            if (failure != null || !hasNext)
                            {
                                break;
                            }

                            yield return yielded;
                        }
                    }
                    finally
                    {
                        IDisposable disposable = entityEnumerator as IDisposable;
                        if (disposable != null)
                        {
                            disposable.Dispose();
                        }
                    }
                }

                report.PerformanceSnapshot = Performance.current;
                report.CompletedUtc = DateTime.UtcNow;
                report.TotalReportDurationSeconds = totalStopwatch.Elapsed.TotalSeconds;
                report.Quality = BuildQualityReport(report);

                if (benchmark && failure == null)
                {
                    bool createComparison = _config.CompareBenchmarkToPrevious || baselineLabel != null;
                    report.PreviousBenchmarkComparison = BuildAndStoreBenchmarkComparison(report, baselineLabel, createComparison);
                }

                if (failure != null)
                {
                    report.Error = failure.ToString();
                    PrintError("Performance report failed: " + failure);
                }

                try
                {
                    SaveReport(report);
                    LogSummary(report);
                    SendDiscordReport(report);
                }
                catch (Exception ex)
                {
                    PrintError("Unable to save performance report: " + ex);
                }
            }
            finally
            {
                totalStopwatch.Stop();
                _reportRunning = false;
                _activeCoroutine = null;
                _activeLabel = null;
            }
        }

        #endregion

        #region Plugin metrics

        private Dictionary<string, PluginCounter> TryCapturePluginCounters(out Exception failure)
        {
            failure = null;
            try
            {
                Dictionary<string, PluginCounter> counters = new Dictionary<string, PluginCounter>(StringComparer.OrdinalIgnoreCase);
                Plugin[] loadedPlugins = plugins.GetAll();

                for (int i = 0; i < loadedPlugins.Length; i++)
                {
                    Plugin plugin = loadedPlugins[i];
                    if (plugin == null || plugin.IsCorePlugin || plugin.Name == Name || _excludedPlugins.Contains(plugin.Name))
                    {
                        continue;
                    }

#if CARBON
                    double hookTimeSeconds = plugin.TotalHookTime.TotalSeconds;
                    double memoryValueBytes = Convert.ToDouble(plugin.TotalMemoryUsed, CultureInfo.InvariantCulture);
                    string memoryMetricKind = "CurrentMemoryUsed";
#else
                    double hookTimeSeconds = plugin.TotalHookTime;
                    double memoryValueBytes = plugin.TotalHookMemory;
                    string memoryMetricKind = "CumulativeHookAllocations";
#endif

                    counters[plugin.Name] = new PluginCounter
                    {
                        Name = plugin.Name,
                        Version = plugin.Version.ToString(),
                        HookTimeSeconds = hookTimeSeconds,
                        MemoryValueBytes = memoryValueBytes,
                        MemoryMetricKind = memoryMetricKind
                    };
                }

                return counters;
            }
            catch (Exception ex)
            {
                failure = ex;
                return new Dictionary<string, PluginCounter>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private PluginReport BuildPluginReport(
            Dictionary<string, PluginCounter> current,
            Dictionary<string, PluginCounter> baseline,
            DateTime? baselineCapturedUtc,
            DateTime capturedUtc)
        {
            PluginReport pluginReport = new PluginReport
            {
                CapturedUtc = capturedUtc,
                BaselineAvailable = baseline != null && baseline.Count > 0 && baselineCapturedUtc.HasValue,
                ObservationDurationSeconds = baselineCapturedUtc.HasValue
                    ? Math.Max(0, (capturedUtc - baselineCapturedUtc.Value).TotalSeconds)
                    : 0,
                Entries = new List<PluginReportEntry>()
            };

            double observedHookTimeTotal = 0;

            foreach (KeyValuePair<string, PluginCounter> pair in current)
            {
                PluginCounter counter = pair.Value;
                PluginCounter previous = null;
                bool comparable = baseline != null
                                  && baseline.TryGetValue(pair.Key, out previous)
                                  && previous.Version == counter.Version
                                  && counter.HookTimeSeconds >= previous.HookTimeSeconds;

                double? hookDelta = comparable
                    ? (double?)(counter.HookTimeSeconds - previous.HookTimeSeconds)
                    : null;
                double? memoryDelta = comparable
                    ? (double?)(counter.MemoryValueBytes - previous.MemoryValueBytes)
                    : null;

                if (hookDelta.HasValue && hookDelta.Value > 0)
                {
                    observedHookTimeTotal += hookDelta.Value;
                }

                pluginReport.Entries.Add(new PluginReportEntry
                {
                    Name = counter.Name,
                    Version = counter.Version,
                    TotalHookTimeSeconds = counter.HookTimeSeconds,
                    ObservedHookTimeSeconds = hookDelta,
                    HookTimeMillisecondsPerMinute = hookDelta.HasValue && pluginReport.ObservationDurationSeconds > 0
                        ? (double?)(hookDelta.Value * 60000d / pluginReport.ObservationDurationSeconds)
                        : null,
                    MemoryMetricKind = counter.MemoryMetricKind,
                    MemoryValueBytes = counter.MemoryValueBytes,
                    ObservedMemoryChangeBytes = memoryDelta
                });
            }

            pluginReport.ObservedHookTimeTotalSeconds = observedHookTimeTotal;

            for (int i = 0; i < pluginReport.Entries.Count; i++)
            {
                PluginReportEntry entry = pluginReport.Entries[i];
                if (entry.ObservedHookTimeSeconds.HasValue && observedHookTimeTotal > 0)
                {
                    entry.ObservedHookTimeSharePercent = entry.ObservedHookTimeSeconds.Value * 100d / observedHookTimeTotal;
                }
            }

            pluginReport.Entries.Sort(delegate(PluginReportEntry left, PluginReportEntry right)
            {
                double leftValue = left.ObservedHookTimeSeconds ?? left.TotalHookTimeSeconds;
                double rightValue = right.ObservedHookTimeSeconds ?? right.TotalHookTimeSeconds;
                int comparison = rightValue.CompareTo(leftValue);
                return comparison != 0
                    ? comparison
                    : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < pluginReport.Entries.Count; i++)
            {
                pluginReport.Entries[i].ImpactRank = i + 1;
            }

            return pluginReport;
        }

        #endregion

        #region Entity metrics

        private IEnumerator BuildEntityReport(PerformanceReport report)
        {
            List<BaseEntity> entities = Pool.Get<List<BaseEntity>>();
            Dictionary<string, EntityCounter> counters = new Dictionary<string, EntityCounter>(StringComparer.Ordinal);
            Stopwatch scanStopwatch = Stopwatch.StartNew();

            try
            {
                foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
                {
                    BaseEntity entity = networkable as BaseEntity;
                    if (entity != null)
                    {
                        entities.Add(entity);
                    }
                }

                EntityReport entityReport = new EntityReport
                {
                    SnapshotSize = entities.Count,
                    Entries = new List<EntityReportEntry>()
                };

                int processedSinceYield = 0;
                for (int i = 0; i < entities.Count; i++)
                {
                    BaseEntity entity = entities[i];
                    entityReport.Processed++;
                    processedSinceYield++;

                    if (entity == null || !entity.IsValid())
                    {
                        entityReport.InvalidOrDestroyed++;
                    }
                    else
                    {
                        string shortName = entity.ShortPrefabName;
                        if (string.IsNullOrEmpty(shortName))
                        {
                            entityReport.MissingShortName++;
                        }
                        else if (_excludedEntities.Contains(shortName))
                        {
                            entityReport.Excluded++;
                        }
                        else
                        {
                            EntityCounter counter;
                            if (!counters.TryGetValue(shortName, out counter))
                            {
                                counter = new EntityCounter();
                                counters.Add(shortName, counter);
                            }

                            if (entity.OwnerID == 0)
                            {
                                counter.Unowned++;
                                entityReport.Unowned++;
                            }
                            else
                            {
                                counter.Owned++;
                                entityReport.Owned++;
                            }

                            counter.Total++;
                            entityReport.Total++;
                        }
                    }

                    if (processedSinceYield >= _config.EntityBatchSize)
                    {
                        processedSinceYield = 0;
                        if (_config.LogEntityProgress)
                        {
                            Puts(string.Format(CultureInfo.InvariantCulture,
                                "Entity report progress: {0:F1}% ({1}/{2})",
                                entities.Count == 0 ? 100d : entityReport.Processed * 100d / entities.Count,
                                entityReport.Processed,
                                entities.Count));
                        }

                        yield return null;
                    }
                }

                foreach (KeyValuePair<string, EntityCounter> pair in counters)
                {
                    entityReport.Entries.Add(new EntityReportEntry
                    {
                        ShortName = pair.Key,
                        Total = pair.Value.Total,
                        Owned = pair.Value.Owned,
                        Unowned = pair.Value.Unowned
                    });
                }

                entityReport.Entries.Sort(delegate(EntityReportEntry left, EntityReportEntry right)
                {
                    int comparison = right.Total.CompareTo(left.Total);
                    return comparison != 0
                        ? comparison
                        : string.Compare(left.ShortName, right.ShortName, StringComparison.Ordinal);
                });

                scanStopwatch.Stop();
                entityReport.ScanDurationSeconds = scanStopwatch.Elapsed.TotalSeconds;
                report.Entities = entityReport;
            }
            finally
            {
                scanStopwatch.Stop();
                Pool.FreeUnmanaged(ref entities);
            }
        }

        #endregion

        #region Performance sampling

        private void CaptureRollingPerformanceSample()
        {
            if (_rollingPerformance == null)
            {
                _rollingPerformance = new PerformanceAccumulator(DateTime.UtcNow, PerformanceMetrics);
            }

            _rollingPerformance.Capture(Performance.current);
        }

        private void RecordEnvironmentEvent(string eventType, string description)
        {
            _environmentEvents.Add(new EnvironmentEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Type = eventType,
                Description = description
            });

            const int maximumRememberedEvents = 100;
            if (_environmentEvents.Count > maximumRememberedEvents)
            {
                _environmentEvents.RemoveRange(0, _environmentEvents.Count - maximumRememberedEvents);
            }
        }

        private QualityReport BuildQualityReport(PerformanceReport report)
        {
            QualityReport quality = new QualityReport
            {
                SuitableForComparison = true,
                Warnings = new List<string>(),
                EnvironmentEvents = new List<EnvironmentEvent>()
            };

            if (report.OnlinePlayers != report.CompletedOnlinePlayers)
            {
                quality.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Online player count changed from {0} to {1} during the report.",
                    report.OnlinePlayers, report.CompletedOnlinePlayers));
            }

            int minimumSamples = report.Mode == "benchmark"
                ? _config.MinimumBenchmarkSamplesForComparison
                : 3;
            if (report.PerformanceWindow == null || report.PerformanceWindow.SampleCount < minimumSamples)
            {
                quality.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Only {0} performance samples were captured; at least {1} are required for a meaningful comparison.",
                    report.PerformanceWindow == null ? 0 : report.PerformanceWindow.SampleCount,
                    minimumSamples));
            }

            DateTime windowStart = report.ObservationStartedUtc == default(DateTime)
                ? report.StartedUtc
                : report.ObservationStartedUtc;
            DateTime windowEnd = report.ObservationCompletedUtc == default(DateTime)
                ? report.CompletedUtc
                : report.ObservationCompletedUtc;

            for (int i = 0; i < _environmentEvents.Count; i++)
            {
                EnvironmentEvent environmentEvent = _environmentEvents[i];
                if (environmentEvent.TimestampUtc >= windowStart && environmentEvent.TimestampUtc <= windowEnd)
                {
                    quality.EnvironmentEvents.Add(environmentEvent);
                    quality.Warnings.Add(environmentEvent.Type + ": " + environmentEvent.Description);
                }
            }

            MetricSummary gcMetric = FindMetric(report.PerformanceWindow, "GC Triggered");
            if (gcMetric != null && gcMetric.Maximum > 0)
            {
                quality.Warnings.Add("At least one sampled performance tick reported a garbage collection.");
            }

            quality.SuitableForComparison = quality.Warnings.Count == 0;
            return quality;
        }

        private BenchmarkComparison BuildAndStoreBenchmarkComparison(
            PerformanceReport currentReport,
            string requestedBaselineLabel,
            bool createComparison)
        {
            BenchmarkReference current = BenchmarkReference.FromReport(currentReport);
            BenchmarkReference previous = null;
            BenchmarkReferenceStore store = new BenchmarkReferenceStore
            {
                Entries = new List<BenchmarkReference>()
            };

            try
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(DataRoot + "/PreviousBenchmark"))
                {
                    previous = Interface.Oxide.DataFileSystem.ReadObject<BenchmarkReference>(DataRoot + "/PreviousBenchmark");
                }

                if (Interface.Oxide.DataFileSystem.ExistsDatafile(DataRoot + "/BenchmarkReferences"))
                {
                    store = Interface.Oxide.DataFileSystem.ReadObject<BenchmarkReferenceStore>(DataRoot + "/BenchmarkReferences")
                            ?? store;
                    store.Entries = store.Entries ?? new List<BenchmarkReference>();
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Unable to read benchmark references: " + ex.Message);
            }

            if (previous != null && !string.IsNullOrEmpty(previous.ReportId)
                && FindBenchmarkReference(store.Entries, previous.Label) == null)
            {
                StoreBenchmarkReference(store.Entries, previous);
            }

            BenchmarkReference baseline = requestedBaselineLabel == null
                ? previous
                : FindBenchmarkReference(store.Entries, requestedBaselineLabel);

            Interface.Oxide.DataFileSystem.WriteObject(DataRoot + "/PreviousBenchmark", current);
            StoreBenchmarkReference(store.Entries, current);
            while (store.Entries.Count > _config.MaximumNamedBenchmarkReferences)
            {
                store.Entries.RemoveAt(store.Entries.Count - 1);
            }
            Interface.Oxide.DataFileSystem.WriteObject(DataRoot + "/BenchmarkReferences", store);

            if (!createComparison)
            {
                return null;
            }

            BenchmarkComparison comparison = new BenchmarkComparison
            {
                BaselineReportId = baseline == null ? null : baseline.ReportId,
                BaselineLabel = baseline == null ? requestedBaselineLabel : baseline.Label,
                BaselineCompletedUtc = baseline == null ? default(DateTime) : baseline.CompletedUtc,
                Metrics = new List<BenchmarkComparisonMetric>(),
                ContextWarnings = new List<string>(),
                Confidence = "High"
            };

            if (baseline == null || string.IsNullOrEmpty(baseline.ReportId))
            {
                comparison.Confidence = "Low";
                comparison.ContextWarnings.Add(requestedBaselineLabel == null
                    ? "No preceding benchmark is available yet. This run has been stored as the next baseline."
                    : "The requested baseline '" + requestedBaselineLabel + "' was not found. Run that labeled benchmark first.");
                return comparison;
            }

            AddComparisonMetric(comparison, "Average Frame Time", baseline.AverageFrameTime, current.AverageFrameTime, true, "ms", true, null);
            AddComparisonMetric(comparison, "Average Frame Rate", baseline.AverageFrameRate, current.AverageFrameRate, false, "fps", true, null);

            bool pluginSignalReliable = baseline.PluginHookObservedMilliseconds >= _config.MinimumPluginHookMillisecondsForComparison
                                        && current.PluginHookObservedMilliseconds >= _config.MinimumPluginHookMillisecondsForComparison;
            string pluginNoiseNote = pluginSignalReliable
                ? null
                : string.Format(CultureInfo.InvariantCulture,
                    "One or both windows measured less than {0:F1} ms of total plugin hook time.",
                    _config.MinimumPluginHookMillisecondsForComparison);
            AddComparisonMetric(comparison, "Plugin Hook Time Rate",
                baseline.PluginHookMillisecondsPerMinute,
                current.PluginHookMillisecondsPerMinute,
                true,
                "ms/min",
                pluginSignalReliable,
                pluginNoiseNote);

            if (baseline.OnlinePlayers != current.OnlinePlayers)
            {
                comparison.ContextWarnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Online players differ: baseline {0}, current {1}.", baseline.OnlinePlayers, current.OnlinePlayers));
            }

            if (baseline.EntityCount > 0 && current.EntityCount > 0)
            {
                double entityDifferencePercent = (current.EntityCount - baseline.EntityCount) * 100d / baseline.EntityCount;
                if (Math.Abs(entityDifferencePercent) >= 5d)
                {
                    comparison.ContextWarnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Entity count differs by {0:+0.0;-0.0;0.0}%: baseline {1}, current {2}.",
                        entityDifferencePercent, baseline.EntityCount, current.EntityCount));
                }
            }

            if (baseline.ObservationDurationSeconds > 0 && current.ObservationDurationSeconds > 0)
            {
                double durationDifferencePercent = Math.Abs(current.ObservationDurationSeconds - baseline.ObservationDurationSeconds)
                                                   * 100d / baseline.ObservationDurationSeconds;
                if (durationDifferencePercent >= 10d)
                {
                    comparison.ContextWarnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Observation durations differ by {0:F1}%: baseline {1:F1}s, current {2:F1}s.",
                        durationDifferencePercent, baseline.ObservationDurationSeconds, current.ObservationDurationSeconds));
                }
            }

            if (!baseline.SuitableForComparison || !current.SuitableForComparison)
            {
                comparison.ContextWarnings.Add("One or both benchmark windows contain quality warnings.");
            }

            bool baselineSampleCountAvailable = baseline.SampleCount.HasValue && baseline.SampleCount.Value > 0;
            int currentSampleCount = current.SampleCount ?? 0;
            if (!baselineSampleCountAvailable)
            {
                comparison.ContextWarnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Baseline sample count is unavailable because it predates schema 3; current {0}, required {1}.",
                    currentSampleCount, _config.MinimumBenchmarkSamplesForComparison));
            }
            else if (baseline.SampleCount.Value < _config.MinimumBenchmarkSamplesForComparison
                     || currentSampleCount < _config.MinimumBenchmarkSamplesForComparison)
            {
                comparison.ContextWarnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Insufficient samples for a robust comparison: baseline {0}, current {1}, required {2}.",
                    baseline.SampleCount.Value, currentSampleCount, _config.MinimumBenchmarkSamplesForComparison));
            }

            if (comparison.ContextWarnings.Count > 0)
            {
                comparison.Confidence = "Low";
            }
            else if (!pluginSignalReliable)
            {
                comparison.Confidence = "Moderate";
            }

            return comparison;
        }

        private static BenchmarkReference FindBenchmarkReference(List<BenchmarkReference> entries, string label)
        {
            if (entries == null || string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && string.Equals(entries[i].Label, label, StringComparison.OrdinalIgnoreCase))
                {
                    return entries[i];
                }
            }

            return null;
        }

        private static void StoreBenchmarkReference(List<BenchmarkReference> entries, BenchmarkReference current)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] != null && string.Equals(entries[i].Label, current.Label, StringComparison.OrdinalIgnoreCase))
                {
                    entries.RemoveAt(i);
                }
            }

            entries.Insert(0, current);
        }

        private static void AddComparisonMetric(
            BenchmarkComparison comparison,
            string name,
            double? baseline,
            double? current,
            bool lowerIsBetter,
            string unit,
            bool reliable,
            string note)
        {
            if (!baseline.HasValue || !current.HasValue)
            {
                return;
            }

            double? changePercent = null;
            double? improvementPercent = null;
            string result = "Inconclusive";
            if (reliable && Math.Abs(baseline.Value) >= 0.0000001d)
            {
                changePercent = (current.Value - baseline.Value) * 100d / Math.Abs(baseline.Value);
                improvementPercent = lowerIsBetter ? -changePercent : changePercent;
                result = Math.Abs(improvementPercent.Value) < 1d
                    ? "No material change"
                    : improvementPercent.Value > 0 ? "Improved" : "Regressed";
            }

            comparison.Metrics.Add(new BenchmarkComparisonMetric
            {
                Name = name,
                Unit = unit,
                Baseline = baseline.Value,
                Current = current.Value,
                ChangePercent = changePercent,
                ImprovementPercent = improvementPercent,
                Result = result,
                Reliable = reliable,
                Note = note
            });
        }

        private static MetricSummary FindMetric(PerformanceWindowReport window, string name)
        {
            if (window == null || window.Metrics == null)
            {
                return null;
            }

            for (int i = 0; i < window.Metrics.Count; i++)
            {
                if (string.Equals(window.Metrics[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return window.Metrics[i];
                }
            }

            return null;
        }

        #endregion

        #region Storage and logging

        private void SaveReport(PerformanceReport report)
        {
            string timestamp = report.CompletedUtc.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
            string archiveName = string.Format(CultureInfo.InvariantCulture,
                "{0}/Reports/{1:yyyy-MM-dd}/{2}_{3}", DataRoot, report.CompletedUtc, timestamp, report.ReportId.Substring(0, 8));

            Interface.Oxide.DataFileSystem.WriteObject(archiveName, report);

            if (_config.WriteLatestReport)
            {
                Interface.Oxide.DataFileSystem.WriteObject(DataRoot + "/Latest", report);
            }

            ApplyRetentionPolicy();
        }

        private void ApplyRetentionPolicy()
        {
            if (_config.MaximumArchivedReports <= 0 && _config.MaximumReportAgeDays <= 0)
            {
                return;
            }

            string reportRoot = Path.Combine(Interface.Oxide.DataDirectory, DataRoot, "Reports");
            if (!Directory.Exists(reportRoot))
            {
                return;
            }

            FileInfo[] files = new DirectoryInfo(reportRoot).GetFiles("*.json", SearchOption.AllDirectories);
            Array.Sort(files, delegate(FileInfo left, FileInfo right)
            {
                return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            });

            DateTime oldestAllowed = _config.MaximumReportAgeDays > 0
                ? DateTime.UtcNow.AddDays(-_config.MaximumReportAgeDays)
                : DateTime.MinValue;

            for (int i = 0; i < files.Length; i++)
            {
                bool exceedsCount = _config.MaximumArchivedReports > 0 && i >= _config.MaximumArchivedReports;
                bool exceedsAge = _config.MaximumReportAgeDays > 0 && files[i].LastWriteTimeUtc < oldestAllowed;
                if (exceedsCount || exceedsAge)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch (Exception ex)
                    {
                        PrintWarning("Unable to remove expired report '" + files[i].FullName + "': " + ex.Message);
                    }
                }
            }
        }

        private void LogSummary(PerformanceReport report)
        {
            Puts(string.Format(CultureInfo.InvariantCulture,
                "Report '{0}' ({1}) completed in {2:F3}s. ID: {3}",
                report.Label,
                report.Mode,
                report.TotalReportDurationSeconds,
                report.ReportId));

            if (report.Plugins == null || report.Plugins.Entries == null || report.Plugins.Entries.Count == 0)
            {
                return;
            }

            int count = Math.Min(_config.TopPluginCountInConsole, report.Plugins.Entries.Count);
            for (int i = 0; i < count; i++)
            {
                PluginReportEntry entry = report.Plugins.Entries[i];
                string impact = entry.ObservedHookTimeSeconds.HasValue
                    ? string.Format(CultureInfo.InvariantCulture,
                        "{0:F3}ms observed ({1:F1}% of measured plugin hook time, {2:F3}ms/min)",
                        entry.ObservedHookTimeSeconds.Value * 1000d,
                        entry.ObservedHookTimeSharePercent ?? 0,
                        entry.HookTimeMillisecondsPerMinute ?? 0)
                    : string.Format(CultureInfo.InvariantCulture,
                        "{0:F3}s cumulative hook time (baseline pending)", entry.TotalHookTimeSeconds);

                Puts(string.Format(CultureInfo.InvariantCulture,
                    "Plugin impact #{0}: {1} {2} - {3}", entry.ImpactRank, entry.Name, entry.Version, impact));
            }
        }

        private void SendDiscordReport(PerformanceReport report)
        {
            DiscordConfiguration discord = _config.Discord;
            if (discord == null || !discord.Enabled || string.IsNullOrWhiteSpace(discord.WebhookUrl))
            {
                return;
            }

            if (discord.OnlySendBenchmarks && report.Mode != "benchmark")
            {
                return;
            }

            if (!discord.WebhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || discord.WebhookUrl.IndexOf("/api/webhooks/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                PrintWarning("Discord webhook is enabled, but the configured URL is not a valid HTTPS Discord webhook URL.");
                return;
            }

            DiscordPayload payload = BuildDiscordPayload(report, discord);
            string json = JsonConvert.SerializeObject(payload, Formatting.None, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" }
            };

            webrequest.Enqueue(discord.WebhookUrl, json, delegate(int code, string response)
            {
                if (code != 200 && code != 204)
                {
                    PrintWarning(string.Format(CultureInfo.InvariantCulture,
                        "Discord webhook delivery failed with HTTP {0}. {1}",
                        code,
                        string.IsNullOrEmpty(response) ? "No response body was returned." : Truncate(response, 300)));
                }
                else if (discord.LogSuccessfulDelivery)
                {
                    Puts("Performance report delivered to Discord.");
                }
            }, this, RequestMethod.POST, headers, 10f);
        }

        private DiscordPayload BuildDiscordPayload(PerformanceReport report, DiscordConfiguration discord)
        {
            List<DiscordField> fields = new List<DiscordField>();
            string context = string.Format(CultureInfo.InvariantCulture,
                "**Mode:** {0}\n**Observation:** {1:F1}s\n**Players:** {2} → {3}\n**Entities:** {4:N0}\n**Samples:** {5}",
                EscapeDiscordMarkdown(report.Mode),
                report.ObservationDurationSeconds,
                report.OnlinePlayers,
                report.CompletedOnlinePlayers,
                report.Entities == null ? 0 : report.Entities.Total,
                report.PerformanceWindow == null ? 0 : report.PerformanceWindow.SampleCount);
            fields.Add(new DiscordField("Context", context, true));

            MetricSummary averageFrameTime = FindMetric(report.PerformanceWindow, "Average Frame Time")
                                             ?? FindMetric(report.PerformanceWindow, "Frame Time");
            MetricSummary rawFrameTime = FindMetric(report.PerformanceWindow, "Frame Time")
                                         ?? averageFrameTime;
            MetricSummary frameRate = FindMetric(report.PerformanceWindow, "Average Frame Rate")
                                      ?? FindMetric(report.PerformanceWindow, "Frame Rate");
            string performance = string.Format(CultureInfo.InvariantCulture,
                "**Frame time:** {0}\n**Frame time P95:** {1}\n**Frame rate:** {2}",
                FormatMetric(averageFrameTime, false),
                rawFrameTime == null ? "n/a" : rawFrameTime.Percentile95.ToString("F2", CultureInfo.InvariantCulture) + " ms",
                FormatMetric(frameRate, false));
            fields.Add(new DiscordField("Performance", performance, true));

            if (report.Plugins != null && report.Plugins.Entries != null)
            {
                int pluginCount = Math.Min(discord.TopPluginCount, report.Plugins.Entries.Count);
                System.Text.StringBuilder pluginText = new System.Text.StringBuilder();
                for (int i = 0; i < pluginCount; i++)
                {
                    PluginReportEntry entry = report.Plugins.Entries[i];
                    if (pluginText.Length > 0)
                    {
                        pluginText.Append('\n');
                    }

                    pluginText.AppendFormat(CultureInfo.InvariantCulture,
                        "`#{0}` **{1}** — {2:F2} ms ({3:F1}%) · {4:F2} ms/min",
                        entry.ImpactRank,
                        EscapeDiscordMarkdown(entry.Name),
                        (entry.ObservedHookTimeSeconds ?? 0) * 1000d,
                        entry.ObservedHookTimeSharePercent ?? 0,
                        entry.HookTimeMillisecondsPerMinute ?? 0);
                }

                if (pluginText.Length > 0)
                {
                    fields.Add(new DiscordField("Top plugin impactors", Truncate(pluginText.ToString(), 1024), false));
                }
            }

            if (report.PreviousBenchmarkComparison != null)
            {
                System.Text.StringBuilder comparisonText = new System.Text.StringBuilder();
                comparisonText.Append("**Confidence: ")
                    .Append(EscapeDiscordMarkdown(report.PreviousBenchmarkComparison.Confidence))
                    .AppendLine("**");

                if (!string.IsNullOrWhiteSpace(report.PreviousBenchmarkComparison.BaselineReportId))
                {
                    comparisonText.Append("Compared with **")
                        .Append(EscapeDiscordMarkdown(report.PreviousBenchmarkComparison.BaselineLabel))
                        .AppendLine("**");
                }

                for (int i = 0; i < report.PreviousBenchmarkComparison.Metrics.Count; i++)
                {
                    BenchmarkComparisonMetric metric = report.PreviousBenchmarkComparison.Metrics[i];
                    comparisonText.Append(EscapeDiscordMarkdown(metric.Name))
                        .Append(": **")
                        .Append(EscapeDiscordMarkdown(metric.Result))
                        .Append("**");

                    if (metric.ImprovementPercent.HasValue)
                    {
                        comparisonText.AppendFormat(CultureInfo.InvariantCulture,
                            " by {0:F1}%", Math.Abs(metric.ImprovementPercent.Value));
                    }

                    comparisonText.Append(" (`")
                        .Append(FormatComparisonValue(metric.Baseline, metric.Unit))
                        .Append(" → ")
                        .Append(FormatComparisonValue(metric.Current, metric.Unit))
                        .AppendLine("`)");

                    if (!string.IsNullOrWhiteSpace(metric.Note))
                    {
                        comparisonText.Append("↳ ")
                            .Append(EscapeDiscordMarkdown(metric.Note))
                            .Append('\n');
                    }
                }

                for (int i = 0; i < report.PreviousBenchmarkComparison.ContextWarnings.Count; i++)
                {
                    comparisonText.Append("⚠️ ")
                        .Append(EscapeDiscordMarkdown(report.PreviousBenchmarkComparison.ContextWarnings[i]))
                        .Append('\n');
                }

                fields.Add(new DiscordField("Benchmark comparison", Truncate(comparisonText.ToString(), 1024), false));
            }

            if (report.Quality != null)
            {
                string qualityText = report.Quality.SuitableForComparison
                    ? "✅ Current observation passed all configured quality checks."
                    : "⚠️ " + Truncate(string.Join("\n⚠️ ", report.Quality.Warnings.ToArray()), 1000);
                fields.Add(new DiscordField("Current measurement quality", qualityText, false));
            }

            DiscordEmbed embed = new DiscordEmbed
            {
                Title = (report.Mode == "benchmark" ? "Performance benchmark: " : "Performance report: ")
                        + Truncate(EscapeDiscordMarkdown(report.Label), 200),
                Description = report.Mode == "benchmark"
                    ? "A controlled performance observation window completed. Full raw data remains on the server."
                    : "A rolling server performance report completed. Full raw data remains on the server.",
                Color = HasComparisonWarnings(report) ? 16753920 : 3066993,
                Timestamp = report.CompletedUtc.ToString("o", CultureInfo.InvariantCulture),
                Fields = fields,
                Footer = new DiscordFooter
                {
                    Text = "Report " + report.ReportId + " · Schema " + report.SchemaVersion
                }
            };

            string roleId = NormalizeDiscordId(discord.MentionRoleId);
            return new DiscordPayload
            {
                Username = string.IsNullOrWhiteSpace(discord.Username) ? "Server Performance Analyzer" : Truncate(discord.Username, 80),
                AvatarUrl = string.IsNullOrWhiteSpace(discord.AvatarUrl) ? null : discord.AvatarUrl,
                Content = roleId == null ? null : "<@&" + roleId + ">",
                AllowedMentions = new DiscordAllowedMentions
                {
                    Parse = new string[0],
                    Roles = roleId == null ? new string[0] : new[] { roleId }
                },
                Embeds = new List<DiscordEmbed> { embed }
            };
        }

        private static bool HasComparisonWarnings(PerformanceReport report)
        {
            return (report.Quality != null && !report.Quality.SuitableForComparison)
                   || (report.PreviousBenchmarkComparison != null
                       && !string.Equals(report.PreviousBenchmarkComparison.Confidence, "High", StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatComparisonValue(double value, string unit)
        {
            return value.ToString("F2", CultureInfo.InvariantCulture) + " " + unit;
        }

        private static string FormatMetric(MetricSummary metric, bool useLast)
        {
            if (metric == null)
            {
                return "n/a";
            }

            double value = useLast ? metric.Last : metric.Average;
            return value.ToString("F2", CultureInfo.InvariantCulture) + " " + metric.Unit;
        }

        private static string NormalizeDiscordId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Trim();
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return null;
                }
            }

            return value;
        }

        private static string EscapeDiscordMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\")
                .Replace("`", "\\`")
                .Replace("*", "\\*")
                .Replace("_", "\\_")
                .Replace("~", "\\~")
                .Replace("|", "\\|")
                .Replace(">", "\\>");
        }

        private static string Truncate(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
            {
                return value;
            }

            return value.Substring(0, Math.Max(0, maximumLength - 1)) + "…";
        }

        private static string NormalizeLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unlabeled";
            }

            value = value.Trim();
            if (value.Length > 64)
            {
                value = value.Substring(0, 64);
            }

            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; i++)
            {
                if (char.IsControl(characters[i]))
                {
                    characters[i] = ' ';
                }
            }

            return new string(characters);
        }

        #endregion

        #region Configuration

        protected override void LoadConfig()
        {
            CopyLegacyConfigIfNeeded();
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException("Configuration deserialized to null.");
                }
            }
            catch (Exception ex)
            {
                PrintError("Configuration is invalid; defaults will be written. " + ex.Message);
                _config = Configuration.CreateDefault();
            }

            ValidateConfiguration();
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = Configuration.CreateDefault();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void CopyLegacyConfigIfNeeded()
        {
            try
            {
                string currentPath = Path.Combine(Interface.Oxide.ConfigDirectory, CurrentConfigFileName);
                string legacyPath = Path.Combine(Interface.Oxide.ConfigDirectory, LegacyConfigFileName);
                if (!File.Exists(currentPath) && File.Exists(legacyPath))
                {
                    File.Copy(legacyPath, currentPath, false);
                    Puts("Copied the legacy configuration to " + CurrentConfigFileName + ".");
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Unable to copy the legacy configuration: " + ex.Message);
            }
        }

        private void CopyLegacyDataIfNeeded()
        {
            string legacyRoot = Path.Combine(Interface.Oxide.DataDirectory, LegacyDataRoot);
            string currentRoot = Path.Combine(Interface.Oxide.DataDirectory, DataRoot);
            if (!Directory.Exists(legacyRoot))
            {
                return;
            }

            try
            {
                string[] files = Directory.GetFiles(legacyRoot, "*", SearchOption.AllDirectories);
                int copied = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    string relativePath = files[i].Substring(legacyRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string destination = Path.Combine(currentRoot, relativePath);
                    if (File.Exists(destination))
                    {
                        continue;
                    }

                    string destinationDirectory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    File.Copy(files[i], destination, false);
                    copied++;
                }

                if (copied > 0)
                {
                    Puts(string.Format(CultureInfo.InvariantCulture,
                        "Copied {0} legacy data file(s) into {1}.", copied, DataRoot));
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Unable to copy legacy report data: " + ex.Message);
            }
        }

        private void ValidateConfiguration()
        {
            _config.ReportIntervalSeconds = _config.ReportIntervalSeconds <= 0
                ? 0
                : Math.Max(30, _config.ReportIntervalSeconds);
            _config.PerformanceSampleIntervalSeconds = Mathf.Clamp(_config.PerformanceSampleIntervalSeconds, 1f, 60f);
            _config.BenchmarkSampleIntervalSeconds = Mathf.Clamp(_config.BenchmarkSampleIntervalSeconds, 0.25f, 10f);
            _config.EntityBatchSize = Math.Max(50, Math.Min(10000, _config.EntityBatchSize));
            _config.DefaultBenchmarkDurationSeconds = Math.Max(10, _config.DefaultBenchmarkDurationSeconds);
            _config.MinimumBenchmarkDurationSeconds = Math.Max(5, _config.MinimumBenchmarkDurationSeconds);
            _config.MaximumBenchmarkDurationSeconds = Math.Max(_config.MinimumBenchmarkDurationSeconds, _config.MaximumBenchmarkDurationSeconds);
            _config.DefaultBenchmarkDurationSeconds = Math.Min(_config.DefaultBenchmarkDurationSeconds, _config.MaximumBenchmarkDurationSeconds);
            _config.DefaultBenchmarkWarmupSeconds = Math.Max(0, _config.DefaultBenchmarkWarmupSeconds);
            _config.MaximumBenchmarkWarmupSeconds = Math.Max(_config.DefaultBenchmarkWarmupSeconds, _config.MaximumBenchmarkWarmupSeconds);
            _config.MinimumBenchmarkSamplesForComparison = Math.Max(10, Math.Min(1000, _config.MinimumBenchmarkSamplesForComparison));
            _config.MinimumPluginHookMillisecondsForComparison = Math.Max(0, _config.MinimumPluginHookMillisecondsForComparison);
            _config.MaximumNamedBenchmarkReferences = Math.Max(10, Math.Min(1000, _config.MaximumNamedBenchmarkReferences));
            _config.TopPluginCountInConsole = Math.Max(0, Math.Min(25, _config.TopPluginCountInConsole));
            _config.MaximumArchivedReports = Math.Max(0, _config.MaximumArchivedReports);
            _config.MaximumReportAgeDays = Math.Max(0, _config.MaximumReportAgeDays);
            _config.ExcludedPlugins = _config.ExcludedPlugins ?? new string[0];
            _config.ExcludedEntities = _config.ExcludedEntities ?? new string[0];
            _config.Discord = _config.Discord ?? new DiscordConfiguration();
            _config.Discord.TopPluginCount = Math.Max(1, Math.Min(15, _config.Discord.TopPluginCount));
            if (string.Equals(_config.Discord.Username, "Performance Monitor Enhanced", StringComparison.Ordinal))
            {
                _config.Discord.Username = "Server Performance Analyzer";
            }
        }

        private void RebuildExclusionCaches()
        {
            _excludedPlugins = new HashSet<string>(_config.ExcludedPlugins, StringComparer.OrdinalIgnoreCase);
            _excludedEntities = new HashSet<string>(_config.ExcludedEntities, StringComparer.OrdinalIgnoreCase);
        }

        private class Configuration
        {
            [JsonProperty("Create automatic reports every seconds (0 disables)")]
            public int ReportIntervalSeconds = 0;

            [JsonProperty("Performance sampling interval seconds")]
            public float PerformanceSampleIntervalSeconds = 5f;

            [JsonProperty("Benchmark sampling interval seconds")]
            public float BenchmarkSampleIntervalSeconds = 1f;

            [JsonProperty("Entities processed per server frame")]
            public int EntityBatchSize = 750;

            [JsonProperty("Include plugin report")]
            public bool IncludePluginReport = true;

            [JsonProperty("Include entity report")]
            public bool IncludeEntityReport = true;

            [JsonProperty("Write a Latest.json convenience file")]
            public bool WriteLatestReport = true;

            [JsonProperty("Maximum archived reports (0 is unlimited)")]
            public int MaximumArchivedReports = 336;

            [JsonProperty("Maximum report age in days (0 is unlimited)")]
            public int MaximumReportAgeDays = 14;

            [JsonProperty("Default benchmark duration seconds")]
            public int DefaultBenchmarkDurationSeconds = 300;

            [JsonProperty("Minimum benchmark duration seconds")]
            public int MinimumBenchmarkDurationSeconds = 10;

            [JsonProperty("Maximum benchmark duration seconds")]
            public int MaximumBenchmarkDurationSeconds = 3600;

            [JsonProperty("Default benchmark warm-up seconds")]
            public int DefaultBenchmarkWarmupSeconds = 15;

            [JsonProperty("Maximum benchmark warm-up seconds")]
            public int MaximumBenchmarkWarmupSeconds = 300;

            [JsonProperty("Compare each benchmark with the previous benchmark")]
            public bool CompareBenchmarkToPrevious = true;

            [JsonProperty("Minimum benchmark samples for comparison")]
            public int MinimumBenchmarkSamplesForComparison = 30;

            [JsonProperty("Minimum total plugin hook milliseconds for comparison")]
            public double MinimumPluginHookMillisecondsForComparison = 5d;

            [JsonProperty("Maximum named benchmark references")]
            public int MaximumNamedBenchmarkReferences = 100;

            [JsonProperty("Top plugin count written to console")]
            public int TopPluginCountInConsole = 10;

            [JsonProperty("Log entity scan progress")]
            public bool LogEntityProgress = false;

            [JsonProperty("Excluded plugins")]
            public string[] ExcludedPlugins = new string[0];

            [JsonProperty("Excluded entity short names")]
            public string[] ExcludedEntities = new string[0];

            [JsonProperty("Discord webhook")]
            public DiscordConfiguration Discord = new DiscordConfiguration();

            public static Configuration CreateDefault()
            {
                return new Configuration();
            }
        }

        private class DiscordConfiguration
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("Webhook URL")]
            public string WebhookUrl = string.Empty;

            [JsonProperty("Only send benchmark reports")]
            public bool OnlySendBenchmarks = true;

            [JsonProperty("Username")]
            public string Username = "Server Performance Analyzer";

            [JsonProperty("Avatar URL")]
            public string AvatarUrl = string.Empty;

            [JsonProperty("Role ID to mention (empty disables mentions)")]
            public string MentionRoleId = string.Empty;

            [JsonProperty("Top plugin count")]
            public int TopPluginCount = 10;

            [JsonProperty("Log successful deliveries")]
            public bool LogSuccessfulDelivery = true;
        }

        #endregion

        #region Data models

        private class PerformanceReport
        {
            [JsonProperty("Schema Version")]
            public int SchemaVersion;

            [JsonProperty("Report ID")]
            public string ReportId;

            [JsonProperty("Label")]
            public string Label;

            [JsonProperty("Mode")]
            public string Mode;

            [JsonProperty("Server Identity")]
            public string ServerIdentity;

            [JsonProperty("Started UTC")]
            public DateTime StartedUtc;

            [JsonProperty("Completed UTC")]
            public DateTime CompletedUtc;

            [JsonProperty("Observation Started UTC")]
            public DateTime ObservationStartedUtc;

            [JsonProperty("Observation Completed UTC")]
            public DateTime ObservationCompletedUtc;

            [JsonProperty("Warm-up Duration Seconds")]
            public double WarmupDurationSeconds;

            [JsonProperty("Observation Duration Seconds")]
            public double ObservationDurationSeconds;

            [JsonProperty("Total Report Duration Seconds")]
            public double TotalReportDurationSeconds;

            [JsonProperty("Online Players")]
            public int OnlinePlayers;

            [JsonProperty("Sleeping Players")]
            public int SleepingPlayers;

            [JsonProperty("Completed Online Players")]
            public int CompletedOnlinePlayers;

            [JsonProperty("Completed Sleeping Players")]
            public int CompletedSleepingPlayers;

            [JsonProperty("Performance Window")]
            public PerformanceWindowReport PerformanceWindow;

            [JsonProperty("Performance Snapshot")]
            public Performance.Tick PerformanceSnapshot;

            [JsonProperty("Plugins")]
            public PluginReport Plugins;

            [JsonProperty("Entities")]
            public EntityReport Entities;

            [JsonProperty("Measurement Quality")]
            public QualityReport Quality;

            [JsonProperty("Previous Benchmark Comparison", NullValueHandling = NullValueHandling.Ignore)]
            public BenchmarkComparison PreviousBenchmarkComparison;

            [JsonProperty("Error", NullValueHandling = NullValueHandling.Ignore)]
            public string Error;
        }

        private class PluginReport
        {
            [JsonProperty("Captured UTC")]
            public DateTime CapturedUtc;

            [JsonProperty("Baseline Available")]
            public bool BaselineAvailable;

            [JsonProperty("Observation Duration Seconds")]
            public double ObservationDurationSeconds;

            [JsonProperty("Observed Plugin Hook Time Total Seconds")]
            public double ObservedHookTimeTotalSeconds;

            [JsonProperty("Entries")]
            public List<PluginReportEntry> Entries;
        }

        private class PluginReportEntry
        {
            [JsonProperty("Impact Rank")]
            public int ImpactRank;

            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Version")]
            public string Version;

            [JsonProperty("Total Hook Time Seconds")]
            public double TotalHookTimeSeconds;

            [JsonProperty("Observed Hook Time Seconds", NullValueHandling = NullValueHandling.Ignore)]
            public double? ObservedHookTimeSeconds;

            [JsonProperty("Observed Hook Time Share Percent", NullValueHandling = NullValueHandling.Ignore)]
            public double? ObservedHookTimeSharePercent;

            [JsonProperty("Hook Time Milliseconds Per Minute", NullValueHandling = NullValueHandling.Ignore)]
            public double? HookTimeMillisecondsPerMinute;

            [JsonProperty("Memory Metric Kind")]
            public string MemoryMetricKind;

            [JsonProperty("Memory Value Bytes")]
            public double MemoryValueBytes;

            [JsonProperty("Observed Memory Change Bytes", NullValueHandling = NullValueHandling.Ignore)]
            public double? ObservedMemoryChangeBytes;
        }

        private class PluginCounter
        {
            public string Name;
            public string Version;
            public double HookTimeSeconds;
            public double MemoryValueBytes;
            public string MemoryMetricKind;
        }

        private class EntityReport
        {
            [JsonProperty("Snapshot Size")]
            public int SnapshotSize;

            [JsonProperty("Processed")]
            public int Processed;

            [JsonProperty("Total Included")]
            public int Total;

            [JsonProperty("Owned")]
            public int Owned;

            [JsonProperty("Unowned")]
            public int Unowned;

            [JsonProperty("Excluded")]
            public int Excluded;

            [JsonProperty("Invalid Or Destroyed")]
            public int InvalidOrDestroyed;

            [JsonProperty("Missing Short Name")]
            public int MissingShortName;

            [JsonProperty("Scan Duration Seconds")]
            public double ScanDurationSeconds;

            [JsonProperty("Entries")]
            public List<EntityReportEntry> Entries;
        }

        private class EntityReportEntry
        {
            [JsonProperty("Short Name")]
            public string ShortName;

            [JsonProperty("Total")]
            public int Total;

            [JsonProperty("Owned")]
            public int Owned;

            [JsonProperty("Unowned")]
            public int Unowned;
        }

        private class EntityCounter
        {
            public int Total;
            public int Owned;
            public int Unowned;
        }

        private class PerformanceWindowReport
        {
            [JsonProperty("Started UTC")]
            public DateTime StartedUtc;

            [JsonProperty("Completed UTC")]
            public DateTime CompletedUtc;

            [JsonProperty("Duration Seconds")]
            public double DurationSeconds;

            [JsonProperty("Sample Count")]
            public int SampleCount;

            [JsonProperty("Metrics")]
            public List<MetricSummary> Metrics;
        }

        private class MetricSummary
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Source Member")]
            public string SourceMember;

            [JsonProperty("Unit")]
            public string Unit;

            [JsonProperty("Samples")]
            public int Samples;

            [JsonProperty("Minimum")]
            public double Minimum;

            [JsonProperty("Maximum")]
            public double Maximum;

            [JsonProperty("Average")]
            public double Average;

            [JsonProperty("Standard Deviation")]
            public double StandardDeviation;

            [JsonProperty("Median")]
            public double Median;

            [JsonProperty("Percentile 95")]
            public double Percentile95;

            [JsonProperty("Percentile 99")]
            public double Percentile99;

            [JsonProperty("Last")]
            public double Last;
        }

        private class QualityReport
        {
            [JsonProperty("Suitable For Comparison")]
            public bool SuitableForComparison;

            [JsonProperty("Warnings")]
            public List<string> Warnings;

            [JsonProperty("Environment Events")]
            public List<EnvironmentEvent> EnvironmentEvents;
        }

        private class EnvironmentEvent
        {
            [JsonProperty("Timestamp UTC")]
            public DateTime TimestampUtc;

            [JsonProperty("Type")]
            public string Type;

            [JsonProperty("Description")]
            public string Description;
        }

        private class BenchmarkComparison
        {
            [JsonProperty("Baseline Report ID")]
            public string BaselineReportId;

            [JsonProperty("Baseline Label")]
            public string BaselineLabel;

            [JsonProperty("Baseline Completed UTC")]
            public DateTime BaselineCompletedUtc;

            [JsonProperty("Metrics")]
            public List<BenchmarkComparisonMetric> Metrics;

            [JsonProperty("Context Warnings")]
            public List<string> ContextWarnings;

            [JsonProperty("Comparison Confidence")]
            public string Confidence;
        }

        private class BenchmarkComparisonMetric
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Unit")]
            public string Unit;

            [JsonProperty("Baseline")]
            public double Baseline;

            [JsonProperty("Current")]
            public double Current;

            [JsonProperty("Change Percent")]
            public double? ChangePercent;

            [JsonProperty("Improvement Percent")]
            public double? ImprovementPercent;

            [JsonProperty("Result")]
            public string Result;

            [JsonProperty("Reliable")]
            public bool Reliable;

            [JsonProperty("Note", NullValueHandling = NullValueHandling.Ignore)]
            public string Note;
        }

        private class BenchmarkReferenceStore
        {
            [JsonProperty("Entries")]
            public List<BenchmarkReference> Entries;
        }

        private class BenchmarkReference
        {
            public string ReportId;
            public string Label;
            public DateTime CompletedUtc;
            public double? AverageFrameTime;
            public double? AverageFrameRate;
            public double? PluginHookMillisecondsPerMinute;
            public double PluginHookObservedMilliseconds;
            public double ObservationDurationSeconds;
            public int? SampleCount;
            public int OnlinePlayers;
            public int EntityCount;
            public bool SuitableForComparison;

            public static BenchmarkReference FromReport(PerformanceReport report)
            {
                MetricSummary frameTime = FindMetric(report.PerformanceWindow, "Average Frame Time")
                                          ?? FindMetric(report.PerformanceWindow, "Frame Time");
                MetricSummary frameRate = FindMetric(report.PerformanceWindow, "Average Frame Rate")
                                          ?? FindMetric(report.PerformanceWindow, "Frame Rate");

                return new BenchmarkReference
                {
                    ReportId = report.ReportId,
                    Label = report.Label,
                    CompletedUtc = report.CompletedUtc,
                    AverageFrameTime = frameTime == null ? null : (double?)frameTime.Average,
                    AverageFrameRate = frameRate == null ? null : (double?)frameRate.Average,
                    PluginHookMillisecondsPerMinute = report.Plugins == null || report.Plugins.ObservationDurationSeconds <= 0
                        ? null
                        : (double?)(report.Plugins.ObservedHookTimeTotalSeconds * 60000d / report.Plugins.ObservationDurationSeconds),
                    PluginHookObservedMilliseconds = report.Plugins == null
                        ? 0
                        : report.Plugins.ObservedHookTimeTotalSeconds * 1000d,
                    ObservationDurationSeconds = report.ObservationDurationSeconds,
                    SampleCount = report.PerformanceWindow == null ? null : (int?)report.PerformanceWindow.SampleCount,
                    OnlinePlayers = report.CompletedOnlinePlayers,
                    EntityCount = report.Entities == null ? 0 : report.Entities.Total,
                    SuitableForComparison = report.Quality != null && report.Quality.SuitableForComparison
                };
            }
        }

        private class DiscordPayload
        {
            [JsonProperty("username")]
            public string Username;

            [JsonProperty("avatar_url")]
            public string AvatarUrl;

            [JsonProperty("content")]
            public string Content;

            [JsonProperty("allowed_mentions")]
            public DiscordAllowedMentions AllowedMentions;

            [JsonProperty("embeds")]
            public List<DiscordEmbed> Embeds;
        }

        private class DiscordAllowedMentions
        {
            [JsonProperty("parse")]
            public string[] Parse;

            [JsonProperty("roles")]
            public string[] Roles;
        }

        private class DiscordEmbed
        {
            [JsonProperty("title")]
            public string Title;

            [JsonProperty("description")]
            public string Description;

            [JsonProperty("color")]
            public int Color;

            [JsonProperty("timestamp")]
            public string Timestamp;

            [JsonProperty("fields")]
            public List<DiscordField> Fields;

            [JsonProperty("footer")]
            public DiscordFooter Footer;
        }

        private class DiscordField
        {
            [JsonProperty("name")]
            public string Name;

            [JsonProperty("value")]
            public string Value;

            [JsonProperty("inline")]
            public bool Inline;

            public DiscordField(string name, string value, bool inline)
            {
                Name = name;
                Value = value;
                Inline = inline;
            }
        }

        private class DiscordFooter
        {
            [JsonProperty("text")]
            public string Text;
        }

        private class MetricDefinition
        {
            public readonly string Name;
            public readonly string MemberName;
            public readonly string Unit;

            public MetricDefinition(string name, string memberName, string unit)
            {
                Name = name;
                MemberName = memberName;
                Unit = unit;
            }
        }

        private class MetricAccumulator
        {
            private const int MaximumPercentileSamples = 4096;
            private readonly MetricDefinition _definition;
            private readonly List<double> _percentileSamples = new List<double>();
            private readonly System.Random _reservoirRandom;
            private int _count;
            private double _mean;
            private double _sumOfSquares;
            private double _minimum = double.MaxValue;
            private double _maximum = double.MinValue;
            private double _last;

            public MetricAccumulator(MetricDefinition definition)
            {
                _definition = definition;
                _reservoirRandom = new System.Random(definition.MemberName.GetHashCode());
            }

            public void Add(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    return;
                }

                _count++;
                double delta = value - _mean;
                _mean += delta / _count;
                double delta2 = value - _mean;
                _sumOfSquares += delta * delta2;
                _minimum = Math.Min(_minimum, value);
                _maximum = Math.Max(_maximum, value);
                _last = value;

                if (_percentileSamples.Count < MaximumPercentileSamples)
                {
                    _percentileSamples.Add(value);
                }
                else
                {
                    int replacementIndex = _reservoirRandom.Next(_count);
                    if (replacementIndex < MaximumPercentileSamples)
                    {
                        _percentileSamples[replacementIndex] = value;
                    }
                }
            }

            public MetricSummary CreateSummary()
            {
                List<double> sortedSamples = new List<double>(_percentileSamples);
                sortedSamples.Sort();

                return new MetricSummary
                {
                    Name = _definition.Name,
                    SourceMember = _definition.MemberName,
                    Unit = _definition.Unit,
                    Samples = _count,
                    Minimum = _count == 0 ? 0 : _minimum,
                    Maximum = _count == 0 ? 0 : _maximum,
                    Average = _count == 0 ? 0 : _mean,
                    StandardDeviation = _count > 1 ? Math.Sqrt(_sumOfSquares / (_count - 1)) : 0,
                    Median = GetPercentile(sortedSamples, 0.5d),
                    Percentile95 = GetPercentile(sortedSamples, 0.95d),
                    Percentile99 = GetPercentile(sortedSamples, 0.99d),
                    Last = _count == 0 ? 0 : _last
                };
            }

            private static double GetPercentile(List<double> sortedValues, double percentile)
            {
                if (sortedValues == null || sortedValues.Count == 0)
                {
                    return 0;
                }

                double position = (sortedValues.Count - 1) * percentile;
                int lower = (int)Math.Floor(position);
                int upper = (int)Math.Ceiling(position);
                if (lower == upper)
                {
                    return sortedValues[lower];
                }

                double fraction = position - lower;
                return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
            }
        }

        private class PerformanceAccumulator
        {
            private readonly DateTime _startedUtc;
            private DateTime _completedUtc;
            private readonly MetricDefinition[] _definitions;
            private readonly MetricAccumulator[] _metrics;
            private readonly Dictionary<string, MemberInfo> _memberCache = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);
            private Type _capturedType;

            public int SampleCount { get; private set; }

            public DateTime StartedUtc
            {
                get { return _startedUtc; }
            }

            public DateTime CompletedUtc
            {
                get { return _completedUtc == default(DateTime) ? DateTime.UtcNow : _completedUtc; }
            }

            public double DurationSeconds
            {
                get
                {
                    DateTime end = _completedUtc == default(DateTime) ? DateTime.UtcNow : _completedUtc;
                    return Math.Max(0, (end - _startedUtc).TotalSeconds);
                }
            }

            public PerformanceAccumulator(DateTime startedUtc, MetricDefinition[] definitions)
            {
                _startedUtc = startedUtc;
                _definitions = definitions;
                _metrics = new MetricAccumulator[definitions.Length];
                for (int i = 0; i < definitions.Length; i++)
                {
                    _metrics[i] = new MetricAccumulator(definitions[i]);
                }
            }

            public void Capture(object tick)
            {
                if (tick == null)
                {
                    return;
                }

                Type type = tick.GetType();
                if (_capturedType != type)
                {
                    _capturedType = type;
                    _memberCache.Clear();
                }

                bool capturedAny = false;
                for (int i = 0; i < _definitions.Length; i++)
                {
                    double value;
                    if (TryReadDouble(tick, _definitions[i].MemberName, out value))
                    {
                        _metrics[i].Add(value);
                        capturedAny = true;
                    }
                }

                if (capturedAny)
                {
                    SampleCount++;
                }
            }

            public void Close(DateTime completedUtc)
            {
                _completedUtc = completedUtc;
            }

            public PerformanceWindowReport CreateReport()
            {
                List<MetricSummary> summaries = new List<MetricSummary>(_metrics.Length);
                for (int i = 0; i < _metrics.Length; i++)
                {
                    MetricSummary summary = _metrics[i].CreateSummary();
                    if (summary.Samples > 0)
                    {
                        summaries.Add(summary);
                    }
                }

                return new PerformanceWindowReport
                {
                    StartedUtc = _startedUtc,
                    CompletedUtc = _completedUtc == default(DateTime) ? DateTime.UtcNow : _completedUtc,
                    DurationSeconds = DurationSeconds,
                    SampleCount = SampleCount,
                    Metrics = summaries
                };
            }

            private bool TryReadDouble(object instance, string memberName, out double value)
            {
                value = 0;
                MemberInfo member;
                if (!_memberCache.TryGetValue(memberName, out member))
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                    member = (MemberInfo)_capturedType.GetField(memberName, flags)
                             ?? _capturedType.GetProperty(memberName, flags);
                    _memberCache[memberName] = member;
                }

                if (member == null)
                {
                    return false;
                }

                object rawValue;
                FieldInfo field = member as FieldInfo;
                if (field != null)
                {
                    rawValue = field.GetValue(instance);
                }
                else
                {
                    PropertyInfo property = member as PropertyInfo;
                    rawValue = property != null ? property.GetValue(instance, null) : null;
                }

                if (rawValue == null)
                {
                    return false;
                }

                if (rawValue is bool)
                {
                    value = (bool)rawValue ? 1d : 0d;
                    return true;
                }

                try
                {
                    value = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        #endregion
    }
}
