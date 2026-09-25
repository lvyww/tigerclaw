"""Isolated fixed .7/.3 MITLM build; optimizers unavailable by construction."""
import argparse,hashlib,json,subprocess
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--source',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
src=a.source/'src';w=a.output;w.mkdir(parents=True,exist_ok=True)
(w/'config.h').write_text('#define PACKAGE_STRING "MITLM fixed-weight offline build"\n#define HAVE___BUILTIN_CLZ 1\n')
(w/'no_optimizer.cpp').write_text('#include <cstdio>\n#include <cstdlib>\nextern "C" void mitlm_lbfgs(...) { std::fputs("Optimization disabled\\n",stderr);std::abort(); }\nextern "C" void mitlm_setulb(...) { std::fputs("Optimization disabled\\n",stderr);std::abort(); }\n')
s=(src/'interpolate-ngram.cpp').read_text();before='mitlm::ParamVector params(ilm.defParams());';assert s.count(before)==1
s=s.replace(before,before+'\n    if(params.length()!=1) { std::cerr << "Expected two fixed ARPA models";return 2; }\n    params[0]=std::log(0.3/0.7);')
(w/'interpolate_fixed.cpp').write_text(s)
s=(src/'NgramVector.cpp').read_text();before='ngramMap = Range(size());\n        return false;';assert s.count(before)==1
# Even without a positional reorder, vocabulary/history IDs were remapped.
# Rebuild the hash and bounded views before any subsequent lookup/merge.
s=s.replace(before,'ngramMap = Range(size());\n        _Reindex(_indices.length());\n        Range r(_length);\n        _wordsView.attach(_words[r]);\n        _histsView.attach(_hists[r]);\n        return false;')
(w/'NgramVector_fixed.cpp').write_text(s)
files=[f for f in src.glob('*.cpp') if f.name not in ['estimate-ngram.cpp','evaluate-ngram.cpp','interpolate-ngram.cpp','NgramVector.cpp']]+list((src/'util').glob('*.cpp'))
cmd=['c++','-O2','-std=gnu++11','-fpermissive','-I'+str(w),'-I'+str(src),'-include',str(w/'config.h'),*[str(f) for f in files],str(w/'NgramVector_fixed.cpp'),str(w/'interpolate_fixed.cpp'),str(w/'no_optimizer.cpp'),'-o',str(w/'interpolate-fixed-v3')]
subprocess.run(cmd,check=True)
(w/'COPYING').write_bytes((a.source/'COPYING').read_bytes())
manifest=dict(command=cmd,source=str(a.source),weight_old=.7,optimizers_disabled=True,source_hashes={name:hashlib.sha256((src/name).read_bytes()).hexdigest() for name in ['NgramVector.cpp','InterpolatedNgramLM.cpp','interpolate-ngram.cpp']})
(w/'mitlm-build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
