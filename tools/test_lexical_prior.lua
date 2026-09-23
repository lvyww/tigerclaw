-- Compact lexical-prior format and decoder integration regressions.
local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
rime_api = { get_user_data_dir = function() return repo end }

local lexical = require("tiger_sentence_lexical")
local checks = 0
local function check(value, message)
    checks = checks + 1
    assert(value, message)
end

local path = repo .. "/rime/tiger_sentence/tiger_sentence.lexical.bin"
local model, load_error = lexical.load(path)
check(model ~= nil, load_error or "compact lexical model did not load")
check(model.entry_count == 50000, "unexpected lexical entry count")
check(model.bytes == 150032, "unexpected lexical byte size")
check(model.minimum_length == 2 and model.maximum_length == 4,
    "unexpected lexical length range")

local first, second = lexical.hashes("就是")
check(first == 1407972940 and second == 592017872,
    "Lua/Python lexical hash contract changed")
check(lexical.contains(model, "就是"), "known word 就是 is absent")
check(lexical.contains(model, "一个"), "known word 一个 is absent")
check(not lexical.contains(model, "一"), "one-character input bypassed length guard")
check(not lexical.contains(model, "这是一个测试词"), "long input bypassed length guard")
check(lexical.score(model, "就是") == 1.0, "single lexical edge score changed")
check(lexical.score(model, "就是一个") == 2.0,
    "overlap-aware lexical cover score changed")
check(lexical.score(model, "") == 0.0, "empty lexical score changed")

local malformed = repo .. "/_lexical_malformed.bin"
local handle = assert(io.open(malformed, "wb"))
assert(handle:write("TCSLEX01broken"))
handle:close()
local bad, bad_error = lexical.load(malformed)
check(bad == nil and bad_error ~= nil, "malformed lexical file was accepted")
local fallback, fallback_error = lexical.load_first({ malformed, path })
check(fallback ~= nil and fallback_error == nil,
    "load_first did not continue past a malformed earlier path")
assert(os.remove(malformed))

local fixture_path = repo .. "/sentence-fivegram-mobile.bin"
local created_fixture = false
local fixture_handle = io.open(fixture_path, "rb")
if fixture_handle then
    fixture_handle:close()
elseif string.pack then
    local make_fixture = dofile(repo .. "/tools/model_fixture.lua")
    make_fixture(fixture_path)
    created_fixture = true
end

local sentence = dofile(os.getenv("TIGER_SENTENCE_MODULE") or
    repo .. "/rime/tiger_sentence/lua/rime/tiger_sentence/tiger_sentence.lua")
local status = sentence.lexical_status()
check(status.loaded and status.entries == 50000 and status.bytes == 150032,
    "decoder did not expose the loaded lexical model")
local parameters = sentence.decoder_parameters()
check(parameters.canonical_code_reward == 2.0,
    "production canonical-code prior changed")
check(parameters.lexical_prior_weight == 0.1,
    "production lexical-prior weight changed")
check(parameters.canonical_isolation_factor == 0.0 and
    parameters.canonical_isolation_min_code_length == 4,
    "production rare-character guard changed")

local function find_candidate(raw, text)
    sentence.reset_decode_cache()
    for _, candidate in ipairs(sentence.decode_full(raw)) do
        if candidate.text == text then return candidate end
    end
    return nil
end

-- Lua 5.3+ gets a deterministic small KN fixture in the isolated regression
-- tree.  LuaJIT lacks string.pack and still exercises the format module and
-- no-model guard below.
local model_features_tested = sentence.model_status().loaded
if model_features_tested then
    sentence.set_decoder_parameters_for_test({
        canonical_code_reward = 0,
        lexical_prior_weight = 0,
        canonical_isolation_factor = 1,
        canonical_isolation_min_code_length = 4
    })
    sentence.reset_decode_cache()
    local legacy_pool = sentence.decode_full("jqtusotuqiueottu")._confidence_candidates
    sentence.set_decoder_parameters_for_test({
        canonical_code_reward = 2,
        lexical_prior_weight = 0.1,
        canonical_isolation_factor = 0,
        canonical_isolation_min_code_length = 4
    })
    sentence.reset_decode_cache()
    local prior_pool = sentence.decode_full("jqtusotuqiueottu")._confidence_candidates
    check(#legacy_pool == #prior_pool, "ranking priors changed the completed Beam size")
    for index = 1, #legacy_pool do
        check(legacy_pool[index].text == prior_pool[index].text and
            legacy_pool[index].confidence_score == prior_pool[index].confidence_score,
            "ranking priors changed the Beam candidates or confidence mass")
    end

    sentence.set_decoder_parameters_for_test({
        canonical_code_reward = 0,
        lexical_prior_weight = 0,
        canonical_isolation_factor = 1,
        canonical_isolation_min_code_length = 4
    })
    local code_before = find_candidate("lets", "旋")
    check(code_before ~= nil, "canonical prior probe candidate is absent")
    sentence.set_decoder_parameters_for_test({ canonical_code_reward = 2 })
    local code_after = find_candidate("lets", "旋")
    check(code_after ~= nil and code_after.code_score == 8,
        "four-key canonical code did not receive +8")
    check(math.abs(code_after.score - code_before.score - 8) < 1e-9,
        "canonical code reward changed by an unexpected amount")
    check(code_after.confidence_score == code_before.confidence_score,
        "canonical code reward leaked into confidence")

    local rare_before = find_candidate("wvfn", "徵")
    check(rare_before ~= nil and sentence.path_isolation_penalty(rare_before.path) == 2,
        "legacy rare-character penalty probe is invalid")
    sentence.set_decoder_parameters_for_test({ canonical_isolation_factor = 0 })
    local rare_after = find_candidate("wvfn", "徵")
    check(rare_after ~= nil and sentence.path_isolation_penalty(rare_after.path) == 0,
        "full four-key evidence did not protect a rare character")
    check(math.abs(rare_after.score - rare_before.score - 2) < 1e-9 and
        rare_after.confidence_score == rare_before.confidence_score,
        "rare-character protection changed confidence or the wrong score amount")

    local lexical_before = find_candidate("gyot", "就是")
    check(lexical_before ~= nil, "lexical reranker probe candidate is absent")
    sentence.set_decoder_parameters_for_test({ lexical_prior_weight = 0.1 })
    local lexical_after = find_candidate("gyot", "就是")
    check(lexical_after ~= nil and math.abs((lexical_after.lexical_score or 0) - 0.1) < 1e-9,
        "known two-character word did not receive its bounded lexical vote")
    check(math.abs(lexical_after.score - lexical_before.score - 0.1) < 1e-9 and
        lexical_after.confidence_score == lexical_before.confidence_score,
        "lexical prior changed confidence or the wrong score amount")

    sentence.set_decoder_parameters_for_test({
        canonical_code_reward = 2,
        lexical_prior_weight = 0.1,
        canonical_isolation_factor = 0,
        canonical_isolation_min_code_length = 4
    })

    local unlocked = find_candidate("letsot", "旋是")
    check(unlocked ~= nil, "locked-prefix ranking probe candidate is absent")
    sentence.reset_decode_cache()
    local locked_results = sentence.decode("letsot", false, "旋", {
        raw = "lets", text = "旋", boundaries = "4,3;"
    })
    local locked = nil
    for _, candidate in ipairs(locked_results) do
        if candidate.text == "旋是" then locked = candidate; break end
    end
    check(locked ~= nil and locked.score == unlocked.score and
        locked.confidence_score == unlocked.confidence_score and
        locked.code_score == unlocked.code_score and
        sentence.path_isolation_penalty(locked.path) ==
            sentence.path_isolation_penalty(unlocked.path),
        "locked-prefix replay lost a ranking-prior feature")
end

-- A lexical file is harmless without an n-gram model: the documented
-- rank-first fallback must not receive final-stage lexical scores.
sentence.ensure_lexicon(nil)
sentence.set_model_enabled(false)
for _, candidate in ipairs(sentence.decode_full("ueot")) do
    check((candidate.lexical_score or 0) == 0,
        "lexical prior changed no-model fallback")
end

-- Deleting inside an already buffered multi-character edge keeps its opaque
-- raw boundary. It no longer has canonical evidence, but must remain usable as
-- locked context when typing resumes.
sentence.reset_decode_cache()
local opaque_lock = sentence.decode("ccot", false, "燃", {
    raw = "cc", text = "燃", boundaries = "2,3;"
})
check(opaque_lock[1] and opaque_lock[1].text == "燃是",
    "partially deleted multi-character lock rejected resumed input")

if created_fixture then assert(os.remove(fixture_path)) end

print(string.format(
    '{"status":"passed","lexical_checks":%d,"entries":%d,"bytes":%d,"model_features":%s}',
    checks, model.entry_count, model.bytes, tostring(model_features_tested)))
