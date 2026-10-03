# Server Performance Analyzer

Server Performance Analyzer is a Rust server plugin for uMod and Carbon. It creates low-impact performance reports and repeatable benchmark windows designed for before/after plugin comparisons.

## Why this version exists

Lifetime hook totals are useful for finding broad trends, but they are poor evidence for whether a specific change improved a server. This version adds observation-window deltas, normalized rates, percentile statistics, labeled benchmarks with warm-up periods, measurement-quality warnings, automatic previous-run comparisons, Discord summaries, bounded report retention, and a batched entity scan.

The monitor reports correlation and measured framework counters. It cannot prove that a plugin alone caused low FPS: player count, entity count, saves, networking, GC, other plugins, and game updates can all change the result. Compare runs under similar server conditions.

## Commands

All commands require server-admin access.

- `monitor.report [label]` creates a report from the rolling performance window. After the first report, plugin values include deltas since the preceding report.
- `monitor.createreport [label]` is a compatibility alias for `monitor.report`.
- `monitor.benchmark <label> [durationSeconds] [warmupSeconds] [baselineLabel]` observes a dedicated, labeled window. Defaults are a 15-second warm-up and 300-second observation. The optional baseline label selects a previously completed benchmark instead of simply using the preceding run.
- `monitor.benchmarkset <label> [runs] [durationSeconds] [warmupSeconds] [baselineLabel]` runs 2–20 consecutive benchmark windows and sends one aggregate Discord report. Central values are medians across runs, ranges expose variability, and every member report remains archived as raw JSON.
- `monitor.status` shows whether a report or benchmark is active.

Recommended A/B workflow:

1. Run `monitor.benchmark before-change 300 15` during representative activity.
2. Make one controlled change.
3. Recreate similar player/entity/server activity.
4. Run `monitor.benchmark after-change 300 15 before-change`.
5. Review the automatic comparison, quality warnings, frame-time percentiles, and per-plugin observed hook time.

Change only one thing between runs. Use several alternating A/B runs when a decision matters; one pair can be distorted by player behavior, saves, garbage collection, entity changes, networking, or game updates.

For higher-confidence work, prefer `monitor.benchmarkset before-change 3 300 15`, make one controlled change, then run `monitor.benchmarkset after-change 3 300 15 before-change`. Only the aggregate set report is sent to Discord, avoiding one message per member run.

## Plugin impact metrics

Plugins are ranked by hook time accumulated during the observation window. Each entry includes:

- observed hook time;
- share of all measured plugin hook time;
- hook milliseconds per minute, which normalizes different window lengths;
- cumulative hook time for context;
- the framework memory counter and its change during the window.

Discord also shows the total measured plugin-hook load as milliseconds per second, milliseconds per minute, and an approximate percentage of one main thread. Relative plugin share identifies who dominates measured plugin work; absolute load shows whether that work is large enough to matter to the server as a whole.

On uMod, the memory value is cumulative hook allocation. On Carbon, it is the framework's current-memory estimate. These values have different semantics and should not be compared across frameworks.

Hook time only measures execution visible to the framework's hook accounting. It may not capture background work, native calls, external services, or all work scheduled by a plugin.

## Benchmark quality and comparison

Every benchmark records average, minimum, maximum, standard deviation, median, P95, and P99 statistics from a bounded sample reservoir. Benchmarks use a dedicated one-second sampling interval by default, independent of the lower-frequency rolling monitor. The warm-up occurs before counters and performance samples begin, reducing startup and cache effects.

Player and networked-entity counts are sampled throughout each benchmark. Comparisons use average observed workload rather than only start/end snapshots. Benchmark sets summarize the median and range of run-level frame time, plugin load, players, and entities.

Reports flag known comparison hazards, including player-count changes, too few samples, server saves, plugin load/unload events, garbage collection, entity-count differences, and mismatched observation durations. A warning does not invalidate the raw measurements, but it lowers the reported comparison confidence.

When enabled, each completed benchmark is compared with either the explicitly requested named baseline or the preceding benchmark. Reports show baseline and current values, plain-language improvement/regression wording, and High, Moderate, or Low comparison confidence. Plugin-hook percentage changes are marked inconclusive when either window contains less than the configured noise-floor amount of measured hook time. Named references are retained in `oxide/data/ServerPerformanceAnalyzer/BenchmarkReferences.json`.

## Discord webhook

Discord delivery is opt-in. Add the webhook URL to the generated config on the server; never commit it to Git. By default, only benchmark reports are sent. Every embed identifies its source server. Leave `Server description (empty uses server hostname)` blank to use Rust's live `server.hostname`, or set a short override such as `2x` or `NA 3x`. The embed contains run context, frame performance, highest-impact plugins, comparison results, and measurement-quality warnings. Full JSON remains on the server.

An optional role ID can be mentioned. Mentions are restricted to that configured role, and report labels/plugin names are escaped before being placed in the embed.

## Output

Archived reports are written to:

`oxide/data/ServerPerformanceAnalyzer/Reports/yyyy-MM-dd/`

The optional convenience copy is:

`oxide/data/ServerPerformanceAnalyzer/Latest.json`

Reports use UTC timestamps and collision-resistant IDs. Old archives are removed according to the configured maximum count and age.

## Performance design

- Performance sampling is lightweight and configurable.
- Entity references are snapshotted once, then processed in batches over multiple server frames.
- Exclusions use case-insensitive hash sets.
- Plugin totals are captured once per boundary rather than repeatedly.
- Pooled entity lists are always returned with `finally` cleanup.
- Report state is cleared even after failures, so a failed report cannot permanently disable later reports.

## Default configuration

```json
{
  "Create automatic reports every seconds (0 disables)": 0,
  "Performance sampling interval seconds": 5.0,
  "Benchmark sampling interval seconds": 1.0,
  "Entities processed per server frame": 750,
  "Include plugin report": true,
  "Include entity report": true,
  "Write a Latest.json convenience file": true,
  "Maximum archived reports (0 is unlimited)": 336,
  "Maximum report age in days (0 is unlimited)": 14,
  "Default benchmark duration seconds": 300,
  "Minimum benchmark duration seconds": 10,
  "Maximum benchmark duration seconds": 3600,
  "Default benchmark warm-up seconds": 15,
  "Maximum benchmark warm-up seconds": 300,
  "Default benchmark set runs": 3,
  "Maximum benchmark set runs": 10,
  "Benchmark set cooldown seconds": 5.0,
  "Compare each benchmark with the previous benchmark": true,
  "Minimum benchmark samples for comparison": 30,
  "Minimum total plugin hook milliseconds for comparison": 5.0,
  "Maximum named benchmark references": 100,
  "Top plugin count written to console": 10,
  "Log entity scan progress": false,
  "Excluded plugins": [],
  "Excluded entity short names": [],
  "Discord webhook": {
    "Enabled": false,
    "Webhook URL": "",
    "Server description (empty uses server hostname)": "",
    "Only send benchmark reports": true,
    "Username": "Server Performance Analyzer",
    "Avatar URL": "",
    "Role ID to mention (empty disables mentions)": "",
    "Top plugin count": 10,
    "Log successful deliveries": true
  }
}
```

Automatic report intervals below 30 seconds are clamped to 30 seconds. Benchmark duration, warm-up, sampling frequency, quality thresholds, and retained named references are clamped to safe configured limits.

When upgrading from version 2.1.0, the plugin copies the legacy `PerformanceMonitorEnhanced` configuration and report data into the new `ServerPerformanceAnalyzer` locations. Legacy files are retained as a recoverable backup.

## Credits and license

This is a ground-up enhancement inspired by Orange's original MIT-licensed Performance Monitor plugin. See [LICENSE](LICENSE).
