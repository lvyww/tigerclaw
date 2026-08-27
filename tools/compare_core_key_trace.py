#!/usr/bin/env python3
"""Replay one captured TSF key trace against two Core frontends.

The trace is JSON Lines.  Every line is sent verbatim to both commands; a
response is expected for requests and key events, while Core notifications
(``focus``, ``caret``, ``ime_active``, ``composition_canceled`` and
``hook_native_disabled``) must not produce a response.  The tool deliberately
does not manufacture key messages: by default it requires at least one key
line to carry physical-event metadata (scan/repeat/extended/NumLock).  This
keeps a passing run meaningful for the installed TSF bridge instead of a set
of hand-written protocol calls.

Example (the commands are argv, not shell strings)::

    python tools/compare_core_key_trace.py \
      --rust target/debug/tigerclaw-core-rust -- --stdio \
      --csharp C:/trace-adapter/csharp-core-adapter.exe \
      --trace captures/wordpad.jsonl

The adapter used for the C# side should read and write one JSON object per
line.  ``--allow-synthetic`` is intended only for debugging a small fixture.
"""

from __future__ import annotations

import argparse
import json
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any, Iterable, Sequence


NOTIFICATIONS = {
    "focus",
    "caret",
    "ime_active",
    "composition_canceled",
    "hook_native_disabled",
}


class LineReader:
    def __init__(self, stream: Any) -> None:
        self.lines: queue.Queue[str | BaseException | None] = queue.Queue()
        self.thread = threading.Thread(target=self._read, args=(stream,), daemon=True)
        self.thread.start()

    def _read(self, stream: Any) -> None:
        try:
            for line in stream:
                self.lines.put(line.rstrip("\r\n"))
        except BaseException as error:  # surfaced by wait() below
            self.lines.put(error)
        finally:
            self.lines.put(None)

    def wait(self, timeout: float) -> str | None:
        try:
            value = self.lines.get(timeout=timeout)
        except queue.Empty as error:
            raise TimeoutError("Core produced no response before timeout") from error
        if isinstance(value, BaseException):
            raise RuntimeError(str(value)) from value
        if value is None:
            raise RuntimeError("Core exited before producing a response")
        return value

    def drain(self) -> list[str]:
        values: list[str] = []
        while True:
            try:
                value = self.lines.get_nowait()
            except queue.Empty:
                return values
            if isinstance(value, str):
                values.append(value)


def start(command: Sequence[str]) -> tuple[subprocess.Popen[str], LineReader]:
    if not command:
        raise ValueError("an empty Core command was supplied")
    process = subprocess.Popen(
        list(command),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )
    assert process.stdin is not None
    assert process.stdout is not None
    return process, LineReader(process.stdout)


def send(process: subprocess.Popen[str], line: str) -> None:
    if process.stdin is None:
        raise RuntimeError("Core stdin is unavailable")
    process.stdin.write(line + "\n")
    process.stdin.flush()


def scalar(value: Any, default: Any = None) -> Any:
    # C# often omits optional response properties while Rust's dependency-free
    # adapter emits null/empty values.  Normalize only those representational
    # differences; all actual key-state fields remain strict comparisons.
    return default if value is None else value


def normalized(response: str) -> dict[str, Any]:
    try:
        value = json.loads(response)
    except json.JSONDecodeError as error:
        raise ValueError(f"invalid JSON response: {response!r}") from error
    if not isinstance(value, dict):
        raise ValueError(f"response is not a JSON object: {response!r}")
    candidates = value.get("candidates")
    if candidates is not None and not isinstance(candidates, list):
        raise ValueError(f"response candidates is not an array: {response!r}")
    return {
        "success": scalar(value.get("success"), False),
        "handled": scalar(value.get("handled"), False),
        "commit_text": scalar(value.get("commit_text"), ""),
        "input_buffer": scalar(value.get("input_buffer"), ""),
        "candidates": candidates,
        "selected_index": scalar(value.get("selected_index"), -1),
        "keyboard_open": scalar(value.get("keyboard_open"), False),
        "cancel_composition": scalar(value.get("cancel_composition"), False),
        "composition_tracking": scalar(value.get("composition_tracking"), False),
        "composition_pending": scalar(value.get("composition_pending"), False),
        "ensure_system_layout_en": scalar(value.get("ensure_system_layout_en"), False),
    }


def trace_lines(path: Path) -> list[tuple[int, str, dict[str, Any]]]:
    events: list[tuple[int, str, dict[str, Any]]] = []
    with path.open("r", encoding="utf-8") as stream:
        for number, raw in enumerate(stream, 1):
            line = raw.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            try:
                event = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"{path}:{number}: invalid JSON: {error}") from error
            if not isinstance(event, dict):
                raise ValueError(f"{path}:{number}: event is not an object")
            if not isinstance(event.get("type"), str):
                raise ValueError(f"{path}:{number}: event has no string type")
            events.append((number, line, event))
    if not events:
        raise ValueError(f"{path}: trace is empty")
    return events


def require_physical_trace(events: Iterable[tuple[int, str, dict[str, Any]]]) -> None:
    key_count = 0
    physical_count = 0
    for _, _, event in events:
        if event.get("type") != "key":
            continue
        key_count += 1
        if any(name in event for name in ("scan", "repeat", "extended", "numLock", "num_lock")):
            physical_count += 1
        action = event.get("action")
        if action not in ("down", "up", "key_down", "key_up"):
            raise ValueError("every key event must carry a down/up action")
        if not isinstance(event.get("vk"), int):
            raise ValueError("every key event must carry an integer vk")
    if key_count == 0:
        raise ValueError("trace contains no key events")
    if physical_count == 0:
        raise ValueError(
            "trace contains no scan/repeat/extended/NumLock metadata; "
            "use a captured TSF trace or pass --allow-synthetic for a fixture"
        )


def stop(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    if process.stdin is not None:
        try:
            process.stdin.close()
        except OSError:
            pass
    try:
        process.wait(timeout=1.0)
    except subprocess.TimeoutExpired:
        process.terminate()
        try:
            process.wait(timeout=1.0)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=1.0)


def compare(
    rust_command: Sequence[str],
    csharp_command: Sequence[str],
    events: Sequence[tuple[int, str, dict[str, Any]]],
    timeout: float,
) -> int:
    rust_process, rust_reader = start(rust_command)
    csharp_process, csharp_reader = start(csharp_command)
    try:
        for index, (line_number, line, event) in enumerate(events, 1):
            send(rust_process, line)
            send(csharp_process, line)
            notification = event["type"] in NOTIFICATIONS
            if notification:
                # Notifications are intentionally response-free.  Give both
                # readers one short scheduling window so an accidental reply
                # is still diagnosed without delaying a long physical trace.
                time.sleep(min(timeout, 0.02))
                rust_extra = rust_reader.drain()
                csharp_extra = csharp_reader.drain()
                if rust_extra or csharp_extra:
                    print(
                        json.dumps(
                            {
                                "event": index,
                                "line": line_number,
                                "input": event,
                                "error": "notification produced a response",
                                "rust": rust_extra,
                                "csharp": csharp_extra,
                            },
                            ensure_ascii=False,
                            indent=2,
                        ),
                        file=sys.stderr,
                    )
                    return 1
                continue

            try:
                rust_response = normalized(rust_reader.wait(timeout))
                csharp_response = normalized(csharp_reader.wait(timeout))
            except (TimeoutError, RuntimeError, ValueError) as error:
                print(
                    f"event {index} (trace line {line_number}) failed: {error}\n{line}",
                    file=sys.stderr,
                )
                return 1
            if rust_response != csharp_response:
                print(
                    json.dumps(
                        {
                            "event": index,
                            "line": line_number,
                            "input": event,
                            "rust": rust_response,
                            "csharp": csharp_response,
                        },
                        ensure_ascii=False,
                        indent=2,
                    ),
                    file=sys.stderr,
                )
                return 1
    finally:
        stop(rust_process)
        stop(csharp_process)
    print(f"matched {len(events)} trace events")
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rust", nargs="+", required=True, help="Rust Core command and arguments")
    parser.add_argument("--csharp", nargs="+", required=True, help="C# Core/adapter command and arguments")
    parser.add_argument("--trace", required=True, type=Path, help="captured TSF JSONL trace")
    parser.add_argument("--timeout", type=float, default=2.0, help="response timeout in seconds (default: 2)")
    parser.add_argument(
        "--allow-synthetic",
        action="store_true",
        help="allow traces without physical scan/repeat/extended/NumLock fields",
    )
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    events = trace_lines(args.trace)
    if not args.allow_synthetic:
        require_physical_trace(events)
    return compare(args.rust, args.csharp, events, args.timeout)


if __name__ == "__main__":
    raise SystemExit(main())
