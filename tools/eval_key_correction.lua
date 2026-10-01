-- Evaluate actual Lua reader/decoder, not a native surrogate. Isolated data only.
local pack,data,baseline,cases,output,penalties = table.unpack(arg)
package.path=pack..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return data end}
local old=dofile(baseline..'/lua/tiger_sentence.lua')
for name in pairs(package.loaded) do if name:match('^tiger_sentence') then package.loaded[name]=nil end end
local s=require('tiger_sentence')
old.ensure_lexicon();s.ensure_lexicon()
assert(old.model_status().loaded and s.model_status().loaded)
local weights={};for n in penalties:gmatch('%d+') do weights[#weights+1]=tonumber(n) end
local f=assert(io.open(output,'w'));f:write('id\tdataset\tvariant\tpenalty\tbase_correct\ttop1\ttop5\tchanged\tfirst\tseconds\n')
local function matches(a,b)
    assert(#a==#b,'off candidate count differs')
    for i=1,#a do assert(a[i].text==b[i].text and math.abs(a[i].score-b[i].score)<1e-9 and
        a[i].segmented==b[i].segmented,'off candidate mismatch') end
end
local function evaluate(id,dataset,variant,raw,target)
    s.correction.set_enabled(false);s.reset_decode_cache()
    local exact=s.decode(raw,false,'')
    if variant==0 then old.reset_decode_cache();matches(exact,old.decode(raw,false,'')) end
    local first=exact[1] and exact[1].text or ''
    local correct=first==target
    s.correction.set_enabled(true)
    local started=os.clock();local result=s.decode(raw,false,'');local elapsed=os.clock()-started
    local cache=s.correction.cache
    for _,p in ipairs(weights) do
        local ranked=result
        if cache.raw==raw and cache.unranked then
            ranked=s.correction.merge(exact,s.correction.rank_candidates(cache.unranked,p),20)
        end
        local text=ranked[1] and ranked[1].text or '';local top5=false
        for i=1,math.min(5,#ranked) do top5=top5 or ranked[i].text==target end
        f:write(string.format('%s\t%s\t%d\t%d\t%d\t%d\t%d\t%d\t%s\t%.6f\n',id,dataset,variant,p,
            correct and 1 or 0,text==target and 1 or 0,top5 and 1 or 0,text~=first and 1 or 0,text,elapsed))
    end
end
local rows=0
for line in io.lines(cases) do
    local id,dataset,raw,target,one,two=line:match('([^\t]+)\t([^\t]+)\t([^\t]+)\t([^\t]+)\t([^\t]+)\t([^\t]+)')
    assert(two,'bad case row')
    evaluate(id,dataset,0,raw,target)
    if one~='-' then evaluate(id,dataset,1,one,target);evaluate(id,dataset,2,two,target) end
    rows=rows+1
    if rows%25==0 then f:flush();print(rows);io.stdout:flush() end
end
f:close();print('complete',rows)
