-- Isolated CPU benchmark; no LevelDb, installed input method or user writes.
-- lua tools/bench_rime_learning.lua rime/tiger_sentence/lua [repeats]
local root, repeats = arg[1] or "rime/tiger_sentence/lua", tonumber(arg[2]) or 10
local learning = dofile(root .. "/tiger_sentence_learning.lua")
local now = os.time()
local function code(i)
    local result = ""
    for _=1,4 do result=string.char(97+i%26)..result; i=math.floor(i/26) end
    return result
end
local function measure(fn)
    local samples={}
    for i=1,repeats do
        local run=fn() -- fixture preparation is outside timing
        collectgarbage("collect")
        local started=os.clock(); run(); samples[i]=(os.clock()-started)*1000
    end
    table.sort(samples)
    return samples[math.ceil(#samples/2)],samples[#samples]
end
for _, count in ipairs({100,1000,9999}) do
    for _, distinct in ipairs({false,true}) do
        local events={}
        for i=1,count do events[i]={time=now,mode="test",code=code(distinct and i or i%100),text="甲乙",context=""} end
        local function fixture()
            local history={}; for i,e in ipairs(events) do history[i]=e end
            local builder=learning.runtime_index or learning.build
            return {db={update=function()return true end},events=history,index=builder(history,now),
                scored_at=now,count=count,sequence=count,bytes=0}
        end
        for _, action in ipairs({"confirm","refresh","refresh_query"}) do
            local median,maximum=measure(function()
                local store=fixture()
                if action=="confirm" then
                    return function() assert(learning.confirm(store,{{time=now,mode="test",code=code(count+1),text="甲丙",context=""}})) end
                end
                store.scored_at=now-61
                return function()
                    learning.refresh_scores(store)
                    if action=="refresh_query" then learning.prefix_score(store.index,"test","a","甲","") end
                end
            end)
            print(string.format("records=%d distinct=%s action=%s median_ms=%.4f max_ms=%.4f",count,tostring(distinct),action,median,maximum))
        end
    end
end
