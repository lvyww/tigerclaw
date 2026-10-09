-- Run in the isolated public-pack layout. Production decoder and processor,
-- with a small host/readable-storage fake; never touches an installed input method.
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
check(direct[1].text=="交" and direct.learning_affected and
    (direct[1].source_mask==1 or direct[1].source_mask==3) and (direct[1].learning_score or 0)==0,
    "Direct-to-Direct history cannot change exact table order")
local learned_direct
for _, item in ipairs(direct) do if item.text=="疒" then learned_direct=item end end
check(learned_direct and learned_direct.learning_score==9 and
    learned_direct.early_commit_confidence_score==learned_direct.confidence_score,
    "Direct ranking consumes ordinary score without confidence reward")

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

local merge={{text="A",source_mask=2,score=10},{text="B",source_mask=1,direct_rank=1,score=1},
    {text="C",source_mask=1,direct_rank=2,score=20}}
sentence.apply_source_ordering_for_test("ii",merge)
check(merge[1].text=="A" and merge[2].text=="B" and merge[3].text=="C",
    "no ordinary score retains baseline source order")
merge={{text="A",source_mask=2,score=10},{text="B",source_mask=1,direct_rank=1,score=1},
    {text="C",source_mask=1,direct_rank=2,score=20,learning_score=9},
    {text="D",source_mask=1,direct_rank=3,score=0}}
sentence.apply_source_ordering_for_test("ii",merge)
check(merge[1].text=="B" and merge[2].text=="C" and merge[3].text=="A" and merge[4].text=="D",
    "Direct score promotes only its necessary rank prefix")
-- A learned low Direct rank must not evict its required rank prefix at
-- the beam boundary; unfinished phrase reservations remain separate.
local dense={}
for i=1,80 do dense[#dense+1]={text=string.format("D%03d",i),score=-i,source_mask=1,
    direct_rank=i,max_rank=i,edge_count=1} end
dense[80].score,dense[80].learning_score=100,9
dense[#dense+1]={text="composed",score=50,source_mask=2,max_rank=1,edge_count=2}
dense[#dense+1]={text="potential",score=-1000,learning_potential=2000,source_mask=2,max_rank=1,edge_count=2}
local protected=sentence.dedup_limit_for_test(dense,20)
check(protected[1].text=="D001" and protected[20].text=="D020" and #protected==21 and
    protected[21].text=="potential" and protected._truncated,
    "learned Direct rank prefix must survive beam truncation with potential reserve")
sentence.set_learning_for_test(nil,"")
check(sentence.decode("abcd")[1].text=="交否","disable restores base Composed ranking")

local function key(repr)
    return {keycode=({comma=44,period=46})[repr],repr=function()return repr end,release=function()return false end,
        ctrl=function()return false end,alt=function()return false end,
        super=function()return false end,shift=function()return false end}
end
local function host(name, early, correction_level, min_code_length)
    local properties, listeners, commits = {}, {}, {}
    local context = {input="",caret_pos=0}
    local segment = {selected_index=0}
    segment.menu = {prepare=function(_,n)return math.min(n,#sentence.decode(context.input)) end,
        candidate_count=function()return #sentence.decode(context.input)end}
    context.composition = {empty=function()return context.input=="" end,back=function()return segment end}
    function context:get_property(k)return properties[k] or ""end
    function context:set_property(k,v)properties[k]=v end
    function context:get_option(k)return k=="tiger_sentence_allow_duplicate_single" or (correction_level=="on" and k==sentence.correction.option) or (k=="tiger_sentence_early_commit" and early)end
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
        if name=="tiger_sentence/auto_select_min_code_length" then return min_code_length or 2 end
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
-- Exercise the production processor/notifier and persisted ordinary score.
-- Model-free ordering starts with Direct 捡; first select Composed 拾滑,
-- then recreate the reported 拾滑 -> 捡 correction without synthetic events.
local fusion_env,fusion_ctx,fusion_press,_,fusion_commits=host("fusion-schema",false)
local function type_fusion()
    for ch in ("ujkf"):gmatch(".") do fusion_press(ch) end
end
local fusion_writes=writes
type_fusion();fusion_press("Tab");fusion_press("Escape")
check(writes==fusion_writes,"cancelled cross-source selection must not write")
type_fusion()
local fusion_candidates=sentence.decode("ujkf")
check(fusion_candidates[1].text=="捡" and learning.candidate_is_direct(fusion_candidates[1]) and
    fusion_candidates[2].text=="拾滑" and learning.candidate_is_composed_only(fusion_candidates[2]),
    "ujkf must expose Direct 捡 and Composed 拾滑")
fusion_press("Tab");fusion_press("space")
check(writes==fusion_writes+1 and fusion_commits[#fusion_commits]=="拾滑",
    "one cross-source submission must write one ordinary fragment")
type_fusion()
check(sentence.decode("ujkf")[1].text=="拾滑","Composed choice did not pass notifier and persistence")
fusion_press("Tab");fusion_ctx.repeat_notification=true;fusion_press("space")
fusion_ctx.repeat_notification=false
check(writes==fusion_writes+2 and fusion_commits[#fusion_commits]=="捡",
    "reverse cross-source submission must write one ordinary fragment")
type_fusion()
check(sentence.decode("ujkf")[1].text=="捡","ujkf correction did not promote Direct 捡")
fusion_press("space")
check(writes==fusion_writes+2,"learned cross-source top1 must not reinforce")
sentence.processor_component.fini(fusion_env)
local fusion_reopened=dofile(repo.."/rime/tiger_sentence/lua/tiger_sentence_learning.lua")
fusion_reopened.storage_factory=fake_storage
local fusion_saved=fusion_reopened.open("自学习-fusion-schema")
check(fusion_saved.count==2 and #fusion_saved.events==2,"cross-source records did not survive store restart")
local base_mode=fusion_saved.events[1].mode
check(not fusion_reopened.is_legacy_mode(base_mode) and
    fusion_reopened.score(fusion_saved.index,base_mode,"ujkf","捡","")>0,
    "reloaded ordinary learning lost Direct score")
sentence.set_learning_for_test(fusion_saved.index,base_mode)
check(sentence.decode("ujkf")[1].text=="捡","reloaded preference did not invalidate candidate cache")
sentence.set_learning_for_test(nil,"")

-- One selected Composed path may overtake more than one Direct candidate.
local multi_env,multi_ctx,multi_press,_,multi_commits=host("fusion-multi-schema",false)
for ch in ("cdnu"):gmatch(".") do multi_press(ch) end
local multi_candidates=sentence.decode("cdnu")
check(multi_candidates[1].text=="避" and multi_candidates[3].text=="擘" and
    multi_candidates[4].text=="否戊","multiple-pair regression menu changed")
local multi_writes=writes
multi_press("Tab");multi_press("Tab");multi_press("Tab");multi_press("space")
check(writes==multi_writes+1 and multi_commits[#multi_commits]=="否戊",
    "one explicit two-character selection learns one ordinary whole phrase")
local multi_events=multi_env._tiger_learning.store.events
check(#multi_events==1 and multi_events[1].code=="cdnu" and
    multi_events[1].text=="否戊" and not learning.is_legacy_mode(multi_events[1].mode),
    "candidate count must not multiply ordinary learning events")
sentence.processor_component.fini(multi_env)

-- A Direct head must not hide a crossed Composed rival when the chosen
-- candidate is a later Direct. The Direct chain itself remains rank ordered.
local middle_env,middle_ctx,middle_press=host("intermediate-direct-schema",false)
for ch in ("cdnu"):gmatch(".") do middle_press(ch) end
local middle_menu=sentence.decode("cdnu")
check(middle_menu[1].text=="避" and middle_menu[3].text=="擘" and
    learning.candidate_is_composed_only(middle_menu[2]),"intermediate Composed fixture changed")
local middle_writes=writes
middle_ctx:highlight(2);middle_ctx:confirm_current_selection()
local middle_events=middle_env._tiger_learning.store.events
check(writes==middle_writes+1 and #middle_events==1 and middle_events[1].code=="cdnu" and middle_events[1].text=="擘",
    "non-first Direct must learn against the strongest crossed cross-source rival")
for ch in ("cdnu"):gmatch(".") do middle_press(ch) end
middle_menu=sentence.decode("cdnu")
check(middle_menu[1].text=="避" and middle_menu[2].text=="擘",
    "cross-source score preserves the earlier Direct rank")
middle_press("Escape");sentence.processor_component.fini(middle_env)

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
-- Optional production Q8 matrix uses the real processor/notifier and journal.
-- The score hook preserves a lower exact candidate for the historical ujkf
-- regression; the reusable phrase matrix below uses production scores.
if arg[2] then
    sentence.set_decoder_parameters_for_test({canonical_code_reward=2,whole_input_single_character_reward=0})
    sentence.set_model_enabled(true)
    local function locate(menu,text)
        for i,item in ipairs(menu) do if item.text==text then return i,item end end
        error("missing real-model candidate: "..text)
    end
    for _, level in ipairs({"off","on"}) do
        for _, action in ipairs({"tap","tab"}) do
            local name="numeric-correction-"..level.."-"..action
            local e,c,p,_,submitted,cfg=host(name,false,level,3)
            local function input(raw)
                raw=raw or "ujkf"
                for ch in raw:gmatch(".") do p(ch) end
                check(sentence.model_status().loaded,"numeric learning requires the production model")
                return sentence.decode(raw)
            end
            local function choose(menu,text,cancel)
                local i=locate(menu,text)
                if action=="tab" or cancel then
                    for _=2,i do p("Tab") end
                    p(cancel and "Escape" or "space")
                else c:highlight(i-1);c:confirm_current_selection() end
            end
            local menu=input()
            local di,d=locate(menu,"捡")
            local _,co=locate(menu,"拾滑")
            check(di>1 and not sentence.correction.affected(d) and not sentence.correction.affected(co),
                "fixture keeps a non-first exact candidate")
            local initial_confidence, initial_score=d.confidence_score,d.score
            local correction_scores={}
            for _,item in ipairs(menu) do if sentence.correction.affected(item) then correction_scores[item.text]=item.score end end
            local start=writes
            choose(menu,"捡",true)
            check(writes==start,"cancelled exact choice under correction must not learn")
            if level~="off" then
                choose(input(),"轮滑")
                check(writes==start,"selected corrected candidate must not learn")
            end
            c.transform=true;choose(input(),"捡");c.transform=false
            check(writes==start,"transformed exact commit must not learn")
            for _,path_history in ipairs({false,true}) do
                menu=input()
                local _,candidate=locate(menu,"捡")
                local holder=path_history and candidate.path or candidate
                holder.correction_history=true
                choose(menu,"捡");holder.correction_history=nil
                check(writes==start,"selected correction history rejects the whole submission")
            end
            local count=0
            local function promote(text)
                local current=input()
                while current[1].text~=text and count<20 do
                    local previous=writes
                    c.repeat_notification=true;choose(current,text);c.repeat_notification=false
                    check(writes==previous+1,"manual exact selection writes one bounded ordinary event")
                    count=count+1
                    local ev=e._tiger_learning.store.events[#e._tiger_learning.store.events]
                    check(ev.code=="ujkf" and ev.text==text and ev.mode==e._tiger_learning.mode and
                        ev.levels>=1 and ev.levels<=3,"selection stores typed code and exact text only")
                    current=input()
                end
                check(current[1].text==text,"bounded numeric confirmations must reach first place")
                return current
            end
            menu=promote("捡")
            d=menu[1]
            check(d.learning_score>0 and d.score>initial_score and
                d.confidence_score==initial_confidence and d.early_commit_confidence_score==d.confidence_score,
                "Direct gains numeric ranking with unchanged auto-commit evidence")
            for _,item in ipairs(menu) do
                if correction_scores[item.text] then check(item.score==correction_scores[item.text],
                    "normal fragment score must not enter corrected decode") end
            end
            local store=e._tiger_learning.store
            choose(menu,"捡")
            check(writes==start+count,"learned first choice does not self-reinforce")
            cfg.enabled=false;menu=input()
            check(menu[1].text~="捡" and (select(2,locate(menu,"捡")).learning_score or 0)==0,
                "disable invalidates numeric learning immediately")
            choose(menu,"捡");check(writes==start+count,"disabled learning must not write")
            cfg.enabled=true;menu=input();check(menu[1].text=="捡","reenable restores numeric score")
            p("Escape")
            menu=promote("拾滑");choose(menu,"拾滑")
            menu=promote("捡");choose(menu,"捡")
            sentence.processor_component.fini(e)
            sentence.set_model_enabled(false)
            package.loaded["tiger_sentence_learning"]=nil
            sentence=dofile(override or repo.."/rime/tiger_sentence/lua/tiger_sentence.lua");learning=sentence.learning
            learning.storage_factory=fake_storage
            sentence.set_decoder_parameters_for_test({canonical_code_reward=2,whole_input_single_character_reward=0})
            sentence.set_model_enabled(true);sentence.ensure_lexicon(nil)
            e,c,p,_,submitted,cfg=host(name,false,level,3)
            menu=input()
            check(e._tiger_learning.store~=store and #e._tiger_learning.store.events==count and menu[1].text=="捡",
                "fresh host replays ordinary numeric learning")
            p("Escape");sentence.processor_component.fini(e)
            print(string.format('{"numeric_correction_learning":"passed","level":"%s","selection":"%s","events":%d}',level,action,count))
        end
    end
    sentence.set_decoder_parameters_for_test({canonical_code_reward=0,whole_input_single_character_reward=5})
    for _,action in ipairs({"tap","tab"}) do
        local e,c,p=host("reusable-phrase-"..action,false,"on",3)
        local function input(raw)
            for ch in raw:gmatch(".") do p(ch) end
            return sentence.decode(raw)
        end
        local function confirm(raw,text)
            local menu=input(raw)
            local n,chosen=locate(menu,text)
            check(n>1,"phrase confirmation needs an explicit non-first candidate")
            local old=#e._tiger_learning.store.events
            if action=="tab" then for _=2,n do p("Tab") end;p("space")
            else c:highlight(n-1);c:confirm_current_selection() end
            local records=e._tiger_learning.store.events
            check(#records==old+1,"one phrase choice must write one fragment")
            local ev=records[#records]
            check(ev.code=="kzjuy" and ev.text=="淦掉" and ev.mode==e._tiger_learning.mode,
                "phrase learning must reuse 淦掉 without storing an unrelated prefix")
            return ev
        end
        local before=input("krkzjuy")
        local _,unseen=locate(before,"没淦掉")
        local initial=unseen.score
        p("Escape")
        local bare=confirm("kzjuy","淦掉")
        check(bare.context=="","standalone two-character choice learns whole phrase")
        -- The numerical gap can need more than one confirmation in a new context.
        -- Use different prefixes only while their exact candidate is non-first.
        for _,case in ipairs({{"gkkzjuy","去淦掉"},{"jekzjuy","他淦掉"},{"qokzjuy","都淦掉"},{"jxkzjuy","你淦掉"}}) do
            local menu=input(case[1]);local n=locate(menu,case[2]);p("Escape")
            if n>1 then confirm(case[1],case[2]) end
        end
        local after=input("krkzjuy")
        local n,improved=locate(after,"没淦掉")
        check(improved.score>initial and improved.learning_score>0,
            "unseen prefix must consume reusable phrase score in actual Q8 decoding")
        check(n==1,"confirmed phrase across prefixes should pass the correction for unseen 没")
        for _,ev in ipairs(e._tiger_learning.store.events) do check(ev.text=="淦掉" and ev.code=="kzjuy",
            "phrase transfer must not fall back to memorizing complete prefixed sentences") end
        local event_count=#e._tiger_learning.store.events
        p("Escape");sentence.processor_component.fini(e)
        print(string.format('{"reusable_phrase_learning":"passed","selection":"%s","unseen_first":true,"events":%d,"bare_levels":%d,"unseen_before":%.12f,"unseen_after":%.12f,"unseen_learning":%.12f}',action,event_count,bare.levels,initial,improved.score,improved.learning_score))
    end
end
print(string.format('{"status":"passed","learning_checks":%d,"real_frontend":false}',checks))
