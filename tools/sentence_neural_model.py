#!/usr/bin/env python3
"""TigerClaw 整句实验使用的小型字符因果 Transformer。"""

from __future__ import annotations

import math
from dataclasses import asdict, dataclass
from typing import Any, Dict

import torch
from torch import Tensor, nn
from torch.nn import functional as F


@dataclass(frozen=True)
class ModelConfig:
    vocabulary_size: int = 8192
    context_length: int = 64
    layer_count: int = 8
    embedding_size: int = 256
    head_count: int = 4
    feed_forward_size: int = 1024
    dropout: float = 0.0

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, values: Dict[str, Any]) -> "ModelConfig":
        return cls(**values)


class CausalSelfAttention(nn.Module):
    def __init__(self, config: ModelConfig) -> None:
        super().__init__()
        if config.embedding_size % config.head_count:
            raise ValueError("embedding_size 必须能被 head_count 整除")
        self._head_count = config.head_count
        self._head_size = config.embedding_size // config.head_count
        self._query_key_value = nn.Linear(
            config.embedding_size, config.embedding_size * 3
        )
        self._projection = nn.Linear(config.embedding_size, config.embedding_size)
        self._dropout = nn.Dropout(config.dropout)
        mask = torch.triu(
            torch.ones(config.context_length, config.context_length, dtype=torch.bool),
            diagonal=1,
        )
        self.register_buffer("_causal_mask", mask, persistent=False)

    def forward(self, inputs: Tensor) -> Tensor:
        batch_size, sequence_length, embedding_size = inputs.shape
        query, key, value = self._query_key_value(inputs).chunk(3, dim=-1)

        def split_heads(values: Tensor) -> Tensor:
            return values.view(
                batch_size, sequence_length, self._head_count, self._head_size
            ).transpose(1, 2)

        query = split_heads(query)
        key = split_heads(key)
        value = split_heads(value)
        weights = torch.matmul(query, key.transpose(-2, -1)) / math.sqrt(
            self._head_size
        )
        mask = self._causal_mask[:sequence_length, :sequence_length]
        weights = weights.masked_fill(mask, -1.0e4)
        weights = self._dropout(F.softmax(weights, dim=-1))
        attended = torch.matmul(weights, value)
        attended = attended.transpose(1, 2).contiguous().view(
            batch_size, sequence_length, embedding_size
        )
        return self._projection(attended)


class TransformerBlock(nn.Module):
    def __init__(self, config: ModelConfig) -> None:
        super().__init__()
        self._attention_norm = nn.LayerNorm(config.embedding_size)
        self._attention = CausalSelfAttention(config)
        self._feed_forward_norm = nn.LayerNorm(config.embedding_size)
        self._feed_forward = nn.Sequential(
            nn.Linear(config.embedding_size, config.feed_forward_size),
            nn.GELU(),
            nn.Linear(config.feed_forward_size, config.embedding_size),
            nn.Dropout(config.dropout),
        )

    def forward(self, inputs: Tensor) -> Tensor:
        inputs = inputs + self._attention(self._attention_norm(inputs))
        return inputs + self._feed_forward(self._feed_forward_norm(inputs))


class CharacterTransformer(nn.Module):
    def __init__(self, config: ModelConfig) -> None:
        super().__init__()
        self.config = config
        self._token_embedding = nn.Embedding(
            config.vocabulary_size, config.embedding_size
        )
        self._position_embedding = nn.Embedding(
            config.context_length, config.embedding_size
        )
        self._dropout = nn.Dropout(config.dropout)
        self._blocks = nn.ModuleList(
            TransformerBlock(config) for _ in range(config.layer_count)
        )
        self._final_norm = nn.LayerNorm(config.embedding_size)
        self._output = nn.Linear(
            config.embedding_size, config.vocabulary_size, bias=False
        )
        self._output.weight = self._token_embedding.weight
        self.apply(self._initialize_weights)

    @staticmethod
    def _initialize_weights(module: nn.Module) -> None:
        if isinstance(module, (nn.Linear, nn.Embedding)):
            nn.init.normal_(module.weight, mean=0.0, std=0.02)
            if isinstance(module, nn.Linear) and module.bias is not None:
                nn.init.zeros_(module.bias)

    def forward(self, token_ids: Tensor) -> Tensor:
        _, sequence_length = token_ids.shape
        if sequence_length > self.config.context_length:
            raise ValueError("输入长度超过模型 context_length")
        positions = torch.arange(sequence_length, device=token_ids.device)
        hidden = self._token_embedding(token_ids) + self._position_embedding(positions)
        hidden = self._dropout(hidden)
        for block in self._blocks:
            hidden = block(hidden)
        return self._output(self._final_norm(hidden))

    def parameter_count(self) -> int:
        return sum(parameter.numel() for parameter in self.parameters())
