#!/usr/bin/env python3
"""Build balanced variable-code cases outside the trigram training records."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Dict, Iterable, Iterator, List, Optional, Sequence, Set, Tuple

from prepare_sentence_neural_data import clean_sentences
from sentence_variable_decoder import encode_shortest_text, parse_shortest_code_index
from test_sentence_ngram import ExperimentError


ARCHIVE_ROOT = Path("/mnt/c/Archive/tigerclaw_sentence_ml")
CORPUS_ROOT = Path("/mnt/c/Archive/brightmart_nlp_chinese_corpus")
DEFAULT_LEXICON = (
    Path(__file__).resolve().parents[1]
    / "release_arm64"
    / "码表"
    / "虎整句"
    / "常用字词.txt"
)
DEFAULT_OUTPUT = (
    ARCHIVE_ROOT / "baseline" / "tiger-sentence-validation-10000-cases.json"
)
DEFAULT_EXCLUSIONS = (
    ARCHIVE_ROOT / "baseline" / "tiger-sentence-tune-1000-pools.json",
    ARCHIVE_ROOT / "baseline" / "tiger-sentence-test-1000-pools.json",
)
HAN_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")
SOURCE_FIELDS = {
    "webtext": ("title", "desc", "content"),
    "news": ("title", "desc", "content"),
    "baike": ("title", "desc", "answer"),
    "wiki": ("title", "text"),
}


def source_paths(source: str) -> Tuple[List[Path], int]:
    if source == "webtext":
        return [CORPUS_ROOT / "webtext2019zh" / "web_text_zh_valid.json"], 0
    if source == "news":
        return [CORPUS_ROOT / "new2016zh" / "news2016zh_valid.json"], 0
    if source == "baike":
        return [CORPUS_ROOT / "baike2018qa" / "baike_qa_valid.json"], 0
    if source == "wiki":
        paths = sorted(
            path
            for path in (CORPUS_ROOT / "wiki_zh_2019").rglob("wiki_*")
            if path.is_file()
        )
        # The trigram metadata records 20,000 wiki training records. Unlike the
        # other corpora, wiki has no separate validation file, so skip them.
        return paths, 20000
    raise ExperimentError(f"未知语料来源: {source}")


def read_case_texts(path: Path) -> Iterable[str]:
    if not path.is_file():
        raise ExperimentError(f"排除集不存在: {path}")
    try:
        if path.suffix.lower() == ".jsonl":
            with path.open("r", encoding="utf-8") as stream:
                values = [json.loads(line) for line in stream if line.strip()]
        else:
            values = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as ex:
        raise ExperimentError(f"读取排除集失败 {path}: {ex}") from ex
    if not isinstance(values, list):
        raise ExperimentError(f"排除集根节点不是数组: {path}")
    for value in values:
        if isinstance(value, dict) and isinstance(value.get("text"), str):
            yield value["text"]


def iter_source_sentences(
    source: str,
    maximum_field_characters: int,
    statistics: Dict[str, int],
) -> Iterator[Tuple[int, str]]:
    paths, skip_records = source_paths(source)
    if not paths or any(not path.is_file() for path in paths):
        raise ExperimentError(f"找不到{source}验证语料。")
    records = 0
    for path in paths:
        try:
            stream = path.open("r", encoding="utf-8")
        except (OSError, UnicodeDecodeError) as ex:
            raise ExperimentError(f"打开语料失败 {path}: {ex}") from ex
        with stream:
            for line in stream:
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(record, dict):
                    continue
                records += 1
                if records <= skip_records:
                    continue
                if source == "wiki" and (records - 1) % 100 != 99:
                    continue
                statistics["records"] += 1
                seen_fields = set()
                for field in SOURCE_FIELDS[source]:
                    value = record.get(field)
                    if not isinstance(value, str) or value in seen_fields:
                        continue
                    seen_fields.add(value)
                    for sentence in clean_sentences(value, maximum_field_characters):
                        yield records, sentence


def prepare(args: argparse.Namespace) -> int:
    index = parse_shortest_code_index(args.lexicon)
    excluded: Set[str] = set()
    for path in args.exclude:
        excluded.update(read_case_texts(path))

    source_names = tuple(SOURCE_FIELDS)
    quota = (args.cases + len(source_names) - 1) // len(source_names)
    grouped: Dict[str, List[Dict[str, str]]] = {}
    seen = set(excluded)
    source_statistics = {}
    for source in source_names:
        values: List[Dict[str, str]] = []
        statistics = {
            "records": 0,
            "sentences": 0,
            "rejected_length": 0,
            "rejected_code": 0,
        }
        accepted_by_record: Dict[int, int] = {}
        for record_number, sentence in iter_source_sentences(
            source, args.maximum_field_characters, statistics
        ):
            statistics["sentences"] += 1
            if (
                accepted_by_record.get(record_number, 0)
                >= args.maximum_cases_per_record
            ):
                continue
            text = "".join(HAN_RE.findall(sentence))
            if not args.minimum_length <= len(text) <= args.maximum_length:
                statistics["rejected_length"] += 1
                continue
            if text in seen:
                continue
            try:
                code = encode_shortest_text(text, index)
            except ExperimentError:
                statistics["rejected_code"] += 1
                continue
            seen.add(text)
            values.append({"text": text, "source": source, "code": code})
            accepted_by_record[record_number] = (
                accepted_by_record.get(record_number, 0) + 1
            )
            if len(values) >= quota:
                break
        grouped[source] = values
        source_statistics[source] = statistics

    cases: List[Dict[str, str]] = []
    for position in range(quota):
        for source in source_names:
            values = grouped[source]
            if position < len(values) and len(cases) < args.cases:
                cases.append(values[position])
    if len(cases) < args.cases:
        counts = {source: len(grouped[source]) for source in source_names}
        raise ExperimentError(
            f"只能生成{len(cases)}条，少于目标{args.cases}条；各来源={counts}"
        )

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(cases, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    print(
        json.dumps(
            {
                "output": str(args.output.resolve()),
                "cases": len(cases),
                "sources": {
                    source: sum(case["source"] == source for case in cases)
                    for source in source_names
                },
                "excluded": len(excluded),
                "statistics": source_statistics,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lexicon", type=Path, default=DEFAULT_LEXICON)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--exclude", type=Path, action="append")
    parser.add_argument("--cases", type=int, default=10000)
    parser.add_argument("--minimum-length", type=int, default=6)
    parser.add_argument("--maximum-length", type=int, default=30)
    parser.add_argument("--maximum-field-characters", type=int, default=5000)
    parser.add_argument("--maximum-cases-per-record", type=int, default=1)
    args = parser.parse_args(argv)
    if args.exclude is None:
        args.exclude = list(DEFAULT_EXCLUSIONS)
    if (
        args.cases <= 0
        or not 1 <= args.minimum_length <= args.maximum_length
        or args.maximum_field_characters <= 0
        or args.maximum_cases_per_record <= 0
    ):
        parser.error("样本数和长度范围无效。")
    return args


if __name__ == "__main__":
    try:
        raise SystemExit(prepare(parse_arguments()))
    except ExperimentError as ex:
        raise SystemExit(f"错误: {ex}") from ex
