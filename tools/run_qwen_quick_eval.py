#!/usr/bin/env python3
"""WSL driver: run one isolated ARM64 service at a time, never deploy."""
import argparse
import json
import subprocess
import time
from pathlib import Path

def win(path):
    return subprocess.check_output(['wslpath','-w',str(Path(path).resolve())],text=True).strip()

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--repo',type=Path,required=True)
    p.add_argument('--work',type=Path,required=True)
    p.add_argument('--assembly',type=Path,required=True)
    p.add_argument('--q4',type=Path,required=True)
    p.add_argument('--dotnet',default='/mnt/c/Program Files/dotnet/dotnet.exe')
    p.add_argument('--start-at', default='score:B', choices=['score:B','score:C','perf:B1','perf:C1','perf:C2','perf:B2'])
    a=p.parse_args()
    commands=[]
    for mode,label in [('score','B'),('score','C'),('perf','B1'),('perf','C1'),('perf','C2'),('perf','B2')]:
        model=a.q4 if label.startswith('C') else a.repo/'release_arm64/sentence/Models/sentence-qwen-q8.gguf'
        cases=a.work/('cases.json' if mode=='score' else 'performance-cases.json')
        cmd=[a.dotnet,win(a.assembly),'--quick',win(a.repo),win(a.work),mode,label,win(model),win(cases)]
        commands.append(cmd)
    (a.work/'commands.json').write_text(json.dumps(commands,indent=2))
    start = next(i for i,c in enumerate(commands) if ':'.join(c[5:7]) == a.start_at)
    for cmd in commands[start:]:
        mode,label=cmd[5:7]
        status=dict(mode=mode,label=label,started=time.time(),command=cmd)
        (a.work/'status.json').write_text(json.dumps(status,indent=2))
        print('START',mode,label,flush=True)
        with (a.work/f'{mode}-{label}.log').open('w') as log:
            result=subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT)
        status.update(exit_code=result.returncode,finished=time.time())
        (a.work/'status.json').write_text(json.dumps(status,indent=2))
        if result.returncode:
            raise RuntimeError(f'{mode} {label} failed; see log')
        print('DONE',mode,label,flush=True)

if __name__=='__main__':main()
