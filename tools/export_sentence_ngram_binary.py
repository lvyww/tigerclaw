#!/usr/bin/env python3
"""将实验 JSON.GZ 字符 n-gram 转换为 Core 可直接加载的紧凑二进制。"""

from __future__ import annotations

import argparse
import gzip
import json
import struct
from collections import defaultdict
from pathlib import Path
from typing import Dict, Iterable, Mapping, Sequence, Tuple


MAGIC = b"TCSNGRM1"
BOS = "\x02"
EOS = "\x03"
UNKNOWN = "<unk>"


def write_7bit_int(stream, value: int) -> None:
    while value >= 0x80:
        stream.write(bytes(((value | 0x80) & 0xFF,)))
        value >>= 7
    stream.write(bytes((value,)))


def write_dotnet_string(stream, value: str) -> None:
    encoded = value.encode("utf-8")
    write_7bit_int(stream, len(encoded))
    stream.write(encoded)


def pack_pair(first: int, second: int) -> int:
    return (first << 32) | second


def pack_triple(first: int, second: int, third: int) -> int:
    return first | (second << 21) | (third << 42)


def split_tokens(value: str, expected: int) -> Tuple[str, ...] | None:
    tokens = tuple(value)
    return tokens if len(tokens) == expected else None


def export(source: Path, output: Path) -> None:
    with gzip.open(source, "rt", encoding="utf-8") as stream:
        model = json.load(stream)

    unigrams: Mapping[str, int] = model["unigrams"]
    bigrams: Mapping[str, int] = model["bigrams"]
    trigrams: Mapping[str, int] = model["trigrams"]
    tokens = [UNKNOWN, BOS, EOS]
    tokens.extend(token for token in unigrams if token not in {UNKNOWN, BOS, EOS})
    token_ids = {token: index for index, token in enumerate(tokens)}

    unigram_counts = [0] * len(tokens)
    for token, count in unigrams.items():
        unigram_counts[token_ids[token]] = int(count)

    bigram_entries = []
    bigram_contexts = [0] * len(tokens)
    for key, count in bigrams.items():
        values = split_tokens(key, 2)
        if values is None or any(value not in token_ids for value in values):
            continue
        first, second = (token_ids[value] for value in values)
        count = int(count)
        bigram_entries.append((pack_pair(first, second), count))
        bigram_contexts[first] += count
    bigram_entries.sort()

    trigram_entries = []
    trigram_contexts: Dict[int, int] = defaultdict(int)
    for key, count in trigrams.items():
        values = split_tokens(key, 3)
        if values is None or any(value not in token_ids for value in values):
            continue
        first, second, third = (token_ids[value] for value in values)
        count = int(count)
        trigram_entries.append((pack_triple(first, second, third), count))
        trigram_contexts[pack_pair(first, second)] += count
    trigram_entries.sort()
    trigram_context_entries = sorted(trigram_contexts.items())

    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as stream:
        stream.write(MAGIC)
        stream.write(struct.pack("<i", 1))
        stream.write(struct.pack("<i", len(tokens)))
        for token in tokens:
            write_dotnet_string(stream, token)
        stream.write(struct.pack(f"<{len(unigram_counts)}q", *unigram_counts))
        stream.write(struct.pack("<i", len(bigram_entries)))
        stream.write(struct.pack(f"<{len(bigram_entries)}Q", *(key for key, _ in bigram_entries)))
        stream.write(struct.pack(f"<{len(bigram_entries)}i", *(count for _, count in bigram_entries)))
        stream.write(struct.pack(f"<{len(bigram_contexts)}q", *bigram_contexts))
        stream.write(struct.pack("<i", len(trigram_entries)))
        stream.write(struct.pack(f"<{len(trigram_entries)}Q", *(key for key, _ in trigram_entries)))
        stream.write(struct.pack(f"<{len(trigram_entries)}i", *(count for _, count in trigram_entries)))
        stream.write(struct.pack("<i", len(trigram_context_entries)))
        stream.write(struct.pack(f"<{len(trigram_context_entries)}Q", *(key for key, _ in trigram_context_entries)))
        stream.write(struct.pack(f"<{len(trigram_context_entries)}q", *(count for _, count in trigram_context_entries)))

    print(
        f"output={output.resolve()} size={output.stat().st_size / 1024 / 1024:.1f} MiB "
        f"tokens={len(tokens):,} bigrams={len(bigram_entries):,} "
        f"trigrams={len(trigram_entries):,}"
    )


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    export(args.input, args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
