# Synthetic collector benchmark

This standalone Windows tool starts the repository's actual `collector.py` in new Python subprocesses against newly generated local fixtures. It does not start either product, contact SSH, read user configuration, discover a Codex home, or access a credential file.

Pass an explicit **new absolute local output directory whose parent already exists**:

```powershell
python tests/Collector.Benchmark/benchmark_collectors.py --output C:\path\to\new-benchmark-output
```

The output contains synthetic SQLite databases and rollouts, `benchmark.json` with raw per-child measurements, and `summary.md`. Existing output directories are refused, including empty ones. The tool never deletes outputs and only terminates its own timed-out child processes.

For each fixture, one single-client and one two-client warmup precede four measured rounds: **single, dual, dual, single**. Each round has 20 batches by default. Dual batches launch two actual collectors concurrently against the same fixture. A smaller `--batches 1` is useful for a tool smoke check; report its sample count rather than treating it as the full benchmark.

The fixtures contain 120 and 500 candidate threads, three completed turns per thread, twelve history items per turn, indexed SQLite tables and public rollout messages. Every invocation uses the product's default 120-task limit. Both fixtures return 120 tasks; the 500-thread case does **not** process or display 500 task rollouts. The collector's existing `truncated` field is true whenever the returned count reaches its limit, even for exactly 120 candidates.

Before accepting results the tool checks task selection order, exact latest turn IDs, completed state, full public final reports, exclusion of synthetic command/output/analysis sentinels, equality of normalized result hashes across all measured clients/rounds, and unchanged fixture bytes. Only `collectedAt` is removed for output hashing. Collector source SHA-256 is recorded and checked again after the run.

Measurement meanings:

- **Wall time:** includes child process startup, Python import, collection and delivery of stdout. Batch wall ends when the last child finishes. Dual batch time is not the sum of two wall times.
- **CPU:** Windows `GetProcessTimes` kernel plus user time, queried on retained handles of only the children this script created. Dual CPU is the sum across those two children.
- **Memory:** approximately 5 ms sampling of Windows `GetProcessMemoryInfo`; individual process peak counters and simultaneous working-set sums are reported separately. Observed maxima are lower bounds because a process can reach a larger value between the final sample and exit. Summing individual peaks is not a measured simultaneous peak.
- **I/O:** Windows logical transfer counters include Python startup and data reads, and are not physical disk traffic.
- **Cache and interference:** fixtures are prewarmed; the tool neither flushes OS caches nor isolates CPUs. Other desktop work can affect wall time. ABBA reduces order bias but does not remove interference.

The measurements cover a specific, immutable local synthetic workload. They do not validate real SSH cost, runtime UI/IPC cost, concurrent database writers, whole-product resource use or production migration. They must not be used to mark the overall P0/P1 performance acceptance complete.
