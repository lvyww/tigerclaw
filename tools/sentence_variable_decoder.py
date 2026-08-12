#!/usr/bin/env python3
"""最优单字码及词语可变长度整句实验使用的词图解码器。"""

from __future__ import annotations

import heapq
import math
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Mapping, Sequence, Tuple

try:
    from test_sentence_ngram import BOS, EOS, BeamItem, ExperimentError
except ModuleNotFoundError:  # 支持从仓库根目录按 tools.* 导入
    from .test_sentence_ngram import BOS, EOS, BeamItem, ExperimentError


@dataclass(frozen=True)
class ShortestCodeIndex:
    primary_code_by_char: Mapping[str, str]
    standalone_code_by_char: Mapping[str, str]
    candidates_by_code: Mapping[str, Tuple["LexiconCandidate", ...]]
    code_lengths: Tuple[int, ...]


@dataclass(frozen=True)
class LexiconCandidate:
    text: str
    rank: int


@dataclass(frozen=True)
class LatticeDecodeResult:
    beam: Tuple[BeamItem, ...]
    expanded_states: int
    reachable_positions: int


def parse_shortest_code_index(path: Path) -> ShortestCodeIndex:
    """读取单字最优码和词语候选。

    单字优先选择最短的首选编码（无需选重）；没有首选编码时，选择最短编码。
    只有整个输入本身只有一码时才允许一码切分；其他情况下，每个切分连同
    选重标记在内至少占用二码，所以“一码字母+选重键”是合法切分。
    非首选单字的自动编码附加选重标记：分号表示第二候选，数字表示其余
    候选位。词语保留码表中的全部显式编码，但同样只有首选能由裸编码命中，
    其他候选必须显式选重。
    """

    if not path.is_file():
        raise ExperimentError(f"码表文件不存在: {path}")
    rows: List[Tuple[int, str, str, int]] = []
    try:
        with path.open("r", encoding="utf-8-sig") as stream:
            for source_order, raw_line in enumerate(stream):
                line = raw_line.strip()
                if not line or line.startswith(("---", "#")):
                    continue
                fields = line.split()
                if len(fields) < 2:
                    continue
                text = fields[0]
                code = fields[1].lower()
                frequency = 0
                if len(fields) >= 3:
                    try:
                        frequency = int(fields[2])
                    except ValueError:
                        pass
                if text and code:
                    rows.append((source_order, text, code, frequency))
    except (OSError, UnicodeDecodeError) as ex:
        raise ExperimentError(f"读取码表失败: {ex}") from ex

    # Python 的排序是稳定的，与 Core 的 OrderByDescending 后保留原始顺序一致。
    exact_candidates: Dict[str, List[str]] = defaultdict(list)
    seen_by_code: Dict[str, set[str]] = defaultdict(set)
    for _, text, code, _ in sorted(rows, key=lambda row: row[3], reverse=True):
        if text in seen_by_code[code]:
            continue
        seen_by_code[code].add(text)
        exact_candidates[code].append(text)

    codes_by_character: Dict[str, List[Tuple[int, str]]] = defaultdict(list)
    for source_order, text, code, _ in rows:
        if len(text) == 1 and not any(
            existing_code == code for _, existing_code in codes_by_character[text]
        ):
            codes_by_character[text].append((source_order, code))

    def selection_marker(rank: int) -> str:
        if rank <= 1:
            return ""
        if rank == 2:
            return ";"
        if rank == 3:
            return "'"
        if rank == 10:
            return "0"
        return str(rank)

    def choose_code(
        character: str,
        values: Sequence[Tuple[int, str]],
        minimum_length: int,
    ) -> str | None:
        eligible = [value for value in values if len(value[1]) >= minimum_length]
        if not eligible:
            return None
        first_choice = [
            value
            for value in eligible
            if exact_candidates[value[1]][0] == character
        ]
        pool = first_choice or eligible
        _, code = min(pool, key=lambda value: (len(value[1]), value[0], value[1]))
        rank = exact_candidates[code].index(character) + 1
        return code + selection_marker(rank)

    primary: Dict[str, str] = {}
    standalone: Dict[str, str] = {}
    for character, values in codes_by_character.items():
        sentence_code = choose_code(character, values, 2)
        standalone_code = choose_code(character, values, 1)
        if sentence_code is not None:
            primary[character] = sentence_code
        if standalone_code is not None:
            standalone[character] = standalone_code

    primary_base_codes = {
        character: "".join(ch for ch in input_code if ch.isalpha())
        for character, input_code in primary.items()
    }
    candidates: Dict[str, Tuple[LexiconCandidate, ...]] = {}
    for code, values in exact_candidates.items():
        allowed: List[LexiconCandidate] = []
        for rank, text in enumerate(values, 1):
            if (
                len(code) == 1
                or len(text) > 1
                or primary_base_codes.get(text) == code
            ):
                allowed.append(LexiconCandidate(text, rank))
        if allowed:
            candidates[code] = tuple(allowed)
    if not candidates:
        raise ExperimentError("码表中没有可用的单字或词语编码。")
    return ShortestCodeIndex(
        primary_code_by_char=primary,
        standalone_code_by_char=standalone,
        candidates_by_code=candidates,
        code_lengths=tuple(sorted({len(code) for code in candidates})),
    )


def encode_shortest_text(text: str, index: ShortestCodeIndex) -> str:
    mapping = (
        index.standalone_code_by_char
        if len(text) == 1
        else index.primary_code_by_char
    )
    codes = []
    for position, character in enumerate(text, 1):
        code = mapping.get(character)
        if code is None:
            raise ExperimentError(
                f"第 {position} 个字“{character}”没有符合规则的单字编码。"
            )
        codes.append(code)
    if not codes:
        raise ExperimentError("测试句不能为空。")
    return "".join(codes)


def decode_code_lattice(
    raw_code: str,
    index: ShortestCodeIndex,
    language_model: object,
    beam_width: int,
    rank_penalty: float,
) -> LatticeDecodeResult:
    """在编码位置构成的 DAG 上执行字符语言模型 Beam Search。"""

    if not raw_code:
        raise ExperimentError("编码不能为空。")
    letter_count = sum(character.isalpha() for character in raw_code)
    if letter_count == 0:
        raise ExperimentError("编码中没有字母。")
    code_length = len(raw_code)
    states: List[List[BeamItem]] = [[] for _ in range(code_length + 1)]
    states[0].append(BeamItem(0.0, "", BOS, BOS))
    expanded_states = 0
    reachable_positions = 0

    for position in range(code_length):
        values = states[position]
        if not values:
            continue
        deduplicated: Dict[str, BeamItem] = {}
        for item in values:
            previous = deduplicated.get(item.text)
            if previous is None or item.score > previous.score:
                deduplicated[item.text] = item
        values = list(deduplicated.values())
        reachable_positions += 1
        if len(values) > beam_width:
            beam = heapq.nlargest(beam_width, values, key=lambda item: item.score)
        else:
            beam = sorted(values, key=lambda item: item.score, reverse=True)
        states[position] = []

        for length in index.code_lengths:
            final_position = position + length
            if final_position > code_length:
                continue
            code = raw_code[position:final_position]
            candidates = index.candidates_by_code.get(code)
            if candidates is None:
                continue
            consumed_position = final_position
            selected_rank = 0
            if final_position < code_length and raw_code[final_position] == ";":
                selected_rank = 2
                consumed_position += 1
            elif final_position < code_length and raw_code[final_position] == "'":
                selected_rank = 3
                consumed_position += 1
            elif final_position < code_length and raw_code[final_position].isdigit():
                digit_end = final_position
                while digit_end < code_length and raw_code[digit_end].isdigit():
                    digit_end += 1
                token = raw_code[final_position:digit_end]
                selected_rank = 10 if token == "0" else int(token)
                consumed_position = digit_end
            if code_length > 1 and consumed_position - position < 2:
                continue
            # 每个裸编码段只能取该码位的首选。语言模型可以选择不同切分，
            # 但不能在没有分号、单引号或数字时自行选用同码的第二候选及以后。
            required_rank = selected_rank or 1
            candidates = tuple(
                candidate for candidate in candidates if candidate.rank == required_rank
            )
            if not candidates:
                continue
            destination = states[consumed_position]
            for item in beam:
                for candidate in candidates:
                    score = item.score
                    previous2 = item.previous2
                    previous1 = item.previous1
                    for character in candidate.text:
                        score += language_model.log_probability(
                            previous2, previous1, character
                        )
                        previous2, previous1 = previous1, character
                    if not selected_rank:
                        score -= rank_penalty * math.log1p(candidate.rank - 1)
                    destination.append(
                        BeamItem(
                            score,
                            item.text + candidate.text,
                            previous2,
                            previous1,
                        )
                    )
                    expanded_states += 1

    completed_by_text: Dict[str, BeamItem] = {}
    for item in states[code_length]:
        previous = completed_by_text.get(item.text)
        if previous is None or item.score > previous.score:
            completed_by_text[item.text] = item
    completed = [
        BeamItem(
            item.score
            + language_model.log_probability(item.previous2, item.previous1, EOS),
            item.text,
            item.previous2,
            item.previous1,
        )
        for item in completed_by_text.values()
    ]
    if not completed:
        raise ExperimentError("当前编码还不能完整切分为最优单字码或词语编码。")
    completed.sort(key=lambda item: item.score, reverse=True)
    return LatticeDecodeResult(
        beam=tuple(completed[:beam_width]),
        expanded_states=expanded_states,
        reachable_positions=reachable_positions,
    )
