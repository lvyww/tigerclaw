"""Read-only progress for a running adaptive WeType collection."""
import argparse
import json
from pathlib import Path


def main():
    p=argparse.ArgumentParser()
    p.add_argument('root',type=Path)
    a=p.parse_args();root=a.root
    manifest=json.loads((root/'manifest.json').read_text())
    latest={};last={};attempts=0;interrupted=0
    path=root/'results.jsonl'
    if path.exists():
        with path.open(encoding='utf-8') as stream:
            for line in stream:
                # The last write may still be in flight; complete JSONL records are flushed.
                if not line.endswith('\n'):break
                row=json.loads(line);last=row
                if row['event']=='result':
                    attempts+=1;interrupted+=row['interrupted']
                if row['event']=='decision' and row['valid']:
                    latest[(row['id'],row['stage'])]=row
    fast=[v for (i,s),v in latest.items() if s=='fast']
    slow=[v for (i,s),v in latest.items() if s=='slow']
    resolved={v['id'] for v in slow}|{v['id'] for v in fast if v['correct']}
    passed={v['id'] for v in latest.values() if v['correct']}
    summary=dict(queueCases=manifest['count'],newFastQueued=manifest['newFast'],priorErrorsQueued=manifest['backlogSlow'],
                 resultAttempts=attempts,interruptedAttempts=interrupted,resolvedCases=len(resolved),
                 fastValid=len(fast),fastCorrect=sum(v['correct'] for v in fast),
                 slowValid=len(slow),slowCorrect=sum(v['correct'] for v in slow),
                 adaptivePassed=len(passed),lastEvent=last.get('event'),lastDetail=last.get('detail'),
                 interpretation='Fast-only accuracy and adaptive two-attempt pass rate must be reported separately; prior errors are slow-only backlog.')
    print(json.dumps(summary,ensure_ascii=False,indent=2))


if __name__=='__main__':main()
