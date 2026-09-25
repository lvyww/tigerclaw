"""Build an isolated TCSKNM03 converter with explicit no-pruning mode.

The source remains in the GPL-3.0 Rime repository; no production file is edited.
"""
import argparse, hashlib, json, subprocess
from pathlib import Path

def main():
 p=argparse.ArgumentParser(description=__doc__)
 p.add_argument('--source',type=Path,required=True)
 p.add_argument('--output',type=Path,required=True)
 a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
 s=a.source.read_text()
 replacements={
 'constexpr int kLimit = 128;':'constexpr int kLimit = 128;\nstatic bool preserve_all = false;',
 'if (section >= 4 && history_max > kLimit)':'if (!preserve_all && section >= 4 && history_max > kLimit)',
 'if ((section == 3 || section == 4) && full_max > kLimit)':'if (!preserve_all && (section == 3 || section == 4) && full_max > kLimit)',
 'if (argc != 4)':'if (argc != 4 && argc != 5)',
 'build_model(fs::absolute(argv[1]), fs::absolute(argv[2]), fs::absolute(argv[3]));':'if (argc == 5) {\n            if (std::string(argv[4]) != "--preserve-all") throw std::runtime_error("unknown option");\n            preserve_all = true;\n        }\n        std::cerr << "preserve_all=" << preserve_all << "\\n";\n        build_model(fs::absolute(argv[1]), fs::absolute(argv[2]), fs::absolute(argv[3]));'
 }
 for before,after in replacements.items():
  assert s.count(before)==1,('unexpected converter revision',before)
  s=s.replace(before,after)
 out=a.output/'build_tcs_knm03_preserving.cpp';out.write_text(s)
 license=a.source.parent.parent/'LICENSE'
 (a.output/'LICENSE').write_bytes(license.read_bytes())
 exe=a.output/'build_tcs_knm03_preserving'
 cmd=['c++','-std=c++20','-O3','-DNDEBUG',str(out),'-o',str(exe)]
 subprocess.run(cmd,check=True)
 manifest=dict(source=str(a.source),source_sha256=hashlib.sha256(a.source.read_bytes()).hexdigest(),patched_sha256=hashlib.sha256(out.read_bytes()).hexdigest(),command=cmd,production_modified=False)
 (a.output/'builder-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
 print(exe)
if __name__=='__main__':main()
