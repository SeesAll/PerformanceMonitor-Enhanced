# Changelog

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
