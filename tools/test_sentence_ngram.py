#!/usr/bin/env python3
"""用本地中文语料训练字符 n-gram，并测试二码整句候选。

这是离线实验工具，不接入 TigerClaw 运行时，也不需要网络或第三方依赖。
训练时按行读取 brightmart/nlp_chinese_corpus 的 JSONL 文件；解码时每两个
编码只允许选择码表中对应的单字，再用字符三元模型进行 Beam Search。

示例：
  python3 tools/test_sentence_ngram.py train --output /tmp/tigerclaw-ngram.json.gz
  python3 tools/test_sentence_ngram.py decode --model /tmp/tigerclaw-ngram.json.gz
"""

from __future__ import annotations

import argparse
import gzip
import html
import json
import math
import re
import sys
import time
import unicodedata
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, Iterator, List, Mapping, Optional, Sequence, Tuple


DEFAULT_TEXT = "今天早上我吃了两个面包三根油条"
DEFAULT_CORPUS_ROOT = Path("/mnt/c/Archive/brightmart_nlp_chinese_corpus")
DEFAULT_LEXICON = (
    Path(__file__).resolve().parents[1]
    / "release_arm64"
    / "码表"
    / "B定制-常用"
    / "多多B常用字词.txt"
)

BOS = "\x02"
EOS = "\x03"
MODEL_VERSION = 1
DATASET_FIELDS = {
    "baike": ("title", "desc", "answer"),
    "news": ("title", "desc", "content"),
    "webtext": ("title", "desc", "content"),
    "wiki": ("title", "text"),
}
DEFAULT_DATASET_WEIGHTS = {
    "baike": 1,
    "news": 1,
    "webtext": 2,
    "wiki": 1,
}

TAG_RE = re.compile(r"<[^>]{1,1000}>")
URL_RE = re.compile(r"(?:https?://|www\.)\S+", re.IGNORECASE)
HAN_SEQUENCE_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]+")


class ExperimentError(Exception):
    """可直接展示给命令行用户的实验错误。"""


@dataclass(frozen=True)
class PrimaryCode:
    code: str
    priority_order: int


@dataclass(frozen=True)
class LexiconIndex:
    primary_code_by_char: Mapping[str, PrimaryCode]
    candidates_by_prefix: Mapping[str, Tuple[str, ...]]


@dataclass
class CorpusStats:
    records: int = 0
    invalid_records: int = 0
    fields: int = 0
    sequences: int = 0
    characters: int = 0


@dataclass(frozen=True)
class BeamItem:
    score: float
    text: str
    previous2: str
    previous1: str


@dataclass(frozen=True)
class WordToken:
    text: str
    frequency: int


@dataclass(frozen=True)
class WordSegmentation:
    score: float
    tokens: Tuple[WordToken, ...]


@dataclass(frozen=True)
class RankedItem:
    combined_score: float
    beam_item: BeamItem
    word_segmentation: WordSegmentation


def parse_lexicon(path: Path) -> LexiconIndex:
    if not path.is_file():
        raise ExperimentError(f"码表文件不存在: {path}")

    primary: Dict[str, PrimaryCode] = {}
    grouped: Dict[str, List[Tuple[int, str]]] = defaultdict(list)
    try:
        with path.open("r", encoding="utf-8-sig") as stream:
            for source_order, raw_line in enumerate(stream):
                line = raw_line.rstrip("\r\n")
                if not line or line.startswith("---"):
                    continue
                fields = line.split("\t")
                if len(fields) < 2:
                    continue
                text = fields[0]
                code = fields[1].strip().lower()
                if len(text) != 1 or not code:
                    continue

                previous = primary.get(text)
                if previous is None:
                    primary[text] = PrimaryCode(code, source_order)
                elif len(code) > len(previous.code):
                    primary[text] = PrimaryCode(code, previous.priority_order)
    except (OSError, UnicodeDecodeError) as ex:
        raise ExperimentError(f"读取码表失败: {ex}") from ex

    for character, entry in primary.items():
        if len(entry.code) >= 2:
            grouped[entry.code[:2]].append((entry.priority_order, character))

    candidates: Dict[str, Tuple[str, ...]] = {}
    for prefix, values in grouped.items():
        values.sort(key=lambda value: value[0])
        candidates[prefix] = tuple(character for _, character in values)
    if not candidates:
        raise ExperimentError("码表中没有可用的二码单字。")
    return LexiconIndex(primary, candidates)


def encode_text(text: str, index: LexiconIndex) -> Tuple[str, ...]:
    codes: List[str] = []
    for position, character in enumerate(text, 1):
        entry = index.primary_code_by_char.get(character)
        if entry is None or len(entry.code) < 2:
            raise ExperimentError(f"第 {position} 个字“{character}”没有可用的二码。")
        codes.append(entry.code[:2])
    if not codes:
        raise ExperimentError("测试句不能为空。")
    return tuple(codes)


def split_code(raw_code: str) -> Tuple[str, ...]:
    code = "".join(raw_code.split()).lower()
    if not code or len(code) % 2:
        raise ExperimentError("整句编码必须是非空的偶数长度编码串。")
    return tuple(code[offset : offset + 2] for offset in range(0, len(code), 2))


def resolve_candidates(
    codes: Sequence[str], index: LexiconIndex
) -> Tuple[Tuple[str, ...], ...]:
    result: List[Tuple[str, ...]] = []
    for position, code in enumerate(codes, 1):
        candidates = index.candidates_by_prefix.get(code)
        if not candidates:
            raise ExperimentError(f"第 {position} 组二码“{code}”没有单字候选。")
        result.append(candidates)
    return tuple(result)


def dataset_paths(root: Path, dataset: str) -> List[Path]:
    if dataset == "baike":
        patterns = ("baike2018qa/baike_qa_train.json",)
    elif dataset == "news":
        patterns = ("new2016zh/news2016zh_train.json",)
    elif dataset == "webtext":
        patterns = ("webtext2019zh/web_text_zh_train.json",)
    elif dataset == "wiki":
        paths = sorted(
            path
            for path in (root / "wiki_zh_2019").rglob("wiki_*")
            if path.is_file()
        )
        if not paths:
            raise ExperimentError(f"找不到 {dataset} 语料文件: {root}")
        return paths
    else:
        raise ExperimentError(f"未知数据集: {dataset}")

    paths = [root / pattern for pattern in patterns]
    missing = [path for path in paths if not path.is_file()]
    if missing:
        raise ExperimentError(f"找不到 {dataset} 语料文件: {missing[0]}")
    return paths


def normalized_han_sequences(text: str, max_field_chars: int) -> Iterator[str]:
    text = unicodedata.normalize("NFKC", html.unescape(text[:max_field_chars]))
    text = TAG_RE.sub(" ", text)
    text = URL_RE.sub(" ", text)
    for match in HAN_SEQUENCE_RE.finditer(text):
        sequence = match.group(0)
        if len(sequence) >= 2:
            yield sequence


def iter_dataset_sequences(
    root: Path,
    dataset: str,
    max_records: int,
    max_field_chars: int,
    stats: CorpusStats,
) -> Iterator[str]:
    fields = DATASET_FIELDS[dataset]
    for path in dataset_paths(root, dataset):
        try:
            stream = path.open("r", encoding="utf-8")
        except (OSError, UnicodeDecodeError) as ex:
            raise ExperimentError(f"读取语料失败 {path}: {ex}") from ex
        with stream:
            for line in stream:
                if max_records and stats.records >= max_records:
                    return
                try:
                    record = json.loads(line)
                except (json.JSONDecodeError, UnicodeDecodeError):
                    stats.invalid_records += 1
                    continue
                if not isinstance(record, dict):
                    stats.invalid_records += 1
                    continue
                stats.records += 1
                seen_fields = set()
                for field in fields:
                    value = record.get(field)
                    if not isinstance(value, str) or not value.strip():
                        continue
                    normalized = value.strip()
                    if normalized in seen_fields:
                        continue
                    seen_fields.add(normalized)
                    stats.fields += 1
                    for sequence in normalized_han_sequences(value, max_field_chars):
                        stats.sequences += 1
                        stats.characters += len(sequence)
                        yield sequence


def update_counts(
    sequence: str,
    weight: int,
    unigrams: Counter[str],
    bigrams: Counter[str],
    trigrams: Counter[str],
) -> None:
    previous2 = BOS
    previous1 = BOS
    for target in sequence + EOS:
        unigrams[target] += weight
        bigrams[previous1 + target] += weight
        trigrams[previous2 + previous1 + target] += weight
        previous2, previous1 = previous1, target


def pruned_counts(counts: Counter[str], minimum: int) -> Dict[str, int]:
    return {key: count for key, count in counts.items() if count >= minimum}


def train_model(args: argparse.Namespace) -> int:
    root = args.corpus_root.resolve()
    if not root.is_dir():
        raise ExperimentError(f"语料目录不存在: {root}")

    weights = dict(DEFAULT_DATASET_WEIGHTS)
    for specification in args.weight:
        try:
            dataset, raw_weight = specification.split("=", 1)
            weight = int(raw_weight)
        except ValueError as ex:
            raise ExperimentError(f"无效权重“{specification}”，应为 DATASET=N。") from ex
        if dataset not in DATASET_FIELDS or weight <= 0:
            raise ExperimentError(f"无效权重“{specification}”。")
        weights[dataset] = weight

    unigrams: Counter[str] = Counter()
    bigrams: Counter[str] = Counter()
    trigrams: Counter[str] = Counter()
    all_stats: Dict[str, CorpusStats] = {}
    started = time.monotonic()
    for dataset in args.datasets:
        stats = CorpusStats()
        all_stats[dataset] = stats
        for sequence in iter_dataset_sequences(
            root,
            dataset,
            args.max_records_per_dataset,
            args.max_field_chars,
            stats,
        ):
            update_counts(sequence, weights[dataset], unigrams, bigrams, trigrams)
        print(
            f"{dataset:7s} 记录={stats.records:,} 字段={stats.fields:,} "
            f"片段={stats.sequences:,} 汉字={stats.characters:,} "
            f"无效JSON={stats.invalid_records:,} 权重={weights[dataset]}",
            flush=True,
        )

    print("正在裁剪并写入模型……", flush=True)
    model = {
        "version": MODEL_VERSION,
        "metadata": {
            "corpus_root": str(root),
            "datasets": list(args.datasets),
            "weights": {name: weights[name] for name in args.datasets},
            "max_records_per_dataset": args.max_records_per_dataset,
            "max_field_chars": args.max_field_chars,
            "minimum_bigram_count": args.min_bigram_count,
            "minimum_trigram_count": args.min_trigram_count,
            "records": {name: stats.records for name, stats in all_stats.items()},
            "sequences": {name: stats.sequences for name, stats in all_stats.items()},
            "characters": {name: stats.characters for name, stats in all_stats.items()},
        },
        "unigrams": dict(unigrams),
        "bigrams": pruned_counts(bigrams, args.min_bigram_count),
        "trigrams": pruned_counts(trigrams, args.min_trigram_count),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        with gzip.open(args.output, "wt", encoding="utf-8", compresslevel=3) as stream:
            json.dump(model, stream, ensure_ascii=False, separators=(",", ":"))
    except OSError as ex:
        raise ExperimentError(f"写入模型失败: {ex}") from ex

    elapsed = time.monotonic() - started
    print(
        f"模型: {args.output.resolve()}\n"
        f"unigram={len(model['unigrams']):,} "
        f"bigram={len(model['bigrams']):,} "
        f"trigram={len(model['trigrams']):,}\n"
        f"文件大小={args.output.stat().st_size / 1024 / 1024:.1f} MiB "
        f"耗时={elapsed:.1f} 秒"
    )
    return 0


class CharacterLanguageModel:
    def __init__(self, payload: Mapping[str, object]) -> None:
        if payload.get("version") != MODEL_VERSION:
            raise ExperimentError("不支持的模型版本。")
        self.metadata = payload.get("metadata", {})
        self.unigrams = self._read_counts(payload, "unigrams")
        self.bigrams = self._read_counts(payload, "bigrams")
        self.trigrams = self._read_counts(payload, "trigrams")
        self.total = sum(self.unigrams.values())
        self.vocabulary_size = max(len(self.unigrams), 1)
        self.bigram_contexts: Counter[str] = Counter()
        self.trigram_contexts: Counter[str] = Counter()
        for key, count in self.bigrams.items():
            self.bigram_contexts[key[0]] += count
        for key, count in self.trigrams.items():
            self.trigram_contexts[key[:2]] += count

    @staticmethod
    def _read_counts(payload: Mapping[str, object], name: str) -> Dict[str, int]:
        raw = payload.get(name)
        if not isinstance(raw, dict):
            raise ExperimentError(f"模型缺少 {name} 计数。")
        return {str(key): int(value) for key, value in raw.items()}

    @classmethod
    def load(cls, path: Path) -> "CharacterLanguageModel":
        if not path.is_file():
            raise ExperimentError(f"模型文件不存在: {path}")
        try:
            with gzip.open(path, "rt", encoding="utf-8") as stream:
                payload = json.load(stream)
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as ex:
            raise ExperimentError(f"读取模型失败: {ex}") from ex
        if not isinstance(payload, dict):
            raise ExperimentError("模型根节点不是 JSON 对象。")
        return cls(payload)

    def log_probability(self, previous2: str, previous1: str, target: str) -> float:
        unigram_probability = (self.unigrams.get(target, 0) + 0.1) / (
            self.total + 0.1 * self.vocabulary_size
        )
        bigram_key = previous1 + target
        bigram_probability = (
            self.bigrams.get(bigram_key, 0) + 5.0 * unigram_probability
        ) / (self.bigram_contexts.get(previous1, 0) + 5.0)
        trigram_key = previous2 + previous1 + target
        trigram_probability = (
            self.trigrams.get(trigram_key, 0) + 5.0 * bigram_probability
        ) / (self.trigram_contexts.get(previous2 + previous1, 0) + 5.0)
        return math.log(max(trigram_probability, 1e-300))


class WordFrequencyModel:
    def __init__(self, frequencies: Mapping[str, int], maximum_word_length: int) -> None:
        self.frequencies = frequencies
        self.maximum_word_length = maximum_word_length

    @classmethod
    def load(
        cls,
        path: Path,
        minimum_frequency: int,
        maximum_word_length: int,
    ) -> "WordFrequencyModel":
        if not path.is_file():
            raise ExperimentError(f"大词频文件不存在: {path}")
        frequencies: Dict[str, int] = {}
        try:
            with path.open("r", encoding="utf-8-sig") as stream:
                for line_number, line in enumerate(stream, 1):
                    fields = line.rstrip("\r\n").split("\t")
                    if len(fields) < 2:
                        raise ExperimentError(
                            f"大词频第 {line_number} 行不是制表符分隔格式。"
                        )
                    word = fields[0]
                    try:
                        frequency = int(fields[1])
                    except ValueError as ex:
                        raise ExperimentError(
                            f"大词频第 {line_number} 行频率不是整数。"
                        ) from ex
                    if frequency < minimum_frequency:
                        continue
                    if not 2 <= len(word) <= maximum_word_length:
                        continue
                    if HAN_SEQUENCE_RE.fullmatch(word) is None:
                        continue
                    frequencies[word] = frequency
        except (OSError, UnicodeDecodeError) as ex:
            raise ExperimentError(f"读取大词频失败: {ex}") from ex
        if not frequencies:
            raise ExperimentError("大词频中没有符合筛选条件的词条。")
        return cls(frequencies, maximum_word_length)

    def best_segmentation(self, text: str) -> WordSegmentation:
        scores = [float("-inf")] * (len(text) + 1)
        previous: List[Optional[Tuple[int, WordToken]]] = [None] * (len(text) + 1)
        scores[0] = 0.0
        for start in range(len(text)):
            if not math.isfinite(scores[start]):
                continue

            fallback_end = start + 1
            if scores[start] > scores[fallback_end]:
                scores[fallback_end] = scores[start]
                previous[fallback_end] = (start, WordToken(text[start], 0))

            final_end = min(len(text), start + self.maximum_word_length)
            for end in range(start + 2, final_end + 1):
                word = text[start:end]
                frequency = self.frequencies.get(word)
                if frequency is None:
                    continue
                candidate_score = scores[start] + math.log1p(frequency)
                if candidate_score > scores[end]:
                    scores[end] = candidate_score
                    previous[end] = (start, WordToken(word, frequency))

        tokens: List[WordToken] = []
        position = len(text)
        while position:
            step = previous[position]
            if step is None:
                raise ExperimentError("词频动态切分失败。")
            position, token = step
            tokens.append(token)
        tokens.reverse()
        return WordSegmentation(scores[len(text)], tuple(tokens))


def rerank_with_words(
    beam: Sequence[BeamItem],
    word_model: WordFrequencyModel,
    word_weight: float,
) -> List[RankedItem]:
    ranked = []
    for item in beam:
        segmentation = word_model.best_segmentation(item.text)
        ranked.append(
            RankedItem(
                item.score + word_weight * segmentation.score,
                item,
                segmentation,
            )
        )
    ranked.sort(key=lambda item: item.combined_score, reverse=True)
    return ranked


def decode_sentence(
    model: CharacterLanguageModel,
    candidate_sets: Sequence[Sequence[str]],
    beam_width: int,
    rank_penalty: float,
) -> List[BeamItem]:
    beam = [BeamItem(0.0, "", BOS, BOS)]
    for candidates in candidate_sets:
        expanded: List[BeamItem] = []
        for item in beam:
            for candidate_rank, character in enumerate(candidates):
                score = item.score + model.log_probability(
                    item.previous2, item.previous1, character
                )
                score -= rank_penalty * math.log1p(candidate_rank)
                expanded.append(
                    BeamItem(score, item.text + character, item.previous1, character)
                )
        expanded.sort(key=lambda item: item.score, reverse=True)
        beam = expanded[:beam_width]

    completed = [
        BeamItem(
            item.score
            + model.log_probability(item.previous2, item.previous1, EOS),
            item.text,
            item.previous2,
            item.previous1,
        )
        for item in beam
    ]
    completed.sort(key=lambda item: item.score, reverse=True)
    return completed


def decode_model(args: argparse.Namespace) -> int:
    started = time.monotonic()
    model = CharacterLanguageModel.load(args.model)
    model_loaded = time.monotonic()
    word_model: Optional[WordFrequencyModel] = None
    if args.word_frequency is not None:
        word_model = WordFrequencyModel.load(
            args.word_frequency,
            args.min_word_frequency,
            args.max_word_length,
        )
    word_model_loaded = time.monotonic()
    index = parse_lexicon(args.lexicon)
    expected_text: Optional[str]
    if args.code:
        codes = split_code(args.code)
        expected_text = None
    else:
        expected_text = args.text or DEFAULT_TEXT
        codes = encode_text(expected_text, index)
    candidate_sets = resolve_candidates(codes, index)

    print(f"模型: {args.model.resolve()}")
    print(f"码表: {args.lexicon.resolve()}")
    if word_model is not None:
        print(
            f"大词频: {args.word_frequency.resolve()} "
            f"（载入 {len(word_model.frequencies):,} 条）"
        )
    print(f"整句编码: {''.join(codes)}")
    for position, (code, candidates) in enumerate(zip(codes, candidate_sets), 1):
        target_note = ""
        if expected_text is not None:
            target = expected_text[position - 1]
            rank = candidates.index(target) + 1
            target_note = f" 目标={target}/{rank}"
        print(f"{position:02d}. {code} 候选数={len(candidates):2d}{target_note}")

    decode_started = time.monotonic()
    beam = decode_sentence(model, candidate_sets, args.beam_width, args.rank_penalty)
    beam_elapsed = time.monotonic() - decode_started
    rerank_started = time.monotonic()
    ranked: Optional[List[RankedItem]] = None
    if word_model is not None:
        ranked = rerank_with_words(beam, word_model, args.word_weight)
    rerank_elapsed = time.monotonic() - rerank_started

    if ranked is None:
        print(f"\n本地候选（Beam={args.beam_width}）:")
        for rank, item in enumerate(beam[: args.count], 1):
            marker = "  <-- 校验句" if item.text == expected_text else ""
            print(f"{rank:02d}. {item.text}  score={item.score:.4f}{marker}")
    else:
        print(
            f"\n字词联合候选（Beam={args.beam_width}，词权重={args.word_weight:g}）:"
        )
        for rank, item in enumerate(ranked[: args.count], 1):
            marker = "  <-- 校验句" if item.beam_item.text == expected_text else ""
            segmentation = "/".join(
                token.text if token.frequency else f"[{token.text}]"
                for token in item.word_segmentation.tokens
            )
            print(
                f"{rank:02d}. {item.beam_item.text}  combined={item.combined_score:.4f} "
                f"char={item.beam_item.score:.4f} word={item.word_segmentation.score:.4f}"
                f"{marker}\n    {segmentation}"
            )

    if expected_text is not None:
        character_expected_rank = next(
            (rank for rank, item in enumerate(beam, 1) if item.text == expected_text),
            None,
        )
        if ranked is None:
            expected_rank = character_expected_rank
        else:
            expected_rank = next(
                (
                    rank
                    for rank, item in enumerate(ranked, 1)
                    if item.beam_item.text == expected_text
                ),
                None,
            )
        previous2 = BOS
        previous1 = BOS
        score = 0.0
        for character in expected_text:
            score += model.log_probability(previous2, previous1, character)
            previous2, previous1 = previous1, character
        score += model.log_probability(previous2, previous1, EOS)
        word_note = ""
        if word_model is not None:
            expected_segmentation = word_model.best_segmentation(expected_text)
            segmented = "/".join(
                token.text if token.frequency else f"[{token.text}]"
                for token in expected_segmentation.tokens
            )
            word_note = (
                f"，词分={expected_segmentation.score:.4f}"
                f"，切分={segmented}"
            )
        ranking_note = str(expected_rank) if expected_rank is not None else "未保留在 Beam 中"
        if ranked is not None:
            character_ranking_note = (
                str(character_expected_rank)
                if character_expected_rank is not None
                else "未保留在 Beam 中"
            )
            ranking_note = f"字符={character_ranking_note}，联合={ranking_note}"
        print(
            f"\n校验句排名: {ranking_note}"
            f"，纯语言模型分数={score:.4f}{word_note}"
        )
    print(
        f"模型加载耗时: {model_loaded - started:.3f} 秒，"
        f"词频加载耗时: {word_model_loaded - model_loaded:.3f} 秒，"
        f"Beam Search 耗时: {beam_elapsed:.3f} 秒，"
        f"词级重排耗时: {rerank_elapsed:.3f} 秒，"
        f"总耗时: {time.monotonic() - started:.3f} 秒"
    )
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    train = subparsers.add_parser("train", help="从本地语料训练抽样 n-gram 模型")
    train.add_argument("--corpus-root", type=Path, default=DEFAULT_CORPUS_ROOT)
    train.add_argument("--output", type=Path, required=True)
    train.add_argument(
        "--datasets",
        nargs="+",
        choices=tuple(DATASET_FIELDS),
        default=tuple(DATASET_FIELDS),
    )
    train.add_argument("--max-records-per-dataset", type=int, default=5000)
    train.add_argument("--max-field-chars", type=int, default=1000)
    train.add_argument("--min-bigram-count", type=int, default=2)
    train.add_argument("--min-trigram-count", type=int, default=2)
    train.add_argument(
        "--weight",
        action="append",
        default=[],
        metavar="DATASET=N",
        help="覆盖语料整数权重，可重复指定；默认 webtext=2，其余=1",
    )
    train.set_defaults(handler=train_model)

    decode = subparsers.add_parser("decode", help="用本地模型执行二码 Beam Search")
    decode.add_argument("--model", type=Path, required=True)
    decode.add_argument("--lexicon", type=Path, default=DEFAULT_LEXICON)
    source = decode.add_mutually_exclusive_group()
    source.add_argument("--text", help="测试句；自动从码表反查整句编码")
    source.add_argument("--code", help="直接提供偶数长度的整句编码")
    decode.add_argument("--count", type=int, default=10)
    decode.add_argument("--beam-width", type=int, default=512)
    decode.add_argument("--rank-penalty", type=float, default=0.03)
    decode.add_argument("--word-frequency", type=Path, help="可选的大词频文件")
    decode.add_argument("--min-word-frequency", type=int, default=300)
    decode.add_argument("--max-word-length", type=int, default=6)
    decode.add_argument("--word-weight", type=float, default=1.4)
    decode.set_defaults(handler=decode_model)

    args = parser.parse_args(argv)
    if args.command == "train":
        if args.max_records_per_dataset < 1:
            parser.error("--max-records-per-dataset 必须大于 0")
        if args.max_field_chars < 1:
            parser.error("--max-field-chars 必须大于 0")
        if args.min_bigram_count < 1 or args.min_trigram_count < 1:
            parser.error("n-gram 最小频次必须大于 0")
    else:
        if args.count < 1 or args.beam_width < args.count:
            parser.error("--count 必须大于 0，且 --beam-width 不能小于 --count")
        if args.rank_penalty < 0:
            parser.error("--rank-penalty 不能小于 0")
        if args.min_word_frequency < 1:
            parser.error("--min-word-frequency 必须大于 0")
        if args.max_word_length < 2:
            parser.error("--max-word-length 必须至少为 2")
        if args.word_weight < 0:
            parser.error("--word-weight 不能小于 0")
    return args


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_arguments(argv)
    try:
        return args.handler(args)
    except ExperimentError as ex:
        print(f"错误: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
