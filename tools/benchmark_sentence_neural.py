#!/usr/bin/env python3
"""测试整句字符 Transformer 在 CPU 或 DirectML 上的训练吞吐。"""

from __future__ import annotations

import argparse
import time
from typing import Optional, Sequence

import torch
from torch.nn import functional as F

from sentence_neural_model import CharacterTransformer, ModelConfig


def resolve_device(name: str) -> torch.device:
    if name == "directml":
        try:
            import torch_directml
        except ImportError as ex:
            raise RuntimeError("当前 Python 环境没有安装 torch-directml") from ex
        return torch_directml.device()
    return torch.device("cpu")


def train_step(
    model: CharacterTransformer,
    optimizer: torch.optim.Optimizer,
    inputs: torch.Tensor,
    targets: torch.Tensor,
) -> float:
    optimizer.zero_grad(set_to_none=True)
    logits = model(inputs)
    loss = F.cross_entropy(logits.reshape(-1, logits.size(-1)), targets.reshape(-1))
    loss.backward()
    optimizer.step()
    return float(loss.detach().cpu())


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", choices=("cpu", "directml"), default="cpu")
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--sequence-length", type=int, default=64)
    parser.add_argument("--warmup-steps", type=int, default=2)
    parser.add_argument("--steps", type=int, default=5)
    parser.add_argument("--layers", type=int, default=8)
    parser.add_argument("--embedding-size", type=int, default=256)
    parser.add_argument("--heads", type=int, default=4)
    parser.add_argument("--feed-forward-size", type=int, default=1024)
    parser.add_argument("--vocabulary-size", type=int, default=8192)
    args = parser.parse_args(argv)
    if min(
        args.batch_size,
        args.sequence_length,
        args.warmup_steps,
        args.steps,
        args.layers,
        args.embedding_size,
        args.heads,
        args.feed_forward_size,
        args.vocabulary_size,
    ) < 1:
        parser.error("所有数值参数必须大于 0")
    return args


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_arguments(argv)
    torch.manual_seed(20260810)
    config = ModelConfig(
        vocabulary_size=args.vocabulary_size,
        context_length=args.sequence_length,
        layer_count=args.layers,
        embedding_size=args.embedding_size,
        head_count=args.heads,
        feed_forward_size=args.feed_forward_size,
    )
    device = resolve_device(args.device)
    model = CharacterTransformer(config).to(device)
    optimizer = torch.optim.AdamW(
        model.parameters(), lr=3.0e-4, foreach=False
    )
    cpu_inputs = torch.randint(
        0,
        config.vocabulary_size,
        (args.batch_size, args.sequence_length + 1),
    )
    inputs = cpu_inputs[:, :-1].to(device)
    targets = cpu_inputs[:, 1:].to(device)

    print(f"device={device}")
    print(f"parameters={model.parameter_count():,}")
    print(
        f"batch={args.batch_size} sequence={args.sequence_length} "
        f"tokens/step={args.batch_size * args.sequence_length:,}"
    )
    for step in range(args.warmup_steps):
        loss = train_step(model, optimizer, inputs, targets)
        print(f"warmup {step + 1}/{args.warmup_steps} loss={loss:.4f}", flush=True)

    started = time.perf_counter()
    loss = 0.0
    for step in range(args.steps):
        loss = train_step(model, optimizer, inputs, targets)
        print(f"step {step + 1}/{args.steps} loss={loss:.4f}", flush=True)
    elapsed = time.perf_counter() - started
    token_count = args.steps * args.batch_size * args.sequence_length
    print(f"elapsed_seconds={elapsed:.3f}")
    print(f"tokens_per_second={token_count / elapsed:.1f}")
    print(f"last_loss={loss:.4f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
