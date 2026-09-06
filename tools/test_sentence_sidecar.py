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
    args = parser.parse_args()

    pipe_name = f"TigerClaw.Sentence.smoke.{os.getpid()}"
    process = subprocess.Popen(
        [
            str(args.exe),
            "--parent-pid", str(os.getpid()),
            "--pipe", pipe_name,
            "--model", str(args.model),
        ]
    )
    pipe_path = rf"\\.\pipe\{pipe_name}"
    try:
        deadline = time.monotonic() + 30
        while True:
            try:
                pipe = open(pipe_path, "r+b", buffering=0)
                break
            except OSError:
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
                        "今天早上我吃了两个人包三根油条",
                        "今天早上我吃了两个面包三根油茶",
                    ],
                },
            )
            brand = send(
                pipe,
                {
                    "type": "rerank",
                    "seq": 3,
                    "generation": 8,
                    "raw_code": "zhhmnwklfxhfgjax",
                    "candidates": [
                        "虎码官方整句版",
                        "虎狂汸整句版",
                        "虎码官方整句板",
                        "虎码官房整句版",
                        "虎马官方整句版",
                    ],
                },
            )
            too_many = send(
                pipe,
                {
                    "type": "rerank",
                    "seq": 4,
                    "generation": 9,
                    "raw_code": "limit",
                    "candidates": ["一", "二", "三", "四", "五", "六"],
                },
            )
        reconnect_deadline = time.monotonic() + 10
        while True:
            try:
                reconnect_pipe = open(pipe_path, "r+b", buffering=0)
                break
            except OSError:
                if process.poll() is not None:
                    raise RuntimeError(f"sidecar exited before reconnect: {process.returncode}")
                if time.monotonic() >= reconnect_deadline:
                    raise TimeoutError("timed out reconnecting to sidecar pipe")
                time.sleep(0.05)
        with reconnect_pipe:
            ping = send(reconnect_pipe, {"type": "ping", "seq": 5})
            shutdown = send(reconnect_pipe, {"type": "shutdown", "seq": 6})
        print(
            json.dumps(
                {
                    "hello": hello,
                    "rerank": rerank,
                    "brand": brand,
                    "too_many": too_many,
                    "ping": ping,
                    "shutdown": shutdown,
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        if not hello.get("success") or not rerank.get("success"):
            return 2
        scores = rerank.get("scores", [])
        if len(scores) != 5:
            return 3
        if scores[0] != max(scores):
            return 4
        brand_scores = brand.get("scores", [])
        if len(brand_scores) != 5 or brand_scores[0] != max(brand_scores):
            return 5
        if too_many.get("success"):
            return 6
        if not ping.get("success") or ping.get("seq") != 5:
            return 7
        if not shutdown.get("success") or shutdown.get("seq") != 6:
            return 8
        process.wait(timeout=10)
        if process.returncode != 0:
            return 9
        return 0
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=10)


if __name__ == "__main__":
    raise SystemExit(main())
