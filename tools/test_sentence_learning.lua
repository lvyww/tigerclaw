-- Run in the isolated public-pack layout. Production decoder and processor,
-- with a small host/LevelDb fake; never touches an installed input method.
local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
rime_api = {get_user_data_dir=function() return arg[2] or repo end}
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
local override = os.getenv("TIGER_SENTENCE_MODULE")
local sentence = override and dofile(override) or require("tiger_sentence")
local learning = sentence.learning
local function fake_storage(name)
    local data=databases[name] or {};databases[name]=data
    return {read=function()return data.text or "" end,close=function()end,
        update=function(_,k,v)if fail_write then return false end;writes=writes+1;data.text=(data.text or "")..v;return true end}
end
learning.storage_factory=fake_storage

sentence.set_model_enabled(false)
sentence.ensure_lexicon(nil)
-- Historical learning ambiguity uses legal two-key duplicate segments.
-- The production default 3 and disabled 0 are covered by the numeric-setting suite.
sentence.apply_auto_select_min_code_length(2)
local checks = 0
local function check(ok, name) checks=checks+1; assert(ok, name) end
local now = os.time()
local function event(code, text, ctx, mode)
    return {time=now, mode=mode or "test", code=code, text=text, context=ctx or ""}
end
local index = learning.build({event("ab", "疒")}, now)
check(learning.score(index,"test","ab","疒","")==9, "first correction equals supplement weight 1000")
check(learning.score(index,"test","ab","疒","甲")==6, "first correction creates cross-context preference")
local function plan(index, events, gap)
    local e=events[1];local prefix=e.context;local offset=prefix=="" and 0 or 2
    local raw=(offset==0 and "" or "zz")..e.code
    local head=offset==0 and nil or {raw_length=offset,text_length=#prefix}
    local function item(text,score,learned)
        return {text=prefix..text,score=score,learning_score=learned,
            path={previous=head,raw_length=#raw,text_length=#prefix+#text}}
    end
    learning.plan_levels(index,events,raw,item("龙族",gap,0),item(e.text,0,learning.score(index,e.mode,e.code,e.text,e.context)))
end
local seeded_two={event("gap","陲机","测试")}
plan(learning.build({},now),seeded_two,10)
check(#seeded_two==1 and seeded_two[1].levels==2 and learning.score(learning.build(seeded_two,now),"test","gap","陲机","测试")==11,"two levels remain one correction")
local seeded_three={event("gap3","陲机","测试")}
plan(learning.build({},now),seeded_three,100)
local third=learning.build(seeded_three,now)
check(#seeded_three==1 and seeded_three[1].levels==3 and learning.score(third,"test","gap3","陲机","测试")==13 and learning.confidence_score(third,"test","gap3","陲机","测试")==9,"three levels retain one actual confirmation")
local subsequent={event("gap3","陲机","测试")}
plan(third,subsequent,4.5)
check(#subsequent==1 and subsequent[1].levels==3,"subsequent correction can also jump three levels")
local twice=learning.build({seeded_three[1],subsequent[1]},now)
check(learning.score(twice,"test","gap3","陲机","测试")==19 and learning.confidence_score(twice,"test","gap3","陲机","测试")==11,"six ranking levels retain two real confirmations")
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
local inner_before={text="设置父女窗口",path={raw_length=6,text_length=18,previous={raw_length=2,text_length=6}}}
local inner_selected={text="设置八妾关系",path={raw_length=6,text_length=18,previous={raw_length=4,text_length=12,
    previous={raw_length=2,text_length=6}}}}
local known_inner=learning.build({event("bb","八妾","旧文")},now)
local reinforced=learning.reinforce_existing(known_inner,"aabbcc",inner_before,inner_selected,0,"test")
check(#reinforced==1 and reinforced[1].code=="bb" and reinforced[1].text=="八妾" and reinforced[1].context=="设置",
    "whole-candidate diff reinforces existing aligned inner fragment")
check(learning.score(learning.build({event("bb","八妾","旧文"),reinforced[1]},now),
    "test","bb","八妾","其他")==8,"implicit confirmation advances cross-context learning exactly one level")
local ambiguous_inner=learning.build({event("bb","八妾","旧文"),event("cc","关系","旧文")},now)
check(#learning.reinforce_existing(ambiguous_inner,"aabbcc",inner_before,inner_selected,0,"test")==0,
    "independent learned fragments are not guessed")
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
local function host(name, early, correction_level)
    local properties, listeners, commits = {}, {}, {}
    local context = {input="",caret_pos=0}
    local segment = {selected_index=0}
    segment.menu = {prepare=function(_,n)return math.min(n,#sentence.decode(context.input)) end,
        candidate_count=function()return #sentence.decode(context.input)end}
    context.composition = {empty=function()return context.input=="" end,back=function()return segment end}
    function context:get_property(k)return properties[k] or ""end
    function context:set_property(k,v)properties[k]=v end
    function context:get_option(k)return k=="tiger_sentence_allow_duplicate_single" or
        (correction_level=="on" and k==sentence.correction.option) or (k=="tiger_sentence_early_commit" and early)end
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
    local config={enabled=true,get_int=function(_,name)
        if name=="tiger_sentence/auto_select_min_code_length" then return 2 end
    end}
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
local first_learning_writes=writes
type_ot();press("Tab");press("space")
local first_learning_records=writes-first_learning_writes
check(first_learning_records==1 and commits[#commits]=="疒否",
    "host Composed submission seeds one to three initial levels")
type_ot();check(sentence.decode("abcd")[1].text=="疒否","next composition uses Composed learning")
press("space");check(writes==first_learning_records,"ordinary learned first choice never reinforces")
type_ot();press("space");check(writes==first_learning_records,"repeated learned top1 use keeps the same level")
type_ot();press("space");check(writes==first_learning_records,"normal top1 use remains write-free")
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
check(writes==previous+1,"Tab next letter submission seeds bounded initial levels")
check(lock_ctx.input=="a","Tab learning keeps live raw suffix")
sentence.processor_component.fini(lock_env)
local reopened = dofile(repo.."/rime/tiger_sentence/lua/tiger_sentence_learning.lua")
reopened.storage_factory=fake_storage
local persisted = reopened.open("自学习-learning-test")
check(persisted.count==first_learning_records and #persisted.events==first_learning_records,
    "database restart loads exactly the seeded explicit correction records")
local saved=persisted.events[1]
check(reopened.score(persisted.index,saved.mode,saved.code,saved.text,saved.context)==7+2*saved.levels,
    "length-framed persistence preserves the seeded initial level")
check(not learning.confirm({db={update=function()error("must not write")end},count=10000}, {event("ab","乙")}),"bounded event history")
local tap_env,tap_ctx,tap_press,tap_type,_,tap_config=host("tap-schema",false)
local before_taps=writes
tap_type();tap_ctx:highlight(0);tap_ctx:confirm_current_selection()
check(writes==before_taps,"tapping first candidate does not reinforce")
tap_type();tap_ctx:highlight(1);tap_ctx.transform=true;tap_ctx:confirm_current_selection();tap_ctx.transform=false
check(writes==before_taps,"transformed tap is not learned")
tap_type();tap_ctx:highlight(1);tap_ctx.repeat_notification=true;tap_ctx:confirm_current_selection()
local tap_seeded=writes-before_taps
check(tap_seeded==1,"non-first tap seeds bounded initial levels without duplicate notification")
tap_type();check(sentence.decode("abcd")[1].text=="疒否","tap changes next Composed ranking")
tap_ctx:highlight(0);tap_ctx:confirm_current_selection()
check(writes==before_taps+tap_seeded,"tapping learned first candidate does not reinforce")
tap_config.enabled=false;tap_type();tap_ctx:highlight(1);tap_ctx:confirm_current_selection()
check(writes==before_taps+tap_seeded,"disabled learning ignores taps")
tap_config.enabled=true;tap_type();tap_press("Tab");local before_tab_tap=writes;tap_ctx:confirm_current_selection()
check(writes==before_tab_tap+1,"Tab followed by tap records bounded manual correction levels")
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
    check(writes==start_writes+1,"punctuation confirmation seeds bounded initial levels")
    check(p_ctx.input=="","sentence submitted before punctuation input")
    local punctuation_writes=writes
    p_press(punctuation)
    check(writes==punctuation_writes,"idle punctuation cannot replay correction")
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
-- The final menu merge preserves both chains and all unrelated metadata.
do
    local mode,raw="exact-correction-test","abcd"
    local e1,e2={text="甲",score=4},{text="乙",score=3}
    local c1,c2={text="丙",score=20,correction_count=1},{text="丁",score=19,correction_count=1}
    local marker={}
    local function menu()return {c1,e1,c2,e2,_confidence_candidates=marker,early_commit_evidence=marker}end
    local function order(items)local t={};for _,v in ipairs(items)do t[#t+1]=v.text end;return table.concat(t)end
    local function apply(events,scope,code,items)
        return learning.apply_exact_correction_ordering(learning.build(events,now),scope or mode,
            code or raw,items or menu(),sentence.correction.affected)
    end
    local event=learning.exact_correction_event(mode,raw,"乙","丙",4)
    check(event.mode=="exact-correction-v1|"..mode and event.code==learning.exact_correction_pair_code(raw,"乙","丙")
        and event.text=="E" and event.context=="" and event.raw_end==4,"independent exact-correction event protocol")
    local unchanged,changed=apply({})
    check(not changed and order(unchanged)=="丙甲丁乙" and unchanged._confidence_candidates==marker
        and unchanged.early_commit_evidence==marker,"zero preferences preserve complete menu metadata")
    check(learning.exact_correction_pair_code("ujkf","捡","轮滑")=="~c73de49eccc9cc576",
        "Lua exact-correction pair hash remains stable")
    local limited=sentence.correction.merge({e1,e2},{c1,c2},1,function(items)
        return learning.apply_exact_correction_ordering(learning.build({event},now),mode,raw,items,sentence.correction.affected)
    end)
    check(#limited==1 and limited[1]==e1,"preference ordering runs before display truncation")
    local result,changed=apply({event})
    check(changed and order(result)=="甲乙丙丁","later exact preference carries only the required exact prefix")
    check(result._confidence_candidates==marker and result.early_commit_evidence==marker
        and c1.score==20 and e2.score==3,"ordering helper preserves evidence references and scores")
    check(order(apply({learning.exact_correction_event(mode,raw,"乙","丁",4)}))=="丙甲乙丁",
        "unrelated earlier correction keeps its position")
    check(order(apply({event},"other"))=="丙甲丁乙" and order(apply({event},mode,"abce"))=="丙甲丁乙",
        "raw code and mode isolate preferences")
    check(order(apply({event},""))=="丙甲丁乙","disabled mode cannot apply a preference")
    local old=learning.fusion_event(mode,raw,"乙","甲",true,4)
    check(order(apply({old}))=="丙甲丁乙","old fusion records do not become correction preferences")
    local history={text="乙",score=3,path={correction_history=true}}
    check(order(apply({event},mode,raw,{c1,e1,c2,history}))=="丙甲丁乙",
        "history-affected candidates cannot act as exact preference endpoints")
    check(order(apply({event},mode,raw,{e1,e2,c1,c2}))=="甲乙丙丁",
        "satisfied preferences never push unrelated candidates further down")
end
-- Optional production-model matrix, sharing the processor/notifier adapter above.
-- Usage: lua test_sentence_learning.lua <source root> <data root with models/>.
if arg[2] then
    sentence.set_model_enabled(true)
    for _, level in ipairs({"off", "on"}) do
        for _, action in ipairs({"tap", "tab"}) do
            local name="fusion-correction-"..level.."-"..action
            local e,c,p,_,submitted,cfg=host(name,false,level)
            local initial_writes=writes
            local function input()
                for ch in ("ujkf"):gmatch(".") do p(ch) end
                check(sentence.model_status().loaded,"correction learning requires the real model")
                return sentence.decode("ujkf")
            end
            local function find(menu,text)
                for i,item in ipairs(menu) do if item.text==text then return i,item end end
                error("missing real-model candidate: "..text)
            end
            local function choose(menu,text,cancel)
                local i=find(menu,text)
                if action=="tab" or cancel then
                    for _=2,i do p("Tab") end
                    p(cancel and "Escape" or "space")
                else c:highlight(i-1);c:confirm_current_selection() end
            end
            local menu=input()
            local direct,d=find(menu,"捡");local composed,co=find(menu,"拾滑")
            check(composed<direct and not sentence.correction.affected(d) and
                not sentence.correction.affected(co),"fixture must compare two exact candidates")
            if level~="off" then
                local corrected,word=find(menu,"轮滑")
                check(corrected<direct and sentence.correction.affected(word),
                    "enabled fixture must include an earlier corrected candidate")
            end
            choose(menu,"捡",true)
            check(writes==initial_writes,"cancelled exact choice under correction must not learn")
            if level~="off" then
                choose(input(),"轮滑")
                check(writes==initial_writes,"selecting a corrected candidate must not learn")
            end
            c.transform=true;choose(input(),"捡");c.transform=false
            check(writes==initial_writes,"mismatched actual commit must discard all new pair events")
            -- Compare every submitted receipt with the actual displayed prefix.
            -- Different correction strengths expose different corrected items.
            local function expected_pairs(menu,text)
                local expected={}
                local n,chosen=find(menu,text)
                for i=1,n-1 do
                    local ahead=menu[i]
                    local ev
                    if sentence.correction.affected(ahead) then
                        ev=learning.exact_correction_event(e._tiger_learning.mode,"ujkf",text,ahead.text,4)
                    elseif learning.candidate_is_direct(chosen) and learning.candidate_is_composed_only(ahead) then
                        ev=learning.fusion_event(e._tiger_learning.mode,"ujkf",text,ahead.text,true,4)
                    elseif learning.candidate_is_composed_only(chosen) and learning.candidate_is_direct(ahead) then
                        ev=learning.fusion_event(e._tiger_learning.mode,"ujkf",ahead.text,text,false,4)
                    end
                    if ev then expected[#expected+1]=ev end
                end
                return expected
            end
            local expected_total=0
            local function confirm_pairs(menu,text)
                local expected=expected_pairs(menu,text)
                local before=expected_total
                choose(menu,text)
                expected_total=expected_total+#expected
                local events=e._tiger_learning.store.events
                check(writes==initial_writes+expected_total and #events==expected_total,
                    "exact/corrected receipts persist once: "..level.."/"..action)
                for i,ev in ipairs(expected) do
                    local saved=events[before+i]
                    check(saved.mode==ev.mode and saved.code==ev.code and saved.text==ev.text and saved.context=="",
                        "only the displayed exact/corrected or exact fusion pair may persist")
                end
            end
            c.repeat_notification=true
            confirm_pairs(input(),"捡")
            c.repeat_notification=false
            local store=e._tiger_learning.store
            menu=input()
            check(menu[1].text=="捡" and find(menu,"捡")<find(menu,"拾滑"),
                "exact choice must become first above correction and retain r2 fusion")
            if level~="off" then
                check(menu.learning_affected and menu.exact_correction_affected and
                    next(menu.early_commit_evidence)==nil,"final reordering clears automatic-commit evidence")
                local cached=sentence.decode("ujkf",true)
                check(cached[1].text=="捡" and cached.exact_correction_affected and
                    next(cached.early_commit_evidence)==nil,"cache hit and evidence upgrade retain learned final order")
            end
            choose(menu,"捡")
            check(writes==initial_writes+expected_total,"learned first choice must not self-reinforce")
            cfg.enabled=false
            menu=input()
            check(menu[1].text~="捡" and find(menu,"拾滑")<find(menu,"捡"),
                "disabled learning removes both final and r2 fusion preferences")
            choose(menu,"捡")
            check(writes==initial_writes+expected_total,"disabled learning also rejects new records")
            cfg.enabled=true
            menu=input();check(menu[1].text=="捡","reenabling learning restores stored final preference")
            confirm_pairs(menu,"拾滑")
            menu=input()
            check(menu[1].text=="拾滑" and find(menu,"拾滑")<find(menu,"捡"),
                "Composed exact selection can also pass correction without breaking the exact chain")
            confirm_pairs(menu,"捡")
            sentence.processor_component.fini(e)
            sentence.set_model_enabled(false)
            package.loaded["tiger_sentence_learning"]=nil
            sentence=dofile(override or repo.."/rime/tiger_sentence/lua/tiger_sentence.lua")
            learning=sentence.learning
            learning.storage_factory=fake_storage
            sentence.set_model_enabled(true);sentence.ensure_lexicon(nil)
            e,c,p,_,submitted,cfg=host(name,false,level)
            menu=input()
            check(e._tiger_learning.store~=store and #e._tiger_learning.store.events==expected_total,
                "fresh host must reopen the persisted records")
            check(menu[1].text=="捡" and find(menu,"捡")<find(menu,"拾滑"),
                "reopened store preserves first place over corrected and exact candidates")
            p("Escape");sentence.processor_component.fini(e)
            print(string.format('{"exact_correction_learning":"passed","level":"%s","selection":"%s","events":%d}',level,action,expected_total))
        end
    end
    -- Upgrade a real r2 journal: no E preference exists initially. New learning
    -- adds the displayed corrected comparisons, preserving the old D record.
    for _,action in ipairs({"tap","tab"}) do
        local name="r2-upgrade-"..action
        local e,c,p,_,submitted,cfg=host(name,false,"on")
        local function input(raw)
            raw=raw or "ujkf"
            for ch in raw:gmatch(".") do p(ch) end
            return sentence.decode(raw)
        end
        input();p("Escape")
        local store=e._tiger_learning.store
        local mode=e._tiger_learning.mode
        local old=learning.fusion_event(mode,"ujkf","捡","拾滑",true,4)
        check(learning.confirm(store,{old}),"r2 upgrade fixture persists an actual old-format fusion record")
        local menu=input()
        local corrected_targets,direct_position,composed_position={},nil,nil
        for i,item in ipairs(menu) do
            if item.text=="捡" then direct_position=i end
            if item.text=="拾滑" then composed_position=i end
        end
        for i=1,direct_position-1 do
            if sentence.correction.affected(menu[i]) then corrected_targets[#corrected_targets+1]=menu[i].text end
        end
        check(menu[1].text=="轮滑" and #corrected_targets>0 and direct_position<composed_position,
            "r2 upgrade fixture keeps enabled correction above the learned exact first")
        p("Escape")
        local other_before=input("yjkf")
        local raw_texts={};for i,item in ipairs(other_before) do raw_texts[i]=item.text end
        p("Escape");menu=input()
        local start=writes
        c.repeat_notification=true
        if action=="tap" then c:highlight(direct_position-1);c:confirm_current_selection()
        else for _=2,direct_position do p("Tab") end;p("space") end
        c.repeat_notification=false
        check(writes==start+#corrected_targets and #store.events==1+#corrected_targets and store.events[1].mode==old.mode and store.events[1].code==old.code and store.events[1].text==old.text,
            "r2 upgrade appends only the observed exact/corrected records")
        for i,text in ipairs(corrected_targets) do
            check(store.events[i+1].mode==learning.exact_correction_mode(mode) and
                store.events[i+1].code==learning.exact_correction_pair_code("ujkf","捡",text) and
                store.events[i+1].text=="E","upgrade must preserve original raw and output pair identity")
        end
        menu=input();check(menu[1].text=="捡","upgraded r2 preference reaches first place")
        c:highlight(0);c:confirm_current_selection()
        check(writes==start+#corrected_targets,"upgraded first candidate does not reinforce")
        local other_after=input("yjkf")
        check(#other_after==#raw_texts,"other raw candidate count remains unchanged")
        for i,text in ipairs(raw_texts) do check(other_after[i].text==text,"real different raw order is isolated") end
        p("Escape")
        sentence.processor_component.fini(e);sentence.set_model_enabled(false)
        package.loaded["tiger_sentence_learning"]=nil
        sentence=dofile(override or repo.."/rime/tiger_sentence/lua/tiger_sentence.lua");learning=sentence.learning
            learning.storage_factory=fake_storage
        sentence.set_model_enabled(true);sentence.ensure_lexicon(nil)
        e,c,p=host(name,false,"on")
        menu=input()
        check(menu[1].text=="捡" and e._tiger_learning.store~=store and #e._tiger_learning.store.events==1+#corrected_targets,
            "r2 upgrade survives reopening the persisted journal")
        p("Escape");sentence.processor_component.fini(e)
        print(string.format('{"r2_upgrade":"passed","selection":"%s","old_records":1,"new_records":%d}',action,#corrected_targets))
    end
end
print(string.format('{"status":"passed","learning_checks":%d,"real_frontend":false}',checks))
