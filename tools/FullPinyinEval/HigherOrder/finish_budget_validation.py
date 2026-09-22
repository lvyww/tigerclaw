"""Run only after all build/decode/feedback jobs finish, to avoid timing interference."""
import json
import subprocess
import sys
import time
from pathlib import Path
from budget500 import O

start = time.monotonic()
while not (O/'test-single-summary.json').exists() or not (O/'feedback-report.json').exists():
    if time.monotonic()-start > 1800:
        raise TimeoutError('Final decode/feedback')
    time.sleep(5)
selection = json.loads((O/'dev-selection.json').read_text())
root = Path(__file__).parent
jobs = [
    ['q8', str(O/'models/joint3-q8.klm'), str(O/'test3q8.jsonl')],
    ['fusion', str(O/'models/joint3-q8.klm'), str(O/'test-fusion.jsonl'), str(O/'models/wd1e8.klm'), '0.625'],
    ['single', str(O/'models'/selection['singleModel']), str(O/'test-single.jsonl')],
]
for args in jobs:
    subprocess.run([sys.executable, str(root/'budget_verify.py')] + args, check=True)
subprocess.run([sys.executable, str(root/'budget_report.py')], check=True)
print('FINAL VALIDATION COMPLETE', flush=True)
