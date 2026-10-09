-- Run in run_regressions.py's owned data copy; production files are restored.
local repo = arg[1] or "."
package.path = repo .. "/lua/?.lua;" .. package.path
rime_api = {get_user_data_dir=function() return repo end}
local sentence = dofile(os.getenv("TIGER_SENTENCE_MODULE") or repo .. "/lua/tiger_sentence.lua")
local checks = 0
local function check(ok, message) checks=checks+1; assert(ok, message) end
local function read(path)
    local f=assert(io.open(path,"rb")); local value=f:read("*a"); f:close(); return value
end
local function write(path, value)
    local f=assert(io.open(path,"wb")); assert(f:write(value)); f:close()
end
local path=repo.."/tiger_sentence.codes.txt"
local original=read(path)
local function env(value, legacy)
    return {engine={schema={schema_id="min-code-test",config={
        get_int=function(_,name)
            if name=="tiger_sentence/high_freq_limit" then return 0 end
            if name=="tiger_sentence/auto_select_min_code_length" then return value end
        end,
        get_bool=function(_,name)
            if name=="tiger_sentence/option_defaults/tiger_sentence_allow_duplicate_single" then return legacy end
            return false
        end
    }}}}
end
local function configure(value, legacy)
    sentence.ensure_lexicon(env(value,legacy))
    return sentence.data_status().auto_select_min_code_length
end
local function contains(values,text)
    for _,v in ipairs(values) do if v.text==text then return v end end
end
local function expect(raw,text,present)
    local result=sentence.decode(raw,true,"")
    check(sentence.results_equal(result,sentence.decode_full(raw,true,"")),"incremental/full mismatch "..raw)
    check((contains(result,text)~=nil)==present,"implicit eligibility "..raw.." / "..text)
    return result
end
local function body()
    write(path,"甲\tab\n乙\tab\n丙\tabc\n丁\tabc\n甲乙\tabc\n己\tabcd\n庚\tabcd\n辛\tabcde\n壬\tabcde\n天\tuvw\n𠀀\tuvw\n戊\txy\n")
    sentence.apply_high_freq_limit(0)
    sentence.set_model_enabled(false)
    check(configure(nil)==3,"missing key did not default to 3")
    check(configure(nil,false)==3,"removed legacy false changed default")
    check(configure(2,false)==2,"removed legacy false overrode numeric value")
    check(configure(0,true)==0,"removed legacy true overrode disabled value")
    for _,spec in ipairs({{-10,0},{0,0},{1,1},{2,2},{3,3},{4,4},{128,128},{129,128},{2.9,2},{"2",3},{0/0,3}}) do
        check(configure(spec[1])==spec[2],"normalization mismatch "..tostring(spec[1]))
    end
    check(sentence.ensure_lexicon({engine={schema={}}}).auto_select_min_code_length==3,"no config inherited previous value")
    check(configure(4)==4,"setting 4 failed")
    local codes=sentence.lexicon_data_view().codes
    local result=sentence.decode("abcxy",true,"")
    local epoch=sentence.correction.epoch
    check(configure(4)==4 and sentence.correction.epoch==epoch,"same effective setting invalidated cache")
    check(sentence.decode("abcxy",true,"")==result,"same setting did not reuse decode result")
    check(sentence.ensure_lexicon(nil).auto_select_min_code_length==4,"schema-less call reset setting")
    check(configure(2)==2 and sentence.correction.epoch>epoch,"changed setting did not invalidate correction cache")
    check(sentence.lexicon_data_view().codes==codes,"threshold change rebuilt immutable lexicon")
    for _,minimum in ipairs({0,1,2,3,4,5,128,3,2,0}) do
        configure(minimum)
        for _,entry in ipairs({{"ab","甲","乙"},{"abc","丙","丁"},{"abcd","己","庚"},{"abcde","辛","壬"},{"uvw","天","𠀀"}}) do
            local code,first,second=entry[1],entry[2],entry[3]
            local allowed=minimum>0 and #code>=minimum
            expect(code,first,true)
            expect(code,second,true) -- Hand selection keeps the complete menu.
            expect(code.."xy",first.."戊",true)
            expect(code.."xy",second.."戊",allowed)
            expect("xy"..code,"戊"..second,allowed)
            for _,suffix in ipairs({";","2","00002"}) do
                expect(code..suffix.."xy",second.."戊",true)
            end
            check(sentence.has_complete_candidate(code.."xy",second.."戊",nil,false)==allowed,
                "required-prefix reachability bypassed threshold")
            check(sentence.has_complete_candidate(code,second,nil,true)==allowed,
                "group eligibility bypassed threshold")
            check(sentence.has_complete_candidate(code,second,nil,false),"menu reachability lost manual choice")
            local raw=code.."xy"
            for n=#raw,1,-1 do
                local part=raw:sub(1,n)
                check(sentence.results_equal(sentence.decode(part,true,""),sentence.decode_full(part,true,"")),
                    "backspace mismatch "..part)
            end
        end
        expect("abcxy","甲乙戊",false) -- Non-first multi-character words remain explicit.
        expect("abc'xy","甲乙戊",true)
        expect("abc3xy","甲乙戊",true)
        expect("abc","甲乙",true)
    end
    configure(2)
    check(sentence.capture_empty_code_candidate("ab","")==nil,"eligible short duplicate lost confidence competition")
    configure(3)
    local pending=sentence.capture_empty_code_candidate("ab","")
    check(pending and pending.candidate_text=="甲","below-threshold duplicate polluted empty-code confidence")
    -- A short entry explicitly confirmed from its whole-input menu remains a
    -- valid locked prefix even after numeric auto-selection is disabled.
    configure(0)
    local lock={raw="ab",text="乙",boundaries="2,3;"}
    local locked=sentence.decode("abxy",true,"乙",lock)
    check(contains(locked,"乙戊")~=nil,"threshold rejected a manually confirmed lock")
    local next_epoch=sentence.correction.epoch
    configure(3)
    check(sentence.correction.epoch>next_epoch,"locked cache survived setting change")
    check(contains(sentence.decode("abxy",true,"乙",lock),"乙戊")~=nil,"setting change lost confirmed lock")
    expect("abxy","乙戊",false)
    -- A previously retained auto-commit proposal must not cross a setting change.
    local props={}
    local context={input="",caret_pos=0,composition={empty=function() return true end}}
    function context:get_property(name) return props[name] or "" end
    function context:set_property(name,value) props[name]=value end
    function context:get_option() return false end
    function context:is_composing() return false end
    local frontend=env(2);frontend.engine.context=context
    frontend.engine.commit_text=function() error("unexpected commit") end
    Candidate=function(kind,start,finish,text,comment)return{type=kind,start=start,_end=finish,text=text,comment=comment}end
    yield=function()end
    sentence.translator("",{start=0,_end=0},frontend)
    local state=frontend._tiger_sentence_transient
    state.trackers={stale={evidence_count=99}};state.last_seen_raw="ab"
    state.empty_code_pending={candidate_text="乙"}
    frontend.engine.schema.config=env(3).engine.schema.config
    sentence.translator("",{start=0,_end=0},frontend)
    check(next(state.trackers)==nil and state.last_seen_raw=="" and state.empty_code_pending==nil,
        "threshold change retained stale auto-commit evidence")
    -- Synthetic Q8 executes the correction lattice with identical edge gates;
    -- its candidate pool is checked before the visible two-correction limit.
    local fixture=repo.."/sentence-fivegram-mobile.bin"
    local existing=io.open(fixture,"rb")
    if existing then existing:close(); error("fixture path already exists") end
    dofile(repo.."/tools/model_fixture.lua")(fixture)
    sentence.set_model_enabled(true)
    local loaded=sentence.model_status().loaded
    if loaded then
        sentence.correction.configure("reference")
        for _,minimum in ipairs({0,2,3,4,2,0}) do
            configure(minimum)
            for _,entry in ipairs({{"sbxy","乙戊",2},{"sbcxy","丁戊",3},{"sbcdxy","庚戊",4}}) do
                if sentence.correction.set_level then sentence.correction.set_level("strong")
                else sentence.correction.set_enabled(true) end
                sentence.decode(entry[1],true,"")
                local allowed=minimum>0 and entry[3]>=minimum
                check((contains(sentence.correction.cache.unranked or {},entry[2])~=nil)==allowed,
                    "correction edge bypassed threshold "..entry[1].." min="..minimum)
                local actual=sentence.decode(entry[1],true,"")
                check(sentence.results_equal(actual,sentence.decode_full(entry[1],true,"")),
                    "correction/full mismatch "..entry[1])
            end
        end
        if sentence.correction.set_level then sentence.correction.set_level("off")
        else sentence.correction.set_enabled(false) end
    else
        print("SKIP synthetic Q8 correction lattice: "..tostring(sentence.model_status().error))
    end
    sentence.set_model_enabled(false)
    os.remove(fixture)
    print(string.format("OK auto_select_min_code_length: %d checks; numeric config, 0/off, boundaries, explicit/menu/lock, Unicode, reachability and caches",checks))
end
local ok,err=xpcall(body,debug.traceback)
write(path,original);sentence.apply_high_freq_limit(1500)
sentence.apply_auto_select_min_code_length(3)
if not ok then error(err,0) end
