-- Execute the production module with Rime-contract fakes, not copied functions.
-- The fake implements byte-offset insertion/deletion and lazy Menu::Prepare.
local repo = arg[1] or "."
rime_api = {get_user_data_dir = function() return repo end}
local sentence = dofile(os.getenv("TIGER_SENTENCE_MODULE") or (repo .. "/lua/tiger_sentence.lua"))
sentence.set_model_enabled(false)
sentence.ensure_lexicon(nil)
local checks, cases = 0, 0
local function check(ok, message)
    checks = checks + 1
    assert(ok, message)
end
local function key(repr)
    return {repr=function()return repr end, release=function()return false end,
        ctrl=function()return false end, alt=function()return false end,
        super=function()return false end, shift=function()return false end}
end
local function environment(raw, caret, automatic)
    local properties, commits = {}, {}
    local context = {input=raw or "", caret_pos=caret or #(raw or ""), updates=0}
    function context:get_property(name)return properties[name] or "" end
    function context:set_property(name,value)properties[name]=value end
    function context:get_option(name)
        return name=="tiger_sentence_allow_duplicate_single" or
            (name=="tiger_sentence_early_commit" and automatic==true)
    end
    function context:is_composing()return self.input~="" end
    function context:push_input(ch)
        self.input=self.input:sub(1,self.caret_pos)..ch..self.input:sub(self.caret_pos+1)
        self.caret_pos=self.caret_pos+#ch;self.updates=self.updates+1
        if self.on_update then self.on_update() end
        return true
    end
    function context:pop_input(n)
        if self.caret_pos<n then return false end
        self.input=self.input:sub(1,self.caret_pos-n)..self.input:sub(self.caret_pos+1)
        self.caret_pos=self.caret_pos-n;self.updates=self.updates+1
        return true
    end
    function context:delete_input(n)
        if self.caret_pos+n>#self.input then return false end
        self.input=self.input:sub(1,self.caret_pos)..self.input:sub(self.caret_pos+n+1)
        self.updates=self.updates+1;return true
    end
    function context:set_input(value)self.input=value;self.caret_pos=#value;self.updates=self.updates+1 end
    function context:clear()self.input="";self.caret_pos=0;self.updates=self.updates+1 end
    local menu={loaded=6,total=20,prepares=0}
    function menu:candidate_count()return self.loaded end
    function menu:prepare(n)
        check(n<=20,"Tab exceeded bounded candidate preparation")
        self.prepares=self.prepares+1;self.loaded=math.max(self.loaded,math.min(n,self.total))
        return self.loaded
    end
    local segment={menu=menu,selected_index=0}
    context.composition={empty=function()return false end,back=function()return segment end}
    function context:has_menu()return self:is_composing() and menu.loaded>0 end
    function context:highlight(i)
        local count=menu:prepare(i+1)
        if i>=count then return false end
        segment.selected_index=i;return true
    end
    function context:confirm_current_selection()return true end
    local engine={context=context,commit_text=function(_,text)commits[#commits+1]=text end}
    return {engine=engine},context,properties,commits,menu,segment
end
local function lock(properties,raw,text,boundaries)
    local out={}
    for _,value in ipairs({raw,text,boundaries})do out[#out+1]=#value..":"..value end
    properties.tiger_sentence_locks=table.concat(out)
end
-- Actual default table: vp appends a -> commit 刘; v|p inserts a -> vap, no commit.
do
    local env,context,_,commits=environment("vp",1,true)
    local observed
    context.on_update=function()observed=context.input end
    check(sentence.processor(key("a"),env)==1,"Middle insertion not handled")
    check(observed=="vap" and context.input=="vap","Middle insertion treated as append")
    check(context.caret_pos==2 and #commits==0,"Middle insertion committed/replaced unrelated text")
    check(context.updates==1,"Middle insertion performed multiple input mutations")
    cases=cases+1
    env,context,_,commits=environment("vp",2,true)
    sentence.processor(key("a"),env)
    check(context.input=="a" and commits[1]=="刘","End-append empty-code behaviour changed")
    cases=cases+1
end
for _,repr in ipairs({"BackSpace","Delete"})do
    local env,context,properties,commits=environment("abcd",2,false)
    lock(properties,"abcd","甲乙","2,3;4,6;")
    check(sentence.processor(key(repr),env)==1,"Locked deletion not handled")
    check(context.input==(repr=="BackSpace" and "acd" or "abd"),"Locked deletion ignored caret")
    check(context.caret_pos==(repr=="BackSpace" and 1 or 2),"Locked deletion moved caret to end")
    check(properties.tiger_sentence_locks=="" and #commits==0,"Edited lock remained active")
    cases=cases+1
end
-- Beginning/end no-op keys do not remove an unrelated character or the lock.
for _,spec in ipairs({{"BackSpace",0},{"Delete",4}})do
    local env,context,properties=environment("abcd",spec[2],false)
    lock(properties,"abcd","甲乙","2,3;4,6;")
    local saved=properties.tiger_sentence_locks
    check(sentence.processor(key(spec[1]),env)==2 and context.input=="abcd","Boundary delete removed data")
    check(properties.tiger_sentence_locks==saved,"Boundary delete discarded a lock")
    cases=cases+1
end
-- A committed prefix cannot be deleted by editing the live tail.
do
    local env,context,properties,commits=environment("abcd",2,true)
    properties.tiger_sentence_committed="vp\t刘"
    lock(properties,"vp","刘","2,3;")
    local saved=properties.tiger_sentence_locks
    sentence.processor(key("BackSpace"),env)
    check(context.input=="acd" and properties.tiger_sentence_committed=="vp\t刘","Live deletion crossed committed prefix")
    check(properties.tiger_sentence_locks==saved and #commits==0,"Committed lock lost/recommitted")
    cases=cases+1
end
-- Reaching an uncommitted lock boundary still releases it, as before.
do
    local env,context,properties=environment("abcde",5,false)
    lock(properties,"abcd","甲乙","2,3;4,6;")
    sentence.processor(key("BackSpace"),env)
    check(context.input=="abcd" and properties.tiger_sentence_locks=="","Boundary backspace failed to unlock")
    cases=cases+1
end
-- Insertion before/inside a lock invalidates it, after it preserves it.
for caret=0,4 do
    local env,context,properties,commits=environment("abcd",caret,false)
    lock(properties,"ab","甲","2,3;")
    local saved=properties.tiger_sentence_locks
    sentence.processor(key("x"),env)
    check(context.input==("abcd"):sub(1,caret).."x"..("abcd"):sub(caret+1),"Insertion lost suffix")
    check(caret<2 and properties.tiger_sentence_locks=="" or caret>=2 and properties.tiger_sentence_locks==saved,
        "Insertion invalidated wrong lock range")
    check(#commits==0,"Manual editing committed text")
    cases=cases+1
end
do
    local env,context,_,commits,menu,segment=environment("ueot",4,false)
    segment.selected_index=5
    check(sentence.processor(key("Tab"),env)==1 and segment.selected_index==6,"Tab wrapped at materialized prefix")
    for _=1,14 do sentence.processor(key("Tab"),env) end
    check(segment.selected_index==0,"Tab did not wrap at true end")
    sentence.processor(key("ISO_Left_Tab"),env)
    check(segment.selected_index==19,"Reverse Tab missed last lazy candidate")
    check(menu.prepares>0 and #commits==0,"Tab selected/committed instead of highlighting")
    cases=cases+1
    -- A pending Tab selection may not confirm stale text after manual editing.
    segment.selected_index=0
    context.caret_pos=1
    sentence.processor(key("a"),env)
    check(context.input=="uaeot" and #commits==0,"Middle edit consumed pending Tab confirmation")
    check(not env._tiger_sentence_transient.tab_pending,"Middle edit retained pending Tab")
    cases=cases+1
end
for _,repr in ipairs({"Left","Right","Home","End"})do
    local env=environment("ueot",2,true)
    sentence.processor(key("Tab"),env)
    check(sentence.processor(key(repr),env)==2,"Caret navigation was swallowed")
    check(not env._tiger_sentence_transient.tab_pending and next(env._tiger_sentence_transient.trackers)==nil,
        "Caret navigation retained append evidence or Tab confirmation")
    cases=cases+1
end
print(string.format('{"status":"passed","cases":%d,"checks":%d,"caret_and_lazy_menu":true,"real_frontend":false}',cases,checks))
