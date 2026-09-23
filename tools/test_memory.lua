-- Memory representations must not change a live composition or scoring epoch.
local repo=arg[1] or "."
package.path=repo.."/rime/tiger_sentence/lua/?.lua;"..package.path
rime_api={get_user_data_dir=function()return repo end}
local sentence=dofile(os.getenv('TIGER_SENTENCE_MODULE') or (repo..'/rime/tiger_sentence/lua/rime/tiger_sentence/tiger_sentence.lua'))
local checks=0
local function check(ok,msg)checks=checks+1;assert(ok,msg)end
check(sentence.memory_status().model==nil,'diagnostics loaded a model')
local memory=sentence.memory_status()
local cache=require('tiger_sentence_cache')
local c=cache.new_columns(3,3)
for i=0,99 do
 cache.put_columns(c,i,0,false,i)
 local slot=c.values[i]
 check(c.columns[1][slot]==0 and c.columns[2][slot]==false and c.columns[3][slot]==i,'column cache lost zero/false')
 check(#c.keys<=3 and (i<3 or c.values[i-3]==nil),'column cache exceeded capacity')
end
sentence.set_model_enabled(arg[2]=='--require-model')
if arg[2]=='--require-model' then check(sentence.model_status().loaded,'production model required') end
local env={engine={schema={config={get_string=function()return 'compact'end}}}}
sentence.configure_memory(env)
check(sentence.memory_status().profile=='compact','schema compact profile not applied')
sentence.configure_memory(nil)
sentence.ensure_lexicon(nil)
sentence.decode('ot')
check(sentence.memory_status().profile=='compact','schema-less decode reset compact profile')
check(not sentence.set_memory_profile('unknown') and sentence.memory_status().profile=='compact','invalid profile changed configuration')
sentence.configure_memory({engine={schema={config={get_string=function()return nil end}}}})
check(sentence.memory_status().profile=='balanced','missing schema profile inherited previous value')
for _,duplicate in ipairs({false,true}) do
 sentence.set_allow_duplicate_single({get_option=function()return duplicate end})
 for _,raw in ipairs({'ot','ueot','awmenamcunta','jeumbauefaalhngyoehiyfbmvmxfzbflrl',string.rep('ot',64)}) do
  sentence.set_memory_profile('balanced')
  local expected=sentence.decode_full(raw,true,'')
  sentence.set_memory_profile('compact');sentence.reset_decode_cache()
  for n=1,#raw do
   local prefix=raw:sub(1,n)
   local actual=sentence.decode(prefix,true,'')
   check(sentence.results_equal(actual,sentence.decode_full(prefix,true,'')),'compact incremental/full mismatch')
   if n%9==0 then
    local status=sentence.trim_memory()
    check(actual==sentence.decode(prefix,true,''),'trim discarded active generation')
    check(status.profile=='compact' and status.logp_entries==0,'trim changed profile or retained score cache')
    if status.model then check(status.model.page_bytes==0 and status.model.page_entries==0,'trim retained model pages') end
   end
  end
  check(sentence.results_equal(expected,sentence.decode(raw,true,'')),'memory profile changed output')
 end
end
local learning=sentence.learning
local events,now={},1800000000
for i=1,600 do events[i]={code=string.format('ab%04d',i),text='记忆中的十六个字不能改变候选啊',mode='memory',context='左文',time=now} end
local index=learning.runtime_index(events,now)
local oracle=learning.build(events,now)
for pass=1,2 do
 for _,event in ipairs(events) do
  check(learning.score(index,event.mode,event.code,event.text,event.context)==learning.score(oracle,event.mode,event.code,event.text,event.context),'learning cache eviction changed score')
 end
 local count=0;for _ in pairs(index.cache)do count=count+1 end
 check(count<=256,'materialized learning cache is unbounded')
end
local partitions=index.partitions
learning.trim_caches(index)
check(index.partitions==partitions and next(index.cache)==nil,'trim removed learning aggregates or retained derived scores')
check(learning.score(index,'memory',events[1].code,events[1].text,'左文')==learning.score(oracle,'memory',events[1].code,events[1].text,'左文'),'trim changed learned preference')
if string.pack and utf8 then
 local path=repo..'/_memory_fixture.bin'
 local make=dofile(repo..'/tools/model_fixture.lua')
 local reference=make(path,100)
 local reader=require('tiger_sentence_ngram').new({page_misses=0,page_bytes=0})
 local model=reader.load(path,{page_bytes=128*1024,context_entries=8,bigram_entries=16,index_pages=1})
 check(model.format=='TCSKNM03','Q8 fixture not loaded')
 local tokens=reference.tokens
 for pass=1,3 do
  for i=1,#tokens do
   local a=utf8.char(tokens[i]);local b=utf8.char(tokens[#tokens+1-i])
   for _,cp in ipairs({0,2,3,0x4e00,0x30001,0x10ffff})do
    local ch=utf8.char(cp)
    check(model.logp(a,b,ch)==reference.logp(a,b,ch),'index paging changed probability')
    check(model.has_observed_bigram(b,ch)==reference.observed(b,ch),'column storage changed observed zero')
   end
   if i%11==0 then model.trim_caches() end
  end
 end
 local status=model.cache_status()
 check(status.page_bytes<=status.page_limit,'page cache exceeded bound')
 check(status.index_cache_limit==1,'index cache limit was ignored')
 model.configure_cache({page_bytes=2*1024*1024,context_entries=4096,bigram_entries=2048,index_pages=16})
 check(model.cache_status().page_limit==2*1024*1024,'model cache limit diagnostic is stale')
 model.close();os.remove(path)
else
 print('SKIP paged model memory fixture: binary APIs unavailable')
end
print(string.format('{"status":"passed","memory_checks":%d,"production_model":%s}',checks,tostring(arg[2]=='--require-model')))
