-- Production decoder, optional real Q8. No host user data is modified.
local pack, data = assert(arg[1]), assert(arg[2])
package.path = pack .. '/lua/?.lua;' .. package.path
rime_api = {get_user_data_dir=function() return data end}
local s = require('tiger_sentence')
local c, checks = s.correction, 0
-- Historical path parity uses the reference search; bounded production policy
-- and truncation behavior are tested separately by test_key_correction_perf.lua.
if c.configure then c.configure('reference') end
local function check(ok, message) checks=checks+1; assert(ok, message) end
check(not c.has_two_han('ab') and not c.has_two_han('甲A') and c.has_two_han('甲𠀀'), 'Han protection')
for _, row in ipairs({'qwertyuiop','asdfghjkl','zxcvbnm'}) do
    for i=1,#row do
        local ns=c.neighbors[row:sub(i,i)]
        check(#ns == ((i==1 or i==#row) and 1 or 2), 'neighbor count')
        for _,ch in ipairs(ns) do check(math.abs(assert(row:find(ch,1,true))-i)==1,'adjacency') end
    end
end
local lex={codes={qwer={},qwee={},qwwr={},qweeew={}},proper_code_prefixes={}}
for code in pairs(lex.codes) do for i=1,#code-1 do lex.proper_code_prefixes[code:sub(1,i)]=true end end
for _,v in ipairs(c.variants('qwer',lex)) do
    local n=0; for i=1,4 do if v.code:sub(i,i)~=('qwer'):sub(i,i) then n=n+1 end end
    check(n==v.count and n<=2,'variant budget')
end
check(#c.variants('qw1r',lex)==0,'selector not a letter')
local exact={{text='原',score=10},{text='次',score=20}}
check(c.merge(exact,{{text='纠',score=21,correction_count=1}},20)[1].text=='原','margin and exact order')
check(c.merge(exact,{{text='纠',score=23,correction_count=1}},20)[1].text=='纠','promotion')
check(#c.merge(exact,{{text='原',score=40,correction_count=1}},20)==2,'text dedup')
s.ensure_lexicon(nil)
local real=s.model_status().loaded
if real then
    local default_penalty=c.penalty
    c.penalty=8 -- Fixed strength for path/budget tests; default is tested below.
    local function validate(raw,result)
        local seen={}
        for _,item in ipairs(result) do
            check(not seen[item.text],'duplicate text');seen[item.text]=true
            if item.correction_count then
                local n=0
                for i=1,#raw do
                    local a,b=raw:sub(i,i),item.corrected_raw:sub(i,i)
                    if a~=b then
                        n=n+1;check(table.concat(c.neighbors[a] or {}):find(b,1,true)~=nil,'non-neighbor edit')
                    end
                end
                check(#raw==#item.corrected_raw and n==item.correction_count and n<=2,'raw/budget')
                check(c.has_two_han(item.text),'single-character correction')
            end
        end
    end
    c.set_enabled(true)
    local corrected=s.decode('kispfidy')
    check(corrected[1].text=='测试一下' and corrected[1].correction_count==2,'two errors recovered')
    check(s.capture_empty_code_candidate('kispfidy','')==nil,'corrected empty-code auto commit')
    for _,raw in ipairs({'ki','kis','kosp','kispfidy','kispfifyiej','kospfifyiejryfenahbmsp','ki;spfify'}) do
        s.reset_decode_cache()
        for n=1,#raw do
            local prefix=raw:sub(1,n)
            local inc=s.decode(prefix,true,''); local full=s.decode_full(prefix,true,'')
            check(s.results_equal(inc,full),'append parity '..prefix);validate(prefix,inc)
            if n<4 then for _,v in ipairs(inc) do check(not v.correction_count,'short correction') end end
        end
        for n=#raw,1,-1 do
            local prefix=raw:sub(1,n)
            check(s.results_equal(s.decode(prefix,true,''),s.decode_full(prefix,true,'')),'backspace parity '..prefix)
        end
    end
    -- Frozen per-budget survivors can be rescored without repeating the lattice.
    s.reset_decode_cache();s.decode('kispfidy')
    local frozen=c.cache
    for _,penalty in ipairs({4,6,8,10,12,16}) do
        local scored=c.merge(frozen.exact,c.rank_candidates(frozen.unranked,penalty),20)
        c.penalty=penalty;s.reset_decode_cache()
        local rebuilt=s.decode('kispfidy')
        check(#scored==#rebuilt,'penalty candidate count')
        for i=1,#scored do
            check(scored[i].text==rebuilt[i].text and math.abs(scored[i].score-rebuilt[i].score)<1e-9 and
                scored[i].correction_count==rebuilt[i].correction_count,'penalty rescore parity '..penalty)
        end
    end
    c.penalty=8;s.reset_decode_cache()
    for _,raw in ipairs({'kospfify2','kispfidy2','kispfidy12'}) do
        s.reset_decode_cache();s.decode(raw)
        local short=raw:sub(1,-2)
        check(s.results_equal(s.decode(short,true,''),s.decode_full(short,true,'')),'cold selector deletion '..raw)
    end
    local selected=s.decode('kispfidy')[1]
    local boundaries,node={},selected.path
    while node and node.raw_length>0 do table.insert(boundaries,1,node.raw_length..','..node.text_length..';');node=node.previous end
    local lock={raw='kispfidy',text=selected.text,boundaries=table.concat(boundaries)..'|'..selected.corrected_raw}
    local raw=lock.raw..'iejryfen'
    local locked=s.decode(raw,false,lock.text,lock)
    check(locked[1].text:sub(1,#lock.text)==lock.text,'lock changed confirmed text')
    check(c.affected(locked[1]),'lost corrected lock provenance')
    for _,tail in ipairs({'ie','iejr','iejryfen','iejr','ifjr'}) do
        local code=lock.raw..tail
        local incremental=s.decode(code,true,lock.text,lock)
        s.reset_decode_cache()
        local rebuilt=s.decode(code,true,lock.text,lock)
        check(s.results_equal(incremental,rebuilt),'locked append/edit/backspace parity '..tail)
    end
    -- Confirming a two-error prefix starts a fresh two-error suffix budget.
    s.decode(lock.raw..'kispfidy',false,lock.text,lock)
    local recovered=false
    for _,item in ipairs(c.cache.unranked) do
        if item.text==lock.text..'测试一下' then
            check(item.correction_count==2 and item.corrected_raw=='kospfifykospfify',
                'confirmed errors consumed the new suffix budget')
            check(c.affected(item),'new suffix lost corrected history')
            recovered=true
        end
    end
    check(recovered,'two-error suffix missing after corrected lock')
    c.penalty=default_penalty;s.reset_decode_cache()
    if c.configure then c.configure('A') end
    check(s.decode('izjfibdsb')[1].text=='即便如此','calibrated default failed known strong correction')
    c.set_enabled(false)
    check(c.affected(s.decode(raw,false,lock.text,lock)[1]),'toggle lost corrected provenance')
    check(s.decode('kispfidy')[1].text~='测试一下','off still correcting')
end
s.set_model_enabled(false);c.set_enabled(true)
for _,v in ipairs(s.decode('kispfidy')) do check(not v.correction_count,'no-model correction') end
print(string.format('{"checks":%d,"real_model":%s}',checks,tostring(real)))
