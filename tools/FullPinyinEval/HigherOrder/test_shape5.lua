local root=arg[1]
package.path=root..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return root end}
local s=require('tiger_sentence');assert(s.set_memory_profile('compact'));s.ensure_lexicon(nil)
local checks=0
local function same(a,b)
 assert(#a==#b,'candidate count')
 for i=1,#a do assert(a[i].text==b[i].text and math.abs(a[i].score-b[i].score)<1e-9,'incremental/full mismatch');checks=checks+1 end
end
for _,raw in ipairs({'nmbytqtwycjuqpumdgrlldhq','iejryfenahbmsp','nuusvbbhoi','otq','xrxbj'}) do
 s.reset_decode_cache()
 for n=1,#raw do same(s.decode(raw:sub(1,n),false,''),s.decode_full(raw:sub(1,n),false,'')) end
 for n=#raw-1,1,-1 do same(s.decode(raw:sub(1,n),false,''),s.decode_full(raw:sub(1,n),false,'')) end
 local r=s.decode_full(raw,false,'')
 if #r>0 then
  local first=r[1].path
  while first.previous and (first.previous.raw_length or 0)>0 do first=first.previous end
  local lock={raw=raw:sub(1,first.raw_length),text=r[1].text:sub(1,first.text_length),boundaries=first.raw_length..','..first.text_length..';'}
  s.reset_decode_cache()
  local a=s.decode(raw,false,'',lock)
  for _,c in ipairs(a) do assert(c.text:sub(1,#lock.text)==lock.text,'crossed locked prefix') end
  s.reset_decode_cache()
  same(a,s.decode(raw,false,'',lock))
 end
end
print('fivegram incremental/backspace/lock checks',checks)
