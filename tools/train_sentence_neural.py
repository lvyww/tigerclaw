#!/usr/bin/env python3
"""训练 TigerClaw 整句实验用的字符因果 Transformer。"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import time
from pathlib import Path
from typing import Any, Dict, Optional, Sequence

import numpy as np
import torch
from torch.nn import functional as F

from sentence_neural_model import CharacterTransformer, ModelConfig


class TrainingError(Exception):
    pass


class BlockSampler:
    def __init__(self, path: Path, context_length: int, seed: int, shuffle: bool) -> None:
        self._tokens = np.memmap(path, dtype="<u2", mode="r")
        self._block_length = context_length + 1
        self._block_count = len(self._tokens) // self._block_length
        if self._block_count < 1:
            raise TrainingError(f"数据文件太短: {path}")
        self._indices = np.arange(self._block_count, dtype=np.int64)
        self._offsets = np.arange(self._block_length, dtype=np.int64)
        self._random = np.random.default_rng(seed)
        self._shuffle = shuffle
        self._position = 0
        if shuffle:
            self._random.shuffle(self._indices)

    def next(self, batch_size: int) -> tuple[torch.Tensor, torch.Tensor]:
        if self._position + batch_size > self._block_count:
            self._position = 0
            if self._shuffle:
                self._random.shuffle(self._indices)
        selected = self._indices[self._position : self._position + batch_size]
        self._position += batch_size
        positions = selected[:, None] * self._block_length + self._offsets[None, :]
        values = np.asarray(self._tokens[positions], dtype=np.int64)
        tokens = torch.from_numpy(values)
        return tokens[:, :-1], tokens[:, 1:]


def resolve_device(name: str) -> torch.device:
    if name == "directml":
        try:
            import torch_directml
        except ImportError as ex:
            raise TrainingError("当前环境没有 torch-directml") from ex
        return torch_directml.device()
    return torch.device("cpu")


def nested_to_cpu(value: Any) -> Any:
    if isinstance(value, torch.Tensor):
        return value.detach().cpu()
    if isinstance(value, dict):
        return {key: nested_to_cpu(item) for key, item in value.items()}
    if isinstance(value, list):
        return [nested_to_cpu(item) for item in value]
    if isinstance(value, tuple):
        return tuple(nested_to_cpu(item) for item in value)
    return value


def move_optimizer_state(optimizer: torch.optim.Optimizer, device: torch.device) -> None:
    for state in optimizer.state.values():
        for key, value in state.items():
            if isinstance(value, torch.Tensor):
                state[key] = value.to(device)


def save_checkpoint(
    path: Path,
    model: CharacterTransformer,
    optimizer: torch.optim.Optimizer,
    step: int,
    tokens_seen: int,
    validation_loss: float,
    vocabulary_path: Path,
    include_optimizer: bool,
) -> None:
    payload = {
        "version": 1,
        "config": model.config.to_dict(),
        "model": nested_to_cpu(model.state_dict()),
        "step": step,
        "tokens_seen": tokens_seen,
        "validation_loss": validation_loss,
        "vocabulary_path": str(vocabulary_path.resolve()),
    }
    if include_optimizer:
        payload["optimizer"] = nested_to_cpu(optimizer.state_dict())
    temporary = path.with_suffix(path.suffix + ".tmp")
    torch.save(payload, temporary)
    os.replace(temporary, path)


@torch.no_grad()
def evaluate(
    model: CharacterTransformer,
    sampler: BlockSampler,
    device: torch.device,
    batch_size: int,
    batches: int,
) -> float:
    model.eval()
    losses = []
    for _ in range(batches):
        inputs, targets = sampler.next(batch_size)
        inputs = inputs.to(device)
        targets = targets.to(device)
        logits = model(inputs)
        loss = F.cross_entropy(
            logits.reshape(-1, logits.size(-1)), targets.reshape(-1)
        )
        losses.append(float(loss.cpu()))
    model.train()
    return sum(losses) / len(losses)


def train(args: argparse.Namespace) -> int:
    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    args.output.mkdir(parents=True, exist_ok=True)
    vocabulary_path = args.data / "vocabulary.json"
    vocabulary = json.loads(vocabulary_path.read_text(encoding="utf-8"))
    config = ModelConfig(
        vocabulary_size=len(vocabulary),
        context_length=args.context_length,
        layer_count=args.layers,
        embedding_size=args.embedding_size,
        head_count=args.heads,
        feed_forward_size=args.feed_forward_size,
        dropout=args.dropout,
    )
    device = resolve_device(args.device)
    model = CharacterTransformer(config).to(device)
    optimizer = torch.optim.AdamW(
        model.parameters(),
        lr=args.learning_rate,
        betas=(0.9, 0.95),
        weight_decay=args.weight_decay,
        foreach=False,
    )
    start_step = 0
    tokens_seen = 0
    best_validation = float("inf")
    latest_path = args.output / "latest.pt"
    if args.resume and latest_path.is_file():
        checkpoint = torch.load(latest_path, map_location="cpu", weights_only=False)
        model.load_state_dict(checkpoint["model"])
        model.to(device)
        optimizer.load_state_dict(checkpoint["optimizer"])
        move_optimizer_state(optimizer, device)
        start_step = int(checkpoint["step"])
        tokens_seen = int(checkpoint["tokens_seen"])
        best_validation = float(checkpoint["validation_loss"])

    train_sampler = BlockSampler(
        args.data / "train.bin", args.context_length, args.seed, shuffle=True
    )
    valid_sampler = BlockSampler(
        args.data / "valid.bin", args.context_length, args.seed + 1, shuffle=False
    )
    tokens_per_step = args.batch_size * args.context_length
    total_steps = math.ceil(args.train_tokens / tokens_per_step)
    run_end_step = total_steps
    if args.stop_after_steps:
        run_end_step = min(total_steps, start_step + args.stop_after_steps)
    warmup_steps = min(args.warmup_steps, max(total_steps // 10, 1))
    log_path = args.output / "training.jsonl"
    started = time.monotonic()
    interval_started = started
    interval_tokens = 0

    print(f"device={device}")
    print(f"parameters={model.parameter_count():,}")
    print(f"vocabulary={len(vocabulary):,}")
    print(f"steps={total_steps:,} tokens/step={tokens_per_step:,}")
    model.train()
    with log_path.open("a", encoding="utf-8") as log:
        for step in range(start_step + 1, run_end_step + 1):
            if step <= warmup_steps:
                learning_rate = args.learning_rate * step / warmup_steps
            else:
                progress = (step - warmup_steps) / max(total_steps - warmup_steps, 1)
                learning_rate = args.min_learning_rate + 0.5 * (
                    args.learning_rate - args.min_learning_rate
                ) * (1.0 + math.cos(math.pi * progress))
            for group in optimizer.param_groups:
                group["lr"] = learning_rate

            inputs, targets = train_sampler.next(args.batch_size)
            inputs = inputs.to(device)
            targets = targets.to(device)
            optimizer.zero_grad(set_to_none=True)
            logits = model(inputs)
            loss = F.cross_entropy(
                logits.reshape(-1, logits.size(-1)), targets.reshape(-1)
            )
            loss.backward()
            optimizer.step()
            loss_value = float(loss.detach().cpu())
            tokens_seen += tokens_per_step
            interval_tokens += tokens_per_step

            if step % args.log_interval == 0 or step == 1:
                now = time.monotonic()
                throughput = interval_tokens / max(now - interval_started, 1e-9)
                event = {
                    "type": "train",
                    "step": step,
                    "loss": loss_value,
                    "learning_rate": learning_rate,
                    "tokens_seen": tokens_seen,
                    "tokens_per_second": throughput,
                    "elapsed_seconds": now - started,
                }
                print(json.dumps(event, ensure_ascii=False), flush=True)
                log.write(json.dumps(event, ensure_ascii=False) + "\n")
                log.flush()
                interval_started = now
                interval_tokens = 0

            if step % args.validation_interval == 0 or step == run_end_step:
                validation_loss = evaluate(
                    model,
                    valid_sampler,
                    device,
                    args.validation_batch_size,
                    args.validation_batches,
                )
                event = {
                    "type": "validation",
                    "step": step,
                    "loss": validation_loss,
                    "perplexity": math.exp(min(validation_loss, 20.0)),
                    "tokens_seen": tokens_seen,
                }
                print(json.dumps(event, ensure_ascii=False), flush=True)
                log.write(json.dumps(event, ensure_ascii=False) + "\n")
                log.flush()
                if step % args.checkpoint_interval == 0 or step == run_end_step:
                    save_checkpoint(
                        latest_path,
                        model,
                        optimizer,
                        step,
                        tokens_seen,
                        validation_loss,
                        vocabulary_path,
                        include_optimizer=True,
                    )
                if validation_loss < best_validation:
                    best_validation = validation_loss
                    save_checkpoint(
                        args.output / "best.pt",
                        model,
                        optimizer,
                        step,
                        tokens_seen,
                        validation_loss,
                        vocabulary_path,
                        include_optimizer=False,
                    )
            elif step % args.checkpoint_interval == 0:
                save_checkpoint(
                    latest_path,
                    model,
                    optimizer,
                    step,
                    tokens_seen,
                    float("nan"),
                    vocabulary_path,
                    include_optimizer=True,
                )
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--device", choices=("cpu", "directml"), default="cpu")
    parser.add_argument("--train-tokens", type=int, default=200_000_000)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--context-length", type=int, default=64)
    parser.add_argument("--layers", type=int, default=8)
    parser.add_argument("--embedding-size", type=int, default=256)
    parser.add_argument("--heads", type=int, default=4)
    parser.add_argument("--feed-forward-size", type=int, default=1024)
    parser.add_argument("--dropout", type=float, default=0.0)
    parser.add_argument("--learning-rate", type=float, default=3.0e-4)
    parser.add_argument("--min-learning-rate", type=float, default=3.0e-5)
    parser.add_argument("--weight-decay", type=float, default=0.1)
    parser.add_argument("--warmup-steps", type=int, default=1000)
    parser.add_argument("--log-interval", type=int, default=100)
    parser.add_argument("--validation-interval", type=int, default=2000)
    parser.add_argument("--validation-batches", type=int, default=20)
    parser.add_argument("--validation-batch-size", type=int, default=64)
    parser.add_argument("--checkpoint-interval", type=int, default=2000)
    parser.add_argument("--seed", type=int, default=20260810)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument(
        "--stop-after-steps",
        type=int,
        default=0,
        help="本进程最多训练多少步；0 表示一直训练到总步数",
    )
    args = parser.parse_args(argv)
    numeric = (
        args.train_tokens,
        args.batch_size,
        args.context_length,
        args.layers,
        args.embedding_size,
        args.heads,
        args.feed_forward_size,
        args.warmup_steps,
        args.log_interval,
        args.validation_interval,
        args.validation_batches,
        args.validation_batch_size,
        args.checkpoint_interval,
    )
    if min(numeric) < 1:
        parser.error("训练规模和间隔参数必须大于 0")
    if args.stop_after_steps < 0:
        parser.error("--stop-after-steps 不能小于 0")
    return args


def main(argv: Optional[Sequence[str]] = None) -> int:
    try:
        return train(parse_arguments(argv))
    except (TrainingError, OSError, ValueError, json.JSONDecodeError) as ex:
        print(f"错误: {ex}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
