#!/usr/bin/env python3
"""Validate and redact opt-in TSF JSONL captures for Core differential tests."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Sequence


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)

    sessions: dict[str, str] = {}
    event_ids: dict[tuple[str, str], str] = {}
    hwnds: dict[Any, int] = {}
    pids: dict[Any, int] = {}
    output: list[str] = ["# Sanitized real TSF capture; window titles and numeric identities are redacted."]
    physical_keys = 0
    for source in args.inputs:
        with source.open("r", encoding="utf-8-sig") as stream:
            for number, raw in enumerate(stream, 1):
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue
                try:
                    value = json.loads(line)
                except json.JSONDecodeError as error:
                    raise SystemExit(f"{source}:{number}: invalid JSON: {error}")
                if not isinstance(value, dict) or not isinstance(value.get("type"), str):
                    raise SystemExit(f"{source}:{number}: expected a Core message object")
                if value["type"] == "key":
                    required = ("action", "vk", "scan", "repeat", "extended")
                    missing = [name for name in required if name not in value]
                    if "numLock" not in value and "num_lock" not in value:
                        missing.append("numLock")
                    if missing:
                        raise SystemExit(f"{source}:{number}: key missing {', '.join(missing)}")
                    physical_keys += 1
                    original_session = str(value.get("client_session", ""))
                    sessions.setdefault(original_session, f"captured-session-{len(sessions) + 1}")
                    session = sessions[original_session]
                    value["client_session"] = session
                    original_event = str(value.get("event_id", ""))
                    key = (original_session, original_event)
                    event_ids.setdefault(key, str(len(event_ids) + 1))
                    value["event_id"] = event_ids[key]
                if "windowTitle" in value:
                    value["windowTitle"] = "<redacted>"
                if "hwnd" in value:
                    original = value["hwnd"]
                    hwnds.setdefault(original, len(hwnds) + 1)
                    value["hwnd"] = hwnds[original]
                if "processId" in value:
                    original = value["processId"]
                    pids.setdefault(original, len(pids) + 1)
                    value["processId"] = pids[original]
                output.append(json.dumps(value, ensure_ascii=False, separators=(",", ":")))
    if physical_keys == 0:
        raise SystemExit("capture contains no physical key messages")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(output) + "\n", encoding="utf-8")
    print(f"wrote {len(output) - 1} messages ({physical_keys} physical keys) to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
