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
    parser.add_argument('--key-correction', action='store_true')
    parser.add_argument('--correction-exhaustion', action='store_true')
    parser.add_argument('--benchmark-raw')
    parser.add_argument('--benchmark-mode', choices=('buffer', 'application'), default='buffer')
    parser.add_argument('--benchmark-correction', choices=('off','A','B','C','reference'), default='off')
    parser.add_argument('--benchmark-early-commit', choices=('on','off'), default='on')
    args = parser.parse_args()
    if args.key_correction or args.correction_exhaustion:
        assert args.production_model and not args.benchmark_raw
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
            if args.correction_exhaustion:
                f.write('\nlocal c=require("tiger_sentence").correction\n'
                        'c.profiles.tiny={seeds=8,one=16,two=8,steps=2}\n'
                        'c.configure("tiny"); c.diagnostics_enabled=true\n')
            if args.benchmark_raw:
                f.write('\nlocal probe=require("tiger_sentence")\n'
                        'probe.correction.diagnostics_enabled=true\n')
                if args.benchmark_correction!='off':
                    f.write('probe.correction.configure("'+args.benchmark_correction+'")\n')
            f.write('''
local tiger = require("tiger_sentence")
tiger.set_model_enabled(PRODUCTION_MODEL)
local original = tiger_sentence_processor.func
tiger_sentence_processor.func = function(key, env)
    local result = original(key, env)
    local live = env._tiger_learning
    env.engine.context:set_property("review_learning_count", tostring(live and live.store and live.store.count or 0))
    if tiger.correction.diagnostics_enabled then
        env.engine.context:set_property("review_correction_searches", tostring(tiger.correction.stats.searches))
        env.engine.context:set_property("review_correction_exhausted", tostring(tiger.correction.last_work and tiger.correction.last_work.incomplete or false))
        env.engine.context:set_property("review_model_loaded", tostring(tiger.model_status().loaded))
    end
    return result
end
'''.replace('PRODUCTION_MODEL', 'true' if args.production_model else 'false'))
        subprocess.run([str(Path(args.exe).resolve()), str(root), str(shared),
                        str(Path(args.plugin).resolve())] +
                       ([args.benchmark_raw, args.benchmark_mode,
                         '0' if args.benchmark_correction=='off' else '1',
                         '1' if args.benchmark_early_commit=='on' else '0'] if args.benchmark_raw else
                        ['exhaustion' if args.correction_exhaustion else 'correction' if args.key_correction else 'production'] if args.production_model else []),
                       check=True, timeout=120)

if __name__ == '__main__':
    main()
