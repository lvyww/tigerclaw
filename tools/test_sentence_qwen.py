#!/usr/bin/env python3
"""Use a local Qwen base model to score sentence candidates without TigerClaw."""

from __future__ import annotations

import argparse
import ctypes
import os
import time
from dataclasses import dataclass
from pathlib import Path
from typing import List, Sequence, Tuple


DEFAULT_CANDIDATES = (
    "不蒙良为矫揉造作",
    "不带一丝矫揉造作",
)


def default_model_path() -> Path:
    if os.name == "nt":
        return Path("C:/Archive/tigerclaw_sentence_ml/qwen3-0.6b-base")
    return Path("/mnt/c/Archive/tigerclaw_sentence_ml/qwen3-0.6b-base")


@dataclass(frozen=True)
class CandidateScore:
    text: str
    total_log_probability: float
    average_token_log_probability: float
    average_character_log_probability: float
    token_count: int
    tokens: Tuple[str, ...]


def process_memory_megabytes() -> Tuple[float, float] | None:
    if os.name != "nt":
        return None

    class ProcessMemoryCounters(ctypes.Structure):
        _fields_ = [
            ("cb", ctypes.c_ulong),
            ("page_fault_count", ctypes.c_ulong),
            ("peak_working_set_size", ctypes.c_size_t),
            ("working_set_size", ctypes.c_size_t),
            ("quota_peak_paged_pool_usage", ctypes.c_size_t),
            ("quota_paged_pool_usage", ctypes.c_size_t),
            ("quota_peak_non_paged_pool_usage", ctypes.c_size_t),
            ("quota_non_paged_pool_usage", ctypes.c_size_t),
            ("pagefile_usage", ctypes.c_size_t),
            ("peak_pagefile_usage", ctypes.c_size_t),
        ]

    counters = ProcessMemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    get_process_memory_info = ctypes.windll.psapi.GetProcessMemoryInfo
    get_process_memory_info.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(ProcessMemoryCounters),
        ctypes.c_ulong,
    ]
    get_process_memory_info.restype = ctypes.c_int
    process = ctypes.windll.kernel32.GetCurrentProcess()
    if not get_process_memory_info(process, ctypes.byref(counters), counters.cb):
        return None
    divisor = 1024.0 * 1024.0
    return counters.working_set_size / divisor, counters.peak_working_set_size / divisor


def resolve_device(name: str):
    import torch

    if name == "directml":
        try:
            import torch_directml
        except ImportError as ex:
            raise RuntimeError("当前Python环境没有安装torch-directml。") from ex
        return torch_directml.device(), "directml"
    if name == "cpu":
        return torch.device("cpu"), "cpu"
    raise ValueError(f"不支持的设备: {name}")


def resolve_dtype(name: str):
    import torch

    values = {
        "float32": torch.float32,
        "float16": torch.float16,
        "bfloat16": torch.bfloat16,
    }
    return values[name]


def encode_candidate(tokenizer, bos_token_id: int, eos_token_id: int, text: str):
    text_ids = tokenizer.encode(text, add_special_tokens=False)
    if not text_ids:
        raise ValueError("候选句不能为空。")
    sequence = [bos_token_id]
    sequence.extend(text_ids)
    sequence.append(eos_token_id)
    visible_tokens = tuple(
        tokenizer.decode(
            [token_id], skip_special_tokens=False, clean_up_tokenization_spaces=False
        )
        for token_id in text_ids
    )
    return sequence, visible_tokens


def load_model(model_path: Path, device, device_label: str, dtype):
    from transformers import AutoConfig, AutoModelForCausalLM

    if device_label != "directml":
        model = AutoModelForCausalLM.from_pretrained(
            model_path,
            local_files_only=True,
            dtype=dtype,
            attn_implementation="eager",
        )
        model.to(device)
        return model

    # transformers loads pretrained tensors in inference mode. torch-directml
    # cannot migrate those tensors because they have no writable version counter,
    # so create ordinary parameters first and then copy the safetensors state.
    from safetensors.torch import load_file

    config = AutoConfig.from_pretrained(model_path, local_files_only=True)
    model = AutoModelForCausalLM.from_config(
        config, dtype=dtype, attn_implementation="eager"
    )
    model.to(device)
    state = load_file(str(model_path / "model.safetensors"), device="cpu")
    missing, unexpected = model.load_state_dict(state, strict=False)
    missing = [name for name in missing if name != "lm_head.weight"]
    if missing or unexpected:
        raise RuntimeError(
            "Qwen权重不匹配: missing="
            + ",".join(missing[:5])
            + " unexpected="
            + ",".join(unexpected[:5])
        )
    del state
    model.tie_weights()
    return model


def score_batch(model, tokenizer, device, encoded, texts, tokens):
    import torch
    from torch.nn import functional as functional

    pad_token_id = tokenizer.pad_token_id
    if pad_token_id is None:
        pad_token_id = tokenizer.eos_token_id
    width = max(len(sequence) - 1 for sequence in encoded)
    input_ids = torch.full(
        (len(encoded), width), pad_token_id, dtype=torch.long
    )
    attention_mask = torch.zeros((len(encoded), width), dtype=torch.long)
    labels = torch.full((len(encoded), width), -100, dtype=torch.long)
    for row, sequence in enumerate(encoded):
        length = len(sequence) - 1
        input_ids[row, :length] = torch.tensor(sequence[:-1], dtype=torch.long)
        attention_mask[row, :length] = 1
        labels[row, :length] = torch.tensor(sequence[1:], dtype=torch.long)

    input_ids = input_ids.to(device)
    attention_mask = attention_mask.to(device)
    labels = labels.to(device)
    # torch-directml cannot maintain version counters for tensors created by
    # inference_mode inside Qwen's rotary embedding path. no_grad still avoids
    # autograd storage and works on both CPU and DirectML.
    with torch.no_grad():
        logits = model(
            input_ids=input_ids,
            attention_mask=attention_mask,
            use_cache=False,
        ).logits
        losses = functional.cross_entropy(
            logits.reshape(-1, logits.shape[-1]),
            labels.reshape(-1),
            ignore_index=-100,
            reduction="none",
        ).reshape(labels.shape)
        losses = losses.to("cpu")

    result: List[CandidateScore] = []
    for row, text in enumerate(texts):
        predicted_tokens = len(encoded[row]) - 1
        total = -float(losses[row, :predicted_tokens].sum())
        result.append(
            CandidateScore(
                text=text,
                total_log_probability=total,
                average_token_log_probability=total / predicted_tokens,
                average_character_log_probability=total / max(len(text), 1),
                token_count=predicted_tokens,
                tokens=tokens[row],
            )
        )
    return result


def score_candidates(args: argparse.Namespace) -> int:
    try:
        from transformers import AutoTokenizer
    except ImportError as ex:
        raise RuntimeError(
            "缺少依赖，请安装 transformers>=4.51、torch 和 safetensors。"
        ) from ex

    model_path = args.model.resolve()
    if not (model_path / "model.safetensors").is_file():
        raise FileNotFoundError(f"模型权重不存在: {model_path}")
    candidates = tuple(args.candidate or DEFAULT_CANDIDATES)
    device, device_label = resolve_device(args.device)
    dtype = resolve_dtype(args.dtype)

    print(f"正在从 {model_path} 加载模型……", flush=True)
    load_started = time.monotonic()
    tokenizer = AutoTokenizer.from_pretrained(
        model_path, local_files_only=True, use_fast=True
    )
    model = load_model(model_path, device, device_label, dtype)
    model.eval()
    load_seconds = time.monotonic() - load_started

    bos_token_id = model.config.bos_token_id
    eos_token_id = model.config.eos_token_id
    if bos_token_id is None or eos_token_id is None:
        raise RuntimeError("模型配置缺少BOS或EOS token。")

    all_scores: List[CandidateScore] = []
    score_started = time.monotonic()
    for start in range(0, len(candidates), args.batch_size):
        texts = candidates[start : start + args.batch_size]
        encoded_with_tokens = [
            encode_candidate(tokenizer, bos_token_id, eos_token_id, text)
            for text in texts
        ]
        encoded = [item[0] for item in encoded_with_tokens]
        tokens = [item[1] for item in encoded_with_tokens]
        all_scores.extend(
            score_batch(model, tokenizer, device, encoded, texts, tokens)
        )
    score_seconds = time.monotonic() - score_started

    ranked = sorted(
        all_scores, key=lambda item: item.total_log_probability, reverse=True
    )
    print(
        f"设备={device_label} dtype={args.dtype} 候选={len(candidates)} "
        f"加载={load_seconds:.3f}s 评分={score_seconds:.3f}s"
    )
    memory = process_memory_megabytes()
    if memory is not None:
        print(f"工作集={memory[0]:.1f}MiB 峰值工作集={memory[1]:.1f}MiB")
    for rank, item in enumerate(ranked, 1):
        print(
            f"{rank:02d}. {item.text}\n"
            f"    总分={item.total_log_probability:.6f} "
            f"token均分={item.average_token_log_probability:.6f} "
            f"字均分={item.average_character_log_probability:.6f} "
            f"预测token={item.token_count}"
        )
        if args.show_tokens:
            print("    tokens=" + " | ".join(item.tokens))
    return 0


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, default=default_model_path())
    parser.add_argument(
        "--candidate",
        action="append",
        help="待评分候选，可重复指定；不指定时使用内置测试句。",
    )
    parser.add_argument("--device", choices=("cpu", "directml"), default="cpu")
    parser.add_argument(
        "--dtype",
        choices=("float32", "float16", "bfloat16"),
        default="float32",
    )
    parser.add_argument("--batch-size", type=int, default=4)
    parser.add_argument("--show-tokens", action="store_true")
    args = parser.parse_args(argv)
    if args.batch_size < 1:
        parser.error("--batch-size 必须大于0")
    return args


def main(argv: Sequence[str] | None = None) -> int:
    try:
        return score_candidates(parse_arguments(argv))
    except (FileNotFoundError, RuntimeError, ValueError) as ex:
        if os.environ.get("TIGERCLAW_QWEN_DEBUG") == "1":
            raise
        print(f"错误: {ex}")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
