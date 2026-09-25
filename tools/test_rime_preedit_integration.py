"""Test production Rime chain with a tiny deterministic table in owned data."""
import argparse
from pathlib import Path
import subprocess
from run_regressions import isolated_sources, temporary_tree

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True)
    parser.add_argument('--plugin', required=True)
    parser.add_argument('--production-model')
    parser.add_argument('--benchmark-raw')
    parser.add_argument('--benchmark-mode', choices=('buffer', 'application'), default='buffer')
    args = parser.parse_args()
    with temporary_tree() as root:
        isolated_sources(root)
        if args.production_model:
            (root / 'models').mkdir(exist_ok=True)
            (root / 'models/sentence-fivegram-mobile.bin').symlink_to(Path(args.production_model).resolve())
        shared = root / '_shared'
        shared.mkdir()
        (shared / 'default.yaml').write_text(
            'config_version: "1.0"\nschema_list:\n  - schema: tiger_sentence\n'
            'menu:\n  page_size: 5\nrecognizer:\n  patterns: {}\n'
            'ascii_composer:\n  switch_key:\n    Shift_L: commit_code\n    Caps_Lock: clear\n')
        if not args.production_model:
            (root / 'tiger_sentence.codes.txt').write_text('刘\tvp\n甲\tab\n乙\tab\n团圆\tcd\n丁\tef\n丙\tef\n𠀀\tgh\n甲\tij\n乙\tij\n甲乙丙\tqr\n')
            (root / 'tiger_sentence.custom.yaml').write_text('patch:\n  tiger_sentence/high_freq_limit: 0\n')
        (root / 'other.schema.yaml').write_text('schema:\n  schema_id: other\n  name: other\n  version: "1"\n')
        with (root / 'rime.lua').open('a') as f:
            f.write('''
local tiger = require("tiger_sentence")
tiger.set_model_enabled(PRODUCTION_MODEL)
local original = tiger_sentence_processor.func
tiger_sentence_processor.func = function(key, env)
    local result = original(key, env)
    local live = env._tiger_learning
    env.engine.context:set_property("review_learning_count", tostring(live and live.store and live.store.count or 0))
    return result
end
'''.replace('PRODUCTION_MODEL', 'true' if args.production_model else 'false'))
        subprocess.run([str(Path(args.exe).resolve()), str(root), str(shared),
                        str(Path(args.plugin).resolve())] +
                       ([args.benchmark_raw, args.benchmark_mode] if args.benchmark_raw else
                        ['production'] if args.production_model else []),
                       check=True, timeout=120)

if __name__ == '__main__':
    main()
