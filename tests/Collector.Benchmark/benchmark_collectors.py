"""Windows-only, synthetic, real collector subprocess benchmark; no service startup."""
import argparse
import concurrent.futures
import ctypes
from ctypes import wintypes
import datetime as dt
import hashlib
import json
import math
import os
import pathlib
import platform
import sqlite3
import statistics
import subprocess
import sys
import time


LIMIT = 120  # The product's default; intentionally do not pass --limit.
BASE_TIME = 1735689600
SAMPLE_SECONDS = 0.005
COLLECTOR = pathlib.Path(__file__).resolve().parents[2] / "collector.py"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=True, separators=(",", ":")).encode()


def new_directory(value):
    path = pathlib.Path(value)
    if (not path.is_absolute() or str(path).startswith(("\\\\", "//"))
            or any(part in (".", "..") or part.endswith((".", " ")) for part in path.parts)
            or any(ord(char) < 32 for char in value)):
        raise ValueError("An explicit new absolute local output directory is required.")
    if path.exists() or not path.parent.is_dir():
        raise ValueError("Output must be new and its parent must already exist.")
    for ancestor in (path.parent, *path.parent.parents):
        info = ancestor.lstat()
        if ancestor.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Redirected directories are not accepted.")
    path.mkdir()
    return path


def fixture(home, count):
    home.mkdir()
    logs = home / "rollouts"
    logs.mkdir()
    state = sqlite3.connect(home / "state_5.sqlite")
    history = sqlite3.connect(home / "thread_history_1.sqlite")
    try:
        state.executescript("""
          CREATE TABLE threads(id TEXT PRIMARY KEY,cwd TEXT,title TEXT,name TEXT,
            rollout_path TEXT,updated_at INTEGER,archived INTEGER,source TEXT,project_id TEXT);
          CREATE TABLE thread_spawn_edges(child_thread_id TEXT);
          CREATE TABLE projects(id TEXT PRIMARY KEY,name TEXT);
          CREATE INDEX threads_recent ON threads(archived,updated_at DESC);
          INSERT INTO projects VALUES('synthetic-project','合成采集测试');
        """)
        history.executescript("""
          CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,status TEXT,
            started_at INTEGER,completed_at INTEGER,rollout_ordinal INTEGER);
          CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_type TEXT,item_json TEXT,
            created_at_ms INTEGER,rollout_ordinal INTEGER);
          CREATE INDEX turns_recent ON thread_turns(thread_id,rollout_ordinal DESC);
          CREATE INDEX items_recent ON thread_items(thread_id,turn_id,rollout_ordinal DESC);
          CREATE INDEX item_type_recent ON thread_items(thread_id,turn_id,item_type,rollout_ordinal DESC);
        """)
        for i in range(count):
            thread = f"synthetic-{i:04d}"
            log = logs / (thread + ".jsonl")
            events = []
            for turn_index in range(3):
                turn = f"{thread}-turn-{turn_index}"
                started = BASE_TIME + i * 100 + turn_index * 20
                history.execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?)",
                                (thread, turn, "completed", started, started + 15, turn_index))
                events.append({"type": "event_msg", "timestamp": started,
                               "payload": {"type": "task_started", "turn_id": turn}})
                for item_index in range(12):
                    when = started + item_index + 1
                    if item_index == 11:
                        obj = {"type": "agentMessage", "phase": "final",
                               "text": "合成验证报告。" * 256 + f"FINAL_{thread}_{turn_index}"}
                    elif item_index % 4 == 0:
                        obj = {"type": "agentMessage", "phase": "commentary",
                               "text": f"合成任务 {i} 正在检查第 {item_index} 项记录。"}
                    else:
                        obj = {"type": "commandExecution", "status": "completed",
                               "command": "SYNTHETIC_EXCLUDED_COMMAND",
                               "aggregatedOutput": "SYNTHETIC_EXCLUDED_OUTPUT" * 8}
                    history.execute("INSERT INTO thread_items VALUES(?,?,?,?,?,?)",
                                    (thread, turn, obj["type"], json.dumps(obj), when * 1000, item_index))
                    if obj["type"] == "agentMessage":
                        events.append({"type": "response_item", "timestamp": when,
                                       "payload": {"type": "message", "role": "assistant",
                                                   "phase": obj["phase"], "turn_id": turn,
                                                   "content": [{"type": "output_text", "text": obj["text"]}]}})
                events.append({"type": "response_item", "timestamp": started + 13,
                               "payload": {"type": "message", "role": "assistant", "phase": "analysis",
                                           "content": [{"type": "output_text", "text": "SYNTHETIC_EXCLUDED_ANALYSIS"}]}})
                events.append({"type": "event_msg", "timestamp": started + 15,
                               "payload": {"type": "task_complete", "turn_id": turn}})
            log.write_text("\n".join(json.dumps(e, ensure_ascii=False) for e in events) + "\n", encoding="utf-8")
            state.execute("INSERT INTO threads VALUES(?,?,?,?,?,?,?,?,?)",
                          (thread, str(home / "unused-project"), "Synthetic task", f"合成任务 {i}",
                           str(log), BASE_TIME + i * 100 + 55, 0, "vscode", "synthetic-project"))
        state.commit()
        history.commit()
    finally:
        state.close()
        history.close()
    return manifest(home)


def manifest(home):
    files = {str(p.relative_to(home)): {"bytes": p.stat().st_size, "sha256": sha(p.read_bytes())}
             for p in sorted(home.rglob("*")) if p.is_file()}
    return {"sha256": sha(canonical(files)), "bytes": sum(f["bytes"] for f in files.values()), "files": files}


class MemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage",
            "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage")]


class IoCounters(ctypes.Structure):
    _fields_ = [(name, ctypes.c_ulonglong) for name in (
        "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
        "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]


class ProcessMetrics:
    def __init__(self):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        self.kernel.GetProcessTimes.restype = wintypes.BOOL
        self.kernel.GetProcessIoCounters.argtypes = [wintypes.HANDLE, ctypes.POINTER(IoCounters)]
        self.kernel.GetProcessIoCounters.restype = wintypes.BOOL
        self.psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
        self.psapi.GetProcessMemoryInfo.restype = wintypes.BOOL

    def memory(self, process):
        counters = MemoryCounters()
        counters.cb = ctypes.sizeof(counters)
        if not self.psapi.GetProcessMemoryInfo(int(process._handle), ctypes.byref(counters), counters.cb):
            return None  # Exiting processes may no longer have an address space.
        return counters.PeakWorkingSetSize, counters.WorkingSetSize

    def finished(self, process):
        times = [wintypes.FILETIME() for _ in range(4)]
        if not self.kernel.GetProcessTimes(int(process._handle), *(ctypes.byref(x) for x in times)):
            raise ctypes.WinError(ctypes.get_last_error())
        ticks = [(x.dwHighDateTime << 32) | x.dwLowDateTime for x in times]
        io = IoCounters()
        if not self.kernel.GetProcessIoCounters(int(process._handle), ctypes.byref(io)):
            raise ctypes.WinError(ctypes.get_last_error())
        return {"cpu_ms": (ticks[2] + ticks[3]) / 10000,
                "kernel_cpu_ms": ticks[2] / 10000, "user_cpu_ms": ticks[3] / 10000,
                "logical_read_bytes": io.ReadTransferCount, "logical_write_bytes": io.WriteTransferCount}


def check_output(stdout, count):
    document = json.loads(stdout)
    if document.get("schemaVersion") != 1 or document.get("error") or document.get("warnings"):
        raise AssertionError("Collector did not return a clean schema-1 result.")
    tasks = document["tasks"]
    expected = [f"synthetic-{i:04d}" for i in range(count - 1, max(-1, count - LIMIT - 1), -1)]
    if [task["id"] for task in tasks] != expected:
        raise AssertionError("Incorrect returned count or selected thread order.")
    for task in tasks:
        if (task["status"] != "completed" or task["turnId"] != task["id"] + "-turn-2"
                or not task["finalReport"].endswith(f"FINAL_{task['id']}_2")
                or task["finalReportTruncated"]):
            raise AssertionError("Turn identity, terminal state, or final report was corrupted.")
    if b"SYNTHETIC_EXCLUDED_" in stdout:
        raise AssertionError("Excluded synthetic analysis/command data leaked into results.")
    if document["truncated"] != (count >= LIMIT):
        raise AssertionError("Unexpected collector truncation flag.")
    document.pop("collectedAt")
    return sha(canonical(document))


def batch(home, count, clients, metrics):
    processes, starts, peaks = [], [], [0] * clients
    batch_start = time.perf_counter()
    combined_observed_peak = 0
    samples = 0
    # Only these explicitly created children are ever waited on or killed.
    with concurrent.futures.ThreadPoolExecutor(max_workers=clients) as pool:
        futures = []
        try:
            for _ in range(clients):
                starts.append(time.perf_counter())
                process = subprocess.Popen(
                    [sys.executable, "-I", "-u", str(COLLECTOR), "--home", str(home)],
                    cwd=str(home), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                    creationflags=subprocess.CREATE_NO_WINDOW)
                processes.append(process)
                def drain(child=process):
                    output, error = child.communicate(timeout=30)
                    return output, error, time.perf_counter()
                futures.append(pool.submit(drain))
            while True:
                current = 0
                for index, process in enumerate(processes):
                    sampled = metrics.memory(process)
                    if sampled is not None:
                        peaks[index] = max(peaks[index], sampled[0])
                        current += sampled[1]
                samples += 1
                combined_observed_peak = max(combined_observed_peak, current)
                if all(f.done() for f in futures):
                    break
                concurrent.futures.wait(futures, timeout=SAMPLE_SECONDS, return_when=concurrent.futures.ALL_COMPLETED)
            results = []
            for i, (process, future) in enumerate(zip(processes, futures)):
                output, error, finished = future.result()
                if process.returncode != 0 or error:
                    raise RuntimeError("Synthetic collector subprocess failed or wrote stderr.")
                results.append({**metrics.finished(process), "wall_ms": (finished - starts[i]) * 1000,
                                "observed_peak_working_set_bytes": peaks[i],
                                "output_bytes": len(output), "result_sha256": check_output(output, count)})
            if any(not p for p in peaks) or len({r["result_sha256"] for r in results}) != 1:
                raise AssertionError("Missing memory sample or inconsistent dual-client results.")
            return {"clients": clients, "wall_ms": (max(f.result()[2] for f in futures) - batch_start) * 1000,
                    "total_cpu_ms": sum(r["cpu_ms"] for r in results),
                    "total_logical_read_bytes": sum(r["logical_read_bytes"] for r in results),
                    "observed_combined_working_set_peak_bytes": combined_observed_peak,
                    "sum_child_observed_peak_working_set_bytes": sum(peaks), "memory_samples": samples,
                    "children": results}
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait()


def distribution(values):
    ordered = sorted(values)
    return {"n": len(values), "mean": statistics.mean(values), "median": statistics.median(values),
            "p95_nearest_rank": ordered[math.ceil(len(ordered) * .95) - 1], "min": ordered[0], "max": ordered[-1]}


def aggregate(records):
    return {key: distribution([r[key] for r in records]) for key in (
        "wall_ms", "total_cpu_ms", "total_logical_read_bytes", "observed_combined_working_set_peak_bytes",
        "sum_child_observed_peak_working_set_bytes")} | {
        "child_wall_ms": distribution([c["wall_ms"] for r in records for c in r["children"]]),
        "child_cpu_ms": distribution([c["cpu_ms"] for r in records for c in r["children"]]),
        "child_observed_peak_working_set_bytes": distribution(
            [c["observed_peak_working_set_bytes"] for r in records for c in r["children"]])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--batches", type=int, default=20)
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This benchmark uses Windows per-child process counters.")
    if not 1 <= args.batches <= 100:
        parser.error("--batches must be between 1 and 100.")
    output = new_directory(args.output)
    source_hash = sha(COLLECTOR.read_bytes())
    metrics = ProcessMetrics()
    report = {
        "schemaVersion": 1, "startedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "syntheticOnly": True, "realSshMeasured": False, "realProductHostMeasured": False,
        "collector": str(COLLECTOR), "collectorSha256": source_hash,
        "python": sys.version, "pythonExecutable": sys.executable, "platform": platform.platform(),
        "logicalProcessorCount": os.cpu_count(), "batchesPerRound": args.batches,
        "order": ["single", "dual", "dual", "single"], "collectorDefaultLimit": LIMIT,
        "measurement": {
            "wall": "Each new child includes Python startup, collection and stdout delivery; batch wall ends at last child exit/drain.",
            "cpu": "GetProcessTimes kernel+user CPU on handles belonging only to children created by this script. Sum is CPU time, not wall time.",
            "io": "GetProcessIoCounters logical transfer bytes include interpreter/SQLite/rollout reads; not physical disk traffic.",
            "memory": "GetProcessMemoryInfo sampled every approximately 5 ms; observed child peaks and observed simultaneous batch total are distinct lower bounds.",
            "cache": "One single and one dual warmup before each ABBA series. OS cache is not flushed; no cold-cache claim.",
            "limit": "120 candidates returns 120; 500 candidates also returns 120. The collector reports truncated=true at exactly its limit too.",
            "scope": "Local synthetic indexed SQLite and rollout files. No SSH, UI, IPC, live writes, credentials, real task state or CPU isolation.",
        }, "datasets": []}
    try:
        for count in (120, 500):
            home = output / f"synthetic-home-{count}"
            before = fixture(home, count)
            dataset = {"candidateThreads": count, "returnedThreads": min(count, LIMIT),
                       "omittedCandidateThreads": max(0, count - LIMIT), "reportedTruncated": count >= LIMIT,
                       "historyTurnsPerThread": 3, "historyItemsPerTurn": 12,
                       "fixtureBefore": before, "warmups": [], "rounds": []}
            report["datasets"].append(dataset)
            for clients in (1, 2):
                dataset["warmups"].append(batch(home, count, clients, metrics))
            expected_hash = dataset["warmups"][0]["children"][0]["result_sha256"]
            for round_index, clients in enumerate((1, 2, 2, 1)):
                records = [batch(home, count, clients, metrics) for _ in range(args.batches)]
                if any(c["result_sha256"] != expected_hash for r in records for c in r["children"]):
                    raise AssertionError("Cross-round result hash changed.")
                dataset["rounds"].append({"round": round_index + 1, "clients": clients,
                                          "summary": aggregate(records), "batches": records})
                print(f"candidates={count} returned={min(count, LIMIT)} round={round_index + 1}/4 clients={clients} batches={args.batches}", flush=True)
            dataset["fixtureAfter"] = manifest(home)
            if dataset["fixtureAfter"] != before:
                raise AssertionError("Collector changed the synthetic fixture.")
            dataset["resultSha256"] = expected_hash
            dataset["allResultsEqual"] = True
            dataset["fixtureUnchanged"] = True
            summaries = {}
            for name, clients in (("single", 1), ("dual", 2)):
                records = [b for r in dataset["rounds"] if r["clients"] == clients for b in r["batches"]]
                summaries[name] = aggregate(records)
            dataset["summary"] = summaries
            dataset["dualToSingleMeanRatio"] = {
                key: summaries["dual"][key]["mean"] / summaries["single"][key]["mean"]
                for key in ("wall_ms", "total_cpu_ms", "child_wall_ms", "observed_combined_working_set_peak_bytes")}
        if sha(COLLECTOR.read_bytes()) != source_hash:
            raise AssertionError("Collector source changed during measurement.")
        report["completedAtUtc"] = dt.datetime.now(dt.timezone.utc).isoformat()
        report["passed"] = True
    except BaseException as error:
        report["passed"] = False
        report["failureType"] = type(error).__name__
        raise
    finally:
        (output / "benchmark.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# Synthetic local collector benchmark", "", "ABBA single/dual/dual/single; prewarmed local files.",
             "CPU is summed child CPU time. Memory is sampled, not a precise simultaneous process-lifetime peak.",
             "This does not measure SSH, either complete product, production scale or background desktop load.", "",
             "| Candidates / returned | Mode | Batches | Mean batch wall ms | Mean summed CPU ms | Child wall p95 ms | Observed combined memory mean MiB |",
             "|---|---|---:|---:|---:|---:|---:|"]
    for dataset in report["datasets"]:
        for mode in ("single", "dual"):
            summary = dataset["summary"][mode]
            lines.append(f"| {dataset['candidateThreads']} / {dataset['returnedThreads']} | {mode} | "
                         f"{summary['wall_ms']['n']} | {summary['wall_ms']['mean']:.2f} | "
                         f"{summary['total_cpu_ms']['mean']:.2f} | {summary['child_wall_ms']['p95_nearest_rank']:.2f} | "
                         f"{summary['observed_combined_working_set_peak_bytes']['mean'] / 1024**2:.2f} |")
    lines += ["", "Both fixtures and all normalized output hashes remained unchanged. Only `collectedAt` is excluded from result hashing.",
              "500 candidates still returns 120 tasks; the omitted 380 task rollouts are not processed or displayed.",
              "The collector reports `truncated=true` for both fixtures, including exactly 120 candidates.",
              "See benchmark.json for each child, round distributions, source hash and measurement definitions."]
    (output / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
