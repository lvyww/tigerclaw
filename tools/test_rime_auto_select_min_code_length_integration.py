"""Verify numeric schema settings through real librime, including process restart."""
import argparse
import json
from pathlib import Path
import subprocess
from run_regressions import isolated_sources, temporary_tree


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True)
    parser.add_argument('--plugin', required=True)
    args = parser.parse_args()
    checks = 0
    cases = [(None, 3), (0, 0), (1, 1), (2, 2), (3, 3), (4, 4),
             (128, 128), (-9, 0), (129, 128), ('"invalid"', 3)]
    for value, expected in cases:
        with temporary_tree() as root:
            isolated_sources(root)
            (root / 'tiger_sentence.codes.txt').write_text(
                '甲\tab\n乙\tab\n丙\tabc\n丁\tabc\n甲乙\tabc\n己\tabcd\n庚\tabcd\n戊\txy\n')
            (root / 'tiger_sentence.char_ranks.txt').write_text('')
            (root / 'tiger_sentence.full_code_whitelist.txt').write_text('')
            schema = root / 'tiger_sentence.schema.yaml'
            schema.write_text(schema.read_text().replace('  auto_select_min_code_length: 3\n', ''))
            patch = 'patch:\n  tiger_sentence/high_freq_limit: 0\n'
            if value is not None:
                patch += '  tiger_sentence/auto_select_min_code_length: ' + str(value) + '\n'
            # Removed settings, whether in old schema patches or preference
            # stores, cannot change the new setting/default.
            patch += '  tiger_sentence/option_defaults/tiger_sentence_allow_duplicate_single: false\n'
            (root / 'tiger_sentence.custom.yaml').write_text(patch)
            (root / 'tiger_sentence.options.yaml').write_text('options:\n  tiger_sentence_allow_duplicate_single: false\n')
            with (root / 'rime.lua').open('a') as output:
                output.write('\nrequire("tiger_sentence").set_model_enabled(false)\n')
            shared = root / '_shared'
            shared.mkdir()
            (shared / 'default.yaml').write_text(
                'config_version: "1.0"\nschema_list:\n  - schema: tiger_sentence\n'
                'menu:\n  page_size: 5\nrecognizer:\n  patterns: {}\n')
            command = [str(Path(args.exe).resolve()), str(root), str(shared),
                       str(Path(args.plugin).resolve()), str(expected)]
            for restart in (False, True):
                result = subprocess.run(command, text=True, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, timeout=120)
                if result.returncode:
                    raise RuntimeError(f'config={value!r} restart={restart}\n{result.stdout}')
                rows = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
                assert len(rows) == 1 and rows[0]['status'] == 'passed', result.stdout
                row = {**rows[0], 'configured': value, 'restarted_process': restart}
                checks += row['checks']
                print(json.dumps(row, ensure_ascii=False), flush=True)
    print(json.dumps({'status': 'passed', 'real_librime_process_cases': len(cases) * 2,
                      'candidate_checks': checks, 'frontend_installation_performed': False}), flush=True)


if __name__ == '__main__':
    main()
