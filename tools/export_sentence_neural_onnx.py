#!/usr/bin/env python3
"""Export TigerClaw's character Transformer checkpoint to ONNX for runtime reranking."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import torch

from sentence_neural_model import CharacterTransformer, ModelConfig


def export_model(checkpoint_path: Path, output_path: Path) -> None:
    checkpoint = torch.load(checkpoint_path, map_location="cpu", weights_only=False)
    config = ModelConfig.from_dict(checkpoint["config"])
    model = CharacterTransformer(config)
    model.load_state_dict(checkpoint["model"])
    model.eval()

    example_length = min(config.context_length, 16)
    example = torch.zeros((2, example_length), dtype=torch.long)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        model,
        example,
        output_path,
        input_names=["token_ids"],
        output_names=["logits"],
        dynamic_axes={
            "token_ids": {0: "batch", 1: "sequence"},
            "logits": {0: "batch", 1: "sequence"},
        },
        opset_version=17,
        do_constant_folding=True,
        dynamo=False,
    )

    metadata = {
        "format": "TigerClaw.Sentence.ONNX.v1",
        "context_length": config.context_length,
        "vocabulary_size": config.vocabulary_size,
        "checkpoint": str(checkpoint_path.resolve()),
    }
    output_path.with_suffix(".json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"model={output_path} ({output_path.stat().st_size / 1024 / 1024:.1f} MiB)")
    print(f"context_length={config.context_length}, vocabulary={config.vocabulary_size}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    export_model(args.checkpoint, args.output)


if __name__ == "__main__":
    main()
