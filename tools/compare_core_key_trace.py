#!/usr/bin/env python3
"""Run deterministic C#-versus-Rust TigerClaw Core trace differentials."""

from __future__ import annotations

import argparse
import json
import os
import queue
import shutil
import struct
import subprocess
import sys
import tempfile
import threading
from pathlib import Path
from typing import Any, Sequence

PHYSICAL_FIELDS = ("scan", "repeat", "extended")
SIDE_EFFECT_FILES = ("config.txt", "custom_words.txt", "用户调整.txt", "自定义选重键.txt")


class LineReader:
    def __init__(self, stream: Any) -> None:
        self.lines: queue.Queue[str | BaseException | None] = queue.Queue()
        threading.Thread(target=self._read, args=(stream,), daemon=True).start()

    def _read(self, stream: Any) -> None:
        try:
            for line in stream:
                self.lines.put(line.rstrip("\r\n"))
        except BaseException as error:
            self.lines.put(error)
        finally:
            self.lines.put(None)

    def wait(self, timeout: float, label: str) -> str:
        try:
            value = self.lines.get(timeout=timeout)
        except queue.Empty as error:
            raise TimeoutError(f"{label} produced no observation before timeout") from error
        if isinstance(value, BaseException):
            raise RuntimeError(f"{label} output failed: {value}") from value
        if value is None:
            raise RuntimeError(f"{label} exited before producing an observation")
        return value


def start(command: Sequence[str]) -> tuple[subprocess.Popen[str], LineReader]:
    process = subprocess.Popen(
        list(command), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace", bufsize=1,
    )
    assert process.stdin is not None and process.stdout is not None
    return process, LineReader(process.stdout)


def stop(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    if process.stdin:
        process.stdin.close()
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired:
        process.terminate()
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=2)


def send(process: subprocess.Popen[str], line: str) -> None:
    if process.stdin is None:
        raise RuntimeError("adapter stdin is unavailable")
    process.stdin.write(line + "\n")
    process.stdin.flush()


def load_trace(path: Path, require_physical: bool) -> list[tuple[int, str, dict[str, Any]]]:
    events: list[tuple[int, str, dict[str, Any]]] = []
    physical_keys = 0
    with path.open("r", encoding="utf-8-sig") as stream:
        for number, raw in enumerate(stream, 1):
            line = raw.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            try:
                event = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"{path}:{number}: invalid JSON: {error}") from error
            if not isinstance(event, dict):
                raise ValueError(f"{path}:{number}: record must be an object")
            if "_diff" not in event and not isinstance(event.get("type"), str):
                raise ValueError(f"{path}:{number}: message has no string type")
            if event.get("type") == "key":
                if not isinstance(event.get("vk"), int) or event.get("action") not in (
                    "down", "up", "key_down", "key_up"
                ):
                    raise ValueError(f"{path}:{number}: malformed physical key")
                if all(name in event for name in PHYSICAL_FIELDS) and (
                    "numLock" in event or "num_lock" in event
                ):
                    physical_keys += 1
            events.append((number, line, event))
    if not events:
        raise ValueError(f"{path}: trace is empty")
    if require_physical and physical_keys == 0:
        raise ValueError(f"{path}: trace contains no complete physical-key metadata")
    return events


def parse_observation(raw: str, label: str) -> dict[str, Any]:
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as error:
        raise ValueError(f"{label} emitted invalid JSON: {raw!r}") from error
    if not isinstance(value, dict) or not isinstance(value.get("snapshot"), dict):
        raise ValueError(f"{label} emitted an invalid observation envelope: {raw!r}")
    return value


def scalar(value: Any, default: Any) -> Any:
    return default if value is None else value


def canonical_response(value: Any) -> Any:
    if value is None:
        return None
    if not isinstance(value, dict):
        raise ValueError("response is not an object or null")
    result = {
        "type": scalar(value.get("type"), "response"),
        "seq": scalar(value.get("seq"), 0),
        "success": scalar(value.get("success"), False),
        "handled": scalar(value.get("handled"), False),
        "commit_text": scalar(value.get("commit_text"), ""),
        "input_buffer": scalar(value.get("input_buffer"), ""),
        "keyboard_open": scalar(value.get("keyboard_open"), False),
        "cancel_composition": scalar(value.get("cancel_composition"), False),
        "composition_tracking": scalar(value.get("composition_tracking"), False),
        "composition_pending": scalar(value.get("composition_pending"), False),
        "ensure_system_layout_en": scalar(value.get("ensure_system_layout_en"), False),
    }
    for key in (
        "protocol_version", "config_version", "lexicon_version", "changed", "schema_list",
        "current_schema", "code", "text", "count", "config_text", "default_text", "error",
    ):
        result[key] = value.get(key)
    return result


def canonical_observation(value: dict[str, Any]) -> dict[str, Any]:
    snapshot = dict(value["snapshot"])
    # Exact numeric generations are private implementation tokens. Their
    # externally visible pending/tracking/content effects remain strict.
    snapshot.pop("sentence_generation", None)
    response = canonical_response(value.get("response"))
    if response and response["composition_tracking"] and response["composition_pending"]:
        # The production response is the atomic observation for a pending
        # generation. A worker may publish between returning that response and
        # the adapter's follow-up snapshot, so compare volatile candidate/
        # segmentation state at the explicit wait_idle checkpoint instead.
        for key in (
            "input_buffer", "active_input_code", "candidates", "candidate_annotations",
            "selected_index", "composition_pending",
        ):
            snapshot.pop(key, None)
    return {
        "response": response,
        "snapshot": snapshot,
        "ui_command": scalar(value.get("ui_command"), ""),
    }


def first_difference(left: Any, right: Any, path: str = "$") -> tuple[str, Any, Any] | None:
    if type(left) is not type(right):
        return path, left, right
    if isinstance(left, dict):
        for key in sorted(set(left) | set(right)):
            if key not in left or key not in right:
                return f"{path}.{key}", left.get(key), right.get(key)
            difference = first_difference(left[key], right[key], f"{path}.{key}")
            if difference:
                return difference
        return None
    if isinstance(left, list):
        if len(left) != len(right):
            return f"{path}.length", len(left), len(right)
        for index, (left_item, right_item) in enumerate(zip(left, right)):
            difference = first_difference(left_item, right_item, f"{path}[{index}]")
            if difference:
                return difference
        return None
    return None if left == right else (path, left, right)


def read_text(path: Path) -> str | None:
    if not path.exists():
        return None
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def config_map(text: str | None) -> dict[str, str] | None:
    if text is None:
        return None
    result: dict[str, str] = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        pieces = line.replace("=", "\t", 1).split("\t", 1)
        if len(pieces) == 2:
            result[pieces[0].strip()] = pieces[1].strip()
    return result


def compare_side_effects(csharp_root: Path, rust_root: Path) -> tuple[str, Any, Any] | None:
    for relative in SIDE_EFFECT_FILES:
        csharp = read_text(csharp_root / relative)
        rust = read_text(rust_root / relative)
        csharp_value: Any = config_map(csharp) if relative == "config.txt" else csharp
        rust_value: Any = config_map(rust) if relative == "config.txt" else rust
        if csharp_value != rust_value:
            return f"side_effects.{relative}", csharp_value, rust_value
    return None


def write_tiny_ngram(root: Path) -> None:
    """Create the smallest valid KN V2 file used by both sentence decoders."""
    path = root / "Models" / "sentence-ngram-v2.bin"
    path.parent.mkdir(parents=True, exist_ok=True)
    bigram_key = (ord("a") << 21) | ord("b")
    payload = b"TCSKNM01" + struct.pack(
        "<iiififqQfiqq",
        1, 2, 0, 0.1, ord("a"), 0.9, 1, bigram_key, 0.5, 0, 0, 0,
    )
    path.write_bytes(payload)


def run_case(
    trace: Path, fixture: Path, rust_exe: Path, csharp_exe: Path, timeout: float,
    require_physical: bool, work_root: Path,
) -> dict[str, Any]:
    case_root = work_root / trace.stem
    csharp_root, rust_root = case_root / "csharp", case_root / "rust"
    shutil.copytree(fixture, csharp_root, dirs_exist_ok=True)
    shutil.copytree(fixture, rust_root, dirs_exist_ok=True)
    write_tiny_ngram(csharp_root)
    write_tiny_ngram(rust_root)
    events = load_trace(trace, require_physical)
    csharp_root_arg = str(csharp_root)
    rust_root_arg = str(rust_root)
    if os.name != "nt":
        if csharp_exe.suffix.lower() == ".exe":
            csharp_root_arg = subprocess.run(
                ["wslpath", "-w", str(csharp_root)], check=True, text=True, capture_output=True
            ).stdout.strip()
        if rust_exe.suffix.lower() == ".exe":
            rust_root_arg = subprocess.run(
                ["wslpath", "-w", str(rust_root)], check=True, text=True, capture_output=True
            ).stdout.strip()
    rust_process, rust_reader = start([str(rust_exe), "--diff-stdio", "--root", rust_root_arg])
    csharp_process, csharp_reader = start([str(csharp_exe), "--core-diff-stdio", csharp_root_arg])
    try:
        for index, (line_number, line, event) in enumerate(events, 1):
            send(rust_process, line)
            send(csharp_process, line)
            rust_value = canonical_observation(parse_observation(rust_reader.wait(timeout, "Rust"), "Rust"))
            csharp_value = canonical_observation(parse_observation(csharp_reader.wait(timeout, "C#"), "C#"))
            difference = first_difference(csharp_value, rust_value)
            if difference:
                pointer, expected, actual = difference
                return {
                    "status": "failed", "trace": str(trace), "event": index, "line": line_number,
                    "input": event, "path": pointer, "csharp": expected, "rust": actual,
                    "csharp_observation": csharp_value, "rust_observation": rust_value,
                    "sandbox": str(case_root),
                }
    finally:
        stop(rust_process)
        stop(csharp_process)
    side_effect_difference = compare_side_effects(csharp_root, rust_root)
    if side_effect_difference:
        pointer, expected, actual = side_effect_difference
        return {
            "status": "failed", "trace": str(trace), "event": len(events), "path": pointer,
            "csharp": expected, "rust": actual, "sandbox": str(case_root),
        }
    return {"status": "passed", "trace": str(trace), "events": len(events)}


def trace_paths(single: Path | None, suite: Path | None) -> list[Path]:
    if single:
        return [single]
    assert suite is not None
    paths = sorted(suite.glob("*.jsonl"))
    if not paths:
        raise ValueError(f"{suite}: no JSONL traces found")
    return paths


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rust", required=True, type=Path)
    parser.add_argument("--csharp", required=True, type=Path)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--trace", type=Path)
    source.add_argument("--suite", type=Path)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--allow-synthetic", action="store_true")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--keep-work", type=Path)
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    for executable in (args.rust, args.csharp):
        if not executable.is_file():
            parser.error(f"executable not found: {executable}")
    if not args.fixture.is_dir():
        parser.error(f"fixture directory not found: {args.fixture}")

    temporary: tempfile.TemporaryDirectory[str] | None = None
    if args.keep_work:
        work_root = args.keep_work.resolve()
        work_root.mkdir(parents=True, exist_ok=True)
    else:
        temporary = tempfile.TemporaryDirectory(prefix="tigerclaw-core-diff-")
        work_root = Path(temporary.name)
    results: list[dict[str, Any]] = []
    try:
        for trace in trace_paths(args.trace, args.suite):
            result = run_case(
                trace.resolve(), args.fixture.resolve(), args.rust.resolve(), args.csharp.resolve(),
                args.timeout,
                (not args.allow_synthetic) or ".captured." in trace.name.lower(),
                work_root,
            )
            results.append(result)
            if result["status"] == "passed":
                print(f"PASS {trace.name}: {result['events']} events")
            else:
                print(json.dumps(result, ensure_ascii=False, indent=2), file=sys.stderr)
                break
    finally:
        report = {"format": "tigerclaw.core.diff.v1", "results": results}
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        if temporary:
            temporary.cleanup()
    if results and all(result["status"] == "passed" for result in results):
        print(f"matched {sum(result['events'] for result in results)} events across {len(results)} traces")
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
