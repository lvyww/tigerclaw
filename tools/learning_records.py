#!/usr/bin/env python3
"""Maintain a Tigirl/TigerClaw V1 journal without touching code tables (Python 3.10+).
Examples: python tools/learning_records.py SCHEME/.tigirl-learning-v1.log show
          python tools/learning_records.py SCHEME/.tigirl-learning-v1.log undo
          python tools/learning_records.py SCHEME/.tigirl-learning-v1.log clear --yes
Use --help for export/import/compaction. All writers use the product lock file.
"""
from __future__ import annotations
import argparse, contextlib, json, os, pathlib, sys, tempfile, time, uuid, zlib
LIMIT = 16 * 1024 * 1024
NAMES = {'.tigirl-learning-v1.log', '.tigerclaw-learning-v1.log'}

def checked_path(value: str) -> pathlib.Path:
    path = pathlib.Path(value).absolute()
    if path.name.lower() not in NAMES:
        raise ValueError('Expected exactly .tigirl-learning-v1.log or .tigerclaw-learning-v1.log; code tables are never accepted')
    return path

@contextlib.contextmanager
def locked(path: pathlib.Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    if os.name == 'nt':
        import ctypes as C
        from ctypes import wintypes as W
        class OVERLAPPED(C.Structure):
            _fields_ = [('Internal', C.c_size_t), ('InternalHigh', C.c_size_t), ('Offset', W.DWORD), ('OffsetHigh', W.DWORD), ('hEvent', W.HANDLE)]
        k = C.WinDLL('kernel32', use_last_error=True)
        k.CreateFileW.argtypes = [W.LPCWSTR, W.DWORD, W.DWORD, C.c_void_p, W.DWORD, W.DWORD, W.HANDLE]; k.CreateFileW.restype = W.HANDLE
        k.LockFileEx.argtypes = [W.HANDLE, W.DWORD, W.DWORD, W.DWORD, W.DWORD, C.POINTER(OVERLAPPED)]
        k.UnlockFileEx.argtypes = [W.HANDLE, W.DWORD, W.DWORD, W.DWORD, C.POINTER(OVERLAPPED)]
        k.CloseHandle.argtypes = [W.HANDLE]
        deadline = time.monotonic() + 10
        while True:
            handle = k.CreateFileW(str(path) + '.lock', 0xc0000000, 3, None, 4, 0x80, None)
            if handle != C.c_void_p(-1).value: break
            error = C.get_last_error()
            if error not in (32, 33) or time.monotonic() >= deadline: raise C.WinError(error)
            time.sleep(.01)
        ov = OVERLAPPED()
        try:
            if not k.LockFileEx(handle, 2, 0, 1, 0, C.byref(ov)): raise C.WinError(C.get_last_error())
            try: yield
            finally: k.UnlockFileEx(handle, 0, 1, 0, C.byref(ov))
        finally: k.CloseHandle(handle)
    else:
        import fcntl
        fd = os.open(str(path) + '.lock', os.O_CREAT | os.O_RDWR, 0o600)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX)
            try: yield
            finally: fcntl.flock(fd, fcntl.LOCK_UN)
        finally: os.close(fd)

def seal(fields: list[str]) -> bytes:
    body = '\t'.join(fields).encode('ascii')
    return body + b'\t' + str(zlib.crc32(body)).encode() + b'\n'

def hex_text(text: str) -> str:
    return text.encode('utf-16-be', errors='strict').hex()

def text_hex(value: str) -> str:
    if len(value) % 4 or len(value) > 2048: raise ValueError('Invalid UTF-16 field')
    return bytes.fromhex(value).decode('utf-16-be', errors='strict')

def valid_event(e: dict) -> bool:
    try:
        for key in ('id', 'mode', 'code', 'text', 'context'):
            if not isinstance(e[key], str): return False
            e[key].encode('utf-16-be', errors='strict')
        return (0 < len(e['id']) <= 128 and e['id'].isascii() and not any(c in e['id'] for c in '\t\r\n')
            and isinstance(e['time'], int) and 0 <= e['time'] <= 9223372036854775807
            and 0 < len(e['mode'].encode('utf-16-be')) <= 1024 and 0 < len(e['code'].encode('utf-16-be')) <= 256
            and 0 < len(e['text']) <= 16 and len(e['context']) <= 2
            and not any(ord(c) < 32 or ord(c) == 127 or 0xe000 <= ord(c) <= 0xf8ff or c in '{}' for c in e['text']))
    except (KeyError, UnicodeError, TypeError): return False

def event_line(e: dict) -> bytes:
    if not valid_event(e): raise ValueError('Invalid learning event in import')
    return seal(['TCL1', 'E', e['id'], str(e['time'])] + [hex_text(e[k]) for k in ('mode', 'code', 'text', 'context')])

def parse(data: bytes) -> tuple[list[dict], set[str]]:
    events, seen, removed = [], set(), set()
    for line in data.split(b'\n')[:-1]:
        try:
            if len(line) > 8192: continue
            body, crc = line.rsplit(b'\t', 1)
            if int(crc) != zlib.crc32(body): continue
            f = body.decode('ascii').split('\t')
            if len(f) < 4 or f[0] != 'TCL1' or not 0 < len(f[2]) <= 128 or f[2] in seen or not 0 <= int(f[3]) <= 9223372036854775807: continue
            if f[1] == 'E' and len(f) == 8:
                e = dict(zip(('mode', 'code', 'text', 'context'), map(text_hex, f[4:])), id=f[2], time=int(f[3]))
                if not valid_event(e): continue
                events.append(e); seen.add(f[2])
            elif f[1] == 'U' and len(f) == 5 and len(f[4]) <= 128:
                removed.add(f[4]); seen.add(f[2])
            elif f[1] == 'C' and len(f) == 4:
                events.clear(); removed.clear(); seen.add(f[2])
        except (ValueError, UnicodeError): continue
    return [e for e in events if e['id'] not in removed][-10000:], seen

def read(path: pathlib.Path) -> bytes:
    if not path.exists(): return b''
    if path.stat().st_size > 64 * 1024 * 1024: raise ValueError('Refusing a journal over 64 MiB')
    return path.read_bytes()

def replace(path: pathlib.Path, data: bytes):
    if len(data) > LIMIT: raise ValueError('Compacted journal still exceeds 16 MiB; preserve a backup and start a fresh journal with both IMEs stopped')
    fd, name = tempfile.mkstemp(prefix=path.name + '.tmp-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        os.replace(name, path)
        if os.name != 'nt':
            folder = os.open(path.parent, os.O_RDONLY)
            try: os.fsync(folder)
            finally: os.close(folder)
    finally:
        if os.path.exists(name): os.unlink(name)

def compact(events: list[dict], seen: set[str]) -> bytes:
    active = {e['id'] for e in events}
    # Keep retired event IDs as U tombstones: delayed/replayed commits must NOT
    # resurrect after undo, clear or compaction. Target self is inactive already.
    tombstones = b''.join(seal(['TCL1', 'U', identity, '0', identity]) for identity in sorted(seen - active))
    return tombstones + b''.join(event_line(e) for e in events)

def maintain(path: pathlib.Path, action: str, source: pathlib.Path | None = None) -> dict:
    if action in ('show', 'export') and not path.exists(): return {'format': 'TCL1', 'events': [], 'count': 0}
    with locked(path):
        data = read(path); events, seen = parse(data)
        if action in ('show', 'export'): return {'format': 'TCL1', 'events': events, 'count': len(events)}
        if action == 'undo':
            if not events: return {'changed': False, 'count': 0}
            events.pop()
        elif action == 'clear': events = []
        elif action == 'import':
            if source is None or source.stat().st_size > LIMIT: raise ValueError('Missing or oversized import')
            payload = json.loads(source.read_text(encoding='utf-8'))
            if payload.get('format') != 'TCL1' or not isinstance(payload.get('events'), list) or len(payload['events']) > 10000: raise ValueError('Invalid TCL1 export')
            for e in payload['events']:
                if not valid_event(e): raise ValueError('Invalid event; nothing was written')
                if e['id'] not in seen: events.append(e); seen.add(e['id'])
            events = events[-10000:]
        elif action != 'compact': raise ValueError('Unknown action')
        replacement = compact(events, seen); replace(path, replacement)
        return {'changed': replacement != data, 'count': len(events), 'bytes_before': len(data), 'bytes_after': len(replacement)}

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('journal'); parser.add_argument('action', choices=['show', 'undo', 'clear', 'export', 'import', 'compact'])
    parser.add_argument('--file', type=pathlib.Path, help='JSON file for export/import')
    parser.add_argument('--yes', action='store_true', help='Required for clear/import/compact')
    args = parser.parse_args()
    try:
        path = checked_path(args.journal)
        if args.action in ('clear', 'import', 'compact') and not args.yes: raise ValueError('This operation requires --yes')
        if args.action in ('export', 'import') and args.file is None: raise ValueError('export/import requires --file')
        if args.file is not None and args.file.absolute() == path: raise ValueError('JSON file cannot be the live journal')
        result = maintain(path, args.action, args.file)
        if args.action == 'export':
            # Exclusive create; never silently replace a previous backup.
            with args.file.open('x', encoding='utf-8') as out: json.dump(result, out, ensure_ascii=False, indent=2)
            result = {'exported': str(args.file), 'count': result['count']}
        print(json.dumps(result, ensure_ascii=False, indent=2)); return 0
    except (OSError, ValueError, TypeError) as e:
        print(f'Learning maintenance failed: {e}', file=sys.stderr); return 1
if __name__ == '__main__': raise SystemExit(main())
