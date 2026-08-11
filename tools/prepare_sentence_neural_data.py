#!/usr/bin/env python3
"""把 brightmart 中文语料清洗并编码为字符语言模型训练分片。"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import sys
import time
import unicodedata
from array import array
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Iterator, List, Mapping, Optional, Sequence, TextIO, Tuple


DEFAULT_CORPUS_ROOT = Path("/mnt/c/Archive/brightmart_nlp_chinese_corpus")
DEFAULT_LEXICON = (
    Path(__file__).resolve().parents[1]
    / "release_arm64"
    / "码表"
    / "B定制-常用"
    / "多多B常用字词.txt"
)
SPECIAL_TOKENS = ("<pad>", "<bos>", "<eos>", "<unk>")
DATASETS = {
    "webtext": ("webtext2019zh/web_text_zh_train.json", ("title", "desc", "content")),
    "news": ("new2016zh/news2016zh_train.json", ("title", "desc", "content")),
    "baike": ("baike2018qa/baike_qa_train.json", ("title", "desc", "answer")),
    "wiki": ("wiki_zh_2019", ("title", "text")),
}
DATASET_SHARES = {"webtext": 0.40, "news": 0.25, "baike": 0.15, "wiki": 0.20}
TAG_RE = re.compile(r"<[^>]{1,1000}>")
URL_RE = re.compile(r"(?:https?://|www\.)\S+", re.IGNORECASE)
TEXT_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff，、；：。！？]+")
HAN_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")
TERMINAL_RE = re.compile(r"[。！？]+")
DELETE_LIGHT_PUNCTUATION = str.maketrans("", "", "，、；：")


class PreparationError(Exception):
    pass


@dataclass
class SplitOutput:
    name: str
    path: Path
    budget: int
    stream: TextIO
    characters: int = 0
    sentences: int = 0
    source_characters: Counter[str] = field(default_factory=Counter)

    @property
    def full(self) -> bool:
        return self.characters >= self.budget

    def write(self, sentence: str, dataset: str, source_remaining: int) -> bool:
        if self.full:
            return False
        remaining = min(self.budget - self.characters, source_remaining)
        if remaining < 1:
            return False
        if len(sentence) > remaining:
            sentence = sentence[:remaining]
        self.stream.write(sentence + "\n")
        self.characters += len(sentence)
        self.sentences += 1
        self.source_characters[dataset] += len(sentence)
        return True


class SeenSentenceHashes:
    def __init__(self) -> None:
        self._values: set[int] = set()

    def add(self, sentence: str) -> bool:
        digest = hashlib.blake2b(sentence.encode("utf-8"), digest_size=8).digest()
        value = int.from_bytes(digest, "little")
        if value in self._values:
            return False
        self._values.add(value)
        return True

    def __len__(self) -> int:
        return len(self._values)


def dataset_paths(root: Path, dataset: str) -> List[Path]:
    relative, _ = DATASETS[dataset]
    if dataset == "wiki":
        paths = sorted(path for path in (root / relative).rglob("wiki_*") if path.is_file())
    else:
        paths = [root / relative]
    if not paths or any(not path.is_file() for path in paths):
        raise PreparationError(f"找不到 {dataset} 语料: {root / relative}")
    return paths


def record_split(dataset: str, record: Mapping[str, object]) -> str:
    identity = next(
        (
            str(record[key])
            for key in ("qid", "news_id", "id", "url", "title")
            if key in record
        ),
        json.dumps(record, ensure_ascii=False, sort_keys=True)[:1000],
    )
    digest = hashlib.blake2b(
        f"{dataset}\0{identity}".encode("utf-8"), digest_size=8
    ).digest()
    bucket = int.from_bytes(digest, "little") % 1000
    if bucket < 900:
        return "train"
    if bucket < 950:
        return "valid"
    return "test"


def clean_sentences(text: str, maximum_field_characters: int) -> Iterator[str]:
    text = unicodedata.normalize("NFKC", html.unescape(text[:maximum_field_characters]))
    text = TAG_RE.sub(" ", text)
    text = URL_RE.sub(" ", text)
    text = text.translate(str.maketrans(",;:!?", "，；：！？"))
    for matched in TEXT_RE.finditer(text):
        chunk = matched.group(0)
        start = 0
        for terminal in TERMINAL_RE.finditer(chunk):
            sentence = chunk[start : terminal.end()].strip("，、；：。！？")
            start = terminal.end()
            if len(HAN_RE.findall(sentence)) >= 2:
                yield sentence
        sentence = chunk[start:].strip("，、；：。！？")
        if len(HAN_RE.findall(sentence)) >= 2:
            yield sentence


def eligible_characters_from_lexicon(path: Path) -> set[str]:
    longest: Dict[str, int] = {}
    with path.open("r", encoding="utf-8-sig") as stream:
        for line in stream:
            if line.startswith("---"):
                continue
            fields = line.rstrip("\r\n").split("\t")
            if len(fields) >= 2 and len(fields[0]) == 1:
                longest[fields[0]] = max(longest.get(fields[0], 0), len(fields[1].strip()))
    return {character for character, length in longest.items() if length >= 2}


def maybe_add_eval_case(
    cases: List[Dict[str, str]],
    sentence: str,
    dataset: str,
    eligible: set[str],
    maximum_cases: int,
) -> None:
    if len(cases) >= maximum_cases:
        return
    per_source_limit = (maximum_cases + len(DATASETS) - 1) // len(DATASETS)
    if sum(case["source"] == dataset for case in cases) >= per_source_limit:
        return
    text = "".join(HAN_RE.findall(sentence))
    if not 6 <= len(text) <= 30 or any(character not in eligible for character in text):
        return
    cases.append({"text": text, "source": dataset})


def encode_file(text_path: Path, binary_path: Path, vocabulary: Mapping[str, int]) -> int:
    bos = vocabulary["<bos>"]
    eos = vocabulary["<eos>"]
    unknown = vocabulary["<unk>"]
    written = 0
    buffer = array("H")
    with text_path.open("r", encoding="utf-8") as source, binary_path.open("wb") as target:
        for line in source:
            sentence = line.rstrip("\r\n")
            buffer.append(bos)
            buffer.extend(vocabulary.get(character, unknown) for character in sentence)
            buffer.append(eos)
            if len(buffer) >= 1_000_000:
                buffer.tofile(target)
                written += len(buffer)
                buffer = array("H")
        if buffer:
            buffer.tofile(target)
            written += len(buffer)
    return written


def allocate(total: int) -> Dict[str, int]:
    result = {name: int(total * share) for name, share in DATASET_SHARES.items()}
    result["webtext"] += total - sum(result.values())
    return result


def prepare(args: argparse.Namespace) -> int:
    started = time.monotonic()
    args.output.mkdir(parents=True, exist_ok=True)
    budgets = {
        "train": allocate(args.train_characters),
        "valid": allocate(args.valid_characters),
        "test": allocate(args.test_characters),
    }
    outputs: Dict[str, SplitOutput] = {}
    for name, total in (
        ("train", args.train_characters),
        ("valid", args.valid_characters),
        ("test", args.test_characters),
    ):
        path = args.output / f"{name}.txt"
        outputs[name] = SplitOutput(name, path, total, path.open("w", encoding="utf-8"))

    seen = SeenSentenceHashes()
    character_counts: Counter[str] = Counter()
    eligible = eligible_characters_from_lexicon(args.lexicon)
    eval_cases: List[Dict[str, str]] = []
    record_counts: Counter[str] = Counter()
    try:
        for dataset, (_, fields) in DATASETS.items():
            dataset_written = Counter()
            for path in dataset_paths(args.corpus_root, dataset):
                with path.open("r", encoding="utf-8") as stream:
                    for line in stream:
                        if all(
                            dataset_written[split] >= budgets[split][dataset]
                            for split in outputs
                        ):
                            break
                        try:
                            record = json.loads(line)
                        except json.JSONDecodeError:
                            continue
                        if not isinstance(record, dict):
                            continue
                        split = record_split(dataset, record)
                        if dataset_written[split] >= budgets[split][dataset]:
                            continue
                        record_counts[dataset] += 1
                        field_seen = set()
                        for field in fields:
                            value = record.get(field)
                            if not isinstance(value, str) or value in field_seen:
                                continue
                            field_seen.add(value)
                            for sentence in clean_sentences(value, args.max_field_characters):
                                if not seen.add(sentence):
                                    continue
                                output = outputs[split]
                                if dataset_written[split] >= budgets[split][dataset]:
                                    break
                                before = output.characters
                                source_remaining = (
                                    budgets[split][dataset] - dataset_written[split]
                                )
                                if output.write(sentence, dataset, source_remaining):
                                    added = output.characters - before
                                    dataset_written[split] += added
                                    if split == "train":
                                        character_counts.update(sentence)
                                    elif split == "test":
                                        maybe_add_eval_case(
                                            eval_cases,
                                            sentence,
                                            dataset,
                                            eligible,
                                            args.eval_cases,
                                        )
                                augmentation_hash = hashlib.blake2b(
                                    sentence.encode("utf-8"), digest_size=1
                                ).digest()[0]
                                if split == "train" and augmentation_hash % 5 == 0:
                                    augmented = sentence.translate(DELETE_LIGHT_PUNCTUATION)
                                    if augmented != sentence and seen.add(augmented):
                                        before = output.characters
                                        source_remaining = (
                                            budgets[split][dataset] - dataset_written[split]
                                        )
                                        if output.write(
                                            augmented, dataset, source_remaining
                                        ):
                                            added = output.characters - before
                                            dataset_written[split] += added
                                            character_counts.update(augmented)
                if all(
                    dataset_written[split] >= budgets[split][dataset]
                    for split in outputs
                ):
                    break
            print(
                f"{dataset:7s} "
                + " ".join(f"{name}={dataset_written[name]:,}" for name in outputs),
                flush=True,
            )
    finally:
        for output in outputs.values():
            output.stream.close()

    vocabulary_characters = [
        character
        for character, _ in character_counts.most_common(
            args.vocabulary_size - len(SPECIAL_TOKENS)
        )
    ]
    vocabulary = {token: index for index, token in enumerate(SPECIAL_TOKENS)}
    vocabulary.update(
        (character, index + len(SPECIAL_TOKENS))
        for index, character in enumerate(vocabulary_characters)
    )
    (args.output / "vocabulary.json").write_text(
        json.dumps(vocabulary, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    token_counts = {}
    for name, output in outputs.items():
        token_counts[name] = encode_file(
            output.path, args.output / f"{name}.bin", vocabulary
        )
    with (args.output / "eval_cases.jsonl").open("w", encoding="utf-8") as stream:
        for case in eval_cases:
            stream.write(json.dumps(case, ensure_ascii=False) + "\n")

    metadata = {
        "version": 1,
        "corpus_root": str(args.corpus_root.resolve()),
        "vocabulary_size": len(vocabulary),
        "special_tokens": list(SPECIAL_TOKENS),
        "split_characters": {name: output.characters for name, output in outputs.items()},
        "split_sentences": {name: output.sentences for name, output in outputs.items()},
        "source_characters": {
            name: dict(output.source_characters) for name, output in outputs.items()
        },
        "token_counts": token_counts,
        "unique_sentence_hashes": len(seen),
        "record_counts": dict(record_counts),
        "eval_cases": len(eval_cases),
        "elapsed_seconds": time.monotonic() - started,
    }
    (args.output / "metadata.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(metadata, ensure_ascii=False, indent=2))
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus-root", type=Path, default=DEFAULT_CORPUS_ROOT)
    parser.add_argument("--lexicon", type=Path, default=DEFAULT_LEXICON)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--train-characters", type=int, default=200_000_000)
    parser.add_argument("--valid-characters", type=int, default=2_000_000)
    parser.add_argument("--test-characters", type=int, default=2_000_000)
    parser.add_argument("--vocabulary-size", type=int, default=8192)
    parser.add_argument("--eval-cases", type=int, default=2000)
    parser.add_argument("--max-field-characters", type=int, default=5000)
    args = parser.parse_args(argv)
    if min(
        args.train_characters,
        args.valid_characters,
        args.test_characters,
        args.eval_cases,
        args.max_field_characters,
    ) < 1:
        parser.error("字符数、评测数和字段长度必须大于 0")
    if not 256 <= args.vocabulary_size <= 65535:
        parser.error("--vocabulary-size 必须在 256 到 65535 之间")
    return args


def main(argv: Optional[Sequence[str]] = None) -> int:
    try:
        return prepare(parse_arguments(argv))
    except (PreparationError, OSError, UnicodeDecodeError) as ex:
        print(f"错误: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
