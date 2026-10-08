-- Production scoring gate and confidence isolation, using the real code table.
-- Run in the isolated tree created by tools/run_regressions.py.
local repo = arg[1] or "."
package.path = repo .. "/lua/?.lua;" .. package.path
rime_api = {get_user_data_dir=function() return repo end}
local fixture_path=repo.."/sentence-fivegram-mobile.bin"
local created=false
local f=io.open(fixture_path,"rb")
if f then f:close() else
    dofile(repo.."/tools/model_fixture.lua")(fixture_path)
    created=true
end
local sentence=dofile(os.getenv("TIGER_SENTENCE_MODULE") or repo.."/lua/tiger_sentence.lua")
local checks=0
local function check(ok,label) checks=checks+1;assert(ok,label) end
local function near(a,b,label) check(math.abs(a-b)<1e-9,label..": "..tostring(a).." vs "..tostring(b)) end
local defaults=sentence.decoder_parameters()
check(defaults.canonical_code_reward==0,"primary-code reward must be disabled in production")
check(defaults.whole_input_single_character_reward==5,"whole single reward must stay +5")
check(defaults.canonical_isolation_factor==0 and defaults.canonical_isolation_min_code_length==4,
    "full-code rare-character protection changed")
check(sentence.model_status().loaded,"scoring gate requires the deterministic model fixture")
sentence.set_allow_duplicate_single(nil)
local function find(raw,text,incremental)
    sentence.reset_decode_cache()
    local menu=incremental and sentence.decode(raw,true) or sentence.decode_full(raw,true)
    for _,item in ipairs(menu._confidence_candidates or menu) do
        if item.text==text then return item end
    end
    for _,item in ipairs(menu) do if item.text==text then return item end end
    error("missing scoring probe "..raw.." -> "..text)
end
local function metadata(raw,text)
    for _,item in ipairs(sentence.lexicon_probe(raw) or {}) do
        if item.t==text then return item end
    end
end
local function delta(n,raw,text,expected,eligible)
    sentence.apply_high_freq_limit(n)
    if eligible~=nil then
        local entry=metadata(raw,text)
        check(entry and entry.whole_single_reward_eligible==eligible,"wrong eligibility "..raw.." N="..n)
    end
    sentence.set_decoder_parameters_for_test({whole_input_single_character_reward=0})
    local before=find(raw,text)
    sentence.set_decoder_parameters_for_test({whole_input_single_character_reward=5})
    local after=find(raw,text)
    near(after.score-before.score,expected,"whole score delta "..raw.." N="..n)
    near(after.confidence_score,before.confidence_score,"whole reward leaked into confidence "..raw)
    near(after.code_score or 0,0,"production path gained primary reward "..raw)
    local inc=find(raw,text,true)
    near(inc.score,after.score,"incremental/full score mismatch "..raw)
    near(inc.confidence_score,after.confidence_score,"incremental/full confidence mismatch "..raw)
end
for _,n in ipairs({0,1500}) do
    delta(n,"u","的",5,true)
    delta(n,"ujk","捡",5,true)
    delta(n,"ujkf","捡",5,true)
    delta(n,"jfob","便",5,true)
    delta(n,"ue","的",n==0 and 5 or 0,n==0)
    delta(n,"ujkf1","捡",0)
    delta(n,"u","工作",0)
    delta(n,"ujkfkf","捡滑",0)
end
-- Rank 4 不: the exact N boundary matters, not an off-by-one file-line count.
delta(3,"cb","不",5,true)
delta(4,"cb","不",0,false)
sentence.apply_high_freq_limit(1500)
check(metadata("unid","的")==nil,"existing high-frequency primary-code filter changed")
sentence.apply_high_freq_limit(0)
check(metadata("unid","的")~=nil,"N=0 failed to retain non-primary codes")
delta(0,"unid","的",5,true)
sentence.apply_high_freq_limit(1500)
if created then assert(os.remove(fixture_path)) end
print(string.format('{"status":"passed","whole_single_reward_checks":%d,"canonical_code_reward":0,"whole_reward":5}',checks))
