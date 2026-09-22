"""Build one independent IRSTLM weighted-difference / KenLM Q8 variant."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from budget500 import O, E, sha, dump

S = Path('/home/yc/tmp/tiger500')
name, threshold = sys.argv[1:3]
checkpoint = S / 'original.irstlm'
source = checkpoint if checkpoint.exists() else S / 'sorted.arpa'
model = O / 'models' / (name + '.klm')
assert not model.exists()
arpa = S / (name + '.arpa')
assert not arpa.exists()
env = os.environ.copy()
env['TMP'] = str(S)
commands = [
    [str(S / 'prune500'), str(source), str(arpa), threshold, str(S / 'probes.txt'),
     '-' if checkpoint.exists() else str(checkpoint)],
    [str(E / 'bin/build_binary'), '-T', str(S), '-S', '3G', '-q', '8', '-b', '8', '-a', '64',
     'trie', str(arpa), str(S / (name + '.klm'))],
]
for i, cmd in enumerate(commands):
    with (O / 'logs' / f'{name}-build{i}.log').open('x') as log:
        print('RUN', cmd, flush=True)
        subprocess.run(cmd, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
local = S / (name + '.klm')
shutil.copyfile(local, model)
digest = sha(local)
assert digest == sha(model)
dump(O / (name + '-build.json'), {'threshold': threshold, 'method': 'IRSTLM absolute weighted difference, all orders 2..5; forward-prefix loader; backoffs recalculated',
     'commands': commands, 'bytes': model.stat().st_size, 'sha256': digest,
     'under500MB': model.stat().st_size <= 500_000_000,
     'withTrigramQ8Under500MB': model.stat().st_size + (O / 'models/joint3-q8.klm').stat().st_size <= 500_000_000})
print('BUILT', name, model.stat().st_size, flush=True)
