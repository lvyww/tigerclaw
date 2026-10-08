#!/usr/bin/env python3
"""File-backed protocol comparison. Uses fresh fixtures, no production IPC/UI."""
import argparse, json, pathlib, tempfile, subprocess, os, hashlib
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--native',type=pathlib.Path,required=True)
p.add_argument('--output',type=pathlib.Path,required=True)
p.add_argument('--corrupt-model',action='store_true')
p.add_argument('--globalization',choices=['icu','nls'],default='icu')
a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[1]
out=a.output.resolve();out.mkdir(parents=True,exist_ok=True)
def win(path):
 value=str(path.resolve())
 return value[5].upper()+':'+value[6:].replace('/','\\') if value.startswith('/mnt/') else value
def write(path,text):
 with path.open('wb') as file:
  file.write(text.encode('utf-8'));file.flush();os.fsync(file.fileno())
 assert path.read_bytes()==text.encode('utf-8')
requests=[]
def request(kind,**fields):requests.append(dict(type=kind,seq=len(requests)+1,**fields))
def key(vk,action='down',**fields):request('key',vk=vk,action=action,client_session='portable-protocol',event_id=str(len(requests)+1),caret_x=20,caret_y=30,**fields)
request('hello');request('query_state');request('get_schema_list')
for text in ['aa','aabb','zz','ab','a','aaaa']:
 for c in text:
  key(ord(c.upper()));key(ord(c.upper()),'up')
 # Retry the same physical press with a new request sequence.
 old=dict(requests[-2]);old['seq']=len(requests)+1;requests.append(old)
 key(0x20);key(0x20,'up');request('query_state')
key(0x41);key(0x08);key(0x41);request('focus',hwnd=0);request('composition_canceled')
key(0x10);key(0x10,'up');key(0x41);request('query_state')
key(0x10);key(0x10,'up');key(0x41);key(0x41);key(0x28);key(0x20)
request('query_state');request('reload_config');request('query_state')
key(0x41);request('reload_mb');request('query_state');key(0x41);key(0x20)
key(0x41);request('reload_config');request('query_state')
key(0x41)
old=dict(requests[-1]);old['seq']=len(requests)+1;requests.append(old)
key(0x41);key(0x20)
request('get_config')
request('get_send_history_count');request('get_last_ci',history_len=4)
request('construct_ci',text='甲乙');request('full_pinyin_info')
request('get_selection_key_config')
request('set_selection_key_config',config_text='2选 0x70')
key(0x41);key(0x41);key(0x70);request('get_last_ci',history_len=1)
request('set_selection_key_config',config_text='invalid')
request('set_config',key='unknown',value='是')
request('set_config',key='',value='是')
request('set_config',key='中文状态下使用英文标点',value=' 是 ')
request('set_config',key='中文状态下使用英文标点',value='是')
request('get_config');request('query_state')
request('add_ci',code='ab',text='新词');key(0x41);key(0x42);key(0x70)
request('add_ci',code='',text='词');request('add_ci',code='ab',text='')
request('query_state');request('reload_mb');request('query_state')
key(0x41);key(0x42);key(0x70)
key(0x41);request('ctrl_space');request('get_send_history_count');request('get_last_ci',history_len=3)
request('ctrl_space');request('set_config',key='Ctrl+空格切换中英文',value='否');request('ctrl_space')
request('set_config',key='Ctrl+空格切换中英文',value='是')
request('set_config',key='当前码表',value='Other');request('query_state');request('get_schema_list')
request('set_config',key='当前码表',value='Plain');request('get_schema_list');request('query_state')
request('set_config',key='整句Tab自学习',value='否');request('get_config')
request('set_config',key='整句Tab自学习',value='是')
request('set_config',key='当前码表',value='Test整句');key(0x41);key(0x41);request('query_state');key(0x20)
request('set_config',key='当前码表',value='Plain')
request('query_state',frontend='hook_native')
key(0x10,frontend='hook_native');key(0x10,'up',frontend='hook_native')
request('set_config',key='使用剪贴板上屏',value='是')
request('set_config',key='使用剪贴板上屏白名单',value='Test.exe')
request('query_state',frontend='hook_native')
request('ime_active',active=True);request('caret',x=40,y=50,height=25)
request('set_config',key='显示拆分',value='是');request('set_config',key='候选窗显示编码',value='是')
request('set_config',key='编码伪装',value='🔴🟩');key(0x41);key(0x41);request('query_state');key(0x20)
request('set_config',key='编码伪装',value='')
request('set_config',key='开启打字音效(娱乐)',value='是');key(0x41);key(0x41);key(0x20)
request('set_config',key='开启打字音效(娱乐)',value='否')
request('hook_native_disabled',disabled=True);request('hook_native_disabled',disabled=False)
request('ime_active',active=False);request('ime_active',active=True)
request('show_menu');request('show_config');request('show_addci');request('exit_core')
request('pinyin_preferences');request('pinyin_manage',action='forget',code='ni',text='你')
request('set_config',key='当前码表',value='nonexistent');request('query_state');request('get_schema_list')
request('set_config',key='当前码表',value='plain');request('query_state');request('get_schema_list')
request('set_config',key='开机自动启动',value='否');request('reload_config');request('query_state')
request('set_config',key='开机自动启动',value='是');request('reload_config');request('query_state')
# Frame continuity across ordinary, uppercase and reverse-pinyin clears.
request('reload_config')
for shifted in [False,True]:
 for end in [8,27,13,9,32,0xBE]:
  key(0x41,shift=shifted);key(end);request('composition_canceled')
for end in [8,27,13,9,32,0xBE,0x31]:
 key(0xC0);key(0x41);key(end);request('composition_canceled')
key(0x41);request('focus',hwnd=919);request('focus',hwnd=919);request('composition_canceled')
for setting in ['中英文不限长混合输入','自动启用整句模式']:
 key(0x41);request('set_config',key=setting,value='否');request('set_config',key=setting,value='是')
request('composition_canceled')
key(0x41);request('set_config',key='当前码表',value='Other');request('set_config',key='当前码表',value='Plain');request('composition_canceled')
request('delete_pinyin_word',code='ni',text='你')
request('key',vk='65',action='down',shift='false',ctrl=0,caret_x='20',caret_y=30.0)
request('composition_canceled')
request('key',vk=65.0,action='down',shift='1',capsLock=None,caps_lock='false',caret_x=20,caret_y=30)
request('composition_canceled')
request('key',vk='invalid',action='down')
request('query_state',seqValue='ignored')
requests.append({'TYPE':'query_state','SEQ':'432'})
request('focus',hwnd=' 123 ',processId='123')
request('composition_canceled')
responses=[];ui_responses=[];command_responses=[]
for native in [False,True]:
 name='cpp' if native else 'csharp'
 root=pathlib.Path(tempfile.mkdtemp(prefix=name+'-',dir=out))
 (root/'tables/Plain').mkdir(parents=True)
 (root/'拼音反查码表').mkdir()
 write(root/'拼音反查码表/table.txt','a 甲\na 乙\n')
 (root/'tables/Other').mkdir(parents=True)
 (root/'tables/Test整句').mkdir(parents=True)
 write(root/'tables/Test整句/table.txt','aa 无模型\n')
 if a.corrupt_model:
  (root/'Models').mkdir()
  write(root/'Models/sentence-fivegram-mobile.bin','invalid-model')
 write(root/'tables/Other/table.txt','aa 其他\n')
 write(root/'tables/Plain/table.txt','aa 甲\naa 乙\nbb 丙\nab 丁\n')
 write(root/'tables/Plain/注释.txt','甲 第一\n乙 第二\n')
 write(root/'tables/Plain/拆分.txt','甲 日丨\n乙 乚\n')
 write(root/'config.txt','码表存储位置\ttables\n当前码表\tPlain\n')
 if not native:
  assembly=repo/'next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll'
  oracle_config=json.loads(assembly.with_suffix('.runtimeconfig.json').read_text())
  oracle_config['runtimeOptions'].setdefault('configProperties',{})['System.Globalization.UseNls']=a.globalization=='nls'
  write(root/'oracle.runtimeconfig.json',json.dumps(oracle_config))
 source=root/'requests.jsonl';dest=root/'responses.jsonl'
 write(source,''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in requests))
 cmd=[str(a.native.resolve()),'--runtime-probe',win(root),win(source),win(dest)] if native else [
  '/mnt/c/Program Files/dotnet/dotnet.exe','exec','--runtimeconfig',win(root/'oracle.runtimeconfig.json'),win(assembly),
  '--native-runtime-probe',win(root),win(source),win(dest)]
 with (root/'probe.log').open('w') as log:subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=60)
 if not native:assert json.loads(pathlib.Path(str(dest)+'.startup.json').read_text())['equal']
 responses.append([json.loads(line) for line in dest.read_text(encoding='utf-8-sig').splitlines()])
 ui_responses.append([json.loads(line) for line in pathlib.Path(str(dest)+'.ui.jsonl').read_text(encoding='utf-8-sig').splitlines()])
 command_responses.append([json.loads(line) for line in pathlib.Path(str(dest)+'.commands.jsonl').read_text(encoding='utf-8-sig').splitlines()])
fields=['type','seq','success','handled','commit_text','input_buffer','keyboard_open','cancel_composition','expect_keyup',
 'input_cursor','composition_tracking','composition_pending','schema_list','current_schema',
 'config_version','lexicon_version','changed','error','config_text','default_text','code','text','count','full_pinyin',
 'native_hook_alt_backslash_toggle_enabled','auto_switch_system_layout_enabled','use_clipboard_commit',
 'clipboard_commit_whitelist','ensure_system_layout_en','protocol_version','core_build','core_commit','core_branch','items']
# Optional absent/empty commit and absent/false cancellation have identical wire semantics.
def canonical(value):
 if value is None:return None
 return {k:value.get(k, '' if k=='commit_text' else False if k=='cancel_composition' else None) for k in fields}
diff=[]
assert len(responses[0])==len(responses[1])==len(requests)
for index,(left,right) in enumerate(zip(*responses)):
 if canonical(left)!=canonical(right):diff.append(dict(index=index,request=requests[index],csharp=left,cpp=right))
command_diff=[dict(index=i,csharp=left,cpp=right) for i,(left,right) in enumerate(zip(*command_responses)) if left!=right]
assert len(command_responses[0])==len(command_responses[1])==len(requests)
write(out/'command-differences.json',json.dumps(command_diff,indent=2)+'\n')
ui_diff=[]
assert len(ui_responses[0])==len(ui_responses[1])==len(requests)
frame_ids=[{},{}]
for index,(left,right) in enumerate(zip(*ui_responses)):
 # Compare token identity transitions, not random GUID bytes.
 left=dict(left);right=dict(right)
 for side,v in enumerate([left,right]):
  token=v.get('CandidateFrameSession'); ids=frame_ids[side]
  if token not in ids:ids[token]=len(ids)
  v['CandidateFrameSession']=ids[token]
 if left!=right:ui_diff.append(dict(index=index,request=requests[index],csharp=left,cpp=right))
write(out/'ui-differences.json',json.dumps(ui_diff,ensure_ascii=False,indent=2)+'\n')
write(out/'differences.json',json.dumps(diff,ensure_ascii=False,indent=2)+'\n')
report=dict(globalization=a.globalization,startupRegistryUnchanged=True,requests=len(requests),differences=len(diff),uiDifferences=len(ui_diff),commandDifferences=len(command_diff),nativeSha256=hashlib.sha256(a.native.read_bytes()).hexdigest(),
 excludedMetadata=['config_path (isolated fixture roots differ)','core_path (separate process executables)' ],normalizedUiFields=['CandidateFrameSession: canonical identity transitions'],productionIpc=False)
write(out/'report.json',json.dumps(report,indent=2)+'\n')
print(json.dumps(report));print(json.dumps(diff[:3],ensure_ascii=False,indent=2))
print(json.dumps(ui_diff[:3],ensure_ascii=False,indent=2))
raise SystemExit(bool(diff or ui_diff or command_diff))
