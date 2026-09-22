"""Freeze corpus annotation independently of decoding (pypinyin==0.55.0)."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import pypinyin
from pypinyin import lazy_pinyin, Style


def sha(path):
    return hashlib.file_digest(open(path, "rb"), "sha256").hexdigest()


def dump(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus", type=Path)
    parser.add_argument("table", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--corrections", type=Path)
    args = parser.parse_args()
    assert pypinyin.__version__ == "0.55.0", pypinyin.__version__
    args.output.mkdir(parents=True, exist_ok=True)
    if (args.output / "cases.jsonl").exists():
        raise SystemExit("Frozen cases already exist; use a new output directory")
    corrections = json.loads(args.corrections.read_text()) if args.corrections else {}
    entries = set()
    for line in args.table.read_text(encoding="utf-8-sig").splitlines():
        text, code, _ = line.split()
        entries.add((text, code))
    rows, excluded, seen = [], [], set()
    for line_number, text in enumerate(args.corpus.read_text(encoding="utf-8-sig").splitlines(), 1):
        text = text.strip()
        if not text or text in seen:
            excluded.append(dict(line=line_number, text=text, reason="empty_or_duplicate"))
            continue
        seen.add(text)
        # Exclude punctuation/Latin/digits explicitly, never according to model output.
        if not all("\u3400" <= c <= "\u9fff" or 0x20000 <= ord(c) <= 0x323AF for c in text):
            excluded.append(dict(line=line_number, text=text, reason="non_han"))
            continue
        syllables = corrections.get(text, "").split() or lazy_pinyin(text, style=Style.NORMAL, v_to_u=False)
        syllables = [{"nve": "nue", "lve": "lue"}.get(s, s) for s in syllables]
        if len(syllables) != len(text) or any(not re.fullmatch("[a-z]+", s) for s in syllables):
            excluded.append(dict(line=line_number, text=text, reason="annotation_unavailable"))
            continue
        identity = hashlib.sha256(text.encode()).hexdigest()
        missing = [dict(index=i, text=c, code=s) for i, (c, s) in enumerate(zip(text, syllables)) if (c, s) not in entries]
        # Joint word/single table coverage, independent of Beam and ranking.
        positions = {0}
        for end in range(1, len(text) + 1):
            if any((text[start:end], "".join(syllables[start:end])) in entries for start in positions.copy()):
                positions.add(end)
        rows.append(dict(id=identity, text=text, code="".join(syllables), syllables=syllables,
                         split="dev" if int(identity[:16], 16) % 5 == 0 else "test",
                         source=f"{args.corpus.name}:{line_number}", covered=len(text) in positions,
                         singleCovered=not missing, missing=missing))
    # Pre-decode audit: hash-ordered, half selected for common polyphonic characters.
    ordered = sorted(rows, key=lambda r: r["id"])
    poly = set("行长重乐还得地的着了便调朝藏差单解数好强薄弹都种")
    audit = [r for r in ordered if poly.intersection(r["text"])][:60]
    audit_ids = {r["id"] for r in audit}
    audit += [r for r in ordered if r["id"] not in audit_ids][:40]
    with (args.output / "cases.jsonl").open("w", encoding="utf-8") as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
    dump(args.output / "audit.json", audit)
    dump(args.output / "excluded.json", excluded)
    package = Path(pypinyin.__file__).parent
    manifest = dict(version=1, corpus=sha(args.corpus), table=sha(args.table), cases=sha(args.output / "cases.jsonl"),
                    pypinyin=pypinyin.__version__, dictionaries={p.name: sha(p) for p in sorted(package.glob("*dict*")) if p.suffix in [".py", ".json"]},
                    preparation=sha(__file__), corrections=sha(args.corrections) if args.corrections else None,
                    split="sha256(text).first64bits % 5 == 0 => dev", rows=len(rows), excluded=len(excluded),
                    dev=sum(r["split"] == "dev" for r in rows), covered=sum(r["covered"] for r in rows),
                    singleCovered=sum(r["singleCovered"] for r in rows))
    dump(args.output / "manifest.json", manifest)
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
