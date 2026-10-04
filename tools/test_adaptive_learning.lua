-- New policy, independent replay oracle and real UTF-8 file I/O in an owned
-- test directory. Only the cross-process lock API is mocked; no live Rime data.
local root=assert(arg[1], 'isolated directory required')
package.path=root..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return root end}
local held={}
LevelDb=function(name)
    local opened=false
    return {open=function()if held[name] then return false end;held[name]=true;opened=true;return true end,
        close=function()if opened then held[name]=nil;opened=false end end}
end
local learning=require('tiger_sentence_learning')
local text=require('tiger_sentence_learning_text')
local checks=0
local function check(ok,message)checks=checks+1;assert(ok,message)end
local function event(i,word,ctx,levels)
    return {id='event-'..i,time=1791061200,mode='整句测试',code='aabb',text=word or '陲机',context=ctx or '',levels=levels or 1}
end
local function copy(t)local c={};for k,v in pairs(t)do c[k]=v end;return c end
local words={'陲机','龙族','𠀀机'}
local contexts={'','设置','后文'}
local events={}
for i=1,120 do
    events[#events+1]=event(i,words[(i*7)%3+1],contexts[math.floor(i/3)%3+1],i%3+1)
    if i%7==0 then
        local oracle=learning.build(events)
        local runtime=learning.runtime_index(events)
        for _,word in ipairs(words)do for _,ctx in ipairs(contexts)do
            check(learning.score(runtime,'整句测试','aabb',word,ctx)==learning.score(oracle,'整句测试','aabb',word,ctx),'weighted runtime/replay mismatch')
            check(learning.confidence_score(runtime,'整句测试','aabb',word,ctx)==learning.confidence_score(oracle,'整句测试','aabb',word,ctx),'confirmation-count runtime/replay mismatch')
        end end
        for delta=1,3 do
            local extra=event(1000+i,words[i%3+1],contexts[math.floor(i/2)%3+1],delta)
            local combined={};for j,e in ipairs(events)do combined[j]=e end;combined[#combined+1]=extra
            local after=learning.build(combined)
            for _,word in ipairs(words)do for _,ctx in ipairs(contexts)do
                check(math.abs(learning.projected_score(runtime,'整句测试','aabb',word,ctx,{extra},delta)-learning.score(after,'整句测试','aabb',word,ctx))<1e-12,'projection differs from confirmed replay')
            end end
        end
    end
end
-- Inner known fragment discovery keeps real candidate/raw boundaries.
local function candidate(value,points)
    local path
    for _,point in ipairs(points)do path={previous=path,raw_length=point[1],text_length=point[2]} end
    return {text=value,path=path,score=0}
end
local before=candidate('设置父女窗口',{{2,6},{6,18}})
local selected=candidate('设置陲机关系',{{2,6},{4,12},{6,18}})
local known=function(s)return s=='陲机' end
local inner=learning.reinforce_existing(nil,'aabbcc',before,selected,0,'整句测试',known)
check(#inner==1 and inner[1].text=='陲机' and inner[1].code=='bb' and inner[1].context=='设置','supplement-only inner fragment missing')
check(#learning.reinforce_existing(nil,'aabbcc',selected,selected,0,'整句测试',known)==0,'ordinary selection reinforced supplement')
check(#learning.reinforce_existing(nil,'aabbcc',before,selected,5,'整句测试',known)==0,'locked boundary was crossed')
local no_inner_boundaries=candidate('设置陲机关系',{{2,6},{6,18}})
check(#learning.reinforce_existing(nil,'aabbcc',before,no_inner_boundaries,0,'整句测试',known)==0,'substring guessed an illegal raw boundary')
-- Overlapping full-span and inner rewards cannot be added twice.
local overlap={event('outer','陲机','',1),{time=1791061200,mode='整句测试',code='bb',text='机',context='陲',levels=1}}
local a=candidate('龙族',{{2,3},{4,6}});a.score=15
local b=candidate('陲机',{{2,3},{4,6}});b.score=0
learning.plan_levels(learning.build({}),overlap,'aabb',a,b)
check(overlap[1].levels==3 and overlap[2].levels==3,'overlapping rewards were incorrectly summed')
-- Actual file backend, not the in-memory storage adapter used by older tests.
local name='自学习-文件验收'
local path=root..'/'..name..'.txt'
check(not io.open(path,'rb'),'test file already exists')
local store=learning.open(name)
check(store.db and store.count==0,'empty readable store could not open')
check(not io.open(path,'rb'),'opening empty store created a learning file')
local first=event('file','陲机','设置',3)
check(learning.confirm(store,{first}),'file confirmation failed')
check(#store.events==1 and learning.score(store.index,first.mode,first.code,first.text,first.context)==13,'jump did not persist exactly one event')
check(learning.confidence_score(store.index,first.mode,first.code,first.text,first.context)==9,'one jump fabricated maturity')
local function read_file()local f=assert(io.open(path,'rb'));local s=f:read('*a');f:close();return s end
local data=read_file()
check(data:find('本次升级',1,true) and data:find('陲机',1,true) and data:find('设置',1,true) and data:find('\t3\t',1,true),'file is not readable UTF-8')
check(not learning.confirm(store,{first}) and read_file()==data,'duplicate receipt changed file')
local original_update=store.db.update;store.db.update=function()return false end
check(not learning.confirm(store,{event('failed')}) and read_file()==data,'failed write changed data')
store.db.update=original_update;store.db:close()
local reopen=dofile(root..'/lua/tiger_sentence_learning.lua')
local loaded=reopen.open(name)
check(loaded.db and #loaded.events==1 and loaded.events[1].levels==3 and reopen.score(loaded.index,first.mode,first.code,first.text,first.context)==13,'restart lost weighted event')
loaded.db:close()
-- Human edits with BOM/CRLF and no final newline remain valid.
local f=assert(io.open(path,'wb'));f:write('\239\187\191',(data:gsub('\n','\r\n'):gsub('\r\n$','')));f:close()
local reread=dofile(root..'/lua/tiger_sentence_learning.lua')
local edited=reread.open(name)
check(edited.db and #edited.events==1,'BOM/CRLF/no-final-newline edit rejected: '..tostring(edited.error))
check(reread.confirm(edited,{event('second','陲机','设置',2)}),'append after no-final-newline failed')
check(#edited.events==2 and reread.score(edited.index,first.mode,first.code,first.text,first.context)==17,'second jump did not survive file append')
edited.db:close()
local good=read_file();f=assert(io.open(path,'ab'));f:write('broken row\n');f:close()
local damaged=read_file();local bad=dofile(root..'/lua/tiger_sentence_learning.lua').open(name)
check(not bad.db and bad.error and read_file()==damaged,'malformed user edit was silently discarded')
f=assert(io.open(path,'wb'));f:write(good);f:close()
local function valid(e)return type(e.levels)=='number' and e.levels>=1 and e.levels<=3 and e.levels==math.floor(e.levels)end
for _,bad_value in ipairs({'TCL1\tE\told\t100\n',data:gsub('\t3\t','\t4\t'),data:gsub('%d%d%d%d%-%d%d%-%d%dT','2026-02-31T')})do
    check(not pcall(text.parse,bad_value,valid),'invalid/old file accepted')
end
os.remove(path)
print(string.format('{"adaptive_learning_checks":%d,"real_text_file_io":true,"rime_lock_mocked":true,"frontend_tested":false}',checks))
