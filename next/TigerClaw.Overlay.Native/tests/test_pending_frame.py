"""Run the real-window trace and verify removing the optional hint reproduces hiding."""
import argparse
import json
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('executable', type=Path)
parser.add_argument('trace', type=Path)
args = parser.parse_args()
for extra in ([], ['--without-hint']):
    result = subprocess.run([str(args.executable), str(args.trace), *extra],
                            capture_output=True, text=True, timeout=60)
    if not extra:
        if result.returncode:
            raise RuntimeError(f'Pending-frame regression failed: {result.stdout}\n{result.stderr}')
        print(result.stdout, end='', flush=True)
    else:
        expected = 'Pending suffix hid or changed published candidate frame'
        if result.returncode != 1 or expected not in result.stderr:
            raise RuntimeError(f'Old-protocol hiding was not reproduced: {result}')
        print(json.dumps({'negative_control': 'without_pending_hint', 'status': 'passed'}), flush=True)
