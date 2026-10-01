-- Bounded production search contracts; explicitly supplied, isolated model data.
local pack,data=assert(arg[1]),assert(arg[2])
package.path=pack..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return data end}
local s=require('tiger_sentence');s.ensure_lexicon();s.set_memory_profile('compact')
assert(s.model_status().loaded)
local c=s.correction
c.set_enabled(true);c.diagnostics_enabled=true
local checks=0
local function check(value,message) checks=checks+1;assert(value,message) end
local function validate(raw,result)
    local count,seen=0,{}
    for _,v in ipairs(result)do
        check(not seen[v.text],'duplicate text');seen[v.text]=true
        if v.correction_count then
            count=count+1
            check(v.path.raw_length==#raw and #v.corrected_raw==#raw,'partial/stale correction')
            check(v.correction_count<=2 and c.has_two_han(v.text),'invalid correction')
        end
    end
    check(count<=2,'more than two corrected candidates')
    if c.last_work then
        check(c.last_work.one<=c.last_work.limit and c.last_work.two<=c.last_work.limit,'budget overrun')
    end
end
for _,profile in ipairs({'A','B','C'})do
    c.configure(profile);s.reset_decode_cache()
    local result=s.decode('jaefmfmvqcbzl')
    check(result[1].text=='今天天气不错','clean screenshot changed')
    for _,v in ipairs(result)do check(not v.correction_count,'screenshot filled with low-score corrections')end
    for _,raw in ipairs({'kispfidy','izjfibdsb'})do
        s.reset_decode_cache();result=s.decode(raw);validate(raw,result)
        local searches,one,two=c.stats.searches,c.stats.one_steps,c.stats.two_steps
        local again=s.decode(raw,true,'')
        check(c.stats.searches==searches and c.stats.one_steps==one and c.stats.two_steps==two,
            'same generation received a new budget')
        check(#result==#again,'evidence upgrade changed candidate count')
        for i,v in ipairs(result) do
            check(v.text==again[i].text and v.score==again[i].score and
                v.correction_count==again[i].correction_count,'evidence upgrade changed candidate scores')
        end
    end
end
c.configure('A');s.reset_decode_cache()
check(s.decode('kispfidy')[1].text=='测试一下','default lost known two-error correction')
-- Every profile attempts both budgets, including two edits in a single edge.
local exact={[0]={}}
local work=c.begin(exact)
for i=1,work.limit do check(c.reserve(1,1),'one budget ended early') end
check(not c.reserve(1,1),'one exceeded quota')
check(c.reserve(2,2),'one budget starved two-error search')
c.searching=nil
-- Force exhaustion with a test-only profile; it is not a selectable setting.
c.profiles.tiny={seeds=8,one=16,two=8,steps=2};c.configure('tiny');s.reset_decode_cache()
local limited=s.decode('kispfidy',true,'')
check(limited.correction_incomplete,'lost incomplete-search marker')
validate('kispfidy',limited)
check(s.capture_empty_code_candidate('kispfidy','')==nil,'exhausted search authorized auto commit')
local searches=c.stats.searches;s.decode('kispfidy',true,'')
check(c.stats.searches==searches,'exhaustion retry renewed the same budget')
check(s.model_status().loaded,'budget exhaustion disabled the model')
c.configure('A');s.reset_decode_cache()
-- Compare editing reuse only where both executions completed within budget.
for _,raw in ipairs({'kospfifyiejryfen','kospfifyiefryfen','kospfifyie','kospfifyiej','kospfifyiejryfen',
                     'kospfifyiejryden','kospfifyiejryden2','kospfifyiejryden'})do
    local a=s.decode(raw,false,'');local b=s.decode_full(raw,false,'')
    validate(raw,a)
    if not a.correction_incomplete and not b.correction_incomplete then
        check(s.results_equal(a,b),'incremental/full mismatch '..raw)
    end
end
c.configure('A');s.reset_decode_cache()
c.set_enabled(false)
for _,v in ipairs(s.decode('kispfidy'))do check(not v.correction_count,'disabled correction searched')end
print(string.format('{"bounded_correction_checks":%d,"model":true}',checks))
