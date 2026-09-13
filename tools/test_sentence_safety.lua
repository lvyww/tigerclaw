-- Independent probability-pool fixtures and model-failure tests of production
-- functions. debug is test-only: it locates existing private closures, so no
-- hooks or copied decoder implementations enter the runtime module.
local repo=arg[1] or "."
rime_api={get_user_data_dir=function()return repo end}
local sentence=dofile(os.getenv("TIGER_SENTENCE_MODULE") or repo.."/lua/tiger_sentence.lua")
sentence.ensure_lexicon(nil)
sentence.set_model_enabled(false)
local checks,cases=0,0
local function check(ok,message)checks=checks+1;assert(ok,message)end
local function slot(root,name)
    local visited={}
    local function visit(fn)
        if visited[fn] then return end
        visited[fn]=true
        local nested={}
        for i=1,200 do
            local n,v=debug.getupvalue(fn,i)
            if not n then break end
            if n==name then return fn,i,v end
            if type(v)=="function" then nested[#nested+1]=v end
        end
        for _,v in ipairs(nested)do
            local f,i,value=visit(v)
            if f then return f,i,value end
        end
    end
    local f,i,v=visit(root)
    assert(f,"Test cannot locate production upvalue "..name)
    return f,i,v
end
local function value(root,name)local _,_,v=slot(root,name);return v end
local emit=value(sentence.decode,"emit")
local cache=value(sentence.decode,"decode_cache")
local function path(text,raw_length,weight,rank,previous)
    return {text=text,raw_length=raw_length,text_length=#text,score=math.log(weight),
        mass_score=math.log(weight),max_rank=rank,prev1="字",prev2="甲",
        edge_count=previous and 2 or 1,previous=previous,supplement_score=0}
end
local function seed_cache(raw,states,result)
    sentence.reset_decode_cache()
    cache.raw=raw;cache.states=states;cache.result=result;cache.includes_early_commit=false
    cache.required_text_prefix="";cache.allow_duplicate=true
end
-- 21 complete paths (< beam 200): display may omit the dissenting path, but
-- independent full-pool mass is 20 / 20.9, not 100 percent.
do
    local states={[4]={}}
    for i=1,20 do states[4][i]=path("甲"..tostring(i),4,1,1,path("甲",2,1,1)) end
    states[4][21]=path("乙字",4,0.9,2,path("乙",2,0.9,2))
    local result=emit("aabb",states,4,true,"")
    check(#result==20,"Display candidate limit changed")
    local evidence=sentence.find_prefix_evidence(result.early_commit_evidence.prefixes,"甲",2)
    check(evidence and math.abs(evidence.share-20/20.9)<1e-12,"Display Top-K inflated confidence")
    check(evidence.share<0.995 and result.early_commit_evidence.proposal=="","Omitted dissent allowed early proposal")
    -- The translator-to-processor evidence upgrade uses the same full pool.
    result=emit("aabb",states,4,false,"")
    seed_cache("aabb",states,result)
    local upgraded=sentence.decode("aabb",true,"")
    evidence=sentence.find_prefix_evidence(upgraded.early_commit_evidence.prefixes,"甲",2)
    check(upgraded==result and math.abs(evidence.share-20/20.9)<1e-12,"Evidence cache upgrade lost off-menu mass")
    sentence.reset_decode_cache();cases=cases+1
end
-- The current boundary may be tiny although an earlier beam was truncated.
do
    local new_states=value(sentence.decode,"new_states")
    local expand=value(sentence.decode,"expand_range")
    local states=new_states(2);states[0]._truncated=true
    expand("ot",states,0,2)
    check(#states[2]>0 and states[2]._truncated,"Descendant lost ancestor truncation")
    local result=emit("ot",states,2,true,"")
    check(result.early_commit_evidence.confidence_truncated and result.early_commit_evidence.proposal=="",
        "Descendant published confidence after lost search mass")
    cases=cases+1
end
-- A ranking-only top 20 can hide the dominant dissent in confidence space.
-- Strong empty-code acceptance must use all eligible scored paths, too.
do
    local states={[4]={}}
    states[4][1]=path("甲一",4,1,1,path("甲",2,1,1))
    for i=2,20 do states[4][i]=path("甲"..i,4,1e-8,i,path("甲",2,1e-8,i)) end
    states[4][21]=path("乙字",4,0.01,21,path("乙",2,0.01,21))
    local result=emit("aabb",states,4,false,"")
    seed_cache("aabb",states,result)
    check(sentence.capture_empty_code_candidate("aabb","")==nil,"Empty-code confidence ignored off-menu dissent")
    sentence.reset_decode_cache();cases=cases+1
end
-- Inject failures at the same scoring boundary used by paged model reads.
-- The whole lattice must be rebuilt in fallback mode, not half model/half zero.
local function model(fake)
    sentence.set_model_enabled(false)
    sentence.set_model_enabled(true)
    local f,i=slot(sentence.decode,"kn_model")
    debug.setupvalue(f,i,fake)
    value(sentence.decode,"clear_model_dependent_caches")()
end
for _,operation in ipairs({"logp","has_observed_bigram","nonfinite"})do
    local calls,closed=0,0
    local fake={close=function()closed=closed+1 end,
        logp=function()return -1 end,has_observed_bigram=function()return false end}
    if operation=="has_observed_bigram" then
        fake.has_observed_bigram=function()calls=calls+1;error("injected model page read failure")end
    else
        fake.logp=function()
            calls=calls+1
            if calls==4 then
                if operation=="nonfinite" then return 0/0 end
                error("injected model page read failure")
            end
            return -1
        end
    end
    model(fake)
    local ok,result=pcall(sentence.decode,"jqtusotuqiueottu",true,"")
    check(ok,"Model failure escaped decode guard")
    check(calls>=1 and closed==1,"Failed model was not closed exactly once")
    local status=sentence.model_status()
    check(not status.loaded and status.error:find("runtime n%-gram failure"),"Model failure was not diagnosable")
    local reference=sentence.decode_full("jqtusotuqiueottu",true,"")
    check(sentence.results_equal(result,reference),"Model failure mixed two scoring policies")
    sentence.decode("ueot",true,"")
    check(closed==1,"Failed model was reopened or closed twice")
    cases=cases+1
end
-- Non-model failures are never converted to a successful fallback result.
do
    local closed=0
    model({logp=function()return -1 end,close=function()closed=closed+1 end})
    local ok=pcall(sentence.decode,{invalid=true})
    check(not ok and closed==0,"Non-model programming error was swallowed")
    sentence.set_model_enabled(false)
    check(closed==1,"Explicit model disable did not close its resource")
    cases=cases+1
end
-- Input is already modified when decoding fails. The processor must still
-- accept it exactly once, with no stale automatic commit from the old model.
for _,synchronous_translation in ipairs({false,true})do
    local closed=0
    model({logp=function()error("injected model page read failure")end,
        close=function()closed=closed+1 end})
    local properties,commits={},{}
    local context={input="ueo",caret_pos=3,pushes=0}
    function context:get_property(k)return properties[k] or "" end
    function context:set_property(k,v)properties[k]=v end
    function context:get_option(k)return k=="tiger_sentence_early_commit" or k=="tiger_sentence_allow_duplicate_single" end
    function context:is_composing()return self.input~="" end
    function context:push_input(ch)
        self.pushes=self.pushes+1;self.input=self.input..ch;self.caret_pos=#self.input
        if synchronous_translation then
            Candidate=function(_,_,_,text)return {text=text}end;yield=function()end
            sentence.translator(self.input,{start=0,_end=#self.input},{engine={context=self}})
        end
    end
    local env={engine={context=context,commit_text=function(_,text)commits[#commits+1]=text end}}
    local event={repr=function()return "t" end,release=function()return false end,
        ctrl=function()return false end,alt=function()return false end,super=function()return false end}
    check(sentence.processor(event,env)==1,"Post-mutation model failure leaked the key")
    check(context.input=="ueot" and context.pushes==1 and #commits==0,"Failed decode duplicated/lost input")
    check(closed==1,"Processor failure did not retire the model")
    cases=cases+1
end
-- Actual loader failure: the probing handle and truncated mobile handle close,
-- without depending on Lua's garbage collector eventually reclaiming them.
do
    local original=io.open;local closes=0
    io.open=function(name,mode)
        if name~="injected-model.bin" then return original(name,mode)end
        return {read=function()return "TCSKNM02" end,close=function()closes=closes+1 end}
    end
    local ok=pcall(sentence.load_ngram_model,"injected-model.bin")
    io.open=original
    check(not ok and closes==2,"Truncated model loader leaked its open handle")
    cases=cases+1
end
print(string.format('{"status":"passed","cases":%d,"checks":%d,"independent_evidence":true,"model_faults_injected":true,"real_model":false}',cases,checks))
