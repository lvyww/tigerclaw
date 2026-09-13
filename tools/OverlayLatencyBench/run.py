"""Windows-only isolated sequential replay. UI-done is NOT screen presentation."""
import argparse
import ctypes as C
from ctypes import wintypes as W
import hashlib
import json
import mmap
import platform
from pathlib import Path
import random
import queue
import shutil
import statistics
import struct
import subprocess
import time
import threading

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('stage', type=Path)
p.add_argument('--count', type=int, default=1000)
p.add_argument('--rounds', type=int, default=2)
p.add_argument('--variants', nargs='+', choices=['wpf', 'native'], default=['wpf', 'native'])
p.add_argument('--scenarios', nargs='+', choices=['fixed', 'resize', 'vertical', 'sentence'], default=['fixed', 'resize', 'vertical', 'sentence'])
p.add_argument('--label', default='results', help='Alphanumeric/hyphen output stem; preserves separate runs')
p.add_argument('--snapshot-publish', action='store_true', help='Mirror the new mutex-protected snapshot + notification publisher')
p.add_argument('--publisher-assembly', type=Path, help='Use the actual C# UiStatePublisher via isolated Core.Tests stdio probe')
p.add_argument('--dotnet', default='dotnet')
a = p.parse_args()
if a.publisher_assembly and a.snapshot_publish:
    p.error('Choose actual publisher OR Python protocol mirror')
if not a.label or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_' for c in a.label):
    p.error('Invalid output label')
stage = a.stage.resolve()
manifest = json.loads((stage / 'manifest.json').read_text())
session = manifest['session']
root = 'Local\\TigerClaw.Overlay.Test.' + session
k = C.WinDLL('kernel32', use_last_error=True)
u = C.WinDLL('user32', use_last_error=True)
u.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]
u.PostMessageW.argtypes = [W.HWND, W.UINT, W.WPARAM, W.LPARAM]
k.GetTickCount64.restype = C.c_ulonglong
k.CreateMutexW.argtypes = [C.c_void_p, W.BOOL, W.LPCWSTR]
k.CreateMutexW.restype = W.HANDLE
k.CreateEventW.argtypes = [C.c_void_p, W.BOOL, W.BOOL, W.LPCWSTR]
k.CreateEventW.restype = W.HANDLE
k.WaitForSingleObject.argtypes = [W.HANDLE, W.DWORD]
k.ReleaseMutex.argtypes = [W.HANDLE]
k.SetEvent.argtypes = [W.HANDLE]
k.CloseHandle.argtypes = [W.HANDLE]
freq = C.c_longlong()
k.QueryPerformanceFrequency(C.byref(freq))
def qpc():
    n = C.c_longlong()
    k.QueryPerformanceCounter(C.byref(n))
    return n.value

class Memory(C.Structure):
    _fields_ = [('cb', W.DWORD), ('PageFaultCount', W.DWORD)] + [(name, C.c_size_t) for name in
        ['PeakWorkingSetSize', 'WorkingSetSize', 'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage',
         'QuotaPeakNonPagedPoolUsage', 'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage']]
ps = C.WinDLL('psapi')
ps.GetProcessMemoryInfo.argtypes = [W.HANDLE, C.POINTER(Memory), W.DWORD]
k.GetProcessTimes.argtypes = [W.HANDLE] + [C.POINTER(W.FILETIME)] * 4
def metrics(child):
    m = Memory(); m.cb = C.sizeof(m)
    if not ps.GetProcessMemoryInfo(int(child._handle), C.byref(m), m.cb):
        raise C.WinError()
    stamps = [W.FILETIME() for _ in range(4)]
    if not k.GetProcessTimes(int(child._handle), *[C.byref(x) for x in stamps]):
        raise C.WinError()
    cpu = sum((s.dwHighDateTime << 32) + s.dwLowDateTime for s in stamps[2:]) / 1e7
    return dict(working_set=m.WorkingSetSize, private_commit=m.PrivateUsage, cpu_seconds=cpu)

def close_owned(child):
    if child.poll() is not None:
        return
    callback_type = C.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
    @callback_type
    def visit(hwnd, _):
        owner = W.DWORD()
        u.GetWindowThreadProcessId(hwnd, C.byref(owner))
        if owner.value == child.pid:
            u.PostMessageW(hwnd, 0x10, 0, 0)  # Only this exact test child's windows.
        return True
    u.EnumWindows(visit, 0)
    try:
        child.wait(timeout=5)
    except subprocess.TimeoutExpired:
        child.terminate(); child.wait(timeout=5)

native = stage / 'native-build/Release/TigerClaw.Overlay.Native.Test.exe'
shutil.copy2(native.with_name('TigerClaw.Overlay.exe'), native)
wpf = stage / 'TigerClaw.Overlay/bin/Release/net48/TigerClaw.Overlay.Wpf.Latency.exe'
ui = mmap.mmap(-1, 128 * 1024, tagname=root + '.Ui')
core = mmap.mmap(-1, 16, tagname=root + '.Core')
ack = mmap.mmap(-1, 32, tagname=root + '.Ack')
snapshot = mmap.mmap(-1, 128 * 1024, tagname=root + '.Ui.Snapshot.v2') if a.snapshot_publish else None
gate = k.CreateMutexW(None, False, root + '.Ui.Snapshot.v2.Lock') if snapshot else None
changed = k.CreateEventW(None, False, False, root + '.Ui.Snapshot.v2.Changed') if snapshot else None
if snapshot and (not gate or not changed):
    raise C.WinError()
result = dict(session=session, frequency=freq.value, count=a.count,
              snapshot_publish=a.snapshot_publish,
              actual_publisher=str(a.publisher_assembly) if a.publisher_assembly else None,
              environment=platform.platform(), replay_version=2,
              single_poll_diagnostic=manifest['single_poll_diagnostic'] if 'single_poll_diagnostic' in manifest else False,
              caveat='Sequential replay, sound and reveal delays off. Application-update completion is NOT photon/present latency; WPF deferred layout/render is excluded.',
              binaries={str(x): hashlib.sha256(x.read_bytes()).hexdigest() for x in
                        [wpf if v == 'wpf' else native for v in a.variants]}, groups=[])
raw = []
seq = 0
publisher = None
publisher_lines = None
def drain_lines(process, lines):
    for line in process.stdout:
        lines.put(line)
    lines.put('')

def stop_publisher():
    global publisher
    if publisher:
        publisher.stdin.close()
        try:
            publisher.wait(timeout=5)
        except subprocess.TimeoutExpired:
            publisher.terminate(); publisher.wait(timeout=5)
        publisher = None
rng = random.Random(7219)
def publish(child, state):
    global seq
    seq += 1
    payload = json.dumps(state, ensure_ascii=False).encode('utf-8')
    tick = k.GetTickCount64()
    core[:16] = struct.pack('<qq', seq, tick)
    start = qpc()
    # Same legacy order as UiStatePublisher, including sequence-before-payload.
    if publisher:
        publisher.stdin.write(json.dumps(state, ensure_ascii=True) + '\n'); publisher.stdin.flush()
        reply = publisher_lines.get(timeout=10).split()
        if len(reply) != 3 or int(reply[0]) != seq:
            raise RuntimeError('Actual publisher acknowledgement mismatch: ' + str(reply))
        start, published = map(int, reply[1:])
    else:
        ui[:8] = struct.pack('<q', seq)
        ui[8:20] = struct.pack('<qi', tick, len(payload))
        ui[20:20+len(payload)] = payload
        ui.flush()
    if snapshot:
        held = k.WaitForSingleObject(gate, 0)
        if held in (0, 0x80):
            try:
                snapshot[:20] = struct.pack('<qqi', 0, tick, len(payload))
                snapshot[20:20+len(payload)] = payload
                snapshot[:8] = struct.pack('<q', seq)
            finally:
                k.ReleaseMutex(gate)
        k.SetEvent(changed)
    if not publisher:
        published = qpc()
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        seen, read, dispatch, done = struct.unpack('<qqqq', ack[:32])
        if seen == seq:
            if not start <= read <= dispatch <= done:
                raise RuntimeError('Invalid timestamp order')
            return dict(seq=seq, start=start, published=published, read=read, ui=dispatch, done=done)
        if child.poll() is not None:
            raise RuntimeError(f'Test child exited: {child.returncode}')
        time.sleep(.001)
    raise TimeoutError(f'No acknowledgement for {seq}')

def summary(rows, left, right):
    xs = sorted((r[right] - r[left]) * 1000 / freq.value for r in rows)
    return dict(mean=statistics.mean(xs), p50=xs[int((len(xs)-1)*.5)],
                p95=xs[int((len(xs)-1)*.95)], p99=xs[int((len(xs)-1)*.99)], maximum=xs[-1])

try:
    for round_number in range(a.rounds):
        for variant in (a.variants if round_number % 2 == 0 else list(reversed(a.variants))):
            core[:16] = struct.pack('<qq', 1, k.GetTickCount64())
            ui[:20] = bytes(20); ack[:] = bytes(32)
            seq = 0
            if a.publisher_assembly:
                publisher = subprocess.Popen([a.dotnet, str(a.publisher_assembly), '--ui-publish-stdio', session],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding='utf-8')
                publisher_lines = queue.Queue()
                threading.Thread(target=drain_lines, args=(publisher, publisher_lines), daemon=True).start()
                if publisher_lines.get(timeout=10).strip() != 'READY':
                    stop_publisher()
                    raise RuntimeError('Actual publisher failed to initialize')
            exe = wpf if variant == 'wpf' else native
            command = [str(exe)] + ([] if variant == 'wpf' else ['--test-session', session])
            try:
                child = subprocess.Popen(command, cwd=exe.parent)
            except BaseException:
                stop_publisher()
                raise
            try:
                for scenario in a.scenarios:
                    rng.seed(7219)
                    rows = []
                    before = metrics(child)
                    began = time.perf_counter()
                    for i in range(a.count + 50):
                        # ASCII code changes every generation; Chinese candidates exercise font fallback.
                        code = f'{i:08x}'
                        candidates = ['而三', '旋', '而疋', '可以', '输入法']
                        if scenario == 'resize': code += 'abcdefgh' * (i % 6)
                        if scenario == 'sentence': candidates = ['新人上午来面试，讨论输入法候选窗口的显示效果' + x for x in candidates]
                        state = dict(CandidateVisible=True, IsChinese=True, InputCode=code,
                            Candidates=candidates, CandidateAnnotations=['le ts', 'lets', 'le ts', '', ''],
                            ShowInputCodeInCandidateWindow=True, ShowCandidateIndex=True,
                            VerticalCandidates=scenario == 'vertical', FontSize=20, FontName='Microsoft YaHei',
                            SelectedCandidateIndex=-1, ThemeName='',
                            CaretX=120, CaretY=180, CaretHeight=20, HideStatusBar=True,
                            CandidateExpandDelayMs=0, AnnotationExpandDelayMs=0, SoundVolumePercent=0)
                        row = publish(child, state)
                        if i >= 50:
                            row.update(variant=variant, scenario=scenario, round=round_number)
                            rows.append(row); raw.append(row)
                        time.sleep(rng.choice([0, .003, .007]))
                    after = metrics(child)
                    group = dict(variant=variant, scenario=scenario, round=round_number,
                                 received=len(rows), missing=0,
                                 publish_to_read=summary(rows, 'start', 'read'),
                                 read_to_ui=summary(rows, 'read', 'ui'),
                                 ui_update=summary(rows, 'ui', 'done'),
                                 publish_to_update=summary(rows, 'start', 'done'),
                                 memory=after, cpu_seconds=after['cpu_seconds']-before['cpu_seconds'],
                                 elapsed_seconds=time.perf_counter()-began)
                    result['groups'].append(group)
                    (stage / (a.label + '.json')).write_text(json.dumps(result, indent=2))
                    print(variant, scenario, 'read p50/p95:', round(group['publish_to_read']['p50'], 2),
                          round(group['publish_to_read']['p95'], 2), 'update p50:', round(group['ui_update']['p50'], 2), flush=True)
            finally:
                close_owned(child)
                stop_publisher()
finally:
    stop_publisher()
    (stage / (a.label + '-raw.json')).write_text(json.dumps(raw))
    (stage / (a.label + '.json')).write_text(json.dumps(result, indent=2))
    ui.close(); core.close(); ack.close()
    if snapshot:
        snapshot.close(); k.CloseHandle(changed); k.CloseHandle(gate)
print('Saved', stage / (a.label + '.json'))
