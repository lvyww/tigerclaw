"""Validate and evaluate the size-selected expanded Rime model."""
import json,subprocess,time
from pathlib import Path
HERE=Path(__file__).resolve().parent
ROOT=Path('/mnt/c/Archive/char5-corpus4_0-20260922')
OUT=ROOT/'rime-joint-compression'
W=Path('/home/yc/tmp/shape-mix-rime')
RIME=Path('/mnt/c/Users/yc/Desktop/tiger-sentense-rime_personalized_sync_20260918')
def run(cmd,log):
 with log.open('w') as f:subprocess.run(list(map(str,cmd)),stdout=f,stderr=subprocess.STDOUT,check=True)
def main():
 while True:
  selected=json.loads((OUT/'selected.json').read_text())
  if selected['history_limit']>=960:break
  log=(W/'expand.log').read_text()
  if 'Traceback' in log:raise RuntimeError('size expansion failed; see expand.log')
  time.sleep(10)
 assert selected['bytes']<=500000000
 print('NORMALIZATION',flush=True)
 run(['lua',HERE/'check_tcs03_normalization.lua',RIME,selected['model'],selected['arpa']],OUT/'normalization-final.log')
 print('LEXICAL',flush=True)
 run(['lua',HERE/'audit_tcs03_lexical.lua',RIME,selected['model'],ROOT/'lexical-supplement-v1/lexical-50000.tsv'],OUT/'lexical-coverage.log')
 print('EVALUATE 20000',flush=True)
 run(['python3',HERE/'evaluate_corpus4_compressed.py','--selected',OUT/'selected.json','--baseline',ROOT/'evaluation-shape-20k-20260923','--output',OUT/'evaluation','--method','mix70_tcs3_joint','--extra-baseline',OUT/'control-mainline-tcs3/predictions.jsonl','--tcs-reader-root',RIME],OUT/'evaluation.log')
 run(['python3',HERE/'report_rime_joint_compression.py'],OUT/'report.log')
 print('DONE',flush=True)
if __name__=='__main__':main()
