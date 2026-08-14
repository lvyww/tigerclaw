#!/usr/bin/env python3
"""Read Rime::Grammar/1.0 files for offline TigerClaw experiments."""

from __future__ import annotations

import mmap
import struct
import sys
from functools import lru_cache
from pathlib import Path
from typing import List, Tuple

try:
    from test_sentence_ngram import BOS, EOS, ExperimentError
except ModuleNotFoundError:  # Support imports through tools.* from the repo root.
    from .test_sentence_ngram import BOS, EOS, ExperimentError


FORMAT_PREFIX = b"Rime::Grammar/"
VALUE_SCALE = 10000.0
NON_COLLOCATION_PENALTY = -12.0


def encode_rime_grammar(text: str) -> bytes:
    """Encode Unicode exactly as librime-octagram's gram_encoding.cc does."""

    encoded = bytearray()
    for character in text:
        value = ord(character)
        if value < 0x80:
            encoded.append(0xE0 if value == 0 else value)
        elif 0x4000 <= value < 0xA000:
            if (value & 0xFF) == 0:
                encoded.extend((0xE1, (value >> 8) + 0x40))
            else:
                encoded.extend(((value >> 8) + 0x40, value & 0xFF))
        else:
            bits = 32
            while bits > 0 and (value & 0xFE000000) == 0:
                bits -= 7
                value <<= 7
            byte_count = (bits + 6) // 7
            encoded.append(0xE0 | byte_count)
            while byte_count > 0:
                byte_count -= 1
                encoded.append(((value >> 25) & 0x7F) | 0x80)
                value <<= 7
    return bytes(encoded)


class RimeGramDatabase:
    """Memory-mapped Darts-clone trie stored in a Rime grammar file."""

    def __init__(self, path: Path) -> None:
        if sys.byteorder != "little":
            raise ExperimentError("Rime .gram实验读取器仅支持小端平台。")
        if not path.is_file():
            raise ExperimentError(f"Rime .gram文件不存在: {path}")
        self.path = path
        try:
            self._stream = path.open("rb")
            self._mapping = mmap.mmap(self._stream.fileno(), 0, access=mmap.ACCESS_READ)
        except OSError as ex:
            raise ExperimentError(f"无法打开Rime .gram文件 {path}: {ex}") from ex

        try:
            if len(self._mapping) < 44:
                raise ExperimentError(f"Rime .gram文件过短: {path}")
            format_name = self._mapping[:32].split(b"\0", 1)[0]
            if not format_name.startswith(FORMAT_PREFIX):
                raise ExperimentError(f"不是Rime Grammar文件: {path}")
            unit_count = struct.unpack_from("<I", self._mapping, 36)[0]
            relative_offset = struct.unpack_from("<i", self._mapping, 40)[0]
            array_offset = 40 + relative_offset
            array_bytes = unit_count * 4
            if (
                unit_count == 0
                or array_offset < 44
                or array_offset + array_bytes > len(self._mapping)
            ):
                raise ExperimentError(f"Rime .gram双数组元数据无效: {path}")
            self.format_name = format_name.decode("ascii", errors="replace")
            self.unit_count = unit_count
            self._units = memoryview(self._mapping)[
                array_offset : array_offset + array_bytes
            ].cast("I")
        except Exception:
            self.close()
            raise

    def close(self) -> None:
        units = getattr(self, "_units", None)
        if units is not None:
            units.release()
            self._units = None
        mapping = getattr(self, "_mapping", None)
        if mapping is not None:
            mapping.close()
            self._mapping = None
        stream = getattr(self, "_stream", None)
        if stream is not None:
            stream.close()
            self._stream = None

    def __enter__(self) -> "RimeGramDatabase":
        return self

    def __exit__(self, _type, _value, _traceback) -> None:
        self.close()

    @staticmethod
    def _offset(unit: int) -> int:
        return (unit >> 10) << ((unit & (1 << 9)) >> 6)

    @staticmethod
    def _label(unit: int) -> int:
        return unit & ((1 << 31) | 0xFF)

    def lookup(self, context: bytes, word: bytes) -> List[Tuple[int, int]]:
        """Return (matched word bytes, value) for prefixes after context."""

        units = self._units
        node_position = 0
        unit = units[node_position]
        for value in context:
            node_position ^= self._offset(unit) ^ value
            if node_position >= self.unit_count:
                return []
            unit = units[node_position]
            if self._label(unit) != value:
                return []

        position = node_position ^ self._offset(unit)
        matches: List[Tuple[int, int]] = []
        for index, value in enumerate(word):
            position ^= value
            if position >= self.unit_count:
                break
            unit = units[position]
            if self._label(unit) != value:
                break
            position ^= self._offset(unit)
            if unit & (1 << 8):
                matches.append((index + 1, units[position] & 0x7FFFFFFF))
                if len(matches) >= 8:
                    break
        return matches


class RimeGrammarScorer:
    """librime-octagram Query() behavior without its final-word rear bonus."""

    def __init__(
        self,
        database: RimeGramDatabase,
        collocation_max_length: int,
        collocation_min_length: int,
        collocation_penalty: float = -12.0,
        weak_collocation_penalty: float = -24.0,
        non_collocation_penalty: float = NON_COLLOCATION_PENALTY,
    ) -> None:
        self.database = database
        self.collocation_max_length = collocation_max_length
        self.collocation_min_length = collocation_min_length
        self.collocation_penalty = collocation_penalty
        self.weak_collocation_penalty = weak_collocation_penalty
        self.non_collocation_penalty = non_collocation_penalty

    @lru_cache(maxsize=1000000)
    def query(self, context: str, word: str) -> float:
        if not context or not word:
            return self.non_collocation_penalty
        maximum_part = min(8, self.collocation_max_length - 1)
        context_query = context[-maximum_part:]
        word_query = word[:maximum_part]
        encoded_word = encode_rime_grammar(word_query)
        result = self.non_collocation_penalty
        context_length = len(context_query)
        for start in range(context_length):
            context_part = context_query[start:]
            encoded_context = encode_rime_grammar(context_part)
            matches = self.database.lookup(encoded_context, encoded_word)
            for matched_bytes, raw_value in matches:
                matched_word_length = self._encoded_unicode_length(
                    encoded_word[:matched_bytes]
                )
                collocation_length = len(context_part) + matched_word_length
                whole_query = start == 0 and matched_bytes == len(encoded_word)
                penalty = (
                    self.collocation_penalty
                    if collocation_length >= self.collocation_min_length or whole_query
                    else self.weak_collocation_penalty
                )
                result = max(result, raw_value / VALUE_SCALE + penalty)
        return result

    @staticmethod
    def _encoded_unicode_length(encoded: bytes) -> int:
        position = 0
        length = 0
        while position < len(encoded):
            first = encoded[position]
            if (first & 0x80) == 0:
                position += 1
            elif (first & 0xF0) == 0xE0:
                position += (first & 0x0F) + 1
            else:
                position += 2
            length += 1
        return length


class RimeBGCAndBGWModel:
    """Candidate-boundary scorer used only by the offline lattice decoder."""

    def __init__(
        self,
        bgc_path: Path,
        bgw_path: Path,
        bgc_weight: float = 1.0,
        bgw_weight: float = 1.0,
        mode: str = "character",
    ) -> None:
        if mode not in ("character", "boundary"):
            raise ExperimentError(f"未知BGC+BGW评分模式: {mode}")
        self.bgc_database = RimeGramDatabase(bgc_path)
        self.bgw_database = RimeGramDatabase(bgw_path)
        self.bgc = RimeGrammarScorer(self.bgc_database, 2, 2)
        self.bgw = RimeGrammarScorer(self.bgw_database, 4, 3)
        self.bgc_weight = bgc_weight
        self.bgw_weight = bgw_weight
        self.mode = mode

    def close(self) -> None:
        self.bgc_database.close()
        self.bgw_database.close()

    def score_candidate(self, context: str, word: str) -> float:
        context_limit = context[-3:]
        word_limit = word if self.mode == "character" else word[:3]
        return self._score_candidate_cached(context_limit, word_limit)

    @lru_cache(maxsize=1000000)
    def _score_candidate_cached(self, context: str, word: str) -> float:
        if self.mode == "character":
            score = 0.0
            running_context = context
            for character in word:
                score += self._score_transition(running_context, character)
                running_context = (running_context + character)[-3:]
            return score
        return self._score_transition(context, word)

    @lru_cache(maxsize=1000000)
    def _score_transition(self, context: str, word: str) -> float:
        # Subtract the common non-collocation penalty. It carries no information
        # and otherwise creates a strong bias toward paths with fewer lexicon edges.
        bgc_score = self.bgc.query(context[-1:], word[:1]) - NON_COLLOCATION_PENALTY
        bgw_score = self.bgw.query(context[-3:], word[:3]) - NON_COLLOCATION_PENALTY
        return self.bgc_weight * bgc_score + self.bgw_weight * bgw_score

    @staticmethod
    def score_end(_text: str) -> float:
        # Rime's rear lookup depends on the final lexicon edge. The experimental
        # BeamItem intentionally remains runtime-compatible and does not retain it.
        return 0.0


class TrigramAndBGWModel:
    """Add BGW collocation bonuses to TigerClaw's character trigram score."""

    def __init__(
        self,
        trigram: object,
        bgw_path: Path,
        bgw_weight: float,
    ) -> None:
        self.trigram = trigram
        self.bgw_database = RimeGramDatabase(bgw_path)
        self.bgw = RimeGrammarScorer(self.bgw_database, 4, 3)
        self.bgw_weight = bgw_weight

    def close(self) -> None:
        self.bgw_database.close()

    def score_candidate(self, context: str, word: str) -> float:
        return self._score_candidate_cached(context[-3:], word)

    @lru_cache(maxsize=1000000)
    def _score_candidate_cached(self, context: str, word: str) -> float:
        history = (BOS + BOS + context)[-2:]
        previous2, previous1 = history[0], history[1]
        running_context = context
        score = 0.0
        for character in word:
            score += self.trigram.log_probability(
                previous2, previous1, character
            )
            bgw_score = (
                self.bgw.query(running_context[-3:], character)
                - NON_COLLOCATION_PENALTY
            )
            score += self.bgw_weight * bgw_score
            previous2, previous1 = previous1, character
            running_context = (running_context + character)[-3:]
        return score

    def score_end(self, text: str) -> float:
        return self._score_end_cached(text[-2:])

    @lru_cache(maxsize=1000000)
    def _score_end_cached(self, context: str) -> float:
        history = (BOS + BOS + context)[-2:]
        return self.trigram.log_probability(history[0], history[1], EOS)
