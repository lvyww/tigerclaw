-- Shared fragment-selection contract, independent of model/menu ordering.
local repo=arg[1] or "."
package.path=repo.."/rime/tiger_sentence/lua/?.lua;"..package.path
local learning=require("tiger_sentence_learning")
local journal=require("tiger_sentence_learning_text")
local checks=0
local function check(ok,name) checks=checks+1;assert(ok,name) end
local now=os.time()
local function event(code,text,ctx,mode,levels)
    return {time=now,code=code,text=text,context=ctx or "",mode=mode or "test",levels=levels or 1}
end
local function item(text,raw_ends,text_ends,score)
    local letters={}
    for c in text:gmatch("[%z\1-\127\194-\244][\128-\191]*") do letters[#letters+1]=c end
    local node
    for i,r in ipairs(raw_ends) do
        local t=0
        for n=1,(text_ends and text_ends[i] or i) do t=t+#letters[n] end
        node={raw_length=r,text_length=t,previous=node}
    end
    return {text=text,path=node,score=score or 0,learning_score=0}
end
local function select_events(raw,before,selected,index,floor,supplemental,mode)
    return learning.selection_events(raw,before,selected,floor or 0,mode or "test",index,supplemental)
end
local b=item("滤掉",{3,5})
local s=item("淦掉",{3,5})
local bare=select_events("kzjuy",b,s)
check(#bare==1 and bare[1].code=="kzjuy" and bare[1].text=="淦掉" and bare[1].context=="" and
    bare[1].raw_start==0 and bare[1].raw_end==5,"standalone two-character choice records whole phrase only")
local known=learning.build({event("kzjuy","淦掉")},now)
local char_known=learning.build({event("kzj","淦")},now)
check(select_events("kzjuy",b,s,char_known)[1].text=="淦掉","standalone phrase takes priority over known character")
for _,c in ipairs({{"gk","去"},{"je","他"},{"qo","都"},{"jx","你"},{"kr","没"}}) do
    local before=item(c[2].."滤掉",{2,5,7})
    local selected=item(c[2].."淦掉",{2,5,7})
    local events=select_events(c[1].."kzjuy",before,selected,known)
    check(#events==1 and events[1].code=="kzjuy" and events[1].text=="淦掉" and
        events[1].context==c[2] and events[1].raw_start==2 and events[1].raw_end==7,
        "known phrase crosses a smaller correction diff for "..c[2])
end
local coarse_before=item("侮金掉",{3,5,7})
local coarse_selected=item("他淦掉",{2,5,7})
local coarse=select_events("jekzjuy",coarse_before,coarse_selected,known)
check(#coarse==1 and coarse[1].code=="kzjuy" and coarse[1].text=="淦掉" and coarse[1].context=="他",
    "known phrase crosses shared diff and does not learn 他淦")
local no_known=select_events("jekzjuy",coarse_before,coarse_selected)
check(#no_known==1 and no_known[1].code=="jekzj" and no_known[1].text=="他淦",
    "without a confirmed fragment preserve the validated original diff")
local supplemental=select_events("jekzjuy",coarse_before,coarse_selected,nil,0,function(t)return t=="淦掉"end)
check(#supplemental==1 and supplemental[1].text=="淦掉","supplemental phrase is valid reusable evidence")
local long_before=item("甲乙丙丁戊",{2,4,6,8,10})
local long_selected=item("甲淦掉丁己",{2,4,6,8,10})
local nested=learning.build({event("bb","淦"),event("bbcc","淦掉")},now)
local selected=select_events("aabbccddee",long_before,long_selected,nested)
check(#selected==2 and selected[1].code=="bbcc" and selected[1].text=="淦掉" and selected[2].code=="ee" and
    selected[2].text=="己","unique containing phrase replaces overlap and preserves independent diff")
local ambiguous=learning.build({event("bb","淦"),event("cc","掉")},now)
selected=select_events("aabbccddee",long_before,long_selected,ambiguous)
check(#selected==3 and selected[1].text=="淦" and selected[2].text=="掉" and selected[3].text=="己",
    "independent known matches fall back to original diff")
selected=select_events("aabbccddee",long_before,long_selected,nested,4)
check(#selected==2 and selected[1].code=="cc" and selected[1].text=="掉" and selected[2].text=="己",
    "locked floor excludes a known phrase beginning before the lock")
check(#select_events("kzjuy",b,s,known,5)==0,"fully locked choice learns nothing")
check(#select_events("kzjuy",b,b,known)==0,"same selected text learns nothing")
check(#select_events("kzjuy",b,{text=s.text,path={raw_length=3,text_length=#s.text}},known)==0,
    "partial selected path cannot become a phrase")
check(#select_events("kzjuy",{text=b.text,path={raw_length=3,text_length=#b.text}},s,known)==0,
    "partial baseline cannot supply diff boundaries")
check(#select_events("kzjuy",b,{text=s.text,path={raw_length=5,text_length=5}},known)==0,
    "boundary within UTF-8 character is invalid")
check(#select_events("kzjuy",b,{text="\255"..s.text,path={raw_length=5,text_length=#s.text+1}},known)==0,
    "invalid complete UTF-8 path cannot learn")
check(#select_events("kzjuy",b,s,known,0,nil,"fusion-v1|test")==0 and
    #select_events("kzjuy",b,s,known,0,nil,"exact-correction-v1|test")==0,
    "retired mode cannot generate fragment events")
local exists_before=item("淦掉乙",{2,4,6})
local exists_selected=item("甲淦掉",{2,4,6})
selected=select_events("aabbcc",exists_before,exists_selected,nested)
check(#selected==3 and selected[1].text=="甲" and selected[2].text=="淦",
    "existing baseline occurrence does not confirm a shifted phrase")
selected=select_events("abcd",item("甲乙",{2,4}),item("😀乙",{2,4}))
check(#selected==1 and selected[1].text=="😀乙" and selected[1].text_end==7,
    "two Unicode scalars use actual UTF-8 byte boundaries")
local huge_raw=string.rep("a",129)
check(#select_events(huge_raw,item("甲乙",{129},{2}),item("丙丁",{129},{2}))==0,
    "long-code selection cannot write more than 128 keys")
local limit_raw=string.rep("a",128)
selected=select_events(limit_raw,item("甲乙",{128},{2}),item("丙丁",{128},{2}))
check(#selected==1 and #selected[1].code==128,"128-key fragment boundary remains supported")
-- Corrected rival scores are actual final scores: they never receive projected
-- ordinary rewards against the typed raw code during the level calculation.
b.score=10
local polluted=learning.build({event("kzj","滤","",nil,3),event("kzj","滤","",nil,3)},now)
local corrected=select_events("kzjuy",b,s)
learning.plan_levels(polluted,corrected,"kzjuy",b,s,true)
check(corrected[1].levels==2,"corrected planner must use actual final score without projected reward")
local normal=select_events("kzjuy",b,s)
learning.plan_levels(polluted,normal,"kzjuy",b,s,false)
check(normal[1].levels==3,"ordinary planner still projects competing fragment rewards")
local seeded=learning.build(corrected,now)
check(learning.score(seeded,"test","kzjuy","淦掉","")==11 and
    learning.confidence_score(seeded,"test","kzjuy","淦掉","")==9,
    "graded correction raises ranking levels while counting one manual confirmation")
-- Old hard-order rows stay valid journal syntax but are ignored before replay,
-- active-window truncation, and the event quota. Ordinary old rows still work.
local old_fusion=event("~fdeadbeef","D","","fusion-v1|test",3);old_fusion.id="old-fusion"
local old_correction=event("~cdeadbeef","E","","exact-correction-v1|test",3);old_correction.id="old-correction"
local old_ordinary=event("kzjuy","淦掉","","test");old_ordinary.id="ordinary"
for _,index in ipairs({learning.build({old_fusion,old_correction,old_ordinary},now),
    learning.runtime_index({old_fusion,old_correction,old_ordinary},now)}) do
    check(#index.codes==1 and index.codes[1]=="kzjuy" and learning.score(index,"test","kzjuy","淦掉","")==9,
        "legacy rows never enter runtime/replay index")
    check(learning.score(index,old_fusion.mode,old_fusion.code,"D","")==0 and
        learning.score(index,old_correction.mode,old_correction.code,"E","")==0,
        "legacy pair score is always zero")
end
local rows={journal.header,journal.encode(old_ordinary)}
for i=1,10002 do
    local ev=i%2==0 and old_fusion or old_correction
    ev.id=string.format("记录%06d",i)
    rows[#rows+1]=journal.encode(ev)
end
local data=table.concat(rows)
local disk=data
learning.storage_factory=function()
    return {read=function()return disk end,close=function()end,update=function(_,k,value)disk=disk..value;return true end}
end
local store=learning.open("legacy-quota-fragment-test")
check(not store.error and store.count==1 and #store.events==1 and store.events[1].id=="ordinary" and store.sequence>=10002,
    "legacy rows do not evict ordinary history from the 10000-event window")
check(not learning.confirm(store,{old_fusion,old_correction}) and disk==data and store.count==1,
    "legacy writes ignored without rewriting journal bytes")
local next_id=string.format("记录%06d",store.sequence+1)
check(learning.confirm(store,{event("ab","甲")}) and store.count==2 and
    store.events[2].id==next_id and disk:sub(1,#data)==data,
    "legacy rows do not consume confirmation quota or collide with historic ids")
local fresh=dofile(repo.."/rime/tiger_sentence/lua/tiger_sentence_learning.lua")
fresh.storage_factory=learning.storage_factory
local reopened=fresh.open("legacy-quota-fragment-test")
check(reopened.count==2 and fresh.score(reopened.index,"test","ab","甲","")==9,
    "ordinary append after retired history survives reopen")
print(string.format('{"status":"passed","fragment_selection_checks":%d,"old_pair_rows":10002}',checks))
