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
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Performance Monitor Enhanced", "SeesAll", "2.0.0")]
    [Description("Low-impact server performance reports and repeatable plugin benchmark windows")]
    public class PerformanceMonitorEnhanced : RustPlugin
    {
        private const string ReportCommand = "monitor.report";
        private const string LegacyReportCommand = "monitor.createreport";
        private const string BenchmarkCommand = "monitor.benchmark";
        private const string StatusCommand = "monitor.status";
        private const string DataRoot = "PerformanceMonitorEnhanced";

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
            new MetricDefinition("Ping", "ping", "ms")
        };

        #region Lifecycle

        private void Init()
        {
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
                    TryStartReport("scheduled", 0, false, null);
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
            if (TryStartReport(label, 0, false, arg))
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
                Reply(arg, "Usage: monitor.benchmark <label> [duration seconds]");
                return;
            }

            int duration = GetIntArgument(arg, 1, _config.DefaultBenchmarkDurationSeconds);
            duration = Math.Max(_config.MinimumBenchmarkDurationSeconds, Math.Min(_config.MaximumBenchmarkDurationSeconds, duration));

            if (TryStartReport(label, duration, true, arg))
            {
                Reply(arg, string.Format(CultureInfo.InvariantCulture,
                    "Benchmark '{0}' started for {1} seconds.", NormalizeLabel(label), duration));
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
                Reply(arg, "Performance Monitor Enhanced is idle.");
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

        private bool TryStartReport(string requestedLabel, int benchmarkDurationSeconds, bool benchmark, ConsoleSystem.Arg requester)
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
                CreateReport(_activeLabel, benchmarkDurationSeconds, benchmark));
            return true;
        }

        private IEnumerator CreateReport(string label, int benchmarkDurationSeconds, bool benchmark)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            DateTime startedUtc = DateTime.UtcNow;
            PerformanceReport report = new PerformanceReport
            {
                SchemaVersion = 2,
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
                    benchmarkStartCounters = TryCapturePluginCounters(out failure);
                    if (failure == null)
                    {
                        completedPerformanceWindow = new PerformanceAccumulator(DateTime.UtcNow, PerformanceMetrics);
                        Stopwatch benchmarkStopwatch = Stopwatch.StartNew();

                        while (benchmarkStopwatch.Elapsed.TotalSeconds < benchmarkDurationSeconds)
                        {
                            completedPerformanceWindow.Capture(Performance.current);
                            float remaining = (float)(benchmarkDurationSeconds - benchmarkStopwatch.Elapsed.TotalSeconds);
                            float delay = Mathf.Min(_config.PerformanceSampleIntervalSeconds, Mathf.Max(0.05f, remaining));
                            yield return new WaitForSecondsRealtime(delay);
                        }

                        completedPerformanceWindow.Capture(Performance.current);
                        completedPerformanceWindow.Close(DateTime.UtcNow);
                        report.ObservationDurationSeconds = benchmarkStopwatch.Elapsed.TotalSeconds;
                    }
                }
                else
                {
                    CaptureRollingPerformanceSample();
                    completedPerformanceWindow = _rollingPerformance;
                    completedPerformanceWindow.Close(DateTime.UtcNow);
                    _rollingPerformance = new PerformanceAccumulator(DateTime.UtcNow, PerformanceMetrics);
                    report.ObservationDurationSeconds = completedPerformanceWindow.DurationSeconds;
                }

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

                if (failure != null)
                {
                    report.Error = failure.ToString();
                    PrintError("Performance report failed: " + failure);
                }

                try
                {
                    SaveReport(report);
                    LogSummary(report);
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

        private void ValidateConfiguration()
        {
            _config.ReportIntervalSeconds = _config.ReportIntervalSeconds <= 0
                ? 0
                : Math.Max(30, _config.ReportIntervalSeconds);
            _config.PerformanceSampleIntervalSeconds = Mathf.Clamp(_config.PerformanceSampleIntervalSeconds, 1f, 60f);
            _config.EntityBatchSize = Math.Max(50, Math.Min(10000, _config.EntityBatchSize));
            _config.DefaultBenchmarkDurationSeconds = Math.Max(10, _config.DefaultBenchmarkDurationSeconds);
            _config.MinimumBenchmarkDurationSeconds = Math.Max(5, _config.MinimumBenchmarkDurationSeconds);
            _config.MaximumBenchmarkDurationSeconds = Math.Max(_config.MinimumBenchmarkDurationSeconds, _config.MaximumBenchmarkDurationSeconds);
            _config.DefaultBenchmarkDurationSeconds = Math.Min(_config.DefaultBenchmarkDurationSeconds, _config.MaximumBenchmarkDurationSeconds);
            _config.TopPluginCountInConsole = Math.Max(0, Math.Min(25, _config.TopPluginCountInConsole));
            _config.MaximumArchivedReports = Math.Max(0, _config.MaximumArchivedReports);
            _config.MaximumReportAgeDays = Math.Max(0, _config.MaximumReportAgeDays);
            _config.ExcludedPlugins = _config.ExcludedPlugins ?? new string[0];
            _config.ExcludedEntities = _config.ExcludedEntities ?? new string[0];
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

            [JsonProperty("Top plugin count written to console")]
            public int TopPluginCountInConsole = 10;

            [JsonProperty("Log entity scan progress")]
            public bool LogEntityProgress = false;

            [JsonProperty("Excluded plugins")]
            public string[] ExcludedPlugins = new string[0];

            [JsonProperty("Excluded entity short names")]
            public string[] ExcludedEntities = new string[0];

            public static Configuration CreateDefault()
            {
                return new Configuration();
            }
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

            [JsonProperty("Observation Duration Seconds")]
            public double ObservationDurationSeconds;

            [JsonProperty("Total Report Duration Seconds")]
            public double TotalReportDurationSeconds;

            [JsonProperty("Online Players")]
            public int OnlinePlayers;

            [JsonProperty("Sleeping Players")]
            public int SleepingPlayers;

            [JsonProperty("Performance Window")]
            public PerformanceWindowReport PerformanceWindow;

            [JsonProperty("Performance Snapshot")]
            public Performance.Tick PerformanceSnapshot;

            [JsonProperty("Plugins")]
            public PluginReport Plugins;

            [JsonProperty("Entities")]
            public EntityReport Entities;

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

            [JsonProperty("Last")]
            public double Last;
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
            private readonly MetricDefinition _definition;
            private int _count;
            private double _mean;
            private double _sumOfSquares;
            private double _minimum = double.MaxValue;
            private double _maximum = double.MinValue;
            private double _last;

            public MetricAccumulator(MetricDefinition definition)
            {
                _definition = definition;
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
            }

            public MetricSummary CreateSummary()
            {
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
                    Last = _count == 0 ? 0 : _last
                };
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

                if (rawValue == null || rawValue is bool)
                {
                    return false;
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
