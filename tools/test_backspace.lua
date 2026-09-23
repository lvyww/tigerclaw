-- Actual processor -> synchronous translator, plus a fresh-decoder oracle.
-- Runs only in the regression runner's owned tree, or an explicitly supplied test root.
local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
local model_required, benchmark, compact = false, false, false
for i=2,#arg do
    if arg[i]=="--require-model" then model_required=true end
    if arg[i]=="--benchmark" then benchmark=true end
    if arg[i]=="--compact" then compact=true end
end
rime_api = {get_user_data_dir=function() return repo end}
os.time = function() return 1800000000 end
local real_open = io.open
local entries = "甲\tab\n乙\tab\n是\tcd\n好\tcd\n我\tef\n你\tgh\n𠀀\tij\n甲乙\tklmnop\n"
for i=1,15 do entries=entries.."字"..i.."\tqr\n" end
io.open = function(path, mode)
    local text
    if path == repo.."/rime/tiger_sentence/tiger_sentence.codes.txt" then text=entries
    elseif path == repo.."/rime/tiger_sentence/tiger_sentence.char_ranks.txt" then text="甲\n乙\n是\n好\n我\n你\n𠀀\n"
    elseif path == repo.."/rime/tiger_sentence/tiger_sentence.full_code_whitelist.txt" then text=""
    elseif path == repo.."/rime/tiger_sentence/tiger_sentence.supplement.txt" then text="甲是\t1000\n" end
    if text then return {read=function()return text end,close=function()end} end
    return real_open(path,mode)
end
local sentence = dofile(os.getenv("TIGER_SENTENCE_MODULE") or repo.."/rime/tiger_sentence/lua/rime/tiger_sentence/tiger_sentence.lua")
local oracle = dofile(os.getenv("TIGER_BACKSPACE_ORACLE") or repo.."/rime/tiger_sentence/lua/rime/tiger_sentence/tiger_sentence.lua")
for _,s in ipairs({sentence,oracle}) do
    s.apply_high_freq_limit(0); s.set_model_enabled(model_required)
    if compact then assert(s.set_memory_profile and s.set_memory_profile("compact"), "compact profile unavailable") end
    if model_required then assert(s.model_status().loaded,"backspace requires the supplied model") end
end
local checks=0
local function check(ok,message) checks=checks+1;assert(ok,message) end
local function slot(root,name)
    local seen={}
    local function visit(fn)
        if seen[fn] then return end;seen[fn]=true
        local nested={}
        for i=1,200 do
            local n,v=debug.getupvalue(fn,i);if not n then break end
            if n==name then return fn,i,v end
            if type(v)=="function" then nested[#nested+1]=v end
        end
        for _,f in ipairs(nested) do local p,i,v=visit(f);if p then return p,i,v end end
    end
    local p,i,v=visit(root);assert(p,"missing production closure: "..name);return p,i,v
end
local builds,positions=0,0
for _,name in ipairs(benchmark and {} or {"new_states","expand_range"}) do
    local p,i,f=slot(sentence.decode,name)
    debug.setupvalue(p,i,function(...)
        if name=="new_states" then builds=builds+1 else
            local _,_,from,finish=...;positions=positions+math.max(0,finish-from)
        end
        return f(...)
    end)
end
local _,_,state_of=slot(sentence.processor,"sentence_state")
local learning=sentence.learning
local store={db={update=function()return true end},events={},count=0,bytes=0,sequence=0,scored_at=os.time()}
store.index=learning.runtime_index({},os.time())
learning.open=function()return store end
local function frame(locks)
    local out={}
    for _,l in ipairs(locks) do
        for _,v in ipairs({l.raw,l.text,l.boundaries}) do out[#out+1]=#v..":"..v end
    end
    return table.concat(out)
end
Candidate=function(kind,start,finish,text,comment)
    return {type=kind,start=start,_end=finish,text=text,comment=comment}
end
local function render(s,env)
    local out={};yield=function(c)out[#out+1]=c end
    local ctx=env.engine.context
    s.translator(ctx.input,{start=0,_end=#ctx.input},env)
    return out
end
local function same_menu(a,b)
    check(#a==#b,"backspace changed visible candidate count")
    for i,x in ipairs(a) do
        for _,field in ipairs({"type","start","_end","text","comment","preedit","quality"}) do
            check(x[field]==b[i][field],"backspace changed candidate "..field)
        end
    end
end
local function environment(raw,opts)
    opts=opts or {}
    local properties,commits={},{}
    properties.tiger_sentence_committed=(opts.committed_raw or "").."\t"..(opts.committed_text or "")
    properties.tiger_sentence_buffered_text=opts.buffer or ""
    properties.tiger_sentence_locks=frame(opts.locks or {})
    local marker=opts.buffer and opts.buffer~="" and "~" or ""
    local ctx={input=marker..raw,caret_pos=#marker+#raw,updates=0}
    local schema={schema_id="backspace-test",config={
        get_int=function(_,k)if k=="tiger_sentence/high_freq_limit" then return 0 end end,
        get_bool=function(_,k)if k=="tiger_sentence/tab_learning" then return opts.learning==true end end,
        get_string=function()return opts.profile or (compact and "compact" or nil) end}}
    local engine={context=ctx,schema=schema,commit_text=function(_,t)commits[#commits+1]=t end}
    local env,tenv,renv={engine=engine},{engine=engine},{engine=engine}
    function ctx:get_property(k)return properties[k] or "" end
    function ctx:set_property(k,v)properties[k]=v end
    function ctx:get_option(k)return k=="tiger_sentence_allow_duplicate_single" and opts.duplicate~=false end
    function ctx:set_option()end
    function ctx:is_composing()return self.input~="" end
    ctx.composition={empty=function()return true end}
    function ctx:has_menu()return false end
    local function changed()
        ctx.updates=ctx.updates+1
        ctx.menu=render(sentence,tenv)
    end
    function ctx:set_input(value)self.input=value;self.caret_pos=#value;changed();return true end
    function ctx:refresh_non_confirmed_composition()changed();return true end
    function ctx:push_input(ch)
        local n=self.caret_pos
        self.input=self.input:sub(1,n)..ch..self.input:sub(n+1);self.caret_pos=n+#ch;changed();return true
    end
    function ctx:pop_input(n)
        local c=self.caret_pos;if c<n then return false end
        self.input=self.input:sub(1,c-n)..self.input:sub(c+1);self.caret_pos=c-n;changed();return true
    end
    function ctx:delete_input(n)
        local c=self.caret_pos;if c+n>#self.input then return false end
        self.input=self.input:sub(1,c)..self.input:sub(c+n+1);changed();return true
    end
    function ctx:clear()self.input="";self.caret_pos=0;changed() end
    local function verify()
        oracle.reset_decode_cache()
        same_menu(ctx.menu,render(oracle,renv))
        local st=state_of(ctx,env)
        local lock=st.locks[#st.locks]
        local raw=ctx.input
        if st.buffered_text~="" and raw:sub(1,1)=="~" then raw=raw:sub(2) end
        if raw~="" then
            local a=sentence.decode(st.committed_raw..raw,true,st.committed_text,lock)
            oracle.reset_decode_cache()
            local b=oracle.decode(st.committed_raw..raw,true,st.committed_text,lock)
            check(sentence.results_equal(a,b),"backspace differs from fresh full-pool/evidence decode")
        end
        check(#commits==0,"manual edit committed text")
    end
    -- Install processor state/learning before warming the translator generation.
    local event={repr=function()return "Control_L" end,release=function()return false end,
        ctrl=function()return false end,alt=function()return false end,super=function()return false end,
        shift=function()return false end}
    sentence.processor(event,env)
    changed()
    local function key(repr,inspect)
        event.repr=function()return repr end
        local result=sentence.processor(event,env)
        if result==2 and repr=="BackSpace" then ctx:pop_input(1)
        elseif result==2 and repr=="Delete" then ctx:delete_input(1) end
        if inspect~=false then verify() end
    end
    return env,ctx,properties,key,verify
end
local lock={raw="ab",text="甲",boundaries="2,3;"}
if benchmark then
    -- Only the deletion + synchronous translation is timed. Fixture creation,
    -- warming and explicit collection are outside timing; ordinary GC remains on.
    -- This is a synthetic lexicon CPU probe, never physical iOS input latency.
    print("scenario,length,mean_ms,p95_ms,max_ms,model_loaded")
    for _,kind in ipairs({"locked","buffered-code","buffered-text"}) do
        for _,length in ipairs({32,80,128}) do
            local times={}
            for round=1,31 do
                local raw="ab"..("cdef"):rep(math.ceil(length/4))
                raw=raw:sub(1,length+1)
                local opts={locks={lock},duplicate=true}
                if kind=="buffered-code" then
                    opts.buffer="甲";opts.committed_raw="ab";opts.committed_text="甲";raw=raw:sub(3)
                elseif kind=="buffered-text" then
                    local text=("甲𠀀"):rep(length/2)
                    opts.buffer=text;opts.committed_raw=raw;opts.committed_text=text
                    opts.locks={{raw=raw,text=text,boundaries=#raw..","..#text..";"}};raw=""
                end
                sentence.reset_decode_cache()
                local _,ctx,_,key=environment(raw,opts)
                collectgarbage("collect")
                local start=os.clock();key("BackSpace",false);local ms=(os.clock()-start)*1000
                if round>1 then times[#times+1]=ms end
            end
            table.sort(times);local total=0;for _,t in ipairs(times)do total=total+t end
            print(string.format("%s,%d,%.6f,%.6f,%.6f,%s",kind,length,total/#times,
                times[math.ceil(#times*0.95)],times[#times],tostring(model_required)))
        end
    end
    io.open=real_open
    return
end
-- Both input-mutating adapters must keep end-deletion proportional to the end,
-- not to the entire locked suffix. Test from a pasted/full-built generation too.
for _,buffered in ipairs({false,true}) do
 for _,duplicate in ipairs({false,true}) do
    local opts={locks={lock},duplicate=duplicate}
    local raw="ab"..("cdef"):rep(20)
    if buffered then opts.buffer="甲";opts.committed_raw="ab";opts.committed_text="甲";raw=raw:sub(3) end
    local env,ctx,props,key,verify=environment(raw,opts)
    for _=1,12 do
        local b,p=builds,positions
        key("BackSpace",false)
        check(builds==b and positions==p,"Tail Backspace rebuilt the locked lattice")
        verify()
        if compact and _%4==0 then sentence.trim_memory();verify() end
        b,p=builds,positions
        render(sentence,{engine=env.engine})
        check(builds==b and positions==p,"same-generation refresh re-expanded the lattice")
    end
    -- Delete at a caret immediately before the last letter is the same suffix edit.
    ctx.caret_pos=#ctx.input-1
    local b,p=builds,positions;key("Delete",false)
    check(builds==b and positions==p,"Tail Delete rebuilt the locked lattice");verify()
    check(props.tiger_sentence_locks==frame({lock}),"tail edit changed unchanged lock")
 end
end
do
    local _,_,_,key,verify=environment("abklmnopc",{locks={lock}})
    local b,p=builds,positions;key("BackSpace",false)
    check(builds==b and positions==p,"long-code suffix unnecessarily rebuilt");verify()
    local second={raw="abcdef",text="甲是我",boundaries="2,3;4,6;6,9;"}
    local _,_,props,key2,verify2=environment("abcdefg",{locks={lock,second}})
    b=builds;key2("BackSpace",false)
    check(builds>b and props.tiger_sentence_locks==frame({lock}),"nested lock boundary was not rebuilt/unlocked")
    verify2()
end
-- No matching generation, a different lock, middle edits, unlocking, and
-- selector changes must never use the fast path.
for _,spec in ipairs({{raw="abcdcdef",caret=4,key="BackSpace"},
    {raw="abcdcdef",caret=4,key="Delete"},{raw="abc",key="BackSpace"},
    {raw="abc",key="Delete",caret=2},{raw="abqr12",key="BackSpace"},
    {raw="abcd;",key="BackSpace"},{raw="abcd'",key="BackSpace"}}) do
    local _,ctx,props,key,verify=environment(spec.raw,{locks={lock}})
    if spec.caret then ctx.caret_pos=spec.caret end
    local b=builds;key(spec.key,false)
    check(builds>b,"unsafe edit bypassed conservative rebuild");verify()
end
for _,stale in ipairs({"missing","raw","lock"}) do
    local _,ctx,props,key,verify=environment("ab"..("cdef"):rep(10),{locks={lock}})
    if stale=="missing" then sentence.reset_decode_cache()
    elseif stale=="raw" then sentence.decode("abcdef",false,"",lock)
    else props.tiger_sentence_locks=frame({{raw="ab",text="乙",boundaries="2,3;"}}) end
    local b=builds;key("BackSpace",false)
    check(builds>b,"stale generation used the tail fast path");verify()
end
-- Actual learned tail: removal must not retain the old cumulative inhibition
-- flag, even when that tail is not displayed. Pending confirmations are discarded.
do
    local env,ctx,_,key,verify=environment("abcdefgh",{locks={lock},learning=true})
    local mode=env._tiger_learning.mode
    store.index=learning.runtime_index({{code="gh",text="你",context="是我",mode=mode,time=os.time()}},os.time())
    key("Control_L",false);ctx:set_input("abcdefgh")
    local before=sentence.decode("abcdefgh",false,"",lock)
    check(before.learning_affected,"learning fixture did not affect deleted tail")
    env._tiger_learning.pending={{text="你"}};env._tiger_learning.baseline=before[1]
    local b=builds;key("BackSpace",false)
    check(builds>b,"learning-affected deletion reused cumulative inhibition")
    verify()
    check(#env._tiger_learning.pending==0 and env._tiger_learning.baseline==nil,"edit retained pending learning")
    store.index=learning.runtime_index({},os.time())
end
-- Text-only preedit does not need an EOS/model/history replay just to display
-- an empty candidate suffix. Unicode deletion and subsequent code still work.
do
    local text=("甲𠀀"):rep(24)
    local raw=("abij"):rep(24)
    local opts={buffer=text,committed_raw=raw,committed_text=text,
        locks={{raw=raw,text=text,boundaries=#raw..","..#text..";"}}}
    local _,ctx,props,key,verify=environment("",opts)
    for _=1,10 do
        local b,p=builds,positions;key("BackSpace",false)
        check(builds==b and positions==p,"Text-only Backspace replayed locked history")
        local chars={};for c in text:gmatch("[%z\1-\127\194-\244][\128-\191]*") do chars[#chars+1]=c end
        table.remove(chars);text=table.concat(chars)
        check(props.tiger_sentence_buffered_text==text,"Unicode buffer deletion split a character")
        verify()
        check(#ctx.menu==1 and ctx.menu[1].text=="" and ctx.menu[1].preedit==text,"text-only suffix/preedit changed")
    end
    key("c");key("d");verify()
    check(ctx.menu[1].text~="","text-only fast path swallowed resumed code")
end
-- Fault during end rescoring must still downgrade and replay coherently.
if not model_required then
    local _,_,_,key,verify=environment("abcdefc",{locks={lock}})
    local p,i=slot(sentence.decode,"model_disabled");debug.setupvalue(p,i,false)
    local closes=0
    p,i=slot(sentence.decode,"kn_model")
    debug.setupvalue(p,i,{logp=function()error("backspace model failure")end,close=function()closes=closes+1 end})
    p,i=slot(sentence.decode,"logp_cache");debug.setupvalue(p,i,{})
    key("BackSpace",false)
    check(closes==1 and not sentence.model_status().loaded,"backspace model failure escaped coherent fallback")
    verify();sentence.set_model_enabled(false)
end
io.open=real_open
print(string.format('{"status":"passed","backspace_checks":%d,"synchronous_fake_host":true,"model_loaded":%s}',checks,tostring(model_required)))
