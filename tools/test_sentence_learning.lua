-- Run in the isolated public-pack layout. Production decoder and processor,
-- with a small host/LevelDb fake; never touches an installed input method.
local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
rime_api = {get_user_data_dir=function() return repo end}
local databases = {}
local writes, fail_write = 0, false
LevelDb = function(name)
    local data = databases[name] or {}; databases[name] = data
    return {
        open=function() return true end, close=function() end,
        query=function() return {iter=function()
            local keys, n = {}, 0
            for k in pairs(data) do keys[#keys+1]=k end
            table.sort(keys)
            return function() n=n+1; local k=keys[n]; if k then return k,data[k] end end
        end} end,
        update=function(_, k, v) if fail_write then return false end; writes=writes+1; data[k]=v; return true end
    }
end
local sentence = require("tiger_sentence")
local learning = sentence.learning
sentence.set_model_enabled(false)
sentence.ensure_lexicon(nil)
local checks = 0
local function check(ok, name) checks=checks+1; assert(ok, name) end
local now = os.time()
local function event(code, text, ctx, mode)
    return {time=now, mode=mode or "test", code=code, text=text, context=ctx or ""}
end
local index = learning.build({event("ab", "疒")}, now)
check(learning.score(index,"test","ab","疒","")==9, "first correction equals supplement weight 1000")
check(learning.score(index,"test","ab","疒","甲")==6, "first correction creates cross-context preference")
check(learning.score(index,"other","ab","疒","")==0, "mode isolation")
check(learning.score(learning.build({event("ab","疒")},now+3650*86400),"test","ab","疒","")==9,"learning has no time decay")
local repeated={}
for i=1,40 do
    repeated[#repeated+1]=event("ab","疒")
    local level=math.min(10,i)
    check(learning.score(learning.build(repeated,now),"test","ab","疒","")==7+2*level,
        "explicit corrections advance two points per level and cap at ten")
end
check(learning.score(learning.build(repeated,now),"test","ab","疒","其他")==24,
    "cross-context score caps at twenty-four")
local events={event("ab","甲乙","前"),event("ab","甲乙","后"),event("ab","甲乙","后")}
check(learning.score(learning.build(events,now),"test","ab","甲乙","新")==10,"three explicit corrections reach cross-context level three")
events[#events+1]=event("ab","甲丙","后")
check(learning.score(learning.build(events,now),"test","ab","甲乙","后")==6,"manual competitor correction demotes old local choice without time decay")
local before={text="甲乙",path={raw_length=4,text_length=6,previous={raw_length=2,text_length=3}}}
local selected={text="甲丙",path=before.path}
local diff=learning.diff("ABcd",before,selected,0,"test")
check(#diff==1 and diff[1].code=="cd" and diff[1].context=="甲","shared-boundary correction")
check(#learning.diff("abcd",before,selected,4,"test")==0,"locked boundary floor")
check(learning.context("甲乙😀")=="乙😀","unicode scalar context")
local pressure={}
for i=1,10000 do pressure[i]=event("abcd","甲"..tostring(i),tostring(i%10)) end
local indexed=learning.build(pressure,now)
for i=1,10000 do check(learning.prefix_score(indexed,"test","ab","甲",tostring(i%10))>0,"indexed prefix pressure") end

sentence.set_learning_for_test(index,"test")
local direct=sentence.decode("ab",true)
check(direct[1].text=="交" and not direct.learning_affected and
    (direct[1].source_mask==1 or direct[1].source_mask==3) and (direct[1].learning_score or 0)==0,
    "Direct-to-Direct history cannot change exact table order")

local composed_event=event("abcd","疒否")
local composed_index=learning.build({composed_event},now)
sentence.set_learning_for_test(composed_index,"test")
local result=sentence.decode("abcd",true)
check(result[1].text=="疒否" and result.learning_affected,"learning ranks a legal Composed path")
check(#result.early_commit_evidence.prefixes>0,"Composed learning participates in ordinary early-commit evidence")
check(math.abs((result[1].early_commit_confidence_score or result[1].confidence_score)-result[1].confidence_score)<1e-12,
    "first Composed correction contributes zero early confidence")
local full=sentence.decode_full("abcd",true)
check(result[1].score==full[1].score,"learned Composed incremental full parity")
local twice=learning.build({event("abcd","疒否"),event("abcd","疒否")},now)
sentence.set_learning_for_test(twice,"test")
local second=sentence.decode("abcd",true)
check((second[1].early_commit_confidence_score or second[1].confidence_score)>second[1].confidence_score,
    "second explicit Composed correction adds partial early confidence")
local thrice=learning.build({event("abcd","疒否"),event("abcd","疒否"),event("abcd","疒否")},now)
sentence.set_learning_for_test(thrice,"test")
local mature=sentence.decode("abcd",true)
check((mature[1].early_commit_confidence_score or mature[1].confidence_score)>
    (second[1].early_commit_confidence_score or second[1].confidence_score),
    "third explicit Composed correction adds more early confidence")
check(learning.early_commit_maturity(9)==0 and
    learning.early_commit_maturity(11)==0.5 and learning.early_commit_maturity(13)==1,
    "learning maturity maps first/second/third manual-correction levels to 0/0.5/1")

local fusion_mode="sentence-v2|test"
sentence.set_learning_for_test(nil,fusion_mode)
local merge={{text="A",source_mask=2},{text="B",source_mask=1,direct_rank=1},{text="C",source_mask=1,direct_rank=2}}
sentence.apply_fusion_ordering_for_test("ii",merge)
check(merge[1].text=="A" and merge[2].text=="B" and merge[3].text=="C","baseline cross-source order preserved")
local fusion_event=learning.fusion_event(fusion_mode,"ii","C","A",true,2)
sentence.set_learning_for_test(learning.build({fusion_event},now),fusion_mode)
merge={{text="A",source_mask=2},{text="B",source_mask=1,direct_rank=1},{text="C",source_mask=1,direct_rank=2}}
sentence.apply_fusion_ordering_for_test("ii",merge)
check(merge[1].text=="B" and merge[2].text=="C" and merge[3].text=="A",
    "Direct C over Composed A promotes only Direct prefix B,C")
sentence.set_learning_for_test(nil,"")
check(sentence.decode("abcd")[1].text=="交否","disable restores base Composed ranking")

local function key(repr)
    return {keycode=({comma=44,period=46})[repr],repr=function()return repr end,release=function()return false end,
        ctrl=function()return false end,alt=function()return false end,
        super=function()return false end,shift=function()return false end}
end
local function host(name, early)
    local properties, listeners, commits = {}, {}, {}
    local context = {input="",caret_pos=0}
    local segment = {selected_index=0}
    segment.menu = {prepare=function(_,n)return math.min(n,#sentence.decode(context.input)) end,
        candidate_count=function()return #sentence.decode(context.input)end}
    context.composition = {empty=function()return context.input=="" end,back=function()return segment end}
    function context:get_property(k)return properties[k] or ""end
    function context:set_property(k,v)properties[k]=v end
    function context:get_option(k)return k=="tiger_sentence_allow_duplicate_single" or (k=="tiger_sentence_early_commit" and early)end
    function context:is_composing()return self.input~=""end
    function context:has_menu()return self:is_composing()end
    function context:clear()self.input="";self.caret_pos=0;segment.selected_index=0 end
    function context:push_input(ch)self.input=self.input..ch;self.caret_pos=#self.input;segment.selected_index=0 end
    function context:highlight(i)segment.selected_index=i;return true end
    function context:get_commit_text()return self.actual or ""end
    function context:confirm_current_selection()
        self.actual=sentence.decode(self.input)[segment.selected_index+1].text
        if self.transform then self.actual=self.actual.."x"end
        commits[#commits+1]=self.actual
        for _,f in pairs(listeners)do f(self)end
        if self.repeat_notification then for _,f in pairs(listeners)do f(self)end end
        self:clear();return true
    end
    context.commit_notifier={connect=function(_,f)
        listeners[#listeners+1]=f;local i=#listeners
        return {disconnect=function()listeners[i]=nil end}
    end}
    local config={enabled=true,get_int=function()return nil end}
    function config:get_bool()return self.enabled end
    local env={engine={context=context,schema={schema_id=name,config=config},
        commit_text=function(_,text)commits[#commits+1]=text end}}
    local function press(repr)return sentence.processor(key(repr),env)end
    local function type_ot()press("a");press("b");press("c");press("d")end
    return env,context,press,type_ot,commits,config
end
local env,ctx,press,type_ot,commits,config=host("learning-test",false)
press("a");press("b");press("Tab");press("space")
check(writes==0 and sentence.decode("ab")[1].text=="交","manual Direct-to-Direct selection never learns")
type_ot();press("Tab");press("Escape")
check(writes==0,"cancel is not a correction")
type_ot();press("Down");press("space")
check(writes==0,"navigation without Tab does not learn")
type_ot();press("Tab");ctx.transform=true;press("space");ctx.transform=false
check(writes==0,"transformed commit does not learn")
type_ot();press("Tab");press("space")
check(writes==1 and commits[#commits]=="疒否","host Composed submission learns once")
type_ot();check(sentence.decode("abcd")[1].text=="疒否","next composition uses Composed learning")
press("space");check(writes==1,"ordinary learned first choice never reinforces")
type_ot();press("space");check(writes==1,"repeated learned top1 use keeps the same level")
type_ot();press("space");check(writes==1,"normal top1 use remains write-free")
config.enabled=false;type_ot()
check(sentence.decode("abcd")[1].text=="交否","schema setting disables scoring")
press("Escape");config.enabled=true;type_ot()
check(sentence.decode("abcd")[1].text=="疒否","reenable retains preferences")
press("Escape")
sentence.processor_component.fini(env)
local other,_,other_press,other_type=host("other-schema",false)
other_type();check(sentence.decode("abcd")[1].text=="交否","schemas do not share records")
other_press("Tab");fail_write=true;other_press("space");fail_write=false
other_type();check(sentence.decode("abcd")[1].text=="交否","failed persistence does not publish score")
other_press("Escape");sentence.processor_component.fini(other)
local lock_env,lock_ctx,lock_press,lock_type=host("lock-schema",true)
lock_type();lock_press("Tab");local previous=writes;lock_press("a")
check(writes==previous+1,"Tab next letter submission learns")
check(lock_ctx.input=="a","Tab learning keeps live raw suffix")
sentence.processor_component.fini(lock_env)
local reopened = dofile(repo.."/rime/tiger_sentence/lua/tiger_sentence_learning.lua")
local persisted = reopened.open("tiger_sentence_learning_"..learning.hash("learning-test"))
check(persisted.count==1 and #persisted.events==1,"database restart loads only the explicit correction")
local saved=persisted.events[1]
check(reopened.score(persisted.index,saved.mode,saved.code,saved.text,saved.context)==9,
    "length-framed persistence preserves level-one exact score")
check(not learning.confirm({db={update=function()error("must not write")end},count=10000}, {event("ab","乙")}),"bounded event history")
local tap_env,tap_ctx,tap_press,tap_type,_,tap_config=host("tap-schema",false)
local before_taps=writes
tap_type();tap_ctx:highlight(0);tap_ctx:confirm_current_selection()
check(writes==before_taps,"tapping first candidate does not reinforce")
tap_type();tap_ctx:highlight(1);tap_ctx.transform=true;tap_ctx:confirm_current_selection();tap_ctx.transform=false
check(writes==before_taps,"transformed tap is not learned")
tap_type();tap_ctx:highlight(1);tap_ctx.repeat_notification=true;tap_ctx:confirm_current_selection()
check(writes==before_taps+1,"non-first tap learns exactly once without Tab or space")
tap_type();check(sentence.decode("abcd")[1].text=="疒否","tap changes next Composed ranking")
tap_ctx:highlight(0);tap_ctx:confirm_current_selection()
check(writes==before_taps+1,"tapping learned first candidate does not reinforce")
tap_config.enabled=false;tap_type();tap_ctx:highlight(1);tap_ctx:confirm_current_selection()
check(writes==before_taps+1,"disabled learning ignores taps")
tap_config.enabled=true;tap_type();tap_press("Tab");tap_ctx:confirm_current_selection()
check(writes==before_taps+2,"Tab followed by tap records one correction")
sentence.processor_component.fini(tap_env)
for _, punctuation in ipairs({"comma", "period"}) do
    local p_env,p_ctx,p_press,p_type=host("punct-"..punctuation,false)
    local start_writes=writes
    p_type();p_press("Tab");p_press("Escape");p_press(punctuation)
    check(writes==start_writes,"cancel before punctuation does not learn")
    p_type();p_press("Tab");p_ctx.transform=true;p_press(punctuation);p_ctx.transform=false
    check(writes==start_writes,"transformed punctuation commit does not learn")
    p_type();p_press("Tab");p_ctx.repeat_notification=true
    check(p_press(punctuation)==2,"punctuation stays with native punctuator")
    check(writes==start_writes+1,"punctuation confirmation learns exactly once")
    check(p_ctx.input=="","sentence submitted before punctuation input")
    p_press(punctuation)
    check(writes==start_writes+1,"idle punctuation cannot replay correction")
    sentence.processor_component.fini(p_env)
end
-- Runtime partitions must match an independent full journal replay, including
-- competing choices, generalization, caps, old snapshots and clock jumps.
local real_time, clock = os.time, now
os.time = function() return clock end
local optimized = learning.open("incremental-index-equivalence")
local function compare_index(actual, history, at)
    local expected = learning.build(history, at)
    check(table.concat(actual.codes, "|") == table.concat(expected.codes, "|"), "sorted learned codes")
    for _, code in ipairs({"ab", "abcd", "abce", "bc", "zz"}) do
        for _, mode in ipairs({"test", "other"}) do
            for _, ctx in ipairs({"", "前", "后", "新", "😀"}) do
                for _, text in ipairs({"甲乙", "甲丙", "甲", "乙", "😀乙"}) do
                    check(math.abs(learning.score(actual,mode,code,text,ctx) -
                        learning.score(expected,mode,code,text,ctx)) < 1e-9, "incremental exact score parity")
                    check(math.abs(learning.prefix_score(actual,mode,code,text,ctx) -
                        learning.prefix_score(expected,mode,code,text,ctx)) < 1e-9, "incremental prefix score parity")
                end
            end
        end
    end
end
local frozen, frozen_events, frozen_time
for i = 1, 80 do
    clock = clock + (i % 9 == 0 and 30 * 86400 or 73)
    local e = {time=clock - i%4 * 86400, code=({"ab","abcd","abce","bc"})[i%4+1],
        mode=i%5==0 and "other" or "test", text=({"甲乙","甲丙","乙","😀乙"})[i%4+1],
        context=({"","前","后","😀"})[math.floor(i/4)%4+1]}
    check(learning.confirm(optimized, {e}), "incremental confirmation accepted")
    if i%8==0 then compare_index(optimized.index, optimized.events, clock) end
    if i==20 then
        frozen, frozen_time, frozen_events = optimized.index, clock, {}
        for j, value in ipairs(optimized.events) do frozen_events[j] = value end
    end
    clock = clock + 61
    learning.refresh_scores(optimized)
    if i%8==0 then compare_index(optimized.index, optimized.events, clock) end
end
compare_index(frozen, frozen_events, frozen_time)
local future = {time=clock+86400, mode="test", code="abcd", text="甲丙", context="前"}
check(learning.confirm(optimized,{future}), "future timestamp confirmation")
compare_index(optimized.index,optimized.events,clock)
clock=clock+172800; learning.refresh_scores(optimized)
compare_index(optimized.index,optimized.events,clock)
clock=clock-259200; learning.refresh_scores(optimized)
compare_index(optimized.index,optimized.events,clock)
local snapshot = optimized.index
fail_write=true
check(not learning.confirm(optimized,{event("ab","乙")}), "failed write rejects runtime update")
check(optimized.index==snapshot, "failed write retains snapshot identity")
fail_write=false
-- Decay refresh does not replay normal journals, even with 10,000 records.
clock=now
local many={}
for i=1,10000 do many[i]={time=clock,mode="test",code="ab"..tostring(i),text="甲乙",context=""} end
local large={db={update=function()return true end},events=many,index=learning.runtime_index(many,clock),
    scored_at=clock,count=9999,sequence=9999,bytes=0}
local runtime_index=learning.runtime_index
learning.runtime_index=function() error("hot path replayed the journal") end
check(learning.confirm(large,{{time=clock,mode="test",code="ab1",text="甲丙",context=""}}), "large history incremental update")
clock=clock+61; learning.refresh_scores(large)
check(learning.score(large.index,"test","ab1","甲丙","")>=9, "incremental materialization keeps explicit correction")
learning.runtime_index=runtime_index
local capped=learning.open("runtime-cap-parity")
for i=1,45 do
    check(learning.confirm(capped,{{time=clock,mode="test",code="ab",text="甲乙",context="前"}}),"runtime cap confirmation")
    check(math.abs(learning.score(capped.index,"test","ab","甲乙","前")-
        learning.score(learning.build(capped.events,clock),"test","ab","甲乙","前"))<1e-9,"runtime cap parity")
end
local durable_update, accepted_count=capped.db.update,0
capped.db.update=function(self,k,v)
    accepted_count=accepted_count+1
    if accepted_count==2 then return false end
    return durable_update(self,k,v)
end
check(learning.confirm(capped,{{time=clock,mode="test",code="ab",text="甲丙",context="前"},
    {time=clock,mode="test",code="ab",text="乙",context="前"}}),"partially accepted batch published")
check(#capped.events==46,"rejected batch tail excluded")
compare_index(capped.index,capped.events,clock)
capped.db.update=durable_update
-- Hash acceleration must never strand an existing per-schema database.
local function legacy_hash(value)
    local a,b=2166136261,5381
    for i=1,#value do a=(a*65599+value:byte(i))%4294967296; b=(b*33+value:byte(i))%4294967296 end
    return string.format("%08x%08x",a,b)
end
local bytes=""
for i=0,255 do bytes=bytes..string.char(i); check(learning.hash(bytes)==legacy_hash(bytes),"hash byte parity") end
os.time=real_time
print(string.format('{"status":"passed","learning_checks":%d,"real_frontend":false}',checks))
