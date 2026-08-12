#!/usr/bin/env python3
"""Windows smoke test for TigerClaw.Sentence's named-pipe protocol."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import time
from pathlib import Path


def send(pipe, payload: dict) -> dict:
    pipe.write((json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8"))
    data = bytearray()
    while not data.endswith(b"\n"):
        chunk = pipe.read(1)
        if not chunk:
            raise RuntimeError("sidecar closed the pipe")
        data.extend(chunk)
    return json.loads(data.decode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--vocabulary", type=Path, required=True)
    args = parser.parse_args()

    pipe_name = f"TigerClaw.Sentence.smoke.{os.getpid()}"
    process = subprocess.Popen(
        [
            str(args.exe),
            "--parent-pid", str(os.getpid()),
            "--pipe", pipe_name,
            "--model", str(args.model),
            "--vocabulary", str(args.vocabulary),
        ]
    )
    pipe_path = rf"\\.\pipe\{pipe_name}"
    try:
        deadline = time.monotonic() + 30
        while True:
            try:
                pipe = open(pipe_path, "r+b", buffering=0)
                break
            except FileNotFoundError:
                if process.poll() is not None:
                    raise RuntimeError(f"sidecar exited with {process.returncode}")
                if time.monotonic() >= deadline:
                    raise TimeoutError("timed out waiting for sidecar pipe")
                time.sleep(0.1)

        with pipe:
            hello = send(pipe, {"type": "hello", "seq": 1})
            rerank = send(
                pipe,
                {
                    "type": "rerank",
                    "seq": 2,
                    "generation": 7,
                    "raw_code": "smoke",
                    "candidates": [
                        "今天早上我吃了两个面包三根油条",
                        "今天早上我吃了两个面有三根油条",
                        "今天早上我呆了两个面包三根油条",
                    ],
                },
            )
        print(json.dumps({"hello": hello, "rerank": rerank}, ensure_ascii=False, indent=2))
        if not hello.get("success") or not rerank.get("success"):
            return 2
        if len(rerank.get("scores", [])) != 3:
            return 3
        return 0
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=10)


if __name__ == "__main__":
    raise SystemExit(main())
