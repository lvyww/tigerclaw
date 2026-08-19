#!/usr/bin/env python3
"""Repack TCSKNM01 into the paged TCSKNM02 mobile layout.

The conversion preserves every float32 probability and every exported n-gram.
Only the physical layout changes: successors are stored beside their context,
with a sparse page index suitable for bounded, seek-based Lua readers.
"""

from __future__ import annotations

import argparse
import mmap
import struct
from pathlib import Path


SOURCE_MAGIC = b"TCSKNM01"
MOBILE_MAGIC = b"TCSKNM02"
MOBILE_VERSION = 1
HEADER = struct.Struct("<8sIIQIIIIQIIQQQIIQQ")
INDEX = struct.Struct("<QQ")
CONTEXT = struct.Struct("<QfI")
SUCCESSOR = struct.Struct("<If")
UNIGRAM = struct.Struct("<if")
DEFAULT_INDEX_STRIDE = 64
BUFFER_BYTES = 4 * 1024 * 1024


class SourceLayout:
    def __init__(self, data: mmap.mmap) -> None:
        if data[:8] != SOURCE_MAGIC or struct.unpack_from("<i", data, 8)[0] != 1:
            raise ValueError("input is not a TCSKNM01 version 1 model")
        position = 12
        self.unigram_count = struct.unpack_from("<i", data, position)[0]
        position += 4
        self.unigram_offset = position
        position += self.unigram_count * 8
        self.bigram_count = struct.unpack_from("<q", data, position)[0]
        position += 8
        self.bigram_offset = position
        position += self.bigram_count * 12
        self.bigram_context_count = struct.unpack_from("<i", data, position)[0]
        position += 4
        self.bigram_context_offset = position
        position += self.bigram_context_count * 8
        self.trigram_count = struct.unpack_from("<q", data, position)[0]
        position += 8
        self.trigram_offset = position
        position += self.trigram_count * 12
        self.trigram_context_count = struct.unpack_from("<q", data, position)[0]
        position += 8
        self.trigram_context_offset = position
        position += self.trigram_context_count * 12
        if position != len(data):
            raise ValueError("input model has invalid trailing data")


class BufferedOutput:
    def __init__(self, stream) -> None:
        self.stream = stream
        self.buffer = bytearray()

    def tell(self) -> int:
        return self.stream.tell() + len(self.buffer)

    def write(self, value: bytes) -> None:
        self.buffer.extend(value)
        if len(self.buffer) >= BUFFER_BYTES:
            self.flush()

    def flush(self) -> None:
        if self.buffer:
            self.stream.write(self.buffer)
            self.buffer.clear()


def write_context_blocks(
    output: BufferedOutput,
    data: mmap.mmap,
    context_offset: int,
    context_count: int,
    context_record_size: int,
    entry_offset: int,
    entry_count: int,
    index_stride: int,
) -> list[tuple[int, int]]:
    index: list[tuple[int, int]] = []
    entry_index = 0
    for context_index in range(context_count):
        at = context_offset + context_index * context_record_size
        if context_record_size == 8:
            context_key, probability = struct.unpack_from("<if", data, at)
        else:
            context_key, probability = struct.unpack_from("<Qf", data, at)
        if context_index % index_stride == 0:
            index.append((context_key, output.tell()))

        first_entry = entry_index
        while entry_index < entry_count:
            key = struct.unpack_from("<Q", data, entry_offset + entry_index * 12)[0]
            if key >> 21 != context_key:
                break
            entry_index += 1
        successor_count = entry_index - first_entry
        output.write(CONTEXT.pack(context_key, probability, successor_count))
        for successor_index in range(first_entry, entry_index):
            key, value = struct.unpack_from(
                "<Qf", data, entry_offset + successor_index * 12
            )
            output.write(SUCCESSOR.pack(key & 0x1FFFFF, value))

    if entry_index != entry_count:
        raise ValueError(
            f"{entry_count - entry_index} n-grams have no matching context"
        )
    return index


def convert(source: Path, destination: Path, index_stride: int) -> None:
    if index_stride < 16 or index_stride > 65536:
        raise ValueError("index stride must be between 16 and 65536")
    if destination.exists():
        raise FileExistsError(f"destination already exists: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)

    with source.open("rb") as source_stream:
        with mmap.mmap(source_stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
            layout = SourceLayout(data)
            try:
                with destination.open("xb") as destination_stream:
                    destination_stream.write(b"\0" * HEADER.size)
                    output = BufferedOutput(destination_stream)

                    unigram_offset = output.tell()
                    unigram_bytes = layout.unigram_count * UNIGRAM.size
                    output.write(
                        data[
                            layout.unigram_offset : layout.unigram_offset + unigram_bytes
                        ]
                    )

                    bigram_blocks_offset = output.tell()
                    bigram_index = write_context_blocks(
                        output,
                        data,
                        layout.bigram_context_offset,
                        layout.bigram_context_count,
                        8,
                        layout.bigram_offset,
                        layout.bigram_count,
                        index_stride,
                    )
                    bigram_index_offset = output.tell()
                    for key, offset in bigram_index:
                        output.write(INDEX.pack(key, offset))

                    trigram_blocks_offset = output.tell()
                    trigram_index = write_context_blocks(
                        output,
                        data,
                        layout.trigram_context_offset,
                        layout.trigram_context_count,
                        12,
                        layout.trigram_offset,
                        layout.trigram_count,
                        index_stride,
                    )
                    trigram_index_offset = output.tell()
                    for key, offset in trigram_index:
                        output.write(INDEX.pack(key, offset))
                    output.flush()
                    file_size = destination_stream.tell()

                    destination_stream.seek(0)
                    destination_stream.write(
                        HEADER.pack(
                            MOBILE_MAGIC,
                            MOBILE_VERSION,
                            HEADER.size,
                            file_size,
                            index_stride,
                            0,
                            layout.unigram_count,
                            0,
                            unigram_offset,
                            layout.bigram_context_count,
                            len(bigram_index),
                            bigram_blocks_offset,
                            bigram_index_offset,
                            layout.trigram_context_count,
                            len(trigram_index),
                            0,
                            trigram_blocks_offset,
                            trigram_index_offset,
                        )
                    )
            except Exception:
                destination.unlink(missing_ok=True)
                raise

    print(f"source: {source} ({source.stat().st_size / 1048576:.1f} MiB)")
    print(f"mobile: {destination} ({destination.stat().st_size / 1048576:.1f} MiB)")
    print(
        f"contexts: bigram={layout.bigram_context_count:,} "
        f"trigram={layout.trigram_context_count:,}; "
        f"index records={len(bigram_index) + len(trigram_index):,}"
    )


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="source TCSKNM01 model")
    parser.add_argument("destination", type=Path, help="new TCSKNM02 model")
    parser.add_argument(
        "--index-stride",
        type=int,
        default=DEFAULT_INDEX_STRIDE,
        help=f"contexts per disk page (default: {DEFAULT_INDEX_STRIDE})",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_arguments()
    convert(args.source.resolve(), args.destination.resolve(), args.index_stride)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
