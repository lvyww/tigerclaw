#!/usr/bin/env python3
"""从训练 checkpoint 导出不含优化器状态的整句推理模型。"""

from __future__ import annotations

import argparse
from pathlib import Path

import torch


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    payload = {
        "version": checkpoint["version"],
        "config": checkpoint["config"],
        "model": checkpoint["model"],
        "step": checkpoint["step"],
        "tokens_seen": checkpoint["tokens_seen"],
        "validation_loss": checkpoint["validation_loss"],
        "vocabulary_path": checkpoint["vocabulary_path"],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    torch.save(payload, args.output)
    print(f"output={args.output.resolve()}")
    print(f"size_bytes={args.output.stat().st_size}")
    print(f"step={payload['step']} tokens_seen={payload['tokens_seen']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
