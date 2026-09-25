"""Unpruned Articles MKN-5 -> preserved ARPA, TCSKNM03 Q16 and Q8.

Stage outputs are isolated in --work; finalized files are copied and hashed into
--archive. Does not interpolate, install or overwrite a mainline model.
"""
import argparse
import json
import math
import os
from pathlib import Path
import shutil
import struct
import subprocess
import time
from train_articles import dump, sha

HERE = Path(__file__).resolve().parent
KENLM = Path('/home/yc/tools/tigerclaw-brightmart/build/bin')
CONVERTER = Path('/home/yc/tmp/shape-mix-rime/converter/build_tcs_knm03_preserving')
READER = HERE / 'TcsQ8'

SCORE_LUA = r'''
package.path=arg[1]..'/?.lua;'..package.path
local model=assert(require('tiger_sentence_fivegram').load(arg[2]))
local output=assert(io.open(arg[4],'w'))
for line in io.lines(arg[3]) do
 local a,b,c,d,n=model.bos_id,0,0,0,1
 local total,count=0,0
 for token in line:gmatch('%S+') do
  local score;score,a,b,c,d,n=model.step(a,b,c,d,n,token)
  total=total+score;count=count+1
 end
 local score=model.step(a,b,c,d,n,'\3')
 output:write(string.format('%.17g\t%d\n',total+score,count+1))
end
output:close();model.close()
'''


def header(path):
    with path.open('rb') as f:
        raw = f.read(256)
        assert raw[:8] == b'TCSKNM03' and struct.unpack_from('<Q',raw,16)[0] == path.stat().st_size
        return dict(version=struct.unpack_from('<I',raw,8)[0],
                    counts=[struct.unpack_from('<I',raw,28)[0]] +
                    [struct.unpack_from('<Q',raw,64+24*i+16)[0] for i in range(4)])


def verified_copy(src, dst):
    identity=dict(bytes=src.stat().st_size,sha256=sha(src),repaired_blocks=[])
    if dst.exists() and dst.stat().st_size==identity['bytes'] and sha(dst)==identity['sha256']:
        return identity
    partial=dst.with_suffix(dst.suffix+'.partial')
    if not partial.exists() or partial.stat().st_size!=identity['bytes']:
        with src.open('rb') as inp,partial.open('wb') as out:
            shutil.copyfileobj(inp,out,8*1024*1024);out.flush();os.fsync(out.fileno())
    # Large cross-filesystem writes have previously produced localized corruption
    # on this host. Recopy only mismatching blocks, then verify the whole file.
    for attempt in range(3):
        if sha(partial)==identity['sha256']:
            partial.replace(dst)
            return identity
        if attempt==2:raise RuntimeError(f'Copy hash mismatch after repairs: {partial}')
        with src.open('rb') as inp,partial.open('r+b') as out:
            offset=0
            while block:=inp.read(4*1024*1024):
                copied=out.read(len(block))
                if copied!=block:
                    out.seek(offset);out.write(block)
                    identity['repaired_blocks'].append(dict(attempt=attempt+1,offset=offset,bytes=len(block)))
                offset+=len(block)
            out.flush();os.fsync(out.fileno())
        dump(dst.with_suffix(dst.suffix+'.copy-audit.json'),identity)
    raise AssertionError('unreachable')


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--work',type=Path,required=True)
    p.add_argument('--archive',type=Path,required=True)
    a=p.parse_args();a.work.mkdir(parents=True,exist_ok=True);a.archive.mkdir(parents=True,exist_ok=True)
    manifest_path=a.work/'model-manifest.json'
    manifest=json.loads(manifest_path.read_text()) if manifest_path.exists() else dict(
        created=time.time(),order=5,smoothing='modified Kneser-Ney',pruning='none',
        purpose='independent character model; interpolation deferred',stages={},commands=[],
        tools={str(x):sha(x) for x in [KENLM/'lmplz',CONVERTER,Path(__file__),HERE/'train_articles.py',READER/'requantize.cpp']})
    manifest['current_pipeline_sha256']=sha(Path(__file__))
    def run(name,cmd):
        if manifest['stages'].get(name):return
        if shutil.disk_usage(a.work).free < 30*1024**3:raise RuntimeError('Less than 30 GiB free')
        command=list(map(str,cmd));started=time.time()
        dump(a.work/'pipeline-progress.json',dict(stage=name,started=started,command=command))
        print(name,command,flush=True)
        with (a.work/(name+'.log')).open('w') as log:subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,check=True)
        manifest['stages'][name]=dict(seconds=time.time()-started)
        manifest['commands'].append(command);dump(manifest_path,manifest)
    try:
        if not (a.work/'corpus/corpus-manifest.json').exists():
            run('prepare',['python3',HERE/'train_articles.py','--snapshot',a.work/'source.tar',
                '--poetry',a.work/'poetry-source/poetry_txt','--essays',a.work/'中华名家散文精选本：全27册.zip',
                '--provenance','/mnt/c/Archive/char5-corpus4_0-20260922/articles-comparison-20260923/provenance.jsonl',
                '--output',a.work/'corpus'])
        temporary=a.work/'kenlm-temp';temporary.mkdir(exist_ok=True)
        run('lmplz',[KENLM/'lmplz','-o','5','-S','8G','-T',temporary,
            '--text',a.work/'corpus/train.tokens','--arpa',a.work/'char5.arpa','--prune','0'])
        if not manifest['stages'].get('canonical_arpa'):
            counts=[];changed=0
            with (a.work/'char5.arpa').open() as src,(a.work/'char5-tcs.arpa').open('w') as dst:
                for line in src:
                    if line.startswith('ngram '):counts.append(int(line.split('=')[1]))
                    fields=line.split()
                    if len(fields) in (2,3) and fields[1]=='<s>':
                        fields[0]='0';line='\t'.join(fields)+'\n';changed+=1
                    dst.write(line)
            assert len(counts)==5 and changed==1
            manifest['arpa_counts']=counts
            manifest['stages']['canonical_arpa']=dict(bos_sentinels_changed=changed,reason='BOS is context-only; preserve Q8 unigram range')
            dump(manifest_path,manifest)
        run('convert_q16',[CONVERTER,a.work/'char5-tcs.arpa',a.work/'sentence-fivegram-q16.bin',a.work/'tcs-temp','--preserve-all'])
        run('build_quantizer',['c++','-std=c++20','-O3',READER/'requantize.cpp','-o',a.work/'requantize'])
        run('quantize_q8',[a.work/'requantize',a.work/'sentence-fivegram-q16.bin',a.work/'sentence-fivegram-mobile.bin'])
        for name in ('sentence-fivegram-q16.bin','sentence-fivegram-mobile.bin'):
            value=header(a.work/name);assert value['counts']==manifest['arpa_counts'],(name,value,manifest['arpa_counts'])
        reader=a.work/'reader/lua';reader.mkdir(parents=True,exist_ok=True)
        shutil.copy2(READER/'tiger_sentence_fivegram.lua',reader/'tiger_sentence_fivegram.lua')
        run('validate',['python3',READER/'validate.py',a.work/'sentence-fivegram-q16.bin',
            a.work/'sentence-fivegram-mobile.bin',a.work/'reader',a.work/'validation'])
        (a.work/'score.lua').write_text(SCORE_LUA)
        metrics={}
        from TcsQ8.validate import Model
        vocabulary_model=Model(a.work/'sentence-fivegram-mobile.bin')
        vocabulary=set(vocabulary_model.tokens)
        vocabulary_model.data.close();vocabulary_model.file.close()
        # Evaluate once on frozen dev/test, no weight fitting or model selection.
        for split in ('dev','test'):
            model_scores={}
            for name,model in [('q16','sentence-fivegram-q16.bin'),('q8','sentence-fivegram-mobile.bin')]:
                dest=a.work/f'{split}-{name}-scores.tsv'
                run(f'score_{split}_{name}',['luajit',a.work/'score.lua',READER,a.work/model,a.work/'corpus'/f'{split}.tokens',dest])
                scores=[(float(x.split('\t')[0]),int(x.split('\t')[1])) for x in dest.read_text().splitlines()]
                assert scores and all(math.isfinite(s) and n>0 for s,n in scores)
                total=sum(s for s,n in scores);tokens=sum(n for s,n in scores)
                model_scores[name]=dict(rows=len(scores),tokens_including_eos=tokens,log_probability=total,perplexity=math.exp(-total/tokens))
            tokens=[token for line in (a.work/'corpus'/f'{split}.tokens').read_text().splitlines() for token in line.split()]
            model_scores['oov_characters']=sum(token not in vocabulary for token in tokens)
            model_scores['characters']=len(tokens)
            metrics[split]=model_scores
        manifest['heldout']=metrics
        manifest['corpus']=json.loads((a.work/'corpus/corpus-manifest.json').read_text())
        manifest['quantization_validation']=json.loads((a.work/'validation/validation.json').read_text())
        baseline=Path('/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-fivegram-mobile.bin')
        baseline_arpa=Path('/home/yc/tmp/tiger-char5-500/context128.arpa')
        manifest['future_interpolation_reference']={str(x):dict(bytes=x.stat().st_size,sha256=sha(x)) for x in (baseline,baseline_arpa)}
        manifest['artifacts']={}
        for name in ('char5.arpa','sentence-fivegram-q16.bin','sentence-fivegram-mobile.bin'):
            src=a.work/name;dst=a.archive/name
            dump(a.work/'pipeline-progress.json',dict(stage='verified_delivery',artifact=name))
            manifest['artifacts'][name]=verified_copy(src,dst)
        manifest['finished']=time.time();dump(manifest_path,manifest)
        for name in ('dev.tokens','test.tokens','dev.jsonl','test.jsonl','corpus-manifest.json','source-files.jsonl'):
            shutil.copy2(a.work/'corpus'/name,a.archive/name)
        for src in a.work.glob('*.log'):shutil.copy2(src,a.archive/src.name)
        shutil.copytree(a.work/'validation',a.archive/'validation',dirs_exist_ok=True)
        test_exe=HERE.parents[2]/'next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.exe'
        def windows(path):return subprocess.check_output(['wslpath','-w',str(path)],text=True).strip()
        run('csharp_scores',[test_exe,'--shape-fivegram-scores',windows(a.archive/'sentence-fivegram-mobile.bin'),
            windows(a.archive/'validation/queries.tsv'),windows(a.archive/'validation/csharp-scores.txt')])
        expected=list(map(float,(a.archive/'validation/scores-v2-lua.txt').read_text().splitlines()))
        actual=list(map(float,(a.archive/'validation/csharp-scores.txt').read_text().splitlines()))
        assert len(actual)==len(expected)
        error=max(abs(x-y) for x,y in zip(expected,actual));assert error<1e-10,error
        manifest['csharp_validation']=dict(queries=len(actual),max_score_error=error,executable=str(test_exe),sha256=sha(test_exe))
        scripts=a.archive/'scripts';scripts.mkdir(exist_ok=True)
        for src in (Path(__file__),HERE/'train_articles.py',HERE/'test_train_articles.py',READER/'requantize.cpp',READER/'validate.py',READER/'tiger_sentence_fivegram.lua'):
            shutil.copy2(src,scripts/src.name)
        for name in ('builder-manifest.json','build_tcs_knm03_preserving.cpp','LICENSE'):
            shutil.copy2(CONVERTER.parent/name,scripts/name)
        dump(a.archive/'model-manifest.json',manifest)
        dump(manifest_path,manifest)
        dump(a.work/'pipeline-progress.json',dict(stage='complete',artifacts=manifest['artifacts'],heldout=metrics))
        print('COMPLETE',json.dumps(manifest['artifacts']),flush=True)
    except BaseException as error:
        dump(a.work/'pipeline-progress.json',dict(stage='failed',error=str(error),time=time.time()))
        raise


if __name__=='__main__':main()
