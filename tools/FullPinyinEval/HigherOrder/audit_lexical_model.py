"""Audit exact word/internal-bigram existence via KenLM matched ngram orders."""
import argparse, csv, hashlib, json, re, subprocess
from pathlib import Path
TOKEN = re.compile(r'([^\s=]+)=\d+ (\d+) (-?\d+(?:\.\d+)?(?:e[+-]?\d+)?)')

def audit(model, lexical, output, query, require_bigrams=False, require_words=False):
    with lexical.open() as f:
        entries = list(csv.DictReader(f, delimiter='\t'))
    words = [r['word'] for r in entries]
    assert len(words) == len(set(words)) == 50000
    pairs = sorted({w[i:i+2] for w in words for i in range(len(w)-1)})
    def score(texts):
        result = subprocess.run([str(query), '-l', 'lazy', '-n', '-v', 'word', '-v', 'sentence', str(model)],
            input='\n'.join(' '.join(t) for t in texts)+'\n', capture_output=True, text=True, check=True)
        lines = result.stdout.splitlines()
        assert len(lines) == len(texts), (len(lines),len(texts))
        records = {}
        for text,line in zip(texts,lines):
            tokens=TOKEN.findall(line)
            assert ''.join(t[0] for t in tokens)==text, (text,line)
            assert len(tokens)==len(text)
            records[text] = dict(order=int(tokens[-1][1]), log10=float(tokens[-1][2]))
        return records
    ps, ws = score(pairs), score(words)
    missing = [p for p in pairs if ps[p]['order'] < 2]
    affected = [w for w in words if any(w[i:i+2] in missing for i in range(len(w)-1))]
    absent = [w for w in words if ws[w]['order'] < len(w)]
    data = dict(model=str(model), lexical_sha256=hashlib.sha256(lexical.read_bytes()).hexdigest(),
        words=len(words), unique_pairs=len(pairs), missing_pairs=missing,
        all_bigrams_present_words=len(words)-len(affected), full_ngram_present_words=len(words)-len(absent),
        affected_words=affected, missing_full_words=absent,
        by_length={str(n):dict(total=sum(len(w)==n for w in words), full_present=sum(len(w)==n and w not in absent for w in words)) for n in (2,3,4)},
        pair_probes={p:ps[p] for p in ('游戏','安卓','首页','钱包','频道','栏目','时政') if p in ps})
    output.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
    if require_bigrams: assert not missing, missing
    if require_words: assert not absent, absent
    return data

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--model',type=Path,required=True);p.add_argument('--lexical',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);p.add_argument('--query',type=Path,default=Path('/home/yc/tools/tigerclaw-brightmart/build/bin/query'))
    p.add_argument('--require-bigrams',action='store_true');p.add_argument('--require-words',action='store_true')
    a=p.parse_args();d=audit(a.model,a.lexical,a.output,a.query,a.require_bigrams,a.require_words)
    print(json.dumps({k:v for k,v in d.items() if k not in ('affected_words','missing_full_words')},ensure_ascii=False,indent=2))
