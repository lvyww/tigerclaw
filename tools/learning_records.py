#!/usr/bin/env python3
"""查看、撤销、备份或整理可读自学习文件；不读取旧版日志，不修改码表。

show 显示汇总等级；直接编辑文件前请退出使用该输入法的程序。
"""
from __future__ import annotations
import argparse, contextlib, datetime as dt, json, math, os, pathlib, sys, tempfile, time, uuid
LIMIT=16*1024*1024
NAMES={'自学习-虎爪.txt','自学习-虎娘.txt','自学习-全拼.txt'}
FORMAT='虎整句自学习-2'
HEADER='# 虎整句自学习记录（每条学习行代表一次人工纠正；等级上限10）\n# 操作\t时间(UTC)\t片段\t编码\t前文\t本次升级\t模式\t记录编号\t撤销目标\n'.encode('utf-8')
def checked_path(value):
    path=pathlib.Path(value).absolute()
    if path.name not in NAMES:raise ValueError('请选择自学习-虎爪.txt、自学习-虎娘.txt或自学习-全拼.txt；不接受码表或备份文件')
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


def escape_text(s):
    return s.replace('\\','\\\\').replace('\t','\\t').replace('\r','\\r').replace('\n','\\n')
def unescape_text(s):
    out=[];i=0;codes={'t':'\t','r':'\r','n':'\n','\\':'\\'}
    while i<len(s):
        c=s[i]
        if c=='\\':
            i+=1
            if i==len(s) or s[i] not in codes:raise ValueError('未知或未完成的转义')
            c=codes[s[i]]
        out.append(c);i+=1
    return ''.join(out)
def date(value):
    return dt.datetime.fromtimestamp(value,dt.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
def timestamp(value):
    result=dt.datetime.strptime(value,'%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=dt.timezone.utc)
    result=int(result.timestamp())
    if result<0 or date(result)!=value:raise ValueError('无效日期')
    return result
def valid_event(e):
    try:
        for k in ('id','mode','code','text','context'):
            if not isinstance(e[k],str):return False
            e[k].encode('utf-16-be')
        return (0<len(e['id'])<=128 and not any(ord(c)<32 for c in e['id'])
            and type(e['time']) is int and 0<=e['time']<=253402300799
            and type(e.get('levels',1)) is int and 1<=e.get('levels',1)<=3
            and 0<len(e['mode'].encode('utf-16-be'))<=1024 and 0<len(e['code'].encode('utf-16-be'))<=256
            and 0<len(e['text'])<=16 and len(e['context'])<=2
            and not any(ord(c)<32 or ord(c)==127 or 0xe000<=ord(c)<=0xf8ff or c in '{}' for c in e['text']))
    except (KeyError,TypeError,UnicodeError):return False
def row(action,identity,when,text='',code='',context='',levels=0,mode='',target=''):
    return ('\t'.join([action,date(when),escape_text(text),escape_text(code),escape_text(context),str(levels),escape_text(mode),identity,target])+'\n').encode('utf-8')
def event_line(e):
    if not valid_event(e):raise ValueError('无效学习记录')
    return row('学习',e['id'],e['time'],e['text'],e['code'],e['context'],e.get('levels',1),e['mode'])
def parse(data):
    if len(data)>LIMIT:raise ValueError('自学习文件超过16 MiB')
    events=[];seen=set();removed=set()
    for line in data.decode('utf-8-sig').split('\n'):
        line=line.removesuffix('\r')
        if not line or line.startswith('#'):continue
        f=line.split('\t')
        if len(line.encode('utf-8'))>8192 or len(f)!=9 or not 0<len(f[7])<=128:raise ValueError('自学习文件格式错误；没有覆盖原文件')
        when=timestamp(f[1])
        if f[7] in seen:continue
        if f[0]=='学习':
            e={'id':f[7],'time':when,'text':unescape_text(f[2]),'code':unescape_text(f[3]),'context':unescape_text(f[4]),'levels':int(f[5]),'mode':unescape_text(f[6])}
            if not valid_event(e) or f[8]:raise ValueError('无效学习字段')
            events.append(e)
        elif f[0]=='撤销' and 0<len(f[8])<=128:removed.add(f[8])
        elif f[0]=='清空' and not f[8]:events.clear();removed.clear()
        else:raise ValueError('未知的自学习操作')
        seen.add(f[7])
    return [e for e in events if e['id'] not in removed][-10000:],seen|removed

def read(path):
    if not path.exists():return b''
    if path.stat().st_size>LIMIT:raise ValueError('自学习文件超过16 MiB')
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


def compact(events,seen):
    active={e['id'] for e in events}
    tombstones=b''.join(row('撤销',identity,0,target=identity) for identity in sorted(seen-active))
    return HEADER+tombstones+b''.join(event_line(e) for e in events)
def summary(events):
    groups={}
    for e in events:
        choices=groups.setdefault((e['mode'],e['code'],e['context']),{})
        for text,weight in list(choices.items()):
            if text!=e['text']:choices[text]=weight*.25
        choices[e['text']]=min(10,choices.get(e['text'],0)+e.get('levels',1))
    total={}
    for (mode,code,context),choices in groups.items():
        for text,weight in choices.items():total[(mode,code,text)]=total.get((mode,code,text),0)+weight
    result=[]
    for (mode,code,context),choices in groups.items():
        for text,weight in choices.items():
            level=lambda x:min(10,max(0,math.floor(x+1e-12)))
            result.append({'片段':text,'编码':code,'前文':context,'本上下文等级':level(weight),'跨上下文等级':level(total[(mode,code,text)]),'模式':mode})
    return result

def maintain(path,action,source=None):
    if action in ('show','export') and not path.exists():return {'format':FORMAT,'events':[],'count':0,'等级':[]}
    with locked(path):
        data=read(path);events,seen=parse(data)
        if action in ('show','export'):return {'format':FORMAT,'events':events,'count':len(events),'等级':summary(events)}
        if action=='undo':
            if not events:return {'changed':False,'count':0}
            events.pop()
        elif action=='clear':events=[]
        elif action=='import':
            if source is None or source.stat().st_size>LIMIT:raise ValueError('缺少备份或文件过大')
            payload=json.loads(source.read_text(encoding='utf-8'))
            if payload.get('format')!=FORMAT or not isinstance(payload.get('events'),list) or len(payload['events'])>10000:raise ValueError('只接受新版自学习备份')
            for e in payload['events']:
                if not valid_event(e):raise ValueError('无效记录；未写入任何内容')
                if e['id'] not in seen:events.append(e);seen.add(e['id'])
            events=events[-10000:]
        elif action!='compact':raise ValueError('未知操作')
        replacement=compact(events,seen);replace(path,replacement)
        return {'changed':replacement!=data,'count':len(events),'bytes_before':len(data),'bytes_after':len(replacement)}
def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('journal');parser.add_argument('action',choices=['show','undo','clear','export','import','compact'])
    parser.add_argument('--file',type=pathlib.Path);parser.add_argument('--yes',action='store_true')
    args=parser.parse_args()
    try:
        path=checked_path(args.journal)
        if args.action in ('clear','import','compact') and not args.yes:raise ValueError('此操作需要 --yes')
        if args.action in ('export','import') and args.file is None:raise ValueError('需要 --file')
        if args.file is not None and args.file.absolute()==path:raise ValueError('备份不能覆盖自学习文件')
        result=maintain(path,args.action,args.file)
        if args.action=='export':
            with args.file.open('x',encoding='utf-8') as f:json.dump(result,f,ensure_ascii=False,indent=2)
            result={'备份文件':str(args.file),'记录数':result['count']}
        if args.action=='show':
            print('片段\t编码\t前文\t本上下文等级\t跨上下文等级\t模式')
            for record in result['等级']:print('\t'.join(str(x) if x!='' else '（无）' for x in record.values()))
        else:print(json.dumps(result,ensure_ascii=False,indent=2))
        return 0
    except (OSError,ValueError,TypeError,OverflowError) as error:
        print(f'自学习维护失败：{error}',file=sys.stderr);return 1
if __name__=='__main__':raise SystemExit(main())
