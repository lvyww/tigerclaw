#!/usr/bin/env python3
"""Repack a TCSKNM02 mobile model into TCSKNM01 without changing float32 values.

Use convert_sentence_ngram_mobile.py on the result to verify a byte-exact
round trip (with the original index stride). Never overwrites an existing file.
"""
from __future__ import annotations

import argparse
import math
import mmap
from pathlib import Path
import struct

from convert_sentence_ngram_mobile import HEADER, CONTEXT, INDEX, BufferedOutput


def convert(source: Path, destination: Path) -> None:
    with source.open('rb') as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
        if len(data) < HEADER.size:
            raise ValueError('Truncated header')
        (magic, version, header_size, size, stride, reserved, unigrams, reserved2,
         unigram_at, bigrams, bigram_indexes, bigram_at, bigram_index_at,
         trigrams, trigram_indexes, reserved3, trigram_at, trigram_index_at) = HEADER.unpack_from(data)
        if (magic != b'TCSKNM02' or version != 1 or header_size != HEADER.size
                or size != len(data) or not 16 <= stride <= 65536
                or reserved or reserved2 or reserved3 or not unigrams):
            raise ValueError('Unsupported or invalid mobile header')
        if not (unigram_at == HEADER.size and unigram_at + unigrams * 8 == bigram_at
                and bigram_at <= bigram_index_at
                and bigram_index_at + bigram_indexes * INDEX.size == trigram_at
                and trigram_at <= trigram_index_at
                and trigram_index_at + trigram_indexes * INDEX.size == size):
            raise ValueError('Invalid section boundaries')
        last = -1
        unknown = False
        for at in range(unigram_at, bigram_at, 8):
            key, value = struct.unpack_from('<If', data, at)
            if not last < key <= 0x10ffff or not math.isfinite(value) or value < 0:
                raise ValueError('Invalid unigram')
            unknown |= key == 0 and value > 0
            last = key
        if not unknown:
            raise ValueError('Missing unknown probability')

        def blocks(start, end, count, index_at, index_count, max_key):
            if index_count != (count + stride - 1) // stride:
                raise ValueError('Invalid index count')
            at, previous = start, -1
            for i in range(count):
                if at + CONTEXT.size > end:
                    raise ValueError('Truncated context')
                key, value, n = CONTEXT.unpack_from(data, at)
                if not previous < key <= max_key or not math.isfinite(value) or value < 0:
                    raise ValueError('Invalid context')
                if i % stride == 0 and INDEX.unpack_from(data, index_at + i // stride * INDEX.size) != (key, at):
                    raise ValueError('Invalid sparse index')
                successors = at + CONTEXT.size
                stop = successors + n * 8
                if stop > end:
                    raise ValueError('Truncated successors')
                yield key, at, successors, stop, n
                at, previous = stop, key
            if at != end:
                raise ValueError('Trailing context data')

        sections = [(bigram_at, bigram_index_at, bigrams, bigram_index_at, bigram_indexes, 0x10ffff),
                    (trigram_at, trigram_index_at, trigrams, trigram_index_at, trigram_indexes, (0x10ffff << 21) | 0x10ffff)]
        # Validate context/index structure before creating output.
        counts = [sum(b[4] for b in blocks(*section)) for section in sections]
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open('xb') as target:
            try:
                out = BufferedOutput(target)
                out.write(struct.pack('<8sII', b'TCSKNM01', 1, unigrams))
                out.write(data[unigram_at:bigram_at])
                for order, section in enumerate(sections):
                    out.write(struct.pack('<Q', counts[order]))
                    for key, _, start, stop, _ in blocks(*section):
                        previous = -1
                        for at in range(start, stop, 8):
                            successor, value = struct.unpack_from('<If', data, at)
                            if not previous < successor <= 0x10ffff or not math.isfinite(value) or value < 0:
                                raise ValueError('Invalid successor')
                            out.write(struct.pack('<Q', (key << 21) | successor) + data[at + 4:at + 8])
                            previous = successor
                    out.write(struct.pack('<I' if order == 0 else '<Q', section[2]))
                    for key, at, _, _, _ in blocks(*section):
                        out.write(struct.pack('<I' if order == 0 else '<Q', key) + data[at + 8:at + 12])
                out.flush()
            except Exception:
                target.close()
                destination.unlink()
                raise
    print(f'{destination}: {destination.stat().st_size} bytes; n-grams {unigrams}, {counts[0]}, {counts[1]}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    convert(args.source, args.destination)
