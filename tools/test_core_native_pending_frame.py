"""Real-model replay: pending frames survive a missed TSF activation notification.

Usage: python3 tools/test_core_native_pending_frame.py EXE MODEL TABLE NEW_ROOT
Run from WSL with Windows executable/model paths accessible in the workspace.
The fixture disables learning and never opens production IPC or changes registration.
"""
import json
from pathlib import Path
import shutil
import subprocess
import sys


def main():
    exe, model, table, root = map(Path, sys.argv[1:])
    root.mkdir(parents=True, exist_ok=False)
    (root / 'Models').mkdir()
    (root / 'tables/Test整句').mkdir(parents=True)
    shutil.copyfile(model, root / 'Models/sentence-fivegram-mobile.bin')
    shutil.copyfile(table, root / 'tables/Test整句/table.txt')
    (root / 'config.txt').write_text(
        '码表存储位置\ttables\n当前码表\tTest整句\n整句神经重排\t否\n'
        '整句自动提前上屏\t是\n高频字仅使用最优码组句\t0\n整句Tab自学习\t否\n', encoding='utf-8')
    requests = []
    for iteration in range(8):
        # No ime_active=true: Core can restart while the existing TSF remains active.
        for c in 'vujg':
            requests.append(dict(type='key', vk=ord(c.upper()), action='down', caret_x=100, caret_y=100))
            requests.extend(dict(type='query_state') for _ in range(200))
        requests.append(dict(type='focus', hwnd=str(1000 + iteration), processId=1000 + iteration))
        requests.append(dict(type='ime_active', active=False))
        requests.append(dict(type='composition_canceled'))
    source = root / 'requests.jsonl'
    source.write_text(''.join(json.dumps(r) + '\n' for r in requests), encoding='utf-8')
    target = root / 'responses.jsonl'
    def win(path):
        return subprocess.check_output(['wslpath', '-w', str(path.resolve())], text=True).strip()
    subprocess.run([str(exe.resolve()), '--runtime-probe', win(root), win(source), win(target)], check=True)
    states = [json.loads(s) for s in Path(str(target) + '.ui.jsonl').read_text(encoding='utf-8').splitlines()]
    assert len(states) == len(requests)
    pending = 0
    frame = None
    for request, state in zip(requests, states):
        if request['type'] == 'key' and request['vk'] == ord('V'):
            frame = state['CandidateFrameSession']
        if request['type'] in ('key', 'query_state'):
            assert state['CandidateFrameSession'] == frame, 'Continuation replaced its frame identity'
        if request['type'] in ('focus', 'ime_active', 'composition_canceled'):
            assert not state['CandidateHoldWhilePending'], 'Explicit deactivation/cancellation must revoke hold'
        elif state['InputCode'] and not state['Candidates'] and not state['CandidateVisible']:
            pending += 1
            assert state['CandidateHoldWhilePending'], 'Active pending input lost its previous frame without ime_active'
    assert pending, 'Replay did not observe pending decoding; test is inconclusive'
    assert any(s['InputCode'] == 'j' and not s['Candidates'] and s['CandidateVisible'] for s in states)
    assert any(s['InputCode'] == 'jg' and s['Candidates'] == ['个'] for s in states)
    print(f'Passed: {len(states)} snapshots, {pending} pending frames; explicit inactive/cancel gates retained')


if __name__ == '__main__':
    main()
