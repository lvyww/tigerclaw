"""Run Lua regressions in an owned temporary copy, never in live Rime user data.

python tools/run_regressions.py --lua lua5.4 --negative-control
The large optional model is not copied. Binary-model quality remains a separate run.
"""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
PACK = ROOT / "rime/tiger_sentence"


@contextmanager
def temporary_tree():
    temporary = tempfile.TemporaryDirectory(prefix="tiger-rime-regression-")
    try:
        yield Path(temporary.name)
    finally:
        for attempt in range(6):
            try:
                temporary.cleanup()
                break
            except OSError as error:
                if attempt == 5:
                    print(json.dumps({"cleanup_warning": str(error),
                                      "residual": temporary.name, "attempts": 6}), file=sys.stderr)
                else:
                    time.sleep(0.1 * 2 ** attempt)


def isolated_sources(destination):
    shutil.copytree(PACK / "lua", destination / "lua")
    (destination / "tools").mkdir()
    for name in ("test_tiger_sentence_incremental.lua", "test_rime_contract.lua", "test_sentence_safety.lua"):
        source = (ROOT / "tools" / name).read_text(encoding="utf-8")
        # Run the shared suite in the public mirror layout, without copying
        # unrelated TigerClaw tools or any live configuration/model files.
        source = source.replace("/rime/tiger_sentence", "")
        (destination / "tools" / name).write_text(source, encoding="utf-8")
    for pattern in ("*.txt", "*.yaml", "rime.lua"):
        for path in PACK.glob(pattern):
            shutil.copy2(path, destination / path.name)


def execute(lua, root, script, override=None):
    env = os.environ.copy()
    env.pop("TIGER_SENTENCE_MODULE", None)
    if override:
        env["TIGER_SENTENCE_MODULE"] = str(override)
    return subprocess.run([lua, str(root / "tools" / script), str(root)], cwd=root,
                          env=env, text=True, encoding="utf-8", errors="replace",
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)


def negative_controls(lua, root):
    source = (root / "lua/tiger_sentence.lua").read_text(encoding="utf-8")
    variants = [
        ("caret-insert", "test_rime_contract.lua",
         'if caret ~= #live_before then', 'if false then',
         "Insertion invalidated wrong lock range"),
        ("lazy-menu", "test_rime_contract.lua",
         'local count = type(menu.prepare) == "function"\n        and menu:prepare(candidate_limit) or menu:candidate_count()',
         'local count = menu:candidate_count()', "Tab wrapped at materialized prefix"),
        ("caret-delete", "test_rime_contract.lua",
         'local first = repr == "BackSpace" and caret - 1 or caret',
         'local first = #raw - 1', "Boundary delete removed data"),
        ("display-confidence", "test_sentence_safety.lua",
         '            all_candidates,\n            completed._truncated or false,',
         '            result,\n            completed._truncated or false,', "Display Top-K inflated confidence"),
        ("ancestor-truncation", "test_sentence_safety.lua",
         'states[consumed_end]._truncated = true',
         'states[consumed_end]._truncated = false', "Descendant lost ancestor truncation"),
        ("model-retry", "test_sentence_safety.lua",
         'decode = guarded_decode(decode)',
         '-- negative control: decode guard removed', "Model failure escaped decode guard"),
    ]
    for name, script, before, after, expected in variants:
        if source.count(before) != 1:
            raise RuntimeError(f"Negative-control anchor changed: {name}")
        mutant = root / (name + ".lua")
        mutant.write_text(source.replace(before, after), encoding="utf-8")
        result = execute(lua, root, script, mutant)
        if result.returncode == 0 or expected not in result.stdout:
            raise RuntimeError(f"Negative control did not fail at its functional assertion: {name}\n{result.stdout}")
        print(json.dumps({"negative_control": name, "status": "detected"}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lua", default="lua")
    parser.add_argument("--negative-control", action="store_true")
    args = parser.parse_args()
    lua = shutil.which(args.lua)
    if not lua:
        parser.error(f"Lua interpreter not found: {args.lua}")
    lua = str(Path(lua).resolve())
    with temporary_tree() as root:
        isolated_sources(root)
        for script in ("test_tiger_sentence_incremental.lua", "test_rime_contract.lua", "test_sentence_safety.lua"):
            result = execute(lua, root, script)
            print(result.stdout, end="", flush=True)
            result.check_returncode()
        if args.negative_control:
            negative_controls(lua, root)


if __name__ == "__main__":
    main()
