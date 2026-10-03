# Changelog

## 2.6.2

- Added an unlabeled routine form of the benchmark command, such as `monitor.benchmark 30 0`.
- Routine Discord reports now use the clean `Performance Benchmark` title while named before/after runs retain their labels.

## 2.6.1

- Promoted the source server to a dedicated Discord header above the report title.
- Removed the duplicate server line from the compact Context column to improve readability.

## 2.6.0

- Added a Discord server-description setting so reports clearly identify their source server.
- Automatically uses Rust's live `server.hostname` when the description override is blank.
- Falls back to the server identity and then `Unknown server` if no hostname is available.

## 2.5.1

- Made benchmark sets without an explicit baseline establish a new aggregate baseline instead of auto-comparing with an incompatible preceding single run.
- Marked plugin-rate comparisons against pre-schema-5 references inconclusive because those reports used the old warm-up-inclusive denominator.

## 2.5.0

- Added `monitor.benchmarkset` for repeated benchmark runs with a single median/range aggregate report.
- Added sampled player and networked-entity workload windows and workload-aware comparisons.
- Added absolute plugin-hook load (`ms/sec`, `ms/min`, and approximate one-thread percentage) to Discord.
- Added benchmark-set stability and median plugin-impact summaries while retaining every member report as raw JSON.
- Corrected benchmark plugin-rate normalization so warm-up time is excluded from the observation denominator.

## 2.4.0

- Classified routine server saves and sampled garbage collections as contextual quality markers instead of hard comparison failures.
- Added context notes to schema 4 reports and Discord measurement-quality output.
- Downgraded otherwise valid benchmark comparisons with contextual markers to Moderate confidence.

## 2.3.1

- Clarified that missing sample counts from pre-schema-3 baselines are unavailable rather than literal zero-sample observations.

## 2.3.0

- Added an independent one-second benchmark sampling interval.
- Added configurable minimum sample requirements; short smoke tests are now explicitly low-confidence.
- Added optional named-baseline selection to `monitor.benchmark`.
- Added persistent named benchmark references with bounded retention.
- Added High, Moderate, and Low comparison-confidence classifications.
- Added baseline and current values to Discord comparison results.
- Replaced negative “improvement” wording with clear improved/regressed language.
- Added a configurable plugin-hook noise floor to suppress misleading percentages from tiny samples.
- Added observation-duration comparability checks and clearer separation of current-run quality from cross-run confidence.

## 2.2.0

- Renamed the plugin to Server Performance Analyzer.
- Added automatic, non-destructive migration of the legacy configuration and report data.
- Kept the existing `monitor.*` commands for operational compatibility.

## 2.1.0

- Added configurable benchmark warm-up periods.
- Added median, P95, and P99 performance statistics using bounded reservoir sampling.
- Added measurement-quality warnings for player-count changes, insufficient samples, saves, plugin lifecycle events, and garbage collection.
- Added automatic comparison with the previous benchmark for frame time, frame rate, and normalized plugin-hook cost.
- Added opt-in Discord webhook embeds with safe role mentions and compact top-plugin summaries.
- Added observation-boundary player counts and improved benchmark metadata.

## 2.0.0

- Rewrote report orchestration with exception-safe cleanup.
- Added labeled benchmark windows.
- Added rolling performance sampling with average, minimum, maximum, last value, and standard deviation.
- Added per-plugin observation deltas, normalized hook-time rates, impact share, and ranking.
- Added low-impact batched entity processing.
- Added unique UTC report names and `Latest.json`.
- Added configurable count- and age-based archive retention.
- Added configuration validation and safe null handling.
- Added useful command replies and monitor status.
- Retained the original manual command as a compatibility alias.
