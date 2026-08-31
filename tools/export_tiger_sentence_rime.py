#!/usr/bin/env python3
"""Export TigerClaw 虎整句 lexicon to a standalone Rime schema pack."""

from __future__ import annotations

import argparse
import re
from collections import defaultdict
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple


def parse_source(path: Path) -> List[Tuple[str, str]]:
    text = path.read_text(encoding="utf-8-sig")
    entries: List[Tuple[str, str]] = []
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        parts = re.split(r"\s+", line)
        if len(parts) < 2:
            continue
        word, code = parts[0], parts[1].lower()
        if not word or not code or not re.fullmatch(r"[a-z]+", code):
            continue
        entries.append((word, code))
    return entries


def choose_shorter(current: Optional[str], candidate: str) -> str:
    if current is None or len(candidate) < len(current):
        return candidate
    # Entries are emitted in source priority order. Keep the earlier code when
    # lengths tie instead of choosing lexicographically, which made the lower-
    # priority `ladc` displace `ldac` for 燕.
    return current


def is_single_char(text: str) -> bool:
    return len(text) == 1


def default_common_chars() -> Path:
    return (
        Path(__file__).resolve().parents[1]
        / "next"
        / "TigerClaw.Core"
        / "Data"
        / "sentence_common_chars_1500.txt"
    )


def default_char_ranks() -> Path:
    return (
        Path(__file__).resolve().parents[1]
        / "next"
        / "TigerClaw.Core"
        / "Data"
        / "sentence_char_ranks.txt"
    )


def load_character_ranks(path: Path) -> Dict[str, int]:
    ranks: Dict[str, int] = {}
    rank = 0
    if not path.is_file():
        raise SystemExit("character rank list not found: %s" % path)
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        text = raw_line.strip()
        if not text or text.startswith("#"):
            continue
        rank += 1
        ranks.setdefault(text, rank)
    return ranks


def load_common_characters(path: Path) -> set:
    texts: set = set()
    if not path.is_file():
        raise SystemExit("common-character list not found: %s" % path)
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        text = raw_line.strip()
        if not text or text.startswith("#"):
            continue
        texts.add(text)
    return texts


def choose_primary_code(
    character: str,
    codes: Iterable[str],
    exact: Dict[str, List[str]],
    minimum_length: int = 2,
) -> Optional[str]:
    best_first: Optional[str] = None
    best_any: Optional[str] = None
    for code in codes:
        if not code or len(code) < minimum_length:
            continue
        candidates = exact.get(code)
        if not candidates:
            continue
        best_any = choose_shorter(best_any, code)
        if candidates[0] == character:
            best_first = choose_shorter(best_first, code)
    return best_first or best_any


def build_index(
    entries: Sequence[Tuple[str, str]],
    common_characters: Optional[set] = None,
) -> Tuple[Dict[str, List[Tuple[str, int]]], Dict[str, str]]:
    exact: Dict[str, List[str]] = defaultdict(list)
    for word, code in entries:
        if word not in exact[code]:
            exact[code].append(word)

    codes_by_character: Dict[str, List[str]] = defaultdict(list)
    for word, code in entries:
        if is_single_char(word) and code not in codes_by_character[word]:
            codes_by_character[word].append(code)

    primary: Dict[str, str] = {}
    for character, codes in codes_by_character.items():
        chosen = choose_primary_code(character, codes, exact, 2)
        if chosen:
            primary[character] = chosen

    filtered: Dict[str, List[Tuple[str, int]]] = {}
    for code, texts in exact.items():
        allowed: List[Tuple[str, int]] = []
        for index, text in enumerate(texts):
            allow_non_primary = (
                len(code) == 1
                or not is_single_char(text)
                or not common_characters
                or text not in common_characters
            )
            if allow_non_primary or primary.get(text) == code:
                allowed.append((text, index + 1))
        if allowed:
            filtered[code] = allowed
    return filtered, primary


def lua_escape(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\n", "\\n")
        .replace("\r", "")
    )


def write_lua_data(
    path: Path,
    filtered: Dict[str, List[Tuple[str, int]]],
    ranks: Dict[str, int],
) -> None:
    lengths = sorted({len(code) for code in filtered})
    ordered_codes = sorted(filtered, key=lambda item: (len(item), item))
    reachable_characters = {
        character
        for candidates in filtered.values()
        for text, _ in candidates
        for character in text
    }
    lines = [
        "-- Generated by tools/export_tiger_sentence_rime.py. Do not edit.",
        "return {",
        "  lengths = {",
    ]
    for length in lengths:
        lines.append("    %d," % length)
    lines.extend([
        "  },",
        "  codes = {",
    ])
    for code in ordered_codes:
        items = ", ".join(
            '{t="%s",r=%d}' % (lua_escape(text), rank)
            for text, rank in filtered[code]
        )
        lines.append('    ["%s"] = {%s},' % (lua_escape(code), items))
    lines.extend([
        "  },",
        "  character_ranks = {",
    ])
    for text, rank in sorted(ranks.items(), key=lambda item: item[1]):
        if text in reachable_characters:
            lines.append('    ["%s"] = %d,' % (lua_escape(text), rank))
    lines.extend([
        "  },",
        "  unknown_character_rank = 20001,",
        "}",
        "",
    ])
    path.write_text("\n".join(lines), encoding="utf-8")


def parse_selector(raw: str, code_end: int) -> Tuple[int, int]:
    if code_end >= len(raw):
        return 0, code_end
    mark = raw[code_end]
    if mark == ";":
        return 2, code_end + 1
    if mark == "'":
        return 3, code_end + 1
    if mark.isdigit():
        digit_end = code_end
        while digit_end < len(raw) and raw[digit_end].isdigit():
            digit_end += 1
        token = raw[code_end:digit_end]
        return (10 if token == "0" else int(token)), digit_end
    return 0, code_end


def decode_raw(
    raw_code: str,
    filtered: Dict[str, List[Tuple[str, int]]],
    beam_width: int = 200,
    limit: int = 20,
) -> List[Tuple[str, str]]:
    raw = "".join(ch.lower() for ch in raw_code if not ch.isspace())
    if not raw or not any(ch.isalpha() for ch in raw):
        return []
    lengths = sorted({len(code) for code in filtered})
    states: List[List[Tuple[float, str, str]]] = [[] for _ in range(len(raw) + 1)]
    states[0].append((0.0, "", ""))
    for position, current in enumerate(states[:-1]):
        if not current:
            continue
        best: Dict[str, Tuple[float, str, str]] = {}
        for score, text, segmented in current:
            previous = best.get(text)
            if previous is None or score > previous[0]:
                best[text] = (score, text, segmented)
        ranked = sorted(best.values(), key=lambda item: item[0], reverse=True)[:beam_width]
        for length in lengths:
            if position + length > len(raw):
                continue
            code = raw[position:position + length]
            candidates = filtered.get(code)
            if not candidates:
                continue
            selected_rank, consumed_end = parse_selector(raw, position + length)
            if len(raw) > 1 and consumed_end - position < 2:
                continue
            required = selected_rank if selected_rank > 0 else 1
            for score, text, segmented in ranked:
                for cand_text, rank in candidates:
                    if rank != required:
                        continue
                    next_score = score - 0.001 * length
                    if len(cand_text) > 1:
                        next_score += 0.05 * len(cand_text)
                    piece = raw[position:consumed_end]
                    next_seg = piece if not segmented else segmented + " " + piece
                    states[consumed_end].append((next_score, text + cand_text, next_seg))
    completed: Dict[str, Tuple[float, str, str]] = {}
    for score, text, segmented in states[len(raw)]:
        previous = completed.get(text)
        if previous is None or score > previous[0]:
            completed[text] = (score, text, segmented)
    ordered = sorted(completed.values(), key=lambda item: item[0], reverse=True)
    return [(text, segmented) for _, text, segmented in ordered[:limit]]


def default_source() -> Path:
    repo = Path(__file__).resolve().parents[1]
    return repo / "release_arm64" / "码表" / "虎整句" / "虎整句.txt"


def default_output() -> Path:
    return Path(__file__).resolve().parents[1] / "rime" / "tiger_sentence"


def default_supplement() -> Path:
    return (
        Path(__file__).resolve().parents[1]
        / "release_arm64"
        / "码表"
        / "虎整句"
        / "补充语料.txt"
    )


def remove_unused_rime_dict(output: Path) -> None:
    # The schema has no table_translator. A leftover Rime dictionary would
    # look editable but never participate in lookup.
    for stale_name in (
        "tiger_sentence.dict.yaml",
        "tiger_sentence.table.bin",
        "tiger_sentence.prism.bin",
        "tiger_sentence.reverse.bin",
    ):
        stale = output / stale_name
        if stale.is_file():
            stale.unlink()


def remove_superseded_lua_modules(lua_output: Path) -> None:
    stale_names = (
        "tiger_sentence_custom.lua",
        "tiger_sentence_kn.lua",
        "tiger_sentence_lexicon.lua",
        "tiger_sentence_ranks.lua",
        "tiger_sentence_supplement.lua",
    )
    for stale_name in stale_names:
        stale = lua_output / stale_name
        if stale.is_file():
            stale.unlink()
    for stale in lua_output.glob("tiger_sentence_lexicon_[0-9][0-9].lua"):
        stale.unlink()


def write_supplement(source: Path, output: Path) -> None:
    purpose_comment = (
        "# 本文件用于提升虎整句中模型未收录的新词、流行词和个人常用词；"
        "格式为“词条 [权重]”，省略权重时默认为 1000。"
    )
    lines: List[str] = []
    if source.is_file():
        for raw_line in source.read_text(encoding="utf-8-sig").splitlines():
            line = raw_line.rstrip()
            if line:
                lines.append(line)
    if purpose_comment not in lines:
        lines.insert(0, purpose_comment)
    output.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=default_source())
    parser.add_argument("--output", type=Path, default=default_output())
    parser.add_argument("--common-chars", type=Path, default=default_common_chars())
    parser.add_argument("--char-ranks", type=Path, default=default_char_ranks())
    parser.add_argument("--supplement", type=Path, default=default_supplement())
    args = parser.parse_args()
    if not args.source.is_file():
        raise SystemExit("source lexicon not found: %s" % args.source)

    entries = parse_source(args.source)
    common_characters = load_common_characters(args.common_chars)
    filtered, primary = build_index(entries, common_characters)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "lua").mkdir(parents=True, exist_ok=True)
    remove_unused_rime_dict(args.output)
    remove_superseded_lua_modules(args.output / "lua")
    write_supplement(args.supplement, args.output / "tiger_sentence.supplement.txt")
    char_ranks = load_character_ranks(args.char_ranks)
    write_lua_data(
        args.output / "lua" / "tiger_sentence_data.lua",
        filtered,
        char_ranks,
    )
    if char_ranks.get("的") != 1:
        raise SystemExit("rank of 的 is not 1")
    if char_ranks.get("揸", 0) <= 3000:
        raise SystemExit("揸 should be rarer than rank 3000")

    checks = {"的": "ue", "是": "ot", "我": "tu"}
    for character, expected in checks.items():
        actual = primary.get(character)
        if actual != expected:
            raise SystemExit("primary code for %s is %s, expected %s" % (
                character, actual, expected))
        if character not in [text for text, rank in filtered[expected] if rank == 1]:
            raise SystemExit("%s is not rank-1 under %s" % (character, expected))
        if "u" in filtered and character in [text for text, _ in filtered["u"]]:
            if expected != "u":
                # 一码 can still list 的, but long-string decode must ignore it.
                pass

    if primary.get("燕") != "ldac":
        raise SystemExit("primary code for 燕 is %s, expected ldac" % primary.get("燕"))
    if "ldac" not in filtered or not any(text == "燕" for text, _ in filtered["ldac"]):
        raise SystemExit("燕 is not reachable under its preferred code ldac")
    if "ladc" in filtered and any(text == "燕" for text, _ in filtered["ladc"]):
        raise SystemExit("燕 leaked into its non-primary code ladc")

    decoded_ot = decode_raw("ot", filtered)
    decoded_u = decode_raw("u", filtered)
    decoded_ue = decode_raw("ue", filtered)
    decoded_long_u = decode_raw("ueot", filtered)
    if not decoded_ot or decoded_ot[0][0] != "是":
        raise SystemExit("decode ot failed: %s" % (decoded_ot[:3],))
    if not decoded_u or decoded_u[0][0] != "的":
        raise SystemExit("decode u failed: %s" % (decoded_u[:3],))
    if not decoded_ue or decoded_ue[0][0] != "的":
        raise SystemExit("decode ue failed: %s" % (decoded_ue[:3],))
    if not decoded_long_u or not decoded_long_u[0][0].startswith("的是"):
        raise SystemExit("decode ueot failed: %s" % (decoded_long_u[:3],))
    if any(item[0].startswith("的") and "u " in item[1] for item in decoded_long_u):
        raise SystemExit("one-key 的 leaked into multi-key decode")

    rare_on_non_primary = 0
    for code, items in filtered.items():
        if len(code) < 2:
            continue
        for text, rank in items:
            if is_single_char(text) and text not in common_characters and primary.get(text) not in (None, code):
                rare_on_non_primary += 1
                break
    if rare_on_non_primary == 0:
        raise SystemExit("expected rare characters to keep non-primary codes")
    if "oqra" in filtered and not any(text == "尷" for text, _ in filtered["oqra"]):
        raise SystemExit("rare 尷 should remain reachable by non-primary oqra")

    print("source_entries", len(entries))
    print("codes", len(filtered))
    print("primary_chars", len(primary))
    print("common_chars", len(common_characters))
    print("rare_non_primary_codes", rare_on_non_primary)
    print("primary_of_de", primary["的"])
    print("primary_of_shi", primary["是"])
    print("primary_of_wo", primary["我"])
    print("decode_ot", decoded_ot[0])
    print("decode_ueot", decoded_long_u[0])
    print("wrote", args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
