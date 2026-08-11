#!/usr/bin/env python3
"""加载字符 Transformer checkpoint，并批量计算整句对数概率。"""

from __future__ import annotations

import json
from pathlib import Path
from typing import List, Sequence

import torch
from torch.nn import functional as F

from sentence_neural_model import CharacterTransformer, ModelConfig


def resolve_device(name: str) -> torch.device:
    if name == "directml":
        import torch_directml

        return torch_directml.device()
    return torch.device("cpu")


class NeuralSentenceScorer:
    def __init__(
        self,
        checkpoint_path: Path,
        vocabulary_path: Path,
        device_name: str,
    ) -> None:
        checkpoint = torch.load(checkpoint_path, map_location="cpu", weights_only=False)
        config = ModelConfig.from_dict(checkpoint["config"])
        self._device = resolve_device(device_name)
        self._model = CharacterTransformer(config)
        self._model.load_state_dict(checkpoint["model"])
        self._model.to(self._device)
        self._model.eval()
        self._vocabulary = json.loads(vocabulary_path.read_text(encoding="utf-8"))
        self._bos = self._vocabulary["<bos>"]
        self._eos = self._vocabulary["<eos>"]
        self._pad = self._vocabulary["<pad>"]
        self._unknown = self._vocabulary["<unk>"]
        self._context_length = config.context_length

    def _encode(self, text: str) -> List[int]:
        values = [self._bos]
        values.extend(self._vocabulary.get(character, self._unknown) for character in text)
        values.append(self._eos)
        if len(values) - 1 > self._context_length:
            values = values[-(self._context_length + 1) :]
            values[0] = self._bos
        return values

    @torch.no_grad()
    def score(self, texts: Sequence[str], batch_size: int = 32) -> List[float]:
        result: List[float] = []
        for start in range(0, len(texts), batch_size):
            encoded = [self._encode(text) for text in texts[start : start + batch_size]]
            maximum = max(len(values) for values in encoded)
            values = [
                sequence + [self._pad] * (maximum - len(sequence))
                for sequence in encoded
            ]
            tokens = torch.tensor(values, dtype=torch.long, device=self._device)
            inputs = tokens[:, :-1]
            targets = tokens[:, 1:]
            logits = self._model(inputs)
            losses = []
            for row, sequence in enumerate(encoded):
                target_length = len(sequence) - 1
                losses.append(
                    F.cross_entropy(
                        logits[row, :target_length],
                        targets[row, :target_length],
                        reduction="sum",
                    )
                )
            scores = (-torch.stack(losses)).cpu().tolist()
            result.extend(float(score) for score in scores)
        return result
