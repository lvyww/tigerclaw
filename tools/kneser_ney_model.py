#!/usr/bin/env python3
"""Memory-mapped reader for TigerClaw's offline Kneser-Ney V2 model."""

from __future__ import annotations

import math
import mmap
import struct
from functools import lru_cache
from pathlib import Path

try:
    from test_sentence_ngram import ExperimentError
except ModuleNotFoundError:
    from .test_sentence_ngram import ExperimentError


MAGIC = b"TCSKNM01"


class FixedArray:
    def __init__(self, mapping: mmap.mmap, offset: int, count: int, key_format: str):
        self.mapping = mapping
        self.offset = offset
        self.count = count
        self.key_format = key_format
        self.key_size = struct.calcsize(key_format)
        self.record_size = self.key_size + 4

    def lookup(self, key: int, fallback: float = 0.0) -> float:
        low = 0
        high = self.count
        while low < high:
            middle = (low + high) // 2
            value = struct.unpack_from(
                self.key_format,
                self.mapping,
                self.offset + middle * self.record_size,
            )[0]
            if value < key:
                low = middle + 1
            else:
                high = middle
        if low >= self.count:
            return fallback
        position = self.offset + low * self.record_size
        value = struct.unpack_from(self.key_format, self.mapping, position)[0]
        if value != key:
            return fallback
        return struct.unpack_from("<f", self.mapping, position + self.key_size)[0]


class KneserNeyLanguageModel:
    def __init__(self, path: Path) -> None:
        if not path.is_file():
            raise ExperimentError(f"Kneser-Ney模型不存在: {path}")
        self.path = path
        self._stream = path.open("rb")
        self._mapping = mmap.mmap(self._stream.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            self._parse()
        except Exception:
            self.close()
            raise

    def _parse(self) -> None:
        mapping = self._mapping
        if mapping[:8] != MAGIC or struct.unpack_from("<i", mapping, 8)[0] != 1:
            raise ExperimentError(f"Kneser-Ney模型格式无效: {self.path}")
        position = 12
        token_count = struct.unpack_from("<i", mapping, position)[0]
        position += 4
        self.unigrams = FixedArray(mapping, position, token_count, "<i")
        position += token_count * 8
        bigram_count = struct.unpack_from("<q", mapping, position)[0]
        position += 8
        self.bigrams = FixedArray(mapping, position, bigram_count, "<Q")
        position += bigram_count * 12
        bigram_context_count = struct.unpack_from("<i", mapping, position)[0]
        position += 4
        self.bigram_contexts = FixedArray(
            mapping, position, bigram_context_count, "<i"
        )
        position += bigram_context_count * 8
        trigram_count = struct.unpack_from("<q", mapping, position)[0]
        position += 8
        self.trigrams = FixedArray(mapping, position, trigram_count, "<Q")
        position += trigram_count * 12
        trigram_context_count = struct.unpack_from("<q", mapping, position)[0]
        position += 8
        self.trigram_contexts = FixedArray(
            mapping, position, trigram_context_count, "<Q"
        )
        position += trigram_context_count * 12
        if position != len(mapping):
            raise ExperimentError(f"Kneser-Ney模型尾部数据无效: {self.path}")
        self.unknown_probability = self.unigrams.lookup(0, 1e-12)

    def close(self) -> None:
        mapping = getattr(self, "_mapping", None)
        if mapping is not None:
            mapping.close()
            self._mapping = None
        stream = getattr(self, "_stream", None)
        if stream is not None:
            stream.close()
            self._stream = None

    @staticmethod
    def _token(value: str) -> int:
        return ord(value) if value else 0

    @staticmethod
    def _pair(first: int, second: int) -> int:
        return (first << 21) | second

    @staticmethod
    def _triple(first: int, second: int, third: int) -> int:
        return (first << 42) | (second << 21) | third

    @lru_cache(maxsize=1000000)
    def log_probability(self, previous2: str, previous1: str, target: str) -> float:
        first = self._token(previous2)
        second = self._token(previous1)
        third = self._token(target)
        unigram = self.unigrams.lookup(third, self.unknown_probability)
        bigram = self.bigrams.lookup(self._pair(second, third))
        bigram += self.bigram_contexts.lookup(second, 1.0) * unigram
        context = self._pair(first, second)
        probability = self.trigrams.lookup(self._triple(first, second, third))
        probability += self.trigram_contexts.lookup(context, 1.0) * bigram
        return math.log(max(probability, 1e-300))
