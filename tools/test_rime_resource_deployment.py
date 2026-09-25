"""Check real librime cleanup and post-deployment Lua resource loading twice."""
import argparse
from pathlib import Path
import shutil
import subprocess
from run_regressions import isolated_sources, temporary_tree


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--exe', required=True)
    p.add_argument('--plugin', required=True)
    p.add_argument('--model', required=True)
    p.add_argument('--pack', help='Extracted release layout to test instead of source')
    args = p.parse_args()
    with temporary_tree() as root:
        if args.pack:
            shutil.copytree(Path(args.pack), root, dirs_exist_ok=True,
                            ignore=shutil.ignore_patterns('sentence-fivegram-mobile.bin'))
        else:
            isolated_sources(root)
        (root / 'models/sentence-fivegram-mobile.bin').symlink_to(Path(args.model).resolve())
        shared = root / '_shared'
        shared.mkdir()
        (shared / 'default.yaml').write_text(
            'config_version: "1.0"\nschema_list:\n  - schema: tiger_sentence\n'
            'menu:\n  page_size: 5\nrecognizer:\n  patterns: {}\n'
            'ascii_composer:\n  switch_key:\n    Shift_L: commit_code\n')
        with (root / 'rime.lua').open('a') as f:
            f.write('''
local original_resource_init = tiger_sentence_processor.init
tiger_sentence_processor.init = function(env)
    if original_resource_init then original_resource_init(env) end
    local status = require("tiger_sentence").lexical_status()
    env.engine.context:set_property("review_lexical_loaded", tostring(status.loaded))
    env.engine.context:set_property("review_lexical_path", status.path or "")
end
''')
        expected = (root / 'models/tiger_sentence.lexical.bin').read_bytes()
        for run in range(2):
            # A real top-level .bin is the negative control for the cleanup rule.
            (root / 'tiger_sentence.lexical.bin').write_bytes(expected)
            subprocess.run([str(Path(args.exe).resolve()), str(root), str(shared),
                            str(Path(args.plugin).resolve())], check=True, timeout=120)
            assert (root / 'models/tiger_sentence.lexical.bin').read_bytes() == expected
            assert (root / 'trash/tiger_sentence.lexical.bin').read_bytes() == expected
            print(f'PASS deployment and fresh-process load {run + 1}', flush=True)


if __name__ == '__main__':
    main()
