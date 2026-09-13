"""Run the real-window trace; reject three independently broken frame policies."""
import argparse
import json
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('executable', type=Path)
parser.add_argument('trace', type=Path)
args = parser.parse_args()
checks = [
    (args.executable, [], None, None),
    (args.executable, ['--without-hint'], 'without_pending_hint',
     'Pending suffix hid or changed published candidate frame'),
    (args.executable.with_name('overlay_pending_frame_no_session.exe'), [], 'coalesced_session',
     'Coalesced new input retained previous candidates'),
    (args.executable.with_name('overlay_pending_frame_no_code_hold.exe'), [], 'code_only_hold',
     'Published code-only frame lost hold eligibility'),
]
for executable, extra, control, expected in checks:
    result = subprocess.run([str(executable), str(args.trace), *extra],
                            capture_output=True, text=True, timeout=90)
    if control is None:
        if result.returncode:
            raise RuntimeError(f'Pending-frame regression failed: {result.stdout}\n{result.stderr}')
        print(result.stdout, end='', flush=True)
    else:
        if result.returncode != 1 or expected not in result.stderr:
            raise RuntimeError(f'{control} was not rejected for the expected reason: {result}')
        print(json.dumps({'negative_control': control, 'status': 'passed'}), flush=True)
