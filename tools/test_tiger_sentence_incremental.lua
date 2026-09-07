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

sentence.ensure_lexicon(nil)
local status = sentence.data_status()
if not status.built then
    fail("lexicon was not built from the plain-text data files")
end
if status.codes_count < 13000 then
    fail(string.format("code table too small: %d codes", status.codes_count))
end
if #status.errors > 0 then
    fail("data load errors: " .. table.concat(status.errors, "; "))
end
if status.ranks_count ~= 20000 then
    fail(string.format("expected 20000 character ranks, got %d", status.ranks_count))
end
if not status.isolation_enabled then
    fail("isolation penalty disabled although ranks were loaded")
end
if status.whitelist_count ~= 119 then
    fail(string.format("expected 119 whitelist characters, got %d",
        status.whitelist_count))
end
sentence.reset_decode_cache()
local ldac = sentence.decode("ldac")
if not ldac[1] or ldac[1].text ~= "燕" then
    fail("plain-text data did not keep 燕 under preferred code ldac")
end
print(string.format(
    "OK  plain-text data files loaded (%d codes, %d ranks, %d whitelist)",
    status.codes_count, status.ranks_count, status.whitelist_count))

sentence.reset_decode_cache()
local standalone_rl = sentence.decode("rl")
local has_duifang = false
for i = 1, #standalone_rl do
    if standalone_rl[i].text == "对方" then has_duifang = true end
end
if not has_duifang then
    fail("standalone single-edge rl did not expose its non-first candidate")
end
local segmented_rl = sentence.decode("rlrl")
for i = 1, #segmented_rl do
    if segmented_rl[i].text == "对方对方" then
        fail("segmented rlrl reused an implicit non-first rank")
    end
end
print("OK  implicit non-first ranks are limited to one whole-input edge")

local captured_rl = sentence.capture_empty_code_candidate("rl", "")
if not captured_rl or captured_rl.candidate_text ~= "了" then
    fail("implicit non-first rl candidate blocked empty-code primary candidate")
end
print("OK  implicit non-first ranks do not block empty-code auto commit")

sentence.reset_decode_cache()
local lets = sentence.decode_full("lets")
local lets_single = nil
for i = 1, #lets do
    if lets[i].text == "旋" then
        lets_single = lets[i]
        break
    end
end
if not lets_single or not lets[1] or lets[1].text ~= "旋" then
    fail("optimal whole-input single-character reward did not restore 旋 for lets")
end
if math.abs((lets_single.score - lets_single.confidence_score) - 5.0) > 1e-9 then
    fail("whole-input single-character reward was not exactly 5.0 or leaked into confidence")
end
print("OK  optimal whole-input single-character reward is +5.0 and ranking-only")

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

-- The frontend first asks the translator for ordinary candidates, then asks
-- the processor for confidence metadata on the exact same composition before
-- accepting the next key. Long ambiguous input must reuse that cached lattice,
-- and a truncated pool must not wastefully expose unusable prefix evidence.
local ambiguous_40 = "nnczggqrrjrrltwwbwkedmkswgjgiuapnphbszbp"
sentence.reset_decode_cache()
local ordinary_40 = {}
for n = 1, #ambiguous_40 do
    ordinary_40 = sentence.decode(ambiguous_40:sub(1, n), false)
end
local evidence_40 = sentence.decode(ambiguous_40, true)
check_visible_equal("same-raw evidence cache at 40 keys", ordinary_40, evidence_40)
local evidence_metadata_40 = evidence_40.early_commit_evidence or {}
if not evidence_metadata_40.confidence_truncated then
    fail("40-key ambiguity sample did not exercise truncated confidence")
end
if #(evidence_metadata_40.prefixes or {}) ~= 0 or
    (evidence_metadata_40.proposal or "") ~= "" then
    fail("truncated confidence retained unusable early-commit evidence")
end
print("OK  40-key truncated confidence reuses the lattice and skips prefix materialization")

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
if (conditioned.early_commit_evidence or {}).proposal ~= "的是" then
    fail("conditioned early-commit evidence did not retain the committed prefix")
end
print("OK  early-commit evidence is conditioned on committed text")

-- Independent per-(text prefix, raw boundary) evidence, mirroring the
-- Windows Core tracker inputs. Two duplicate candidates agree on the whole
-- word 甲乙 at raw boundary 2 while a negligible path crosses that boundary
-- at 3.
local function fake_boundary_chain(text_lengths, raw_lengths)
    local chain = nil
    for i = #text_lengths, 1, -1 do
        chain = {
            text_length = text_lengths[i],
            raw_length = raw_lengths[i],
            previous = chain
        }
    end
    return chain
end

local prefix_pool = {
    {
        text = "甲乙",
        confidence_score = 0.0,
        path = fake_boundary_chain({ #("甲乙") }, { 2 })
    },
    {
        text = "甲乙",
        confidence_score = math.log(0.0001),
        path = fake_boundary_chain({ #("甲乙") }, { 2 })
    },
    {
        text = "甲丁",
        confidence_score = math.log(1e-7),
        path = fake_boundary_chain({ #("甲"), #("甲丁") }, { 1, 3 })
    }
}
local prefixes = sentence.build_prefix_evidence(prefix_pool)
local stable = sentence.find_prefix_evidence(prefixes, "甲乙", 2)
if not stable then
    fail("per-(text, boundary) evidence lost the closed prefix 甲乙@2")
end
if not stable.boundary_closed then
    fail("boundary closed by weighted mass was not recognized")
end
if stable.share < 0.9999 then
    fail("prefix share did not merge duplicate candidates")
end
local crossing = sentence.find_prefix_evidence(prefixes, "甲丁", 3)
if not crossing or crossing.boundary_closed then
    fail("a negligible crossing boundary must stay open")
end
print("OK  prefix evidence keys share and closed boundaries match Windows")

if sentence.common_text_prefix("上午", "下午") ~= "" then
    fail("common_text_prefix returned a partial-character stem for 上午/下午")
end
if sentence.common_text_prefix("新人上", "新人下") ~= "新人" then
    fail("common_text_prefix lost the shared stem 新人")
end
local noon_prefixes = {
    { text = "下午", raw_length = 2, share = 0.90, boundary_closed = true }
}
if sentence.prefix_contradicted({ text = "上午", raw_length = 2 }, noon_prefixes) then
    fail("上午/下午 must not form a shared-stem contradiction")
end
local fork_text_prefixes = {
    { text = "新人下", raw_length = 4, share = 0.96, boundary_closed = true }
}
if not sentence.prefix_contradicted(
        { text = "新人上", raw_length = 4 }, fork_text_prefixes) then
    fail("a stronger fork after the shared stem 新人 must contradict the tracker")
end
print("OK  shared-stem contradictions compare whole Unicode characters")

local fork_prefixes = {
    { text = "甲乙", raw_length = 2, share = 0.90, boundary_closed = true },
    { text = "甲丁", raw_length = 2, share = 0.96, boundary_closed = true }
}
if not sentence.prefix_contradicted(
        { text = "甲乙", raw_length = 2 }, fork_prefixes) then
    fail("a stronger fork after a shared stem must contradict the tracker")
end
local supported_trackers = {
    ["甲乙" .. string.char(31) .. "2"] = {
        text = "甲乙",
        raw_length = 2,
        evidence_count = 1,
        strong_count = 0,
        gap_count = 0,
        last_share = 0.90
    }
}
local retained = sentence.retain_trackers_without_counting(
    supported_trackers, fork_prefixes)
if next(retained) ~= nil then
    fail("a contradicted tracker survived a comparison-only gap")
end
local stale_prefixes = {
    { text = "甲乙", raw_length = 2, share = 0.99, boundary_closed = false }
}
retained = sentence.retain_trackers_without_counting(
    supported_trackers, stale_prefixes)
local kept = retained["甲乙" .. string.char(31) .. "2"]
if not kept or kept.gap_count ~= 1 then
    fail("a supported tracker did not survive a comparison-only gap")
end
print("OK  comparison-only gaps follow contradiction and support rules")

local strong_eligible = {
    { text = "甲", confidence_score = 0.0 },
    { text = "乙", confidence_score = -20.0 }
}
if not sentence.strong_empty_code_candidate(
        strong_eligible[1], strong_eligible, { text = "甲" }, false) then
    fail("strong empty-code accept rejected an untruncated dominant top")
end
if sentence.strong_empty_code_candidate(
        strong_eligible[1], strong_eligible, { text = "甲" }, true) then
    fail("a truncated candidate pool must reject the strong empty-code accept")
end
print("OK  strong empty-code accept refuses truncated candidate pools")

local unique_probe = sentence.lexicon_probe("vp")
if not unique_probe or not unique_probe[1] then fail("missing uniqueness fixture vp") end
if sentence.has_complete_candidate("vp", "", unique_probe[1].t, true) then
    fail("non-first whole-code candidates must not create group ambiguity")
end
if not sentence.has_complete_candidate("vp", "", "not-the-candidate", true) then
    fail("alternative group output was not found")
end
if sentence.has_complete_candidate("vp", "not-a-prefix", "not-the-candidate", true) then
    fail("alternative group query ignored the committed prefix")
end
print("OK  empty-code uniqueness query excludes output text, not code paths")

local function fake_environment(early_commit, duplicate_single)
    local properties = {}
    local commits = {}
    local context = {
        input = "",
        early_commit = early_commit,
        duplicate_single = duplicate_single ~= false,
        full_shape = false,
        input_mutations = 0,
        clear_calls = 0
    }
    function context:get_property(key) return properties[key] or "" end
    function context:set_property(key, value) properties[key] = value end
    function context:get_option(name)
        if name == "tiger_sentence_early_commit" then return self.early_commit end
        if name == "tiger_sentence_allow_duplicate_single" then
            return self.duplicate_single
        end
        if name == "full_shape" then return self.full_shape end
        return false
    end
    context.duplicate_single = duplicate_single ~= false
    function context:is_composing() return self.input ~= "" end
    function context:push_input(value) self.input = self.input .. value end
    function context:set_input(value)
        self.input_mutations = self.input_mutations + 1
        self.input = value
    end
    function context:clear()
        self.clear_calls = self.clear_calls + 1
        self.input = ""
    end
    local menu = { count = 0 }
    function menu:candidate_count() return self.count end
    local segment = { selected_index = 0, menu = menu }
    local composition = {}
    function composition:empty() return menu.count == 0 end
    function composition:back() return segment end
    context.composition = composition
    function context:has_menu() return menu.count > 0 end
    context.select_calls = 0
    function context:select(index)
        self.select_calls = self.select_calls + 1
        segment.selected_index = index
        return true
    end
    context.highlight_calls = 0
    function context:highlight(index)
        self.highlight_calls = self.highlight_calls + 1
        segment.selected_index = index
        return true
    end
    function context:confirm_current_selection() end
    local engine = { context = context }
    function engine:commit_text(value) commits[#commits + 1] = value end
    return { engine = engine }, context, properties, commits, menu, segment
end

local function fake_key(repr)
    local key = { value = repr }
    function key:release() return false end
    function key:repr() return self.value end
    function key:ctrl() return false end
    function key:alt() return false end
    function key:super() return false end
    function key:shift() return false end
    return key
end

local env_idle_punct, context_idle_punct = fake_environment(false)
if sentence.processor(fake_key("semicolon"), env_idle_punct) ~= 2 or
    sentence.processor(fake_key("apostrophe"), env_idle_punct) ~= 2 or
    context_idle_punct.input ~= "" then
    fail("idle semicolon/apostrophe did not pass through to punctuator")
end
local punctuation_schema = assert(io.open(
    repo .. "/rime/tiger_sentence/tiger_sentence.schema.yaml", "rb"))
local punctuation_schema_content = punctuation_schema:read("*a")
punctuation_schema:close()
if not punctuation_schema_content:find("import_preset: symbols", 1, true) then
    fail("schema does not import the editable symbols.yaml punctuation table")
end
local symbols_file = assert(io.open(
    repo .. "/rime/tiger_sentence/symbols.yaml", "rb"))
local symbols_content = symbols_file:read("*a")
symbols_file:close()
for _, mapping in ipairs({
        '",": { commit: ， }',
        '".": { commit: 。 }',
        '"/": { commit: 、 }',
        '";": { commit: ； }',
        '"?": { commit: ？ }'
    }) do
    if not symbols_content:find(mapping, 1, true) then
        fail("default symbols.yaml is missing direct punctuation mapping " .. mapping)
    end
end
print("OK  idle punctuation is delegated to the symbols.yaml preset")

local env_tab, context_tab, _, _, menu_tab, segment_tab = fake_environment(false)
context_tab.input = "rl"
menu_tab.count = 3
if sentence.processor(fake_key("Tab"), env_tab) ~= 1 or
    segment_tab.selected_index ~= 1 then
    fail("Tab did not highlight the next candidate")
end
sentence.processor(fake_key("Tab"), env_tab)
sentence.processor(fake_key("Tab"), env_tab)
if segment_tab.selected_index ~= 0 then
    fail("Tab did not wrap from the last candidate to the first")
end
if sentence.processor(fake_key("ISO_Left_Tab"), env_tab) ~= 1 or
    segment_tab.selected_index ~= 2 then
    fail("Shift+Tab did not wrap from the first candidate to the last")
end
if context_tab.select_calls ~= 0 or context_tab.highlight_calls ~= 4 then
    fail("Tab called context:select and could commit a sentence candidate")
end
local schema_file = assert(io.open(
    repo .. "/rime/tiger_sentence/tiger_sentence.schema.yaml", "rb"))
local schema_content = schema_file:read("*a")
schema_file:close()
if not schema_content:find("accept: Tab, send: Down", 1, true) or
    not schema_content:find("accept: Shift+Tab, send: Up", 1, true) then
    fail("schema does not route Tab navigation through key_binder")
end
print("OK  Tab highlights candidates cyclically without selecting them")

local env_empty, context_empty, properties_empty, commits_empty = fake_environment(true)
sentence.processor(fake_key("v"), env_empty)
sentence.processor(fake_key("p"), env_empty)
if #commits_empty ~= 0 or context_empty.input ~= "vp" then
    fail("empty-code auto commit fired before the unique code became empty")
end
sentence.processor(fake_key("a"), env_empty)
if #commits_empty ~= 1 or commits_empty[1] ~= "刘" or
    context_empty.input ~= "a" then
    fail("empty-code auto commit did not commit 刘 and retain the new code")
end
print("OK  empty-code auto commit retains the newly typed code")
if not env_empty._tiger_sentence_transient.continuation_after_auto_commit then
    fail("empty-code auto commit did not mark the retained composition as continuation")
end
if properties_empty.tiger_sentence_committed ~= "vp\t刘" then
    fail("empty-code auto commit discarded the committed sentence context")
end
if context_empty.clear_calls ~= 0 or context_empty.input_mutations ~= 1 then
    fail("empty-code auto commit must restore the composition atomically")
end
print("OK  empty-code auto commit preserves context and restores input atomically")

-- The committed prefix migrates from the legacy two-property format once.
-- A navigation key reads the state without starting a new composition.
local env_mig, _, properties_mig = fake_environment(false)
properties_mig.tiger_sentence_committed_raw = "vp"
properties_mig.tiger_sentence_committed_text = "刘"
sentence.processor(fake_key("Down"), env_mig)
if properties_mig.tiger_sentence_committed ~= "vp\t刘" or
    (properties_mig.tiger_sentence_committed_raw or "") ~= "" or
    (properties_mig.tiger_sentence_committed_text or "") ~= "" then
    fail("legacy committed properties were not migrated to the combined property")
end
print("OK  legacy committed properties migrate to the combined property")

-- Idle digits commit directly and arm the decimal-point follow-up,
-- mirroring InputMethodEngine._dotAfterDigitArmed.
local env_dig, context_dig, _, commits_dig = fake_environment(true)
sentence.processor(fake_key("3"), env_dig)
if #commits_dig ~= 1 or commits_dig[1] ~= "3" or context_dig.input ~= "" then
    fail("idle digit did not commit directly")
end
sentence.processor(fake_key("period"), env_dig)
if #commits_dig ~= 2 or commits_dig[2] ~= "." or context_dig.input ~= "" then
    fail("period after digit did not commit as an ASCII decimal point")
end
sentence.processor(fake_key("5"), env_dig)
sentence.processor(fake_key("7"), env_dig)
if #commits_dig ~= 4 or commits_dig[4] ~= "7" then
    fail("digits stopped committing after a decimal point")
end
-- A comma passes through to the punctuator instead of being intercepted.
sentence.processor(fake_key(","), env_dig)
if #commits_dig ~= 4 or context_dig.input ~= "" then
    fail("comma after digits should pass through to the punctuator")
end
-- The arm is consumed by any non-digit key: a second period goes back to
-- the punctuator (Chinese 。 via symbols.yaml).
sentence.processor(fake_key("1"), env_dig)
sentence.processor(fake_key("period"), env_dig)
sentence.processor(fake_key("period"), env_dig)
if #commits_dig ~= 6 or context_dig.input ~= "" then
    fail("the second period after a decimal point should pass through")
end
print("OK  idle digits commit and the following period becomes a decimal point")

-- Full-shape digits still arm the half-width decimal point.
local env_fdig, context_fdig, _, commits_fdig = fake_environment(true)
context_fdig.full_shape = true
sentence.processor(fake_key("3"), env_fdig)
sentence.processor(fake_key("period"), env_fdig)
if #commits_fdig ~= 2 or commits_fdig[1] ~= "３" or commits_fdig[2] ~= "." then
    fail("full-shape digit did not arm the half-width decimal point")
end
print("OK  full-shape digits arm the half-width decimal point")

-- Numpad digits and the numpad decimal key behave like their main-row peers.
local env_kp, _, _, commits_kp = fake_environment(true)
sentence.processor(fake_key("KP_5"), env_kp)
sentence.processor(fake_key("KP_Decimal"), env_kp)
if #commits_kp ~= 2 or commits_kp[1] ~= "5" or commits_kp[2] ~= "." then
    fail("numpad digit/decimal did not commit as 5.")
end
print("OK  numpad digits and decimal key mirror the main row")

-- Letters after a digit still start a normal composition.
local env_ldig, context_ldig, _, commits_ldig = fake_environment(true)
sentence.processor(fake_key("1"), env_ldig)
sentence.processor(fake_key("v"), env_ldig)
if #commits_ldig ~= 1 or context_ldig.input ~= "v" then
    fail("a letter after an idle digit did not start a composition")
end
print("OK  letters after idle digits start a normal composition")

-- The early-commit switch gates empty-code auto commit too; there is no
-- separate option for it.
local env_ecoff, context_ecoff, _, commits_ecoff = fake_environment(false)
sentence.processor(fake_key("v"), env_ecoff)
sentence.processor(fake_key("p"), env_ecoff)
sentence.processor(fake_key("a"), env_ecoff)
sentence.processor(fake_key("b"), env_ecoff)
if #commits_ecoff ~= 0 or context_ecoff.input ~= "vpab" or
    env_ecoff._tiger_sentence_transient.empty_code_pending then
    fail("empty-code auto commit fired while the early-commit switch was off")
end
print("OK  empty-code auto commit obeys the early-commit switch")

context_empty.input = "rl"
local yielded = {}
local old_candidate, old_yield = Candidate, yield
Candidate = function(_, _, _, text, _)
    return { text = text }
end
yield = function(candidate)
    yielded[#yielded + 1] = candidate.text
end
sentence.translator("rl", { start = 0, _end = 2 }, env_empty)
Candidate, yield = old_candidate, old_yield
if #yielded ~= 1 or yielded[1] ~= "了" then
    fail("automatic-commit continuation rl exposed an implicit non-first candidate")
end
print("OK  automatic-commit continuation uses first ranks only")

-- 保留最少编码数量 also gates empty-code auto commit. Every two-letter
-- combination is a valid code in this table, so a retained floor of two can
-- only defer; a floor of one commits on the first appended letter.
local env_ret, context_ret, _, commits_ret = fake_environment(true)
env_ret.engine.schema = {
    config = {
        get_int = function(_, key)
            if key == "tiger_sentence/min_retained_raw_length" then return 2 end
            return nil
        end
    }
}
sentence.processor(fake_key("v"), env_ret)
sentence.processor(fake_key("p"), env_ret)
sentence.processor(fake_key("a"), env_ret)
if #commits_ret ~= 0 or context_ret.input ~= "vpa" then
    fail("min retained raw length did not defer the empty-code commit")
end
if not env_ret._tiger_sentence_transient.empty_code_pending then
    fail("deferred empty-code commit dropped its pending candidate")
end
sentence.processor(fake_key("b"), env_ret)
if #commits_ret ~= 0 or context_ret.input ~= "vpab" then
    fail("a newly complete lexicon path should clear the empty-code pending")
end

local env_ret1, context_ret1, _, commits_ret1 = fake_environment(true)
env_ret1.engine.schema = {
    config = {
        get_int = function(_, key)
            if key == "tiger_sentence/min_retained_raw_length" then return 1 end
            return nil
        end
    }
}
sentence.processor(fake_key("v"), env_ret1)
sentence.processor(fake_key("p"), env_ret1)
sentence.processor(fake_key("a"), env_ret1)
if #commits_ret1 ~= 1 or commits_ret1[1] ~= "刘" or context_ret1.input ~= "a" then
    fail("empty-code commit did not honor a retained raw length of one")
end
print("OK  empty-code auto commit honors min_retained_raw_length")

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
    if count < 1 then fail("early commit emitted an empty fragment") end
end
-- The Windows tracker algorithm intentionally allows confident single
-- characters (今/天 here); only empty chunks are invalid.
print(model.loaded and
    "OK  enabled early commit commits stable prefixes" or
    "OK  model-free equal scores do not fake high confidence")
if env_on.engine.context.clear_calls ~= 0 or
    env_on.engine.context.input_mutations ~= #commits_on then
    fail("probabilistic early commit must restore the composition atomically")
end
print("OK  probabilistic early commit restores input atomically")

env_on._tiger_sentence_transient = {
    trackers = {
        ["旧" .. string.char(31) .. "2"] = {
            text = "旧",
            raw_length = 2,
            evidence_count = 1,
            strong_count = 0,
            gap_count = 0,
            last_share = 0.996
        }
    },
    last_seen_raw = "abcde",
    last_auto_commit_raw_length = 0,
    suspended = false,
    continuation_after_auto_commit = false
}
sentence.processor(fake_key("BackSpace"), env_on)
if next(env_on._tiger_sentence_transient.trackers or {}) ~= nil or
    (env_on._tiger_sentence_transient.last_seen_raw or "") ~= "" then
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
properties_live.tiger_sentence_committed = "committed\t"
context_live.input = string.rep("a", 127)
sentence.processor(fake_key("a"), env_live)
sentence.processor(fake_key("a"), env_live)
if #context_live.input ~= 128 then
    fail("raw limit counted the already committed prefix instead of the live tail")
end
print("OK  raw input cap applies to the live tail only")

-- 允许单字重码组句: segmented paths compete non-first single characters by
-- language-model score; whole-input single edges keep lexicon-rank order.
sentence.reset_decode_cache()
sentence.set_allow_duplicate_single(nil)
local duplicate_on = sentence.decode("gyygch")
local has_yanggao = false
for i = 1, #duplicate_on do
    if duplicate_on[i].text == "羊羔" then has_yanggao = true end
end
if not has_yanggao then
    fail("segmented gyygch lost the non-first single-character path 羊羔")
end
sentence.reset_decode_cache()
if sentence.decode("gch")[1].text ~= "赤" then
    fail("whole-input single edge gch did not keep rank-first order")
end
sentence.set_allow_duplicate_single({
    get_option = function(_, name)
        if name == "tiger_sentence_allow_duplicate_single" then return false end
        return true
    end
})
sentence.reset_decode_cache()
local duplicate_off = sentence.decode("gyygch")
for i = 1, #duplicate_off do
    if duplicate_off[i].text == "羊羔" then
        fail("duplicate-single switch off still exposed 羊羔 implicitly")
    end
end
sentence.set_allow_duplicate_single(nil)
sentence.reset_decode_cache()
print("OK  duplicate single characters compete only while the switch is on")

-- No-model fallback: lexicon rank first, then fewer edges, then score, so
-- direct code entries are not beaten by same-rank concatenations that only
-- win on the per-character +2.0 reward.
sentence.set_model_enabled(false)
sentence.reset_decode_cache()
if sentence.decode("ldac")[1].text ~= "燕" then
    fail("no-model decode lost the direct code entry 燕 under ldac")
end
sentence.reset_decode_cache()
if sentence.decode("gch")[1].text ~= "赤" then
    fail("no-model decode lost the rank-first single edge 赤 under gch")
end
for _, no_model_sample in ipairs({ "ot", "ueot", "ldac", "ueot;", "jqtusotu" }) do
    sentence.reset_decode_cache()
    local incremental = sentence.decode(no_model_sample)
    local full = sentence.decode_full(no_model_sample)
    if not sentence.results_equal(incremental, full) then
        fail("no-model incremental decode diverged from full for " .. no_model_sample)
    end
end
sentence.set_model_enabled(true)
sentence.reset_decode_cache()
print("OK  no-model fallback keeps direct code entries first")

-- Windows probabilistic early-commit regressions, evaluated end to end with
-- the real model: committed prefixes and the remaining visible candidates.
local function run_early_commit_sample(sample)
    local env, context, _, commit_log = fake_environment(true)
    for index = 1, #sample do
        sentence.processor(fake_key(sample:sub(index, index)), env)
    end
    local joined = table.concat(commit_log)
    local yielded = {}
    local old_candidate, old_yield = Candidate, yield
    Candidate = function(_, _, _, text, _) return { text = text } end
    yield = function(candidate) yielded[#yielded + 1] = candidate.text end
    sentence.translator(context.input, { start = 0, _end = #context.input }, env)
    Candidate, yield = old_candidate, old_yield
    return joined, yielded, env, context
end

if model.loaded then
    local joined, yielded = run_early_commit_sample("awmenamcunta")
    if joined ~= "买" then
        fail("awmenamcunta did not probabilistically commit 买 first: " .. joined)
    end
    if yielded[1] ~= "椟还珠" then
        fail("awmenamcunta continuation lost 椟还珠 as top candidate: " ..
            tostring(yielded[1]))
    end
    print("OK  awmenamcunta commits 买 and keeps 椟还珠 visible first")

    joined, yielded = run_early_commit_sample("uriczwxmjou")
    if joined:find("可佛", 1, true) then
        fail("uriczwxmjou committed the transient 可佛: " .. joined)
    end
    print("OK  uriczwxmjou never commits the transient 可佛")

    joined, yielded = run_early_commit_sample("nuusvbbhoi")
    if joined ~= "左手" or yielded[1] ~= "匕首" then
        fail(string.format("nuusvbbhoi became %s + %s", joined, tostring(yielded[1])))
    end
    print("OK  nuusvbbhoi finishes as 左手匕首")

    joined, yielded = run_early_commit_sample("iejryfenahbmsp")
    if joined .. (yielded[1] or "") ~= "新人上午来面试" then
        fail(string.format("iejryfenahbmsp became %s + %s", joined, tostring(yielded[1])))
    end
    if (joined .. (yielded[1] or "")):find("上窦", 1, true) then
        fail("iejryfenahbmsp entered the 新人上窦 continuation")
    end
    print("OK  iejryfenahbmsp finishes as 新人上午来面试")
else
    print("SKIP Windows early-commit regressions (no real model)")
end

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

local performance = sentence.performance_status()
if not performance.current or
    type(performance.current.decode_calls) ~= "number" or
    performance.current.decode_calls <= 0 or
    type(performance.current.decode_total_ms) ~= "number" or
    performance.current.decode_total_ms < 0 then
    fail("composition performance counters were not updated")
end
print("OK  composition performance counters are available")

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

-- Plain-text data editing: filters react to the high-frequency limit, and a
-- foreign code table imports through the same txt format.
local default_codes_count = status.codes_count
sentence.apply_high_freq_limit(0)
local unlimited = sentence.data_status()
if unlimited.high_freq_limit ~= 0 or unlimited.codes_count <= default_codes_count then
    fail("high_freq_limit=0 did not lift the optimal-code restriction")
end
sentence.apply_high_freq_limit(1500)
if sentence.data_status().codes_count ~= default_codes_count then
    fail("high_freq_limit=1500 did not restore the default index")
end
print("OK  high_freq_limit changes rebuild the index immediately")

local original_user_dir = rime_api.get_user_data_dir
local import_dir = repo .. "/rime/tiger_sentence/.test_import"
os.execute("mkdir -p '" .. import_dir .. "'")
local import_codes = io.open(import_dir .. "/tiger_sentence.codes.txt", "wb")
import_codes:write(
    "# minimal imported table\n",
    "的\td\r\n",
    "好\th\n",
    "你\tn\n",
    "你们\tnm\r\n",
    "甲\tja\n",
    "乙\tja\n",
    "BAD\tu1\n"
)
import_codes:close()
rime_api.get_user_data_dir = function()
    return import_dir
end
sentence.apply_high_freq_limit(1500)
local imported = sentence.data_status()
if imported.codes_path ~= import_dir .. "/tiger_sentence.codes.txt" then
    fail("imported table was not preferred from the user directory")
end
if imported.codes_entries ~= 6 then
    fail(string.format("imported table should keep 6 entries, got %d",
        imported.codes_entries))
end
if imported.isolation_enabled then
    fail("missing ranks file must disable the isolation penalty")
end
sentence.reset_decode_cache()
local nm = sentence.decode("nm")
if not nm[1] or nm[1].text ~= "你们" then
    fail("imported table did not decode 你们 from nm")
end
local ja = sentence.decode("ja")
if not ja[1] or ja[1].text ~= "甲" or not ja[2] or ja[2].text ~= "乙" then
    fail("imported table lost line-order ranks for shared code ja")
end
rime_api.get_user_data_dir = original_user_dir
os.execute("rm -rf '" .. import_dir .. "'")
sentence.apply_high_freq_limit(1500)
local restored = sentence.data_status()
if restored.codes_count ~= default_codes_count or
    not restored.isolation_enabled then
    fail("default pack data was not restored after the import test")
end
print("OK  foreign code table imports via plain-text files")

print("all incremental checks matched full decode")
