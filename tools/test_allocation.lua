-- Isolated allocation/representation invariants. No live Rime database.
local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
rime_api = {get_user_data_dir=function() return repo end}
local sentence = dofile(os.getenv("TIGER_SENTENCE_MODULE") or repo.."/rime/tiger_sentence/lua/rime/tiger_sentence/tiger_sentence.lua")
local checks = 0
local function check(ok, msg) checks=checks+1; assert(ok, msg) end
sentence.set_model_enabled(false)
local original_open=io.open
local entries="甲\tab\n乙\tab\n丙\tcd\n丁\tcd\n𠀀\tef\n甲丙\tabcd\n乙丁\tabcd\n"
io.open=function(path,mode)
    if path==repo.."/rime/tiger_sentence/tiger_sentence.codes.txt" then
        return {read=function()return entries end,close=function()end}
    end
    return original_open(path,mode)
end
sentence.apply_high_freq_limit(0)
sentence.set_allow_duplicate_single({get_option=function()return true end})
-- An old published snapshot survives later decode/trim/reset generations.
local function signature(result)
    local out={}
    for _,v in ipairs(result) do
        out[#out+1]=v.text.."|"..v.score
        local n=v.path
        while n do out[#out+1]=(n.text or "")..":"..(n.raw_length or 0);n=n.previous end
    end
    for _,p in ipairs(result.early_commit_evidence.prefixes) do
        out[#out+1]=p.text..":"..p.share..":"..p.base_share
    end
    return table.concat(out,";")
end
local old=sentence.decode("abcdef",true,"");local saved=signature(old)
-- The light evaluator must not call path-isolation (a menu-ranking prior),
-- while computing both confidence scores identically to the full evaluator.
local function seam(root,name)
    local seen={}
    local function find(fn)
        if seen[fn] then return end;seen[fn]=true
        local nested={}
        for i=1,200 do
            local n,v=debug.getupvalue(fn,i);if not n then break end
            if n==name then return fn,i,v end
            if type(v)=="function" then nested[#nested+1]=v end
        end
        for _,child in ipairs(nested) do local f,i,v=find(child);if f then return f,i,v end end
    end
    local f,i,v=find(root);assert(f,"missing performance seam: "..name);return f,i,v
end
local _,_,full=seam(sentence.decode,"evaluate_state")
local _,_,light=seam(sentence.decode,"evaluate_evidence_state")
local f,i,prior=seam(sentence.decode,"path_isolation_penalty")
for _,candidate in ipairs(old._confidence_candidates) do
    local expected=full(candidate.path)
    local function forbidden() error("Partial evidence evaluated ranking-only isolation") end
    debug.setupvalue(f,i,forbidden)
    local ok,actual=pcall(light,candidate.path)
    debug.setupvalue(f,i,prior)
    check(ok,"Partial evidence evaluated ranking-only isolation")
    check(actual.text==expected.text and actual.path==expected.path and
        actual.confidence_score==expected.confidence_score and
        actual.early_commit_confidence_score==expected.early_commit_confidence_score,
        "Light evidence changed confidence")
end
for i=1,25 do sentence.decode("abcd"..string.rep("ab",i%5),true,"");sentence.trim_memory() end
check(signature(old)==saved,"later generation mutated a published path/evidence snapshot")
-- Cached character count remains independent of UTF-8 byte length.
for _,p in ipairs(old.early_commit_evidence.prefixes) do
    local _,count=p.text:gsub("[^\128-\191]","")
    check(p.text_char_count==count,"prefix character count confused bytes with codepoints")
end
-- One pair is evaluated no more than once during a source-stable merge.
local learning=sentence.learning
local original_score=learning.fusion_score
local calls={}
learning.fusion_score=function(_,_,_,d,s)
    local key=d..":"..s;calls[key]=(calls[key] or 0)+1
    return d=="C" and s=="A" and 10 or 0
end
sentence.set_learning_for_test({codes={}},"allocation-test")
local list={{text="A",source_mask=2},{text="B",source_mask=1,direct_rank=1},
    {text="C",source_mask=1,direct_rank=2},{text="D",source_mask=1,direct_rank=3},
    {text="E",source_mask=1,direct_rank=4}}
sentence.apply_fusion_ordering_for_test("ii",list)
check(list[1].text=="B" and list[2].text=="C" and list[3].text=="A" and list[4].text=="D",
    "Fusion prefix promotion no longer produces B,C,A,D")
for _,n in pairs(calls) do check(n==1,"Fusion recalculated an identical pair in one merge") end
learning.fusion_score=original_score;sentence.set_learning_for_test(nil,"");io.open=original_open
print(string.format('{"status":"passed","allocation_checks":%d,"real_frontend":false}',checks))
