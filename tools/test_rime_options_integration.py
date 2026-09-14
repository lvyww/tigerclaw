"""Use separate real librime processes; never touch live Rime user data."""
import argparse
from pathlib import Path
import subprocess
from run_regressions import isolated_sources, temporary_tree


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True)
    parser.add_argument('--plugin', required=True)
    parser.add_argument('--negative-control', action='store_true')
    args = parser.parse_args()
    for scenario in ('write', 'defaults', 'legacy'):
        with temporary_tree() as root:
            isolated_sources(root)
            if args.negative_control:
                schema = root / 'tiger_sentence.schema.yaml'
                text = schema.read_text()
                for name, value in (('early_commit', 1), ('allow_duplicate_single', 1), ('early_commit_to_preedit', 0)):
                    text = text.replace('  - name: tiger_sentence_' + name + '\n',
                                        '  - name: tiger_sentence_' + name + '\n    reset: ' + str(value) + '\n')
                schema.write_text(text)
            shared = root / '_shared'
            shared.mkdir()
            (shared / 'default.yaml').write_text(
                'config_version: "1.0"\nschema_list:\n  - schema: tiger_sentence\n'
                'menu:\n  page_size: 5\nrecognizer:\n  patterns: {}\n')
            (root / 'other.schema.yaml').write_text('schema:\n  schema_id: other\n  name: other\n  version: "1"\n')
            with (root / 'rime.lua').open('a') as f:
                f.write('\nrequire("tiger_sentence").set_model_enabled(false)\n')
            if scenario == 'defaults':
                (root / 'tiger_sentence.custom.yaml').write_text(
                    'patch:\n  tiger_sentence/option_defaults/tiger_sentence_early_commit: false\n'
                    '  tiger_sentence/option_defaults/tiger_sentence_early_commit_to_preedit: true\n')
            if scenario == 'legacy':
                (root / 'user.yaml').write_text(
                    'var:\n  option:\n    tiger_sentence_early_commit: false\n'
                    '    tiger_sentence_allow_duplicate_single: false\n'
                    '    tiger_sentence_early_commit_to_preedit: true\n')
            command = [str(Path(args.exe).resolve()), str(root), str(shared), str(Path(args.plugin).resolve())]
            if args.negative_control:
                result = subprocess.run(command + ['write'], text=True, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, timeout=120)
                assert result.returncode != 0 and 'preference lost' in result.stdout, result.stdout
                print('negative control: schema reset regression detected')
                return
            subprocess.run(command + [scenario], check=True, timeout=120)
            if scenario == 'write':
                saved = root / 'tiger_sentence.options.yaml'
                assert saved.exists(), 'no persistent preference file'
                contents, timestamp = saved.read_bytes(), saved.stat().st_mtime_ns
                # A new OS process cannot inherit Lua's cached table.
                subprocess.run(command + ['read'], check=True, timeout=120)
                assert saved.read_bytes() == contents and saved.stat().st_mtime_ns == timestamp, 'restoration rewrote preferences'


if __name__ == '__main__':
    main()
