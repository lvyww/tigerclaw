-- Behavior and cache regressions for the 18adb70 review. Run in an owned tree
-- through run_regressions.py; no production model or live user DB is required.
local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
local directory = repo
rime_api = {get_user_data_dir=function() return directory end}
local sentence = dofile(os.getenv("TIGER_SENTENCE_MODULE") or repo .. "/rime/tiger_sentence/lua/rime/tiger_sentence/tiger_sentence.lua")
local learning = sentence.learning
local checks = 0
local function check(ok, message) checks=checks+1; assert(ok, message) end
local function clone(value, seen)
    if type(value) ~= "table" then return value end
    seen=seen or {}; if seen[value] then return seen[value] end
    local result={}; seen[value]=result
    for k,v in pairs(value) do result[clone(k,seen)]=clone(v,seen) end
    return setmetatable(result,getmetatable(value))
end
local function write(path, content)
    local file=assert(io.open(path,"wb")); assert(file:write(content)); file:close()
end
-- Mutations are confined to run_regressions.py's owned copy and restored.
local function with_codes(content, callback)
    local path=repo.."/rime/tiger_sentence/tiger_sentence.codes.txt"
    local file=assert(io.open(path,"rb")); local original=file:read("*a"); file:close()
    write(path,content); sentence.apply_high_freq_limit(0)
    local ok,err=pcall(callback)
    write(path,original); sentence.apply_high_freq_limit(1500)
    if not ok then error(err,0) end
end
sentence.ensure_lexicon(nil); sentence.set_model_enabled(false)
-- Whole-input eligibility/rewards must not survive an append to a long code.
for _,code in ipairs({"abcde","abcdef","abcdefgh"}) do
    with_codes("甲\t"..code.."\n乙丙\t"..code.."\n丁\txy\n",function()
        local raw=code.."xy"
        for _,allow in ipairs({true,false}) do
            sentence.set_allow_duplicate_single({get_option=function()return allow end})
            for _,sample in ipairs({raw, code.."2xy", code.."12xy", code.."'xy"}) do
                sentence.reset_decode_cache()
                for i=1,#sample do
                    local prefix=sample:sub(1,i)
                    check(sentence.results_equal(sentence.decode(prefix,true,""),sentence.decode_full(prefix,true,"")),
                        "long-code incremental/full mismatch: "..prefix)
                end
                for i=#sample,1,-1 do
                    local prefix=sample:sub(1,i)
                    check(sentence.results_equal(sentence.decode(prefix,true,""),sentence.decode_full(prefix,true,"")),
                        "long-code delete/full mismatch: "..prefix)
                end
            end
        end
    end)
end
sentence.set_allow_duplicate_single({get_option=function()return true end})
-- Multi-digit selectors can make even a two-letter whole-input edge >4 bytes.
with_codes("甲\tab\n乙\tab\n",function()
    sentence.decode("ab00002",true,"")
    for i=7,2,-1 do
        local raw=("ab00002"):sub(1,i)
        check(sentence.results_equal(sentence.decode(raw,true,""),sentence.decode_full(raw,true,"")),
            "selector deletion reused a stale whole-input bucket")
    end
end)
-- A complete-pool-only change, or a learning inhibition flag, must fail parity.
local base={
    {text="甲",score=1,confidence_score=1,segmented="ab",learning_score=0,
     path={text="甲",text_length=3,raw_length=2,prev2="\2",prev1="甲",score=1}},
    learning_affected=false, _completed_truncated=false,
    early_commit_evidence={prefixes={},raw_lengths={},proposal="",proposal_share=0}
}
base._confidence_candidates={base[1],{text="乙",score=0,confidence_score=0}}
check(sentence.results_equal(base,clone(base)),"identical snapshots differ")
for _,mutate in ipairs({
    function(x)x.learning_affected=true end,
    function(x)x._completed_truncated=true end,
    function(x)x[1].learning_score=1 end,
    function(x)x._confidence_candidates[2].text="丙" end,
    function(x)x._confidence_candidates[2].confidence_score=0.25 end,
    function(x)x[1].path.raw_length=3 end,
    function(x)x[1].path.learning_potential=1 end,
    function(x)x.early_commit_evidence.neutral_incomplete_tail=true end,
    function(x)x.early_commit_evidence.merged_incomplete_tail=true end,
    function(x)x.early_commit_evidence.neutral_low_confidence=true end,
    function(x)x.early_commit_evidence.confidence_truncated=true end,
    function(x)x.early_commit_evidence.raw_lengths["甲"]=2 end
}) do
    local other=clone(base); mutate(other)
    check(not sentence.results_equal(base,other),"behavior-bearing snapshot mutation was ignored")
end
-- Evidence indexing is a view of the original records, not a new probability
-- calculation. Plain arrays still implement the independent linear lookup.
local pool={}
for i=1,80 do
    local text="甲"..tostring(i)
    pool[i]={text=text,confidence_score=-i/3,path={text=text,raw_length=4,
        previous={text="甲",raw_length=2}}}
end
local indexed=sentence.build_prefix_evidence(pool)
local plain={};for i,v in ipairs(indexed)do plain[i]=v end
for _,item in ipairs(indexed)do
    check(sentence.find_prefix_evidence(indexed,item.text,item.raw_length)==
        sentence.find_prefix_evidence(plain,item.text,item.raw_length),"evidence index changed records")
end
check(sentence.find_prefix_evidence(indexed,"missing",999)==nil,"evidence miss became a hit")
setmetatable(indexed,{__len=function()error("indexed lookup scanned the array")end})
check(sentence.find_prefix_evidence(indexed,"甲",2)~=nil,"evidence direct lookup failed")

-- Independent full-build prefix oracle: do NOT use the optimized prefix_score.
local function frame(...)
    local values={...}; for i,v in ipairs(values)do values[i]=#v..":"..v end
    return table.concat(values)
end
local function prefix_reference(index,mode,code,text,ctx)
    if code=="" or text=="" then return 0 end
    local codes,lo,hi=index.codes,1,#index.codes+1
    while lo<hi do local mid=math.floor((lo+hi)/2); if codes[mid]<code then lo=mid+1 else hi=mid end end
    local best=0
    for i=lo,math.min(#codes,lo+63)do
        local value=codes[i];if value:sub(1,#code)~=code then break end
        if #value>#code then
            local s=index.prefixes[frame(value,mode,text)]
            best=math.max(best,s and math.max(s.general,s.exact[ctx] or 0) or 0)
        end
    end
    return best
end
local now,events=1800000000,{}
local function add(code,text,ctx,time,mode)
    events[#events+1]={code=code,text=text,context=ctx,time=time or now,mode=mode or "review"}
end
add("ab","甲乙","") -- occupies the first of the 64 inspected code slots
for i=1,100 do add("ab"..string.format("%03d",i),i==64 and "丁戊" or "甲乙","") end
add("ab001","甲丙","前");add("ab001","甲丙","后")
local ref=learning.build(events,now)
local index=learning.runtime_index(events,now)
check(learning.prefix_score(index,"review","ab","丁","")==0,"equal code no longer consumes the 64-slot window")
for _,time in ipairs({now,now+61,now+30*86400,now-86400})do
    local epoch=learning.runtime_index(events,time)
    local oracle=learning.build(events,time)
    for repeat_i=1,3 do
        for _,mode in ipairs({"review","other"})do
            for _,code in ipairs({"a","ab","ab0","ab00","ab001","ab064","b",""})do
                for _,text in ipairs({"甲","丁","甲乙","","😀"})do
                    for _,ctx in ipairs({"","前","后","甲乙"})do
                        check(learning.prefix_score(epoch,mode,code,text,ctx)==prefix_reference(oracle,mode,code,text,ctx),
                            "prefix cache changed score/window/epoch")
                    end
                end
            end
        end
    end
end
-- Distinct contexts, misses, eviction and old scoring snapshots must stay exact.
for i=1,10000 do
    local ctx=tostring(i)
    check(learning.prefix_score(index,"review","ab","甲",ctx)==prefix_reference(ref,"review","ab","甲",ctx),
        "prefix eviction/context alias changed score")
    check(learning.prefix_score(index,"review","missing"..i,"甲",ctx)==0,"cached arbitrary miss changed score")
end
check(learning.prefix_score(index,"review","ab","甲","")==prefix_reference(ref,"review","ab","甲",""),
    "old snapshot changed after cache eviction")

-- Compare every reward lookup, not just its final maximum, to the original
-- chars/context algorithm. This covers invalid UTF-8, real BOS bytes, missing
-- path metadata and reuse of a path with an unrelated prefix.
local function original_chars(text)
    local result={}
    for c in text:gmatch("[%z\1-\127\194-\244][\128-\191]*")do
        local a,b=c:byte(1,2)
        local n=a<128 and 1 or a<224 and 2 or a<240 and 3 or 4
        if #c~=n or (a==224 and b<160) or (a==237 and b>=160) or
            (a==240 and b<144) or (a==244 and b>=144)then return {} end
        result[#result+1]=c
    end
    if table.concat(result)~=text then return {} end
    return result
end
local calls
local real_score,real_prefix=learning.score,learning.prefix_score
learning.score=function(_,mode,code,text,ctx)calls[#calls+1]=frame("s",mode,code,text,ctx);return 0 end
learning.prefix_score=function(_,mode,code,text,ctx)calls[#calls+1]=frame("p",mode,code,text,ctx);return 0 end
local function reference_reward(text,previous)
    local start=previous
    while true do
        local t,r=start and start.text_length or 0,start and start.raw_length or 0
        local fragment=text:sub(t+1)
        if #original_chars(fragment)>16 then break end
        local ctx=learning.context(text:sub(1,t))
        learning.score(nil,"review",("abcd"):sub(r+1),fragment,ctx)
        learning.prefix_score(nil,"review",("abcd"):sub(r+1),fragment,ctx)
        if not start or r==0 then break end
        start=start.previous
    end
end
local texts={"", "甲", "甲乙", "𠀀😀", "\0\2\3", string.rep("甲",16),string.rep("甲",17)}
for byte=0,255 do
    texts[#texts+1]=string.char(byte).."甲"
    texts[#texts+1]="甲"..string.char(byte)..string.rep("乙",17)
end
for _,text in ipairs(texts)do
    for _,prefix in ipairs({"", "前", "前后", "\2", "\255",string.rep("𠀀",32)})do
        local path={text=prefix,text_length=#prefix,raw_length=2,previous={text="",text_length=0,raw_length=0}}
        for variant=1,3 do
            if variant==2 then path.text=nil elseif variant==3 then path.text="unrelated" end
            calls={};reference_reward(prefix..text,path);local expected=table.concat(calls,"|")
            calls={};learning.reward({codes={"ab"}},"review","abcd",prefix..text,4,path)
            check(table.concat(calls,"|")==expected,"learning text metadata changed a reward lookup")
        end
    end
end
learning.score,learning.prefix_score=real_score,real_prefix
local memo=require("tiger_sentence_cache")
local cache=memo.new(2)
memo.put(cache,"a",false);memo.put(cache,"b",0)
check(cache.values.a==false and cache.values.b==0,"memo lost cached zero/false")
memo.put(cache,"a",true);memo.put(cache,"c",1)
check(cache.values.a==nil and cache.values.b==0 and #cache.keys==2,"memo capacity/update semantics changed")
print(string.format('{"status":"passed","review_checks":%d,"production_model":false}',checks))
