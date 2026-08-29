-- Verify incremental lattice + exact logp cache match a full rebuild.
-- Usage: lua tools/test_tiger_sentence_incremental.lua [repo_root] [--require-model]

local repo = arg[1] or "."
local require_model = false
for i = 2, #arg do
    if arg[i] == "--require-model" then
        require_model = true
    end
end
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path

-- Mirror a real frontend so the decoder also exercises the default
-- per-schema supplemental corpus during incremental/full parity checks.
rime_api = {
    get_user_data_dir = function()
        return repo .. "/rime/tiger_sentence"
    end
}

local sentence = require("tiger_sentence")

local samples = {
    "ot",
    "ueot",
    "ueottu",
    "jqtusotu",
    "ueot;",
    "ueot'",
    "ueot2",
    "ueot12",
    "jqtusotuqiueottu",
}

local long_code = "jqtusotuqiueottu"

local function tops(results, n)
    n = n or 3
    local parts = {}
    for i = 1, math.min(n, #results) do
        parts[#parts + 1] = string.format("%s(%.6f)", results[i].text, results[i].score)
    end
    return table.concat(parts, " | ")
end

local function fail(message)
    io.stderr:write(message .. "\n")
    os.exit(1)
end

local function check_equal(label, incremental, full)
    if not sentence.results_equal(incremental, full) then
        fail(string.format(
            "MISMATCH %s\n  inc: %s\n  full:%s",
            label,
            tops(incremental, 5),
            tops(full, 5)
        ))
    end
    print(string.format("OK  %-40s  %s", label, tops(full, 2)))
end

local function check_visible_equal(label, left, right)
    if #left ~= #right then
        fail(string.format("VISIBLE MISMATCH %s: %d ~= %d", label, #left, #right))
    end
    for i = 1, #left do
        if left[i].text ~= right[i].text or
            left[i].segmented ~= right[i].segmented or
            left[i].score ~= right[i].score or
            left[i].confidence_score ~= right[i].confidence_score then
            fail("VISIBLE MISMATCH " .. label)
        end
    end
end

print("initializing decoder + first full decode...")
local t0 = os.clock()
local first = sentence.decode_full(long_code)
local model = sentence.model_status()
if model.loaded then
    print(string.format(
        "model=%s path=%s ready in %.3fs top=%s",
        model.format,
        model.path,
        os.clock() - t0,
        tops(first, 1)
    ))
else
    print(string.format(
        "model=none ready in %.3fs error=%s top=%s",
        os.clock() - t0,
        model.error or "unknown",
        tops(first, 1)
    ))
    if require_model then
        fail("a real sentence n-gram model was required but could not be loaded")
    end
end

local supplement_status = sentence.supplement_status()
if supplement_status.count < 1 then
    fail(string.format(
        "supplement count expected at least 1, got %d (%s)",
        supplement_status.count or -1,
        supplement_status.error or "no error"))
end
local supplement_candidates = sentence.decode_full("lccpf")
local supplement_target = nil
for i = 1, #supplement_candidates do
    if supplement_candidates[i].text == "茧师" then
        supplement_target = supplement_candidates[i]
        break
    end
end
if not supplement_target then
    fail("supplement target 茧师 was not decoded from lccpf")
end
local expected_supplement = 9.0 + 2.0 * math.log(3.0)
if math.abs((supplement_target.supplement_score or 0.0) - expected_supplement) > 1e-9 then
    fail(string.format(
        "supplement reward expected %.9f, got %.9f",
        expected_supplement,
        supplement_target.supplement_score or 0.0))
end
if math.abs(
    supplement_target.score - supplement_target.confidence_score - expected_supplement) > 1e-9 then
    fail("supplement reward leaked into confidence mass")
end
print("OK  supplemental corpus loaded, ranked, and excluded from confidence mass")

for i = 1, #samples do
    sentence.reset_decode_cache()
    local raw = samples[i]
    local grown = {}
    for n = 1, #raw do
        grown = sentence.decode(raw:sub(1, n))
    end
    local full = sentence.decode_full(raw)
    check_equal("grow " .. raw, grown, full)

    sentence.reset_decode_cache()
    local same = sentence.decode(raw)
    same = sentence.decode(raw)
    check_equal("same " .. raw, same, full)

    sentence.reset_decode_cache()
    local normal = sentence.decode(raw, false)
    local with_early = sentence.decode(raw, true)
    check_visible_equal("early metadata " .. raw, normal, with_early)
    check_equal("early full " .. raw, with_early, sentence.decode_full(raw, true))
end

sentence.reset_decode_cache()
local shrinking = {}
for n = #long_code, 1, -1 do
    shrinking = sentence.decode(long_code:sub(1, n))
    local full = sentence.decode_full(long_code:sub(1, n))
    check_equal("back " .. long_code:sub(1, n), shrinking, full)
end

-- 1-key whole-input then grow / shrink across the length==1 boundary
sentence.reset_decode_cache()
check_equal("one-key u", sentence.decode("u"), sentence.decode_full("u"))
check_equal("one-key to ue", sentence.decode("ue"), sentence.decode_full("ue"))
check_equal("ue back to u", sentence.decode("u"), sentence.decode_full("u"))
check_equal("u to ueot", sentence.decode("ueot"), sentence.decode_full("ueot"))

local boundary_candidates = sentence.decode_full("ueottu")
local boundary = sentence.find_raw_length_for_text("的是", boundary_candidates)
if boundary ~= 4 then
    fail(string.format("wrong raw boundary for 的是: expected 4, got %d", boundary))
end
print("OK  early-commit raw boundary 的是 -> ueot")

sentence.reset_decode_cache()
local conditioned = sentence.decode("ueot", true, "的")
check_equal(
    "conditioned early evidence",
    conditioned,
    sentence.decode_full("ueot", true, "的"))
if (conditioned.early_commit_evidence or {}).proposal ~= "的" then
    fail("conditioned early-commit evidence did not retain 的")
end
print("OK  early-commit evidence is conditioned on committed text")

local proposal, proposal_share = sentence.confidence_proposal({
    { text = "甲乙丙", score = 0 },
    { text = "甲丁戊", score = -10 }
}, 0.995)
if proposal ~= "甲乙" then
    fail("early-commit proposal did not retain the final candidate character: " .. proposal)
end
if proposal_share < 0.995 or proposal_share >= 0.99999 then
    fail("early-commit proposal returned an unexpected confidence share")
end
print("OK  early-commit proposal retains one character")

local strong_proposal, strong_share = sentence.confidence_proposal({
    { text = "甲乙丙", score = 0 },
    { text = "甲丁戊", score = -20 }
}, 0.995)
if strong_proposal ~= "甲乙" or strong_share < 0.99999 then
    fail("strong early-commit evidence was not identified")
end
if sentence.required_early_commit_history({
    { strong = true },
    { strong = true }
}) ~= 2 then
    fail("two consecutive strong generations did not select the two-key window")
end
if sentence.required_early_commit_history({
    { strong = true },
    { strong = false }
}) ~= 3 then
    fail("a weak generation did not retain the three-key window")
end
print("OK  early-commit observation window adapts between two and three keys")

local function fake_environment(early_commit)
    local properties = {}
    local commits = {}
    local context = { input = "", early_commit = early_commit }
    function context:get_property(key) return properties[key] or "" end
    function context:set_property(key, value) properties[key] = value end
    function context:get_option(name)
        return name == "tiger_sentence_early_commit" and self.early_commit
    end
    function context:is_composing() return self.input ~= "" end
    function context:push_input(value) self.input = self.input .. value end
    function context:clear() self.input = "" end
    function context:has_menu() return false end
    function context:confirm_current_selection() end
    local engine = { context = context }
    function engine:commit_text(value) commits[#commits + 1] = value end
    return { engine = engine }, context, properties, commits
end

local function fake_key(repr)
    local key = { value = repr }
    function key:release() return false end
    function key:repr() return self.value end
    function key:ctrl() return false end
    function key:alt() return false end
    function key:super() return false end
    return key
end

local early_sample = "jaefmonyftuderlmljgbmnvs"
local env_off, context_off, _, commits_off = fake_environment(false)
for index = 1, #early_sample do
    sentence.processor(fake_key(early_sample:sub(index, index)), env_off)
end
if #commits_off ~= 0 or context_off.input ~= early_sample then
    fail("default-off early commit changed the composition")
end
print("OK  disabled early commit leaves the composition unchanged")

local env_on, _, properties_on, commits_on = fake_environment(true)
for index = 1, #early_sample do
    sentence.processor(fake_key(early_sample:sub(index, index)), env_on)
end
if model.loaded and #commits_on == 0 then
    fail("enabled early commit did not commit a stable prefix with the real model")
elseif not model.loaded and #commits_on ~= 0 then
    fail("model-free equal scores produced a false high-confidence commit")
end
for i = 1, #commits_on do
    local count = 0
    for _ in commits_on[i]:gmatch("[^\128-\191]") do count = count + 1 end
    if count < 2 then fail("early commit emitted a one-character fragment") end
end
print(model.loaded and
    "OK  enabled early commit commits a stable prefix" or
    "OK  model-free equal scores do not fake high confidence")

env_on._tiger_sentence_transient = {
    proposal = "旧",
    stable = 1,
    evidence_raw = "abcde"
}
sentence.processor(fake_key("BackSpace"), env_on)
if env_on._tiger_sentence_transient.proposal ~= "" or
    env_on._tiger_sentence_transient.stable ~= 0 or
    env_on._tiger_sentence_transient.evidence_raw ~= "" then
    fail("backspace did not invalidate early-commit evidence")
end
if properties_on.tiger_sentence_confidence ~= nil then
    fail("transient confidence leaked into a Rime context property")
end
print("OK  backspace invalidates early-commit evidence")

local context_on = env_on.engine.context
context_on.input = "abcde"
sentence.processor(fake_key("Down"), env_on)
if not env_on._tiger_sentence_transient.suspended then
    fail("manual candidate navigation did not suspend early commit")
end
print("OK  manual candidate navigation suspends early commit")

context_on.input = ""
sentence.processor(fake_key("a"), env_on)
if env_on._tiger_sentence_transient.suspended or context_on.input ~= "a" then
    fail("new composition did not reset stale manual-navigation state")
end
print("OK  new composition clears stale navigation state")

local env_limit, context_limit = fake_environment(false)
for _ = 1, 129 do
    sentence.processor(fake_key("a"), env_limit)
end
if #context_limit.input ~= 128 then
    fail(string.format("raw input limit expected 128, got %d", #context_limit.input))
end
print("OK  raw input is capped at 128 characters")

local env_live, context_live, properties_live = fake_environment(false)
properties_live.tiger_sentence_committed_raw = "committed"
context_live.input = string.rep("a", 127)
sentence.processor(fake_key("a"), env_live)
sentence.processor(fake_key("a"), env_live)
if #context_live.input ~= 128 then
    fail("raw limit counted the already committed prefix instead of the live tail")
end
print("OK  raw input cap applies to the live tail only")

-- grow, append selector, backspace, retype letter
sentence.reset_decode_cache()
local path = "ueot"
for n = 1, #path do
    sentence.decode(path:sub(1, n))
end
local with_sel = sentence.decode("ueot;")
check_equal("append selector", with_sel, sentence.decode_full("ueot;"))
local back = sentence.decode("ueot")
check_equal("backspace selector", back, sentence.decode_full("ueot"))
local more = sentence.decode("ueottu")
check_equal("retype after selector", more, sentence.decode_full("ueottu"))

-- timing: incremental last-key vs full
local warmup = long_code:sub(1, #long_code - 1)
sentence.reset_decode_cache()
sentence.decode(warmup)
local repeats = 30
t0 = os.clock()
for _ = 1, repeats do
    sentence.reset_decode_cache()
    sentence.decode(warmup)
    sentence.decode(long_code)
end
local inc_ms = (os.clock() - t0) * 1000 / repeats

t0 = os.clock()
for _ = 1, repeats do
    sentence.decode_full(long_code)
end
local full_ms = (os.clock() - t0) * 1000 / repeats

print(string.format(
    "last-key %s  incremental=%.2fms  full=%.2fms",
    long_code,
    inc_ms,
    full_ms
))
print("all incremental checks matched full decode")
