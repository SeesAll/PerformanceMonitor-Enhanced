# Performance Monitor Enhanced

Performance Monitor Enhanced is a Rust server plugin for uMod and Carbon. It creates low-impact performance reports and repeatable benchmark windows designed for before/after plugin comparisons.

## Why this version exists

Lifetime hook totals are useful for finding broad trends, but they are poor evidence for whether a specific change improved a server. This version adds observation-window deltas, normalized rates, frame-statistic sampling, labeled benchmarks, bounded report retention, and a batched entity scan.

The monitor reports correlation and measured framework counters. It cannot prove that a plugin alone caused low FPS: player count, entity count, saves, networking, GC, other plugins, and game updates can all change the result. Compare runs under similar server conditions.

## Commands

All commands require server-admin access.

- `monitor.report [label]` creates a report from the rolling performance window. After the first report, plugin values include deltas since the preceding report.
- `monitor.createreport [label]` is a compatibility alias for `monitor.report`.
- `monitor.benchmark <label> [durationSeconds]` observes a dedicated, labeled window. The default duration is 300 seconds.
- `monitor.status` shows whether a report or benchmark is active.

Recommended A/B workflow:

1. Run `monitor.benchmark before-change 300` during representative activity.
2. Make one controlled change.
3. Recreate similar player/entity/server activity.
4. Run `monitor.benchmark after-change 300`.
5. Compare average frame time, frame-time variability, frame rate, and per-plugin observed hook time.

## Plugin impact metrics

Plugins are ranked by hook time accumulated during the observation window. Each entry includes:

- observed hook time;
- share of all measured plugin hook time;
- hook milliseconds per minute, which normalizes different window lengths;
- cumulative hook time for context;
- the framework memory counter and its change during the window.

On uMod, the memory value is cumulative hook allocation. On Carbon, it is the framework's current-memory estimate. These values have different semantics and should not be compared across frameworks.

Hook time only measures execution visible to the framework's hook accounting. It may not capture background work, native calls, external services, or all work scheduled by a plugin.

## Output

Archived reports are written to:

`oxide/data/PerformanceMonitorEnhanced/Reports/yyyy-MM-dd/`

The optional convenience copy is:

`oxide/data/PerformanceMonitorEnhanced/Latest.json`

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
  "Entities processed per server frame": 750,
  "Include plugin report": true,
  "Include entity report": true,
  "Write a Latest.json convenience file": true,
  "Maximum archived reports (0 is unlimited)": 336,
  "Maximum report age in days (0 is unlimited)": 14,
  "Default benchmark duration seconds": 300,
  "Minimum benchmark duration seconds": 10,
  "Maximum benchmark duration seconds": 3600,
  "Top plugin count written to console": 10,
  "Log entity scan progress": false,
  "Excluded plugins": [],
  "Excluded entity short names": []
}
```

Automatic report intervals below 30 seconds are clamped to 30 seconds. Manual benchmark duration is clamped to the configured minimum and maximum.

## Credits and license

This is a ground-up enhancement inspired by Orange's original MIT-licensed Performance Monitor plugin. See [LICENSE](LICENSE).
