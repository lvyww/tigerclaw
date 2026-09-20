"""Real librime/librime-lua learning with production data in owned user dirs."""
import argparse
from pathlib import Path
import subprocess
from run_regressions import isolated_sources, temporary_tree


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True)
    parser.add_argument('--plugin', required=True)
    parser.add_argument('--model', required=True)
    args = parser.parse_args()
    for selection in ('tap', 'tab', 'tab-comma', 'tab-period',
                      'tab-continue', 'tab-continue-comma', 'tab-continue-period',
                      'tab-buffer-continue-comma', 'tab-buffer-continue-period'):
        with temporary_tree() as root:
            isolated_sources(root)
            shared = root / '_shared'
            shared.mkdir()
            (shared / 'default.yaml').write_text(
                'config_version: "1.0"\nschema_list:\n  - schema: tiger_sentence\n'
                'menu:\n  page_size: 20\nrecognizer:\n  patterns: {}\n')
            (root / 'models').mkdir()
            (root / 'models/sentence-ngram-mobile.bin').symlink_to(Path(args.model).resolve())
            subprocess.run([str(Path(args.exe).resolve()), str(root), str(shared),
                            str(Path(args.plugin).resolve()), selection], check=True, timeout=120)


if __name__ == '__main__':
    main()
