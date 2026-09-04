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


def codes_of_character(
    entries: Sequence[Tuple[str, str]],
    character: str,
) -> List[str]:
    codes: List[str] = []
    for word, code in entries:
        if word == character and code not in codes:
            codes.append(code)
    return codes


DEFAULT_HIGH_FREQ_LIMIT = 1500


def default_dist_config() -> Path:
    return Path(__file__).resolve().parents[1] / "dist_config.txt"


def parse_dist_config(path: Path) -> Dict[str, str]:
    values: Dict[str, str] = {}
    if not path.is_file():
        return values
    for raw_line in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip("\r\n")
        if not line or line.startswith("#") or "\t" not in line:
            continue
        key, _, value = line.partition("\t")
        values.setdefault(key.strip(), value.strip())
    return values


def parse_high_freq_limit(raw: Optional[str]) -> int:
    # Mirror CoreRuntimeState.GetSentenceOptimalCodeHighFreqLimit: an absent
    # key keeps the default; an empty or invalid value means 0 (no limit).
    if raw is None:
        return DEFAULT_HIGH_FREQ_LIMIT
    try:
        parsed = int(raw.strip())
    except ValueError:
        return 0
    return parsed if parsed >= 0 else 0


def parse_character_set(raw: Optional[str]) -> set:
    # Mirror CoreRuntimeState.ParseCharacterSet: each Unicode text element in
    # the raw string is one whitelisted character.
    texts: set = set()
    if not raw:
        return texts
    for character in raw.strip():
        if not character.isspace():
            texts.add(character)
    return texts


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


def top_common_characters(ranks: Dict[str, int], limit: int) -> set:
    # Mirror SentenceCharacterRanks.TakeTop(limit): an empty set disables the
    # optimal-code restriction entirely.
    if limit <= 0:
        return set()
    ordered = sorted(ranks.items(), key=lambda item: item[1])
    return {text for text, _ in ordered[:limit]}


def default_schema() -> Path:
    return Path(__file__).resolve().parents[1] / "rime" / "tiger_sentence" / \
        "tiger_sentence.schema.yaml"


def schema_high_freq_limit(path: Path) -> int:
    # The runtime source of truth for the optimal-code limit is the schema
    # config; an absent key keeps the Windows default of 1500.
    if not path.is_file():
        raise SystemExit("schema not found: %s" % path)
    for raw_line in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip()
        if line.startswith("high_freq_limit:"):
            return parse_high_freq_limit(line.partition(":")[2])
    return parse_high_freq_limit(None)


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
    full_code_whitelist: Optional[set] = None,
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

    whitelist = full_code_whitelist or set()
    filtered: Dict[str, List[Tuple[str, int]]] = {}
    for code, texts in exact.items():
        allowed: List[Tuple[str, int]] = []
        for index, text in enumerate(texts):
            allow_non_primary = (
                len(code) == 1
                or not is_single_char(text)
                or not common_characters
                or text not in common_characters
                or text in whitelist
            )
            if allow_non_primary or primary.get(text) == code:
                allowed.append((text, index + 1))
        if allowed:
            filtered[code] = allowed
    return filtered, primary


def codes_header() -> str:
    return (
        "# TigerSentence code table (generated by tools/export_tiger_sentence_rime.py).\n"
        "# Format: one entry per line, \"<text>\\t<code>\"; code letters a-z only.\n"
        "# Line order inside one code defines its lexicon rank (1 = first).\n"
        "# Comments (#) and blank lines are ignored. Edit or replace freely;\n"
        "# re-running the exporter overwrites this file.\n"
    )


def write_codes_txt(path: Path, entries: Sequence[Tuple[str, str]]) -> None:
    seen: set = set()
    lines = [codes_header()]
    for word, code in entries:
        key = (word, code)
        if key in seen:
            continue
        seen.add(key)
        lines.append("%s\t%s\n" % (word, code))
    path.write_text("".join(lines), encoding="utf-8", newline="")


def write_char_ranks_txt(path: Path, ranks: Dict[str, int]) -> None:
    lines = [
        "# TigerSentence character frequency ranks (generated).\n"
        "# One character per line; line order after comments is the rank.\n"
        "# Used for the common-character optimal-code filter and the rare\n"
        "# isolation penalty. Delete this file to disable both.\n"
    ]
    for text, _ in sorted(ranks.items(), key=lambda item: item[1]):
        lines.append(text + "\n")
    path.write_text("".join(lines), encoding="utf-8", newline="")


def write_whitelist_txt(path: Path, whitelist: set) -> None:
    lines = [
        "# TigerSentence full-code whitelist (generated).\n"
        "# Whitelisted common characters keep their non-primary full codes in\n"
        "# sentence building. One or more characters per line; comments (#)\n"
        "# and blank lines are ignored. Empty file disables the whitelist.\n"
    ]
    for character in sorted(whitelist):
        lines.append(character + "\n")
    path.write_text("".join(lines), encoding="utf-8", newline="")


def read_codes_txt(path: Path) -> List[Tuple[str, str]]:
    entries: List[Tuple[str, str]] = []
    if not path.is_file():
        return entries
    for raw_line in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        parts = re.split(r"\s+", line)
        if len(parts) < 2:
            continue
        word, code = parts[0], parts[1].lower()
        if not word or not re.fullmatch(r"[a-z]+", code):
            continue
        entries.append((word, code))
    return entries


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
        "tiger_sentence_data.lua",
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
    parser.add_argument("--dist-config", type=Path, default=default_dist_config())
    parser.add_argument("--schema", type=Path, default=default_schema(),
                        help="schema used to cross-check the runtime default "
                             "high-frequency limit")
    parser.add_argument("--char-ranks", type=Path, default=default_char_ranks())
    parser.add_argument("--full-code-whitelist", default=None,
                        help="override 整句允许全码组句白名单 from dist config")
    parser.add_argument("--supplement", type=Path, default=default_supplement())
    args = parser.parse_args()
    if not args.source.is_file():
        raise SystemExit("source lexicon not found: %s" % args.source)

    dist_config = parse_dist_config(args.dist_config)
    high_freq_limit = parse_high_freq_limit(
        dist_config.get("高频字仅使用最优码组句"))
    schema_limit = schema_high_freq_limit(args.schema)
    if schema_limit != high_freq_limit:
        raise SystemExit(
            "dist_config 高频字仅使用最优码组句=%d does not match the schema "
            "tiger_sentence/high_freq_limit=%d; the schema is the runtime "
            "source of truth" % (high_freq_limit, schema_limit))
    full_code_whitelist = (
        parse_character_set(args.full_code_whitelist)
        if args.full_code_whitelist is not None
        else parse_character_set(
            dist_config.get("整句允许全码组句白名单"))
    )

    entries = parse_source(args.source)
    char_ranks = load_character_ranks(args.char_ranks)
    common_characters = top_common_characters(char_ranks, high_freq_limit)
    filtered, primary = build_index(entries, common_characters, full_code_whitelist)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "lua").mkdir(parents=True, exist_ok=True)
    remove_unused_rime_dict(args.output)
    remove_superseded_lua_modules(args.output / "lua")
    write_supplement(args.supplement, args.output / "tiger_sentence.supplement.txt")
    write_codes_txt(args.output / "tiger_sentence.codes.txt", entries)
    write_char_ranks_txt(args.output / "tiger_sentence.char_ranks.txt", char_ranks)
    write_whitelist_txt(args.output / "tiger_sentence.full_code_whitelist.txt", full_code_whitelist)
    if char_ranks.get("的") != 1:
        raise SystemExit("rank of 的 is not 1")
    if char_ranks.get("揸", 0) <= 3000:
        raise SystemExit("揸 should be rarer than rank 3000")

    # The runtime now builds its index from the plain-text files; verify the
    # written code table re-parses to exactly the source entries.
    reparsed = read_codes_txt(args.output / "tiger_sentence.codes.txt")
    deduped: List[Tuple[str, str]] = []
    seen_pairs: set = set()
    for word, code in entries:
        if (word, code) not in seen_pairs:
            seen_pairs.add((word, code))
            deduped.append((word, code))
    if reparsed != deduped:
        raise SystemExit("written code table does not re-parse to the source entries")
    filtered, primary = build_index(reparsed, common_characters, full_code_whitelist)

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
    # The written code table stays raw, so ladc may appear there. The default
    # runtime filter (1500) must hide 燕 under ladc, and high_freq_limit=0
    # (no optimal-code restriction) must expose it again.
    if "ladc" in filtered and any(text == "燕" for text, _ in filtered["ladc"]):
        raise SystemExit("default runtime filter kept 燕 under non-primary ladc")
    unrestricted, _ = build_index(
        reparsed, top_common_characters(char_ranks, 0), full_code_whitelist)
    if not ("ladc" in unrestricted and
            any(text == "燕" for text, _ in unrestricted["ladc"])):
        raise SystemExit("high_freq_limit=0 should re-expose 燕 under ladc")

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

    # Whitelisted common characters must keep their non-primary full codes so
    # 允许单字重码组句 can compete them through the language model, while
    # non-whitelisted common characters stay restricted to their optimal code.
    whitelist_codes = 0
    for character in sorted(full_code_whitelist):
        codes = codes_of_character(entries, character)
        if len(codes) < 2 or character not in common_characters:
            continue
        primary_code = primary.get(character)
        kept = [
            code
            for code in codes
            if code in filtered
            and any(text == character for text, _ in filtered[code])
        ]
        if primary_code and len(kept) < min(2, len(codes)):
            raise SystemExit(
                "whitelisted %s lost its non-primary codes: %s" % (character, kept))
        whitelist_codes += len(kept) - (1 if primary_code in kept else 0)
    if whitelist_codes == 0 and full_code_whitelist:
        raise SystemExit("whitelist kept no extra non-primary codes")
    restricted_example = None
    for code, items in filtered.items():
        if len(code) < 2:
            continue
        for text, _ in items:
            if (is_single_char(text) and text in common_characters
                    and text not in full_code_whitelist):
                restricted_example = text
                break
        if restricted_example:
            break
    if restricted_example is None:
        raise SystemExit("expected at least one restricted common character")

    print("source_entries", len(entries))
    print("codes", len(filtered))
    print("primary_chars", len(primary))
    print("common_chars", len(common_characters))
    print("high_freq_limit", high_freq_limit)
    print("full_code_whitelist", len(full_code_whitelist))
    print("whitelist_extra_codes", whitelist_codes)
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
