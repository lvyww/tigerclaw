-- Exercise real decoder/frontend entry points, not just ensure_lexicon(env).
-- Usage: lua tools/test_high_freq_limit.lua ROOT [--require-model]
local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
rime_api = {get_user_data_dir = function() return repo end}
local sentence = dofile(os.getenv("TIGER_SENTENCE_MODULE") or (repo .. "/rime/tiger_sentence/lua/rime/tiger_sentence/tiger_sentence.lua"))
local require_model = arg[2] == "--require-model"
sentence.set_model_enabled(require_model)
if require_model then
    local model = sentence.model_status()
    assert(model.loaded, "Production model required: " .. tostring(model.error))
    print("MODEL " .. tostring(model.format) .. " bytes=" .. tostring(model.bytes))
end

local checks = 0
local function check(value, message)
    checks = checks + 1
    assert(value, message)
end
-- Count actual code-table reads: catching a default rebuild followed by a
-- restoring rebuild is stronger than checking only the eventual limit.
local code_reads, open = 0, io.open
io.open = function(path, ...)
    if path:match("tiger_sentence%.codes%.txt$") then code_reads = code_reads + 1 end
    return open(path, ...)
end
local function environment(value, id, unreadable)
    return {engine = {schema = {schema_id = id, config = {
        get_int = function(_, name)
            if unreadable then error("unreadable config") end
            if name == "tiger_sentence/high_freq_limit" then return value end
        end,
        get_bool = function() return false end
    }}}}
end
local function limit_is(expected, message)
    check(sentence.data_status().high_freq_limit == expected, message)
end
local function borrow_without_rebuilding(expected, index, label)
    local reads = code_reads
    for _ = 1, 3 do
        check(sentence.ensure_lexicon(nil).codes == index, label .. ": nil lost index")
        check(sentence.ensure_lexicon({engine={}}).codes == index, label .. ": no schema lost index")
        sentence.decode("ot", true, "")
        sentence.decode_full("ot", true, "")
        check(sentence.lexicon_data_view().codes == index, label .. ": diagnostic changed index")
        limit_is(expected, label .. ": internal entry changed limit")
    end
    check(code_reads == reads, label .. ": internal entry rebuilt lexicon")
end

-- A cold schema-less load alone uses the default. Once configured, all such
-- calls borrow the current index (including explicit numeric zero).
local default_index = sentence.ensure_lexicon(nil).codes
limit_is(1500, "Cold internal load did not use default")
check(code_reads == 1, "Cold load did not read the code table exactly once")
borrow_without_rebuilding(1500, default_index, "cold default")
local unlimited_env = environment(0, "unlimited")
local unlimited_index = sentence.ensure_lexicon(unlimited_env).codes
local reads = code_reads
sentence.decode("ot", false, "")
limit_is(0, "First decode reset explicit high_freq_limit zero")
check(code_reads == reads, "First decode rebuilt configured lexicon")
borrow_without_rebuilding(0, unlimited_index, "explicit zero")
for _, value in ipairs({1, 73, 2400, -5}) do
    local expected = math.max(0, value)
    local env = environment(value, "configured-" .. tostring(value))
    local index = sentence.ensure_lexicon(env).codes
    limit_is(expected, "Explicit/negative limit was not respected")
    borrow_without_rebuilding(expected, index, "configured limit")
end

-- Real schema-bearing calls resolve their own default, even when a schema id
-- is reused after redeployment or two Config instances use the same id.
for _, target in ipairs({
    environment(nil, "keyless"), environment("0", "invalid"),
    environment(nil, "unreadable", true), {engine={schema={schema_id="no-config"}}},
    environment(nil, "unlimited"), environment(nil, nil)
}) do
    sentence.ensure_lexicon(unlimited_env)
    reads = code_reads
    local index = sentence.ensure_lexicon(target).codes
    limit_is(1500, "Schema with missing/invalid key inherited previous limit")
    check(code_reads == reads + 1, "Effective limit change did not rebuild once")
    borrow_without_rebuilding(1500, index, "schema default")
    reads = code_reads
    check(sentence.ensure_lexicon(target).codes == index, "Repeated schema entry rebuilt index")
    check(code_reads == reads, "Repeated schema entry reread code table")
end

-- Alternating A(0)/B(default)/A, and distinct schemas with equal settings.
for _ = 1, 3 do
    for _, spec in ipairs({{unlimited_env, 0}, {environment(nil, "B"), 1500}, {unlimited_env, 0}}) do
        local before = sentence.data_status().high_freq_limit
        reads = code_reads
        local index = sentence.ensure_lexicon(spec[1]).codes
        limit_is(spec[2], "Cross-schema effective limit was not applied")
        check(code_reads == reads + (before == spec[2] and 0 or 1), "Schema switch rebuild count")
        borrow_without_rebuilding(spec[2], index, "alternating schemas")
    end
end
reads = code_reads
unlimited_index = sentence.ensure_lexicon(unlimited_env).codes
check(sentence.ensure_lexicon(environment(0, "same-limit-other-schema")).codes == unlimited_index,
    "Equal limits in distinct schemas did not reuse index")
check(code_reads == reads, "Equal schema limits caused unnecessary rebuild")

-- The public explicit override keeps its historical numeric semantics; nil
-- alone is a no-op, including no I/O and no cache invalidation.
sentence.apply_high_freq_limit(73)
local applied = sentence.ensure_lexicon(nil).codes
reads = code_reads
sentence.apply_high_freq_limit(nil)
limit_is(73, "apply_high_freq_limit(nil) changed active limit")
check(sentence.ensure_lexicon(nil).codes == applied and code_reads == reads,
    "apply_high_freq_limit(nil) rebuilt lexicon")
borrow_without_rebuilding(73, applied, "explicit override")

-- Simulate Rime's synchronous processor -> input update -> translator path,
-- using separate environments sharing one actual schema and context.
Candidate = function(kind, start, finish, text, comment)
    return {type=kind, start=start, _end=finish, text=text, comment=comment}
end
yield = function() end
local function frontend_cycle(value, id)
    local env = environment(value, id)
    local properties = {}
    local ctx = {input="", caret_pos=0}
    function ctx:get_property(name) return properties[name] or "" end
    function ctx:set_property(name, text) properties[name] = text end
    function ctx:get_option(name) return name == "tiger_sentence_allow_duplicate_single" end
    function ctx:is_composing() return self.input ~= "" end
    ctx.composition = {empty=function() return true end}
    env.engine.context = ctx
    env.engine.commit_text = function() error("Unexpected automatic commit") end
    local translator_env = {engine=env.engine}
    local function refresh()
        sentence.translator(ctx.input, {start=0, _end=#ctx.input}, translator_env)
    end
    function ctx:push_input(ch)
        self.input = self.input .. ch
        self.caret_pos = #self.input
        refresh()
        return true
    end
    local expected = value or 1500
    local index = sentence.ensure_lexicon(env).codes
    local before = code_reads
    for ch in ("tujanen"):gmatch(".") do
        local event = {repr=function() return ch end, release=function() return false end,
            ctrl=function() return false end, alt=function() return false end,
            super=function() return false end, shift=function() return false end}
        check(sentence.processor(event, env) == 1, "Processor did not handle ordinary input")
        limit_is(expected, "Processor/translator reset schema limit")
        for _ = 1, 3 do refresh() end
        check(sentence.lexicon_data_view().codes == index, "Frontend lost active index")
        check(code_reads == before, "Per-key processor/translator rebuilt lexicon")
    end
end
frontend_cycle(0, "frontend-unlimited")
frontend_cycle(nil, "frontend-default")
frontend_cycle(0, "frontend-unlimited")
io.open = open
print(string.format("OK high_freq_limit: %d checks; schema-less decoding preserves active index", checks))
