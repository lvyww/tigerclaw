"""Build the exact KenLM helper from a FIFO; avoid a huge temporary ARPA copy."""
import argparse,os,subprocess,time
from pathlib import Path

def build(budget,builder,source,output,order):
 fifo=source/f'stream-index{order}.fifo'
 if fifo.exists():raise RuntimeError(f'Another stream or stale FIFO exists: {fifo}')
 os.mkfifo(fifo);processes=[]
 try:
  consumer=subprocess.Popen([str(builder),'-S','4G','-T',str(source/f'stream-index{order}-temp'),'trie',str(fifo),str(output)])
  processes.append(consumer)
  producer=subprocess.Popen([str(budget),f'export{order}',str(source),str(fifo)])
  processes.append(producer)
  while True:
   states=[p.poll() for p in processes]
   if any(code is not None and code!=0 for code in states):raise RuntimeError(f'Streamed index failed: builder/exporter exit codes {states}')
   if all(code is not None for code in states):break
   time.sleep(.2)
 finally:
  for p in processes:
   if p.poll() is None:p.terminate()
  for p in processes:
   try:p.wait(timeout=10)
   except subprocess.TimeoutExpired:p.kill();p.wait()
  fifo.unlink()
def main():
 p=argparse.ArgumentParser();p.add_argument('budget',type=Path);p.add_argument('builder',type=Path);p.add_argument('source',type=Path);p.add_argument('output',type=Path);p.add_argument('order',type=int,choices=(4,5));a=p.parse_args()
 build(a.budget,a.builder,a.source,a.output,a.order)
if __name__=='__main__':main()
