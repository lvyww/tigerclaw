local repo=arg[1] or "."
package.path=repo.."/rime/tiger_sentence/lua/?.lua;"..package.path
if not string.unpack or not utf8 then
    print('{"status":"skipped","test":"binary-reader","reason":"Lua 5.3+ APIs unavailable"}')
    return
end
local checks=0
local function check(ok,message)checks=checks+1;assert(ok,message)end
local make=dofile(repo.."/tools/model_fixture.lua")
local path=repo.."/_review_ngram_fixture.bin"
local oracle=make(path)
local counters={page_misses=0,page_bytes=0}
local reader=require("tiger_sentence_ngram").new(counters)
local model=reader.load(path)
check(model.bytes==oracle.bytes,"fixture size not checked")
local tokens={}
for _,cp in ipairs(oracle.tokens)do tokens[#tokens+1]=utf8.char(cp)end
tokens[#tokens+1]="";tokens[#tokens+1]=("不存在"):sub(1,3)
for pass=1,2 do
    for _,a in ipairs(tokens)do
        for _,b in ipairs(tokens)do
            for _,c in ipairs(tokens)do
                check(model.logp(a,b,c)==oracle.logp(a,b,c),"paged/cached probability differs from independent float32 oracle")
                check(model.has_observed_bigram(b,c)==oracle.observed(b,c),"zero-valued observed record was confused with missing")
            end
        end
    end
end
-- Force eviction from the bounded pair cache, then revisit earlier queries.
for i=1,100 do for j=1,100 do
    local a,b=utf8.char(0x5000+i),utf8.char(0x6000+j)
    check(model.has_observed_bigram(a,b)==oracle.observed(a,b),"pair eviction changed observed-ness")
end end
for _,a in ipairs(tokens)do for _,b in ipairs(tokens)do
    check(model.logp("甲",a,b)==oracle.logp("甲",a,b),"evicted pair returned a stale probability")
end end
local stats=model.cache_status()
check(stats.page_bytes<=stats.page_limit,"page cache exceeded capacity")
check(stats.page_entries>0,"fixture did not load pages")
model.close()
-- A newly loaded model has new caches, not cached probabilities from a prior file.
model=reader.load(path)
check(model.cache_status().page_entries==0,"reader instances shared a probability cache")
check(model.logp("甲","甲","的")==oracle.logp("甲","甲","的"),"reload changed probability")
model.close()
-- Reuse a closed handle's *path*, not its cache, after corrupting the header.
local file=assert(io.open(path,"wb"));file:write("TCSKNM02");file:close()
local ok=pcall(reader.load,path)
check(not ok,"truncated model header was accepted")
os.remove(path)
print(string.format('{"status":"passed","ngram_checks":%d,"fixture_bytes":%d,"production_model":false}',checks,oracle.bytes))
