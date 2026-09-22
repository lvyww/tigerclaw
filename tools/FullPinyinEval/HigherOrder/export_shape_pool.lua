-- Offline frozen-decoder pool export. No timing, learning, Qwen or deployment.
local root,cases,out=arg[1],arg[2],arg[3]
package.path=root..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return root end}
local s=require('tiger_sentence')
assert(s.set_memory_profile('compact'))
s.ensure_lexicon(nil)
local status=s.model_status();assert(status.loaded,status.error)
local model=s.load_ngram_model(status.path)
assert(s.lexical_status().loaded)
local f=assert(io.open(out,'w'))
f:write('id\tsource\tcode\ttarget\tindex\ttext\tscore\tlm3\trank\tprefer_score\n')
local n=0
for line in io.lines(cases) do
 local id,source,raw,target=line:match('([^\t]+)\t([^\t]+)\t([^\t]+)\t(.+)');assert(raw)
 s.reset_decode_cache()
 local results=s.decode(raw,false,'')
 assert(s.model_status().loaded)
 -- Preserve undecodable cases in the denominator, represented by an empty output.
 if #results==0 then
  f:write(string.format('%s\t%s\t%s\t%s\t1\t\t0\t0\t1\t1\n',id,source,raw,target))
 end
 -- Same comparator decision as emit(), over its complete candidate pool.
 local prefer=false
 for _,v in ipairs(results._confidence_candidates or {}) do
  if v.path and v.path.previous and (v.path.previous.raw_length or 0)>0 then prefer=true end
 end
 for i,v in ipairs(results) do
  local a,b='\2','\2';local score=0
  for _,point in utf8.codes(v.text) do
   local c=utf8.char(point);score=score+model.logp(a,b,c);a,b=b,c
  end
  score=score+model.logp(a,b,'\3')
  f:write(string.format('%s\t%s\t%s\t%s\t%d\t%s\t%.17g\t%.17g\t%d\t%d\n',id,source,raw,target,i,v.text,v.score,score,v.max_rank or 1,prefer and 1 or 0))
 end
 n=n+1
 if n%250==0 then f:flush();print('rows',n);io.stdout:flush() end
end
f:close();model.close();print('complete',n)
