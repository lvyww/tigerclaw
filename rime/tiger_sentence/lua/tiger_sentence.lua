-- TigerClaw-style sentence lattice for Rime, with optional Lua KN scoring.
local data = require("tiger_sentence_data")
local lexicon = {codes = data.codes, lengths = data.lengths}
-- Pure Lua Kneser-Ney V2 reader. TCSKNM02 uses paged I/O and a bounded cache.
local BOS = "\2"
local EOS = "\3"
local SHIFT = 2097152
local MOBILE_HEADER_SIZE = 104
local MOBILE_CACHE_BYTES = 8 * 1024 * 1024
local CONTEXT_CACHE_ENTRIES = 16384
local ISOLATION_CACHE_ENTRIES = 8192

local performance = {
    decode_calls = 0,
    decode_total_ms = 0.0,
    decode_max_ms = 0.0,
    page_misses = 0,
    page_bytes = 0,
    early_evidence_builds = 0,
    isolation_hits = 0,
    isolation_misses = 0,
    last = nil
}

local function record_decode(started)
    local elapsed = (os.clock() - started) * 1000
    performance.decode_calls = performance.decode_calls + 1
    performance.decode_total_ms = performance.decode_total_ms + elapsed
    performance.decode_max_ms = math.max(performance.decode_max_ms, elapsed)
end

local function finish_performance_sample()
    if performance.decode_calls == 0 then return end
    local sample = {
        decode_calls = performance.decode_calls,
        decode_total_ms = performance.decode_total_ms,
        decode_max_ms = performance.decode_max_ms,
        page_misses = performance.page_misses,
        page_bytes = performance.page_bytes,
        early_evidence_builds = performance.early_evidence_builds,
        isolation_hits = performance.isolation_hits,
        isolation_misses = performance.isolation_misses
    }
    performance.last = sample
    performance.decode_calls = 0
    performance.decode_total_ms = 0.0
    performance.decode_max_ms = 0.0
    performance.page_misses = 0
    performance.page_bytes = 0
    performance.early_evidence_builds = 0
    performance.isolation_hits = 0
    performance.isolation_misses = 0
end

local kn_reader = {
    BOS = BOS,
    EOS = EOS
}

local function file_exists(path)
    local file = io.open(path, "rb")
    if not file then
        return false
    end
    file:close()
    return true
end

function kn_reader.candidate_paths()
    local mobile_paths = {}
    local legacy_paths = {}
    if rime_api then
        local user_dir = rime_api.get_user_data_dir and rime_api.get_user_data_dir()
        local shared_dir = rime_api.get_shared_data_dir and rime_api.get_shared_data_dir()
        if user_dir and user_dir ~= "" then
            mobile_paths[#mobile_paths + 1] = user_dir .. "/models/sentence-ngram-mobile.bin"
            mobile_paths[#mobile_paths + 1] = user_dir .. "/sentence-ngram-mobile.bin"
            legacy_paths[#legacy_paths + 1] = user_dir .. "/models/sentence-ngram-v2.bin"
            legacy_paths[#legacy_paths + 1] = user_dir .. "/sentence-ngram-v2.bin"
        end
        if shared_dir and shared_dir ~= "" then
            mobile_paths[#mobile_paths + 1] = shared_dir .. "/models/sentence-ngram-mobile.bin"
            legacy_paths[#legacy_paths + 1] = shared_dir .. "/models/sentence-ngram-v2.bin"
        end
    end
    mobile_paths[#mobile_paths + 1] = "C:/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin"
    mobile_paths[#mobile_paths + 1] = "/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin"
    legacy_paths[#legacy_paths + 1] = "C:/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin"
    legacy_paths[#legacy_paths + 1] = "/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin"
    for _, path in ipairs(legacy_paths) do
        mobile_paths[#mobile_paths + 1] = path
    end
    return mobile_paths
end

local function scalar(token)
    if not token or token == "" then
        return 0
    end
    if token == BOS or token == EOS then
        return string.byte(token)
    end
    return utf8.codepoint(token)
end

local function pack2(first, second)
    -- Avoid parser-level bitwise syntax; model scoring still requires the
    -- string.unpack and utf8 APIs supplied by Lua 5.3+ Rime builds.
    return first * SHIFT + (second % SHIFT)
end

local function load_legacy(path)
    local file = assert(io.open(path, "rb"), "cannot open n-gram: " .. path)
    local data = file:read("*a")
    file:close()
    assert(data and #data > 32, "empty n-gram: " .. path)
    assert(data:sub(1, 8) == "TCSKNM01", "not a TCSKNM01 model: " .. path)

    local function i32(off)
        return (string.unpack("<i4", data, off + 1))
    end
    local function u64(off)
        return (string.unpack("<I8", data, off + 1))
    end
    local function f32(off)
        return (string.unpack("<f", data, off + 1))
    end

    assert(i32(8) == 1, "unsupported n-gram version")
    local uni_count = i32(12)
    local pos = 16
    local uni_off = pos
    pos = pos + uni_count * 8
    local bi_count = u64(pos)
    pos = pos + 8
    local bi_off = pos
    pos = pos + bi_count * 12
    local bi_ctx_count = i32(pos)
    pos = pos + 4
    local bi_ctx_off = pos
    pos = pos + bi_ctx_count * 8
    local tri_count = u64(pos)
    pos = pos + 8
    local tri_off = pos
    pos = pos + tri_count * 12
    local tri_ctx_count = u64(pos)
    pos = pos + 8
    local tri_ctx_off = pos
    local unknown = f32(uni_off + 4)

    local function lookup_i32(offset, count, key, fallback)
        local low, high = 0, count
        while low < high do
        local middle = low + math.floor((high - low) / 2)
            local value = i32(offset + middle * 8)
            if value < key then
                low = middle + 1
            else
                high = middle
            end
        end
        if low >= count then
            return fallback
        end
        local at = offset + low * 8
        if i32(at) == key then
            return f32(at + 4)
        end
        return fallback
    end

    local function lookup_u64(offset, count, key, fallback)
        local low, high = 0, count
        while low < high do
        local middle = low + math.floor((high - low) / 2)
            local value = u64(offset + middle * 12)
            if value < key then
                low = middle + 1
            else
                high = middle
            end
        end
        if low >= count then
            return fallback
        end
        local at = offset + low * 12
        if u64(at) == key then
            return f32(at + 8)
        end
        return fallback
    end

    local function pack3(first, second, third)
        return pack2(first, second) * SHIFT + (third % SHIFT)
    end

    local function logp(prev2, prev1, target)
        local first = scalar(prev2)
        local second = scalar(prev1)
        local third = scalar(target)
        local unigram = lookup_i32(uni_off, uni_count, third, unknown)
        local bigram = lookup_u64(bi_off, bi_count, pack2(second, third), 0.0)
        local bigram_lambda = lookup_i32(bi_ctx_off, bi_ctx_count, second, 1.0)
        bigram = bigram + bigram_lambda * unigram
        local trigram = lookup_u64(tri_off, tri_count, pack3(first, second, third), 0.0)
        local trigram_lambda = lookup_u64(tri_ctx_off, tri_ctx_count, pack2(first, second), 1.0)
        trigram = trigram + trigram_lambda * bigram
        if trigram < 1e-300 then
            trigram = 1e-300
        end
        return math.log(trigram)
    end

    local function has_observed_bigram(prev, target)
        local left = scalar(prev)
        local right = scalar(target)
        local key = pack2(left, right)
        local low, high = 0, bi_count
        while low < high do
        local middle = low + math.floor((high - low) / 2)
            local value = u64(bi_off + middle * 12)
            if value < key then
                low = middle + 1
            else
                high = middle
            end
        end
        return low < bi_count and u64(bi_off + low * 12) == key
    end

    return {
        path = path,
        bytes = #data,
        format = "TCSKNM01",
        logp = logp,
        has_observed_bigram = has_observed_bigram
    }
end

local function load_mobile(path)
    local file = assert(io.open(path, "rb"), "cannot open n-gram: " .. path)
    local header = file:read(MOBILE_HEADER_SIZE)
    assert(header and #header == MOBILE_HEADER_SIZE, "truncated mobile n-gram: " .. path)
    assert(header:sub(1, 8) == "TCSKNM02", "not a TCSKNM02 model: " .. path)

    local version, header_size, file_size, index_stride, _, uni_count, _, uni_off,
        bi_ctx_count, bi_index_count, bi_blocks_off, bi_index_off, tri_ctx_count,
        tri_index_count, _, tri_blocks_off, tri_index_off =
        string.unpack("<I4I4I8I4I4I4I4I8I4I4I8I8I8I4I4I8I8", header, 9)
    assert(version == 1 and header_size == MOBILE_HEADER_SIZE, "unsupported mobile n-gram version")
    assert(index_stride >= 16 and bi_blocks_off < bi_index_off, "invalid mobile bigram layout")
    assert(bi_index_off < tri_blocks_off and tri_blocks_off < tri_index_off, "invalid mobile trigram layout")
    local actual_size = assert(file:seek("end"))
    assert(actual_size == file_size, "mobile n-gram size mismatch")

    local function read_at(offset, count)
        assert(file:seek("set", offset), "cannot seek n-gram")
        local value = file:read(count)
        assert(value and #value == count, "truncated mobile n-gram")
        return value
    end

    local unigrams = read_at(uni_off, uni_count * 8)
    local bi_index = read_at(bi_index_off, bi_index_count * 16)
    local tri_index = read_at(tri_index_off, tri_index_count * 16)
    local unknown = string.unpack("<f", unigrams, 5)

    local cache = {}
    local cache_bytes = 0
    local lru_head = nil
    local lru_tail = nil
    local context_caches = {
        b = { values = {}, keys = {}, next = 1 },
        t = { values = {}, keys = {}, next = 1 }
    }

    local function unlink(entry)
        if entry.previous then
            entry.previous.next = entry.next
        else
            lru_head = entry.next
        end
        if entry.next then
            entry.next.previous = entry.previous
        else
            lru_tail = entry.previous
        end
    end

    local function touch(entry)
        if lru_head == entry then
            return
        end
        if entry.previous or entry.next or lru_tail == entry then
            unlink(entry)
        end
        entry.previous = nil
        entry.next = lru_head
        if lru_head then
            lru_head.previous = entry
        else
            lru_tail = entry
        end
        lru_head = entry
    end

    local function index_key(data, index)
        return string.unpack("<I8", data, index * 16 + 1)
    end

    local function find_page(data, count, key)
        local low, high = 0, count
        while low < high do
        local middle = low + math.floor((high - low) / 2)
            if index_key(data, middle) <= key then
                low = middle + 1
            else
                high = middle
            end
        end
        return low - 1
    end

    local function get_page(kind, index_data, index_count, page, section_end)
        local cache_key = kind .. page
        local entry = cache[cache_key]
        if entry then
            touch(entry)
            return entry.data
        end
        local at = page * 16 + 1
        local offset = string.unpack("<I8", index_data, at + 8)
        local next_offset = section_end
        if page + 1 < index_count then
            next_offset = string.unpack("<I8", index_data, at + 24)
        end
        local data = read_at(offset, next_offset - offset)
        performance.page_misses = performance.page_misses + 1
        performance.page_bytes = performance.page_bytes + #data
        entry = { key = cache_key, data = data, bytes = #data }
        cache[cache_key] = entry
        cache_bytes = cache_bytes + entry.bytes
        touch(entry)
        while cache_bytes > MOBILE_CACHE_BYTES and lru_tail and lru_tail ~= entry do
            local victim = lru_tail
            unlink(victim)
            cache[victim.key] = nil
            cache_bytes = cache_bytes - victim.bytes
        end
        return data
    end

    local function lookup_unigram(key, fallback)
        local low, high = 0, uni_count
        while low < high do
            local middle = low + math.floor((high - low) / 2)
            local value = string.unpack("<i4", unigrams, middle * 8 + 1)
            if value < key then low = middle + 1 else high = middle end
        end
        if low < uni_count then
            local at = low * 8 + 1
            if string.unpack("<i4", unigrams, at) == key then
                return string.unpack("<f", unigrams, at + 4)
            end
        end
        return fallback
    end

    local function lookup_context(kind, index_data, index_count, context_count, section_end, key, target)
        local context_cache = context_caches[kind]
        local cached_context = context_cache.values[key]
        if cached_context then
            if cached_context.missing then
                return 1.0, 0.0, false
            end
            local data = get_page(
                kind, index_data, index_count, cached_context.page, section_end)
            local low, high = 0, cached_context.successor_count
            while low < high do
                local middle = low + math.floor((high - low) / 2)
                local value = string.unpack(
                    "<I4", data, cached_context.successor_position + middle * 8)
                if value < target then low = middle + 1 else high = middle end
            end
            if low < cached_context.successor_count then
                local at = cached_context.successor_position + low * 8
                if string.unpack("<I4", data, at) == target then
                    return cached_context.lambda, string.unpack("<f", data, at + 4), true
                end
            end
            return cached_context.lambda, 0.0, false
        end

        local function remember(value)
            local old_key = context_cache.keys[context_cache.next]
            if old_key ~= nil then
                context_cache.values[old_key] = nil
            end
            context_cache.values[key] = value
            context_cache.keys[context_cache.next] = key
            context_cache.next = context_cache.next % CONTEXT_CACHE_ENTRIES + 1
        end

        local page = find_page(index_data, index_count, key)
        if page < 0 then
            remember({ missing = true })
            return 1.0, 0.0, false
        end
        local data = get_page(kind, index_data, index_count, page, section_end)
        local position = 1
        local remaining = math.min(index_stride, context_count - page * index_stride)
        for _ = 1, remaining do
            local context_key, lambda, successor_count
            context_key, lambda, successor_count, position = string.unpack("<I8fI4", data, position)
            if context_key == key then
                remember({
                    page = page,
                    lambda = lambda,
                    successor_count = successor_count,
                    successor_position = position
                })
                local low, high = 0, successor_count
                while low < high do
                    local middle = low + math.floor((high - low) / 2)
                    local value = string.unpack("<I4", data, position + middle * 8)
                    if value < target then low = middle + 1 else high = middle end
                end
                if low < successor_count then
                    local at = position + low * 8
                    if string.unpack("<I4", data, at) == target then
                        return lambda, string.unpack("<f", data, at + 4), true
                    end
                end
                return lambda, 0.0, false
            end
            if context_key > key then
                remember({ missing = true })
                return 1.0, 0.0, false
            end
            position = position + successor_count * 8
        end
        remember({ missing = true })
        return 1.0, 0.0, false
    end

    local function logp(prev2, prev1, target)
        local first = scalar(prev2)
        local second = scalar(prev1)
        local third = scalar(target)
        local unigram = lookup_unigram(third, unknown)
        local bigram_lambda, bigram_probability = lookup_context(
            "b", bi_index, bi_index_count, bi_ctx_count, bi_index_off, second, third)
        local bigram = bigram_probability + bigram_lambda * unigram
        local trigram_lambda, trigram_probability = lookup_context(
            "t", tri_index, tri_index_count, tri_ctx_count, tri_index_off,
            pack2(first, second), third)
        local probability = trigram_probability + trigram_lambda * bigram
        return math.log(math.max(probability, 1e-300))
    end

    local function has_observed_bigram(prev, target)
        local _, _, observed = lookup_context(
            "b", bi_index, bi_index_count, bi_ctx_count, bi_index_off,
            scalar(prev), scalar(target))
        return observed
    end

    return {
        path = path,
        bytes = file_size,
        format = "TCSKNM02",
        resident_index_bytes = #unigrams + #bi_index + #tri_index,
        cache_limit_bytes = MOBILE_CACHE_BYTES,
        logp = logp,
        has_observed_bigram = has_observed_bigram,
        close = function() file:close() end
    }
end

function kn_reader.load(path)
    local file = assert(io.open(path, "rb"), "cannot open n-gram: " .. path)
    local magic = file:read(8)
    file:close()
    if magic == "TCSKNM02" then
        return load_mobile(path)
    end
    return load_legacy(path)
end

function kn_reader.try_load()
    local failures = {}
    for _, path in ipairs(kn_reader.candidate_paths()) do
        if file_exists(path) then
            local ok, model = pcall(kn_reader.load, path)
            if ok then
                return model, nil
            end
            failures[#failures + 1] = path .. ": " .. tostring(model)
        end
    end
    if #failures > 0 then
        return nil, table.concat(failures, " | ")
    end
    return nil, "no sentence n-gram model found"
end


-- Per-schema supplemental phrase rewards for TigerClaw sentence decoding.
local supplement = {}

local file_name = "tiger_sentence.supplement.txt"
local baseline_reward = 9.0
local weight_scale = 2.0
local baseline_weight = 1000.0
local maximum_reward = 16.0

local function utf_chars(text)
    local chars = {}
    local index = 1
    while index <= #text do
        local first = text:byte(index)
        local length = first < 0x80 and 1 or first < 0xE0 and 2 or first < 0xF0 and 3 or 4
        chars[#chars + 1] = text:sub(index, index + length - 1)
        index = index + length
    end
    return chars
end

local function reward_for_weight(weight)
    local bounded = math.max(1, math.min(1000000000, weight))
    local reward = baseline_reward + weight_scale * math.log(bounded / baseline_weight)
    return math.max(0.0, math.min(maximum_reward, reward))
end

local function empty_matcher(path, load_error)
    return {
        nodes = { { transitions = {}, failure = 1, reward = 0.0 } },
        path = path,
        count = 0,
        error = load_error
    }
end

function supplement.build(entries, path)
    local nodes = { { transitions = {}, failure = 1, reward = 0.0 } }
    local count = 0
    for text, weight in pairs(entries or {}) do
        local reward = reward_for_weight(weight)
        if text ~= "" and reward > 0.0 then
            local state = 1
            local chars = utf_chars(text)
            for i = 1, #chars do
                local next_state = nodes[state].transitions[chars[i]]
                if not next_state then
                    next_state = #nodes + 1
                    nodes[state].transitions[chars[i]] = next_state
                    nodes[next_state] = { transitions = {}, failure = 1, reward = 0.0 }
                end
                state = next_state
            end
            nodes[state].reward = math.max(nodes[state].reward, reward)
            count = count + 1
        end
    end

    if count == 0 then
        return empty_matcher(path, nil)
    end

    local queue = {}
    local head = 1
    for _, child in pairs(nodes[1].transitions) do
        nodes[child].failure = 1
        queue[#queue + 1] = child
    end
    while head <= #queue do
        local current = queue[head]
        head = head + 1
        for ch, child in pairs(nodes[current].transitions) do
            local fallback = nodes[current].failure
            while fallback ~= 1 and not nodes[fallback].transitions[ch] do
                fallback = nodes[fallback].failure
            end
            local failure_target = nodes[fallback].transitions[ch]
            if failure_target and failure_target ~= child then
                nodes[child].failure = failure_target
            else
                nodes[child].failure = 1
            end
            nodes[child].reward = math.max(
                nodes[child].reward,
                nodes[nodes[child].failure].reward)
            queue[#queue + 1] = child
        end
    end
    return { nodes = nodes, path = path, count = count, error = nil }
end

function supplement.load_file(path)
    local handle, open_error = io.open(path, "rb")
    if not handle then
        return empty_matcher(path, open_error)
    end
    local content = handle:read("*a") or ""
    handle:close()
    content = content:gsub("^\239\187\191", "")

    local entries = {}
    for raw_line in (content .. "\n"):gmatch("(.-)\r?\n") do
        local line = raw_line:match("^%s*(.-)%s*$") or ""
        if line ~= "" and line:sub(1, 1) ~= "#" then
            local text, rest = line:match("^(%S+)%s*(.-)$")
            local weight = 1000
            if rest and rest ~= "" then
                if not rest:match("^%d+$") then
                    text = nil
                else
                    weight = tonumber(rest)
                    if not weight or weight <= 0 then text = nil end
                end
            end
            if text then entries[text] = weight end
        end
    end
    return supplement.build(entries, path)
end

local function join_path(directory, name)
    if directory:sub(-1) == "/" or directory:sub(-1) == "\\" then
        return directory .. name
    end
    return directory .. "/" .. name
end

function supplement.default_path()
    if not rime_api or type(rime_api.get_user_data_dir) ~= "function" then
        return nil
    end
    local ok, directory = pcall(rime_api.get_user_data_dir)
    if not ok or type(directory) ~= "string" or directory == "" then
        return nil
    end
    return join_path(directory, file_name)
end

function supplement.load_default()
    local path = supplement.default_path()
    if not path then return empty_matcher(nil, nil) end
    local matcher = supplement.load_file(path)
    -- A missing optional file is normal; only report malformed/unreadable files
    -- after the path has actually resolved in a Rime frontend.
    return matcher
end

function supplement.advance(matcher, state, ch)
    if not matcher or matcher.count == 0 or not ch or ch == "" then
        return 1, 0.0
    end
    local nodes = matcher.nodes
    local current = type(state) == "number" and nodes[state] and state or 1
    while current ~= 1 and not nodes[current].transitions[ch] do
        current = nodes[current].failure
    end
    current = nodes[current].transitions[ch] or 1
    return current, nodes[current].reward
end

supplement.file_name = file_name
supplement.reward_for_weight = reward_for_weight
local ranks = {
    unknown = data.unknown_character_rank or 20001
}
function ranks.rank(ch)
    return data.character_ranks[ch] or ranks.unknown
end

local beam_width = 200
local candidate_limit = 20
local max_raw_length = 128
local rank_penalty = 0.03
local emitted_character_reward = 2.0
local isolation_threshold = 3000
local isolation_lambda = 2.0
-- Two consecutive generations may confirm only when dissenting Beam mass is
-- below 0.001%; every weaker history keeps the original three-key window.
local early_commit_strong_share = 0.99999
local early_commit_retained_raw_length = 3
local BOS = kn_reader.BOS
local EOS = kn_reader.EOS
local kn_model = false
local kn_load_error = nil
local supplement_matcher = supplement.load_default()
local has_supplements = (supplement_matcher.count or 0) > 0
local max_code_len = 1
for i = 1, #lexicon.lengths do
    if lexicon.lengths[i] > max_code_len then
        max_code_len = lexicon.lengths[i]
    end
end
local proper_code_prefixes = {}
for code, _ in pairs(lexicon.codes) do
    for length = 1, #code - 1 do
        proper_code_prefixes[code:sub(1, length)] = true
    end
end

local logp_cache_limit = 32768
local logp_cache = {}
local logp_cache_keys = {}
local logp_cache_next = 1
local observed_cache_limit = 32768
local observed_cache = {}
local observed_cache_keys = {}
local observed_cache_next = 1
local isolation_cache = {}
local isolation_cache_keys = {}
local isolation_cache_next = 1
local aggregate_during_expansion_threshold = 128
local decode_cache = {
    raw = nil,
    states = nil,
    result = nil,
    includes_early_commit = false,
    required_text_prefix = ""
}
local state_separator = "\31"

-- Only committed-prefix state must be visible to both the processor and
-- translator. Keep per-key confidence state on the processor environment so
-- it does not emit a Rime property update (and a redundant UI refresh) for
-- every physical key. The old properties are read once for live migration.
local state_keys = {
    committed_text = "tiger_sentence_committed_text",
    committed_raw = "tiger_sentence_committed_raw",
    confidence = "tiger_sentence_confidence",
    -- Read and clear the old split properties once during migration.
    proposal = "tiger_sentence_proposal",
    stable = "tiger_sentence_stable",
    evidence_raw = "tiger_sentence_evidence_raw"
}

local function transient_state(context, env)
    if not env then
        return {
            proposal = "",
            stable = 0,
            evidence_raw = "",
            history = {},
            suspended = false,
            empty_code_pending = nil,
            continuation_after_auto_commit = false
        }
    end
    if not env._tiger_sentence_transient then
        local confidence = context:get_property(state_keys.confidence) or ""
        local proposal, stable, evidence_raw = confidence:match(
            "^(.-)" .. state_separator .. "(%d+)" .. state_separator .. "(.*)$")
        if proposal == nil then
            proposal = context:get_property(state_keys.proposal) or ""
            stable = context:get_property(state_keys.stable) or "0"
            evidence_raw = context:get_property(state_keys.evidence_raw) or ""
        end
        env._tiger_sentence_transient = {
            proposal = proposal,
            stable = tonumber(stable) or 0,
            evidence_raw = evidence_raw,
            history = {},
            suspended = false,
            empty_code_pending = nil,
            continuation_after_auto_commit = false
        }
    end
    return env._tiger_sentence_transient
end

local function sentence_state(context, env)
    local transient = transient_state(context, env)
    return {
        committed_text = context:get_property(state_keys.committed_text) or "",
        committed_raw = context:get_property(state_keys.committed_raw) or "",
        proposal = transient.proposal,
        stable = transient.stable,
        evidence_raw = transient.evidence_raw,
        history = transient.history or {},
        suspended = transient.suspended or false,
        empty_code_pending = transient.empty_code_pending,
        continuation_after_auto_commit = transient.continuation_after_auto_commit or false
    }
end

local function set_property_if_changed(context, key, value)
    value = value or ""
    if (context:get_property(key) or "") ~= value then
        context:set_property(key, value)
    end
end

local function save_transient_state(context, state, env)
    if env then
        env._tiger_sentence_transient = {
            proposal = state.proposal or "",
            stable = state.stable or 0,
            evidence_raw = state.evidence_raw or "",
            history = state.history or {},
            suspended = state.suspended or false,
            empty_code_pending = state.empty_code_pending,
            continuation_after_auto_commit = state.continuation_after_auto_commit or false
        }
    end
    if env and not env._tiger_sentence_legacy_cleared then
        set_property_if_changed(context, state_keys.confidence, "")
        set_property_if_changed(context, state_keys.proposal, "")
        set_property_if_changed(context, state_keys.stable, "")
        set_property_if_changed(context, state_keys.evidence_raw, "")
        env._tiger_sentence_legacy_cleared = true
    end
end

local function save_sentence_state(context, state, env)
    set_property_if_changed(context, state_keys.committed_text, state.committed_text)
    set_property_if_changed(context, state_keys.committed_raw, state.committed_raw)
    save_transient_state(context, state, env)
end

local function reset_sentence_state(context, env, continuation_after_auto_commit)
    finish_performance_sample()
    save_sentence_state(context, {
        committed_text = "",
        committed_raw = "",
        proposal = "",
        stable = 0,
        evidence_raw = "",
        history = {},
        suspended = false,
        empty_code_pending = nil,
        continuation_after_auto_commit = continuation_after_auto_commit or false
    }, env)
end

local function ensure_kn()
    if kn_model ~= false then
        return kn_model
    end
    kn_model, kn_load_error = kn_reader.try_load()
    kn_model = kn_model or nil
    return kn_model
end

local function model_status()
    local model = ensure_kn()
    if model then
        return {
            loaded = true,
            path = model.path,
            format = model.format,
            bytes = model.bytes,
            error = nil
        }
    end
    return {
        loaded = false,
        path = nil,
        format = nil,
        bytes = 0,
        error = kn_load_error
    }
end

local function logp(prev2, prev1, target)
    local key = prev2 .. "\0" .. prev1 .. "\0" .. target
    local cached = logp_cache[key]
    if cached ~= nil then
        return cached
    end
    local model = ensure_kn()
    local value = 0
    if model then
        value = model.logp(prev2, prev1, target)
    end
    local old_key = logp_cache_keys[logp_cache_next]
    if old_key then
        logp_cache[old_key] = nil
    end
    logp_cache[key] = value
    logp_cache_keys[logp_cache_next] = key
    logp_cache_next = logp_cache_next % logp_cache_limit + 1
    return value
end

local function trailing_selector_span(raw)
    local index = #raw
    while index >= 1 do
        local mark = raw:sub(index, index)
        if mark:match("%d") or mark == ";" or mark == "'" then
            index = index - 1
        else
            break
        end
    end
    return #raw - index
end

local function normalize(raw)
    if not raw then
        return ""
    end
    return raw:lower():gsub("%s+", "")
end

local function has_letter(raw)
    return raw:find("%a") ~= nil
end

local function utf_chars(text)
    local chars = {}
    if utf8 and utf8.codes then
        for _, codepoint in utf8.codes(text) do
            chars[#chars + 1] = utf8.char(codepoint)
        end
        return chars
    end
    local index = 1
    while index <= #text do
        local first = text:byte(index)
        local length = first < 0x80 and 1 or first < 0xE0 and 2 or first < 0xF0 and 3 or 4
        chars[#chars + 1] = text:sub(index, index + length - 1)
        index = index + length
    end
    return chars
end

local function candidate_chars(candidate)
    if not candidate._chars then
        candidate._chars = utf_chars(candidate.t)
    end
    return candidate._chars
end

local function eligible_candidates(candidates, selected_rank, allow_all_ranks)
    if selected_rank == 0 and allow_all_ranks then
        return candidates
    end
    local rank = selected_rank > 0 and selected_rank or 1
    local cache_key = "_rank_" .. tostring(rank)
    local cached = candidates[cache_key]
    if cached then
        return cached
    end
    local selected = {}
    for index = 1, #candidates do
        if candidates[index].r == rank then
            selected[#selected + 1] = candidates[index]
        end
    end
    candidates[cache_key] = selected
    return selected
end

local function has_observed_bigram(previous, target)
    local key = previous .. "\0" .. target
    local cached = observed_cache[key]
    if cached ~= nil then
        return cached
    end
    local model = ensure_kn()
    local value = model and model.has_observed_bigram and
        model.has_observed_bigram(previous, target) or false
    local old_key = observed_cache_keys[observed_cache_next]
    if old_key then
        observed_cache[old_key] = nil
    end
    observed_cache[key] = value
    observed_cache_keys[observed_cache_next] = key
    observed_cache_next = observed_cache_next % observed_cache_limit + 1
    return value
end

local function isolation_penalty(text)
    local model = ensure_kn()
    if not model or not model.has_observed_bigram or not text or text == "" then
        return 0
    end
    local cached = isolation_cache[text]
    if cached ~= nil then
        performance.isolation_hits = performance.isolation_hits + 1
        return cached
    end
    performance.isolation_misses = performance.isolation_misses + 1
    local chars = utf_chars(text)
    local penalty = 0
    for index = 1, #chars do
        local rank = ranks.rank(chars[index])
        if rank > isolation_threshold then
            local left_hit = index > 1 and has_observed_bigram(chars[index - 1], chars[index])
            local right_hit = index < #chars and has_observed_bigram(chars[index], chars[index + 1])
            if not left_hit and not right_hit then
                penalty = penalty + isolation_lambda
            end
        end
    end
    local old_key = isolation_cache_keys[isolation_cache_next]
    if old_key then isolation_cache[old_key] = nil end
    isolation_cache[text] = penalty
    isolation_cache_keys[isolation_cache_next] = text
    isolation_cache_next = isolation_cache_next % ISOLATION_CACHE_ENTRIES + 1
    return penalty
end

local function parse_selector(raw, code_end)
    local next_index = code_end + 1
    if next_index > #raw then
        return 0, code_end
    end
    local mark = raw:sub(next_index, next_index)
    if mark == ";" then
        return 2, next_index
    end
    if mark == "'" then
        return 3, next_index
    end
    if mark:match("%d") then
        local digit_end = next_index
        while digit_end < #raw and raw:sub(digit_end + 1, digit_end + 1):match("%d") do
            digit_end = digit_end + 1
        end
        local token = raw:sub(next_index, digit_end)
        if token == "0" then
            return 10, digit_end
        end
        return tonumber(token) or 0, digit_end
    end
    return 0, code_end
end

local function advance_required_prefix(required, matched_length, candidate_text)
    if matched_length >= #required then
        return matched_length
    end
    candidate_text = candidate_text or ""
    local compare_length = math.min(#candidate_text, #required - matched_length)
    if compare_length == 0 or
        required:sub(matched_length + 1, matched_length + compare_length) ~=
        candidate_text:sub(1, compare_length) then
        return nil
    end
    return math.min(#required, matched_length + #candidate_text)
end

local function has_complete_candidate(raw_code, required_text_prefix)
    local raw = normalize(raw_code)
    if raw == "" or not has_letter(raw) then
        return false
    end

    local required = required_text_prefix or ""
    local states = {}
    for index = 0, #raw do states[index] = {} end
    states[0][0] = true
    for position = 0, #raw - 1 do
        if next(states[position]) then
            for i = 1, #lexicon.lengths do
                local code_length = lexicon.lengths[i]
                local code_end = position + code_length
                if code_end <= #raw then
                    local candidates = lexicon.codes[raw:sub(position + 1, code_end)]
                    if candidates then
                        local selected_rank, consumed_end = parse_selector(raw, code_end)
                        local whole_input_edge = position == 0 and consumed_end == #raw
                        if not (#raw > 1 and consumed_end - position < 2) then
                            local selected = eligible_candidates(
                                candidates, selected_rank, whole_input_edge)
                            for matched_length in pairs(states[position]) do
                                for candidate_index = 1, #selected do
                                    local next_matched = advance_required_prefix(
                                        required,
                                        matched_length,
                                        selected[candidate_index].t)
                                    if next_matched then
                                        states[consumed_end][next_matched] = true
                                    end
                                end
                            end
                        end
                    end
                end
            end
        end
    end
    return states[#raw][#required] or false
end

local function state_better(left, right)
    local left_rank = left.max_rank or 1
    local right_rank = right.max_rank or 1
    if left_rank ~= right_rank then
        return left_rank < right_rank
    end
    if left.score == right.score then
        return left.text < right.text
    end
    return left.score > right.score
end

local function duplicate_better(item, previous)
    local item_rank = item.max_rank or 1
    local previous_rank = previous.max_rank or 1
    return item_rank < previous_rank or
        (item_rank == previous_rank and item.score > previous.score)
end

local function logsumexp(left, right)
    local maximum = math.max(left, right)
    return maximum + math.log(math.exp(left - maximum) + math.exp(right - maximum))
end

local function new_bucket()
    return {}
end

local function add_aggregated(bucket, item)
    local best = bucket._best
    local mass = bucket._mass
    local previous = best[item.text]
    local item_mass = item.mass_score or item.score
    if not previous then
        best[item.text] = item
        mass[item.text] = item_mass
        bucket._order[#bucket._order + 1] = item.text
    else
        mass[item.text] = logsumexp(mass[item.text], item_mass)
        if duplicate_better(item, previous) then
            best[item.text] = item
        end
    end
    best[item.text].mass_score = mass[item.text]
end

local function ensure_aggregated(bucket)
    if bucket._best then
        return
    end
    local best = {}
    local mass = {}
    local order = {}
    for i = 1, #bucket do
        local item = bucket[i]
        local previous = best[item.text]
        local item_mass = item.mass_score or item.score
        if mass[item.text] == nil then
            mass[item.text] = item_mass
        else
            mass[item.text] = logsumexp(mass[item.text], item_mass)
        end
        if not previous then
            order[#order + 1] = item.text
            best[item.text] = item
        elseif duplicate_better(item, previous) then
            best[item.text] = item
        end
    end
    for i = #bucket, 1, -1 do
        bucket[i] = nil
    end
    for i = 1, #order do
        best[order[i]].mass_score = mass[order[i]]
    end
    bucket._best = best
    bucket._mass = mass
    bucket._order = order
end

local function add_state(bucket, item)
    if bucket._frozen then
        bucket._frozen = nil
        ensure_aggregated(bucket)
    end
    if bucket._best then
        add_aggregated(bucket, item)
        return
    end
    bucket[#bucket + 1] = item
    if #bucket >= aggregate_during_expansion_threshold then
        ensure_aggregated(bucket)
    end
end

local function sift_worst_up(heap, index)
    while index > 1 do
        local parent = math.floor(index / 2)
        if not state_better(heap[parent], heap[index]) then
            return
        end
        heap[parent], heap[index] = heap[index], heap[parent]
        index = parent
    end
end

local function sift_worst_down(heap, index)
    while true do
        local left = index * 2
        if left > #heap then
            return
        end
        local right = left + 1
        local worse = left
        if right <= #heap and state_better(heap[left], heap[right]) then
            worse = right
        end
        if not state_better(heap[index], heap[worse]) then
            return
        end
        heap[index], heap[worse] = heap[worse], heap[index]
        index = worse
    end
end

local function select_exact_top(values, limit)
    local heap = {}
    for i = 1, #values do
        local item = values[i]
        if #heap < limit then
            heap[#heap + 1] = item
            sift_worst_up(heap, #heap)
        elseif state_better(item, heap[1]) then
            heap[1] = item
            sift_worst_down(heap, 1)
        end
    end
    table.sort(heap, state_better)
    return heap
end

local function dedup_limit(bucket, limit)
    if not bucket then
        return new_bucket()
    end
    if bucket._frozen then
        return bucket
    end
    ensure_aggregated(bucket)
    local result = {}
    for i = 1, #bucket._order do
        local selected = bucket._best[bucket._order[i]]
        if selected then
            result[#result + 1] = selected
        end
    end
    local truncated_now = #result > limit
    local truncated = (bucket._truncated or false) or truncated_now
    if truncated_now then
        result = select_exact_top(result, limit)
    else
        table.sort(result, state_better)
    end
    local limited = new_bucket()
    for i = 1, #result do
        limited[i] = result[i]
    end
    limited._truncated = truncated
    limited._frozen = true
    return limited
end

local function new_states(length)
    local states = {}
    for index = 0, length do
        states[index] = new_bucket()
    end
    add_state(states[0], {
        score = 0,
        mass_score = 0,
        text = "",
        prev2 = BOS,
        prev1 = BOS,
        max_rank = 1,
        supplement_state = 1,
        supplement_score = 0.0,
        previous = nil,
        text_length = 0,
        raw_length = 0
    })
    return states
end

local function expand_range(raw, states, from_pos, length, minimum_consumed_end)
    minimum_consumed_end = minimum_consumed_end or -1
    for position = from_pos, length - 1 do
        local current = dedup_limit(states[position], beam_width)
        states[position] = current
        if #current > 0 then
            for i = 1, #lexicon.lengths do
                local code_length = lexicon.lengths[i]
                if position + code_length <= length then
                    local code = raw:sub(position + 1, position + code_length)
                    local candidates = lexicon.codes[code]
                    if candidates then
                        local selected_rank, consumed_end = parse_selector(raw, position + code_length)
                        local whole_input_edge = position == 0 and consumed_end == length
                        if consumed_end > minimum_consumed_end and
                            not (length > 1 and consumed_end - position < 2) then
                            local selected_candidates = eligible_candidates(
                                candidates, selected_rank, whole_input_edge)
                            for c = 1, #current do
                                local item = current[c]
                                for k = 1, #selected_candidates do
                                    local candidate = selected_candidates[k]
                                    local score = item.score
                                    local prev2, prev1 = item.prev2, item.prev1
                                    local supplement_state = item.supplement_state or 1
                                    local supplement_added = 0.0
                                    local chars = candidate_chars(candidate)
                                    for ci = 1, #chars do
                                        score = score + logp(prev2, prev1, chars[ci])
                                        score = score + emitted_character_reward
                                        if has_supplements then
                                            local supplement_reward
                                            supplement_state, supplement_reward = supplement.advance(
                                                supplement_matcher, supplement_state, chars[ci])
                                            score = score + supplement_reward
                                            supplement_added = supplement_added + supplement_reward
                                        end
                                        prev2 = prev1
                                        prev1 = chars[ci]
                                    end
                                    if selected_rank == 0 then
                                        if candidate._log_rank == nil then
                                            candidate._log_rank = math.log(candidate.r)
                                        end
                                        score = score - rank_penalty * candidate._log_rank
                                    end
                                    local text = item.text .. candidate.t
                                    add_state(states[consumed_end], {
                                        score = score,
                                        mass_score = (item.mass_score or item.score) +
                                            score - item.score - supplement_added,
                                        text = text,
                                        prev2 = prev2,
                                        prev1 = prev1,
                                        max_rank = math.max(item.max_rank or 1, candidate.r),
                                        supplement_state = supplement_state,
                                        supplement_score = (item.supplement_score or 0.0) +
                                            supplement_added,
                                        previous = item,
                                        text_length = #text,
                                        raw_length = consumed_end
                                    })
                                end
                            end
                        end
                    end
                end
            end
        end
    end
end

local confidence_proposal
local raw_lengths_for_proposal

local function segmented_from_path(raw, path)
    if not raw or raw == "" or not path then
        return ""
    end
    local ends = {}
    while path and (path.raw_length or 0) > 0 do
        ends[#ends + 1] = path.raw_length
        path = path.previous
    end
    local pieces = {}
    local start = 1
    for index = #ends, 1, -1 do
        local finish = ends[index]
        pieces[#pieces + 1] = raw:sub(start, finish)
        start = finish + 1
    end
    return table.concat(pieces, " ")
end

local function evaluate_state(item)
    local ending_adjustment = logp(item.prev2, item.prev1, EOS) -
        isolation_penalty(item.text)
    return {
        score = item.score + ending_adjustment,
        confidence_score = (item.mass_score or item.score) + ending_adjustment,
        text = item.text,
        prev2 = item.prev2,
        prev1 = item.prev1,
        max_rank = math.max(1, item.max_rank or 1),
        supplement_score = item.supplement_score or 0.0,
        path = item
    }
end

local function add_early_commit_states(
    values, required_text_prefix, mass_by_text, best_by_text)
    for i = 1, #values do
        local item = values[i]
        local candidate = item.confidence_score and item or evaluate_state(item)
        if candidate.text and candidate.text ~= "" and
            (required_text_prefix == "" or
             candidate.text:sub(1, #required_text_prefix) == required_text_prefix) then
            local confidence_score = candidate.confidence_score
            local previous_mass = mass_by_text[item.text]
            if previous_mass == nil then
                mass_by_text[candidate.text] = confidence_score
            else
                mass_by_text[candidate.text] = logsumexp(previous_mass, confidence_score)
            end
            local previous = best_by_text[candidate.text]
            if not previous or confidence_score > previous.confidence_score then
                best_by_text[candidate.text] = candidate
            end
        end
    end
end

local function incomplete_code_tail(tail)
    if tail == "" or not tail:match("^[A-Za-z]+$") or
        not proper_code_prefixes[tail] then
        return false
    end
    return #tail < 2 or lexicon.codes[tail] == nil
end

local function build_early_commit_evidence(
    raw, states, completed, completed_truncated, required_text_prefix)
    performance.early_evidence_builds = performance.early_evidence_builds + 1
    local mass_by_text = {}
    local best_by_text = {}
    local truncated = completed_truncated or false
    local uses_incomplete_tail = false
    add_early_commit_states(
        completed, required_text_prefix, mass_by_text, best_by_text)

    local maximum_tail_length = math.min(max_code_len - 1, #raw - 1)
    for tail_length = 1, maximum_tail_length do
        local consumed_length = #raw - tail_length
        local tail = raw:sub(consumed_length + 1)
        if incomplete_code_tail(tail) and states[consumed_length] then
            local partial = dedup_limit(states[consumed_length], beam_width)
            states[consumed_length] = partial
            if #partial > 0 then
                uses_incomplete_tail = true
                truncated = truncated or (partial._truncated or false)
                add_early_commit_states(
                    partial, required_text_prefix, mass_by_text, best_by_text)
            end
        end
    end

    local candidates = {}
    for text, candidate in pairs(best_by_text) do
        candidate.confidence_score = mass_by_text[text]
        candidates[#candidates + 1] = candidate
    end
    if uses_incomplete_tail then
        table.sort(candidates, function(left, right)
            if left.confidence_score == right.confidence_score then
                return left.text < right.text
            end
            return left.confidence_score > right.confidence_score
        end)
    else
        table.sort(candidates, state_better)
    end
    local proposal, proposal_share = confidence_proposal(candidates, 0.995)
    return {
        proposal = proposal,
        proposal_share = proposal_share,
        raw_lengths = raw_lengths_for_proposal(proposal, candidates),
        confidence_truncated = truncated
    }
end

local function emit(raw, states, length, include_early_commit, required_text_prefix)
    local completed = dedup_limit(states[length], beam_width)
    local all_candidates = {}
    for i = 1, #completed do
        all_candidates[i] = evaluate_state(completed[i])
    end
    local result = select_exact_top(all_candidates, candidate_limit)
    for i = 1, #result do
        result[i].segmented = segmented_from_path(raw, result[i].path)
    end
    result.early_commit_evidence = {
        proposal = "",
        raw_lengths = {},
        confidence_truncated = false
    }
    if include_early_commit then
        -- The full-code path only needs the already-selected top candidates
        -- (`result`); widening to the whole beam (`all_candidates`) only
        -- matters once an incomplete tail is merged in, so defer that cost
        -- to build_early_commit_evidence instead of paying it every key.
        result.early_commit_evidence = build_early_commit_evidence(
            raw,
            states,
            result,
            completed._truncated or false,
            required_text_prefix or "")
    end
    return result
end

local function results_equal(left, right)
    if #left ~= #right then
        return false
    end
    local left_evidence = left.early_commit_evidence or {}
    local right_evidence = right.early_commit_evidence or {}
    if (left_evidence.proposal or "") ~= (right_evidence.proposal or "") or
        (left_evidence.proposal_share or 0.0) ~=
        (right_evidence.proposal_share or 0.0) or
        (left_evidence.confidence_truncated or false) ~=
        (right_evidence.confidence_truncated or false) then
        return false
    end
    for i = 1, #left do
        if left[i].text ~= right[i].text
            or left[i].segmented ~= right[i].segmented
            or left[i].score ~= right[i].score
            or (left[i].confidence_score or left[i].score) ~=
                (right[i].confidence_score or right[i].score)
            or (left[i].supplement_score or 0.0) ~=
                (right[i].supplement_score or 0.0)
            or (left[i].max_rank or 1) ~= (right[i].max_rank or 1) then
            return false
        end
    end
    local left_raw_lengths = left_evidence.raw_lengths or {}
    local right_raw_lengths = right_evidence.raw_lengths or {}
    for text, raw_length in pairs(left_raw_lengths) do
        if right_raw_lengths[text] ~= raw_length then
            return false
        end
    end
    for text, raw_length in pairs(right_raw_lengths) do
        if left_raw_lengths[text] ~= raw_length then return false end
    end
    return true
end

local function decode_full(raw_code, include_early_commit, required_text_prefix)
    local raw = normalize(raw_code)
    if raw == "" or not has_letter(raw) then
        return {}
    end
    local length = #raw
    local started = os.clock()
    local states = new_states(length)
    expand_range(raw, states, 0, length)
    local result = emit(
        raw,
        states,
        length,
        include_early_commit or false,
        required_text_prefix or "")
    record_decode(started)
    return result
end

local function decode(raw_code, include_early_commit, required_text_prefix)
    local raw = normalize(raw_code)
    required_text_prefix = required_text_prefix or ""
    if raw == "" or not has_letter(raw) then
        decode_cache.raw = raw
        decode_cache.states = nil
        decode_cache.result = {}
        decode_cache.includes_early_commit = include_early_commit or false
        decode_cache.required_text_prefix = required_text_prefix
        return decode_cache.result
    end
    if decode_cache.raw == raw and decode_cache.result and
        (not include_early_commit or
         (decode_cache.includes_early_commit and
          decode_cache.required_text_prefix == required_text_prefix)) then
        return decode_cache.result
    end

    local length = #raw
    local started = os.clock()
    local states = nil
    local old_raw = decode_cache.raw
    local old_states = decode_cache.states
    if old_states and type(old_raw) == "string" and old_raw ~= "" then
        local old_n = #old_raw
        -- Whole-input one-key edges and implicit non-first ranks may become
        -- segmented after an append. Rebuild the small four-code prefix so
        -- formerly legal states cannot leak into the longer input.
        if old_n <= 4 or length <= 4 then
            states = nil
        elseif length > old_n and raw:sub(1, old_n) == old_raw then
            local max_consume = max_code_len + trailing_selector_span(raw)
            local from_pos = math.max(0, old_n + 1 - max_consume)
            states = old_states
            for index = old_n + 1, length do
                states[index] = new_bucket()
            end
            expand_range(raw, states, from_pos, length, old_n)
        elseif length < old_n and old_raw:sub(1, length) == raw then
            states = old_states
            for index = length + 1, old_n do
                states[index] = nil
            end
        end
    end

    if not states then
        states = new_states(length)
        expand_range(raw, states, 0, length)
    end

    local result = emit(
        raw,
        states,
        length,
        include_early_commit or false,
        required_text_prefix)
    decode_cache.raw = raw
    decode_cache.states = states
    decode_cache.result = result
    decode_cache.includes_early_commit = include_early_commit or false
    decode_cache.required_text_prefix = required_text_prefix
    record_decode(started)
    return result
end

local function trim_segmented_after_raw_prefix(segmented, raw_prefix_length)
    if not segmented or segmented == "" or raw_prefix_length <= 0 then
        return segmented or ""
    end
    local raw_count = 0
    local index = 1
    while index <= #segmented and raw_count < raw_prefix_length do
        if segmented:sub(index, index) ~= " " then
            raw_count = raw_count + 1
        end
        index = index + 1
    end
    while index <= #segmented and segmented:sub(index, index) == " " do
        index = index + 1
    end
    return index <= #segmented and segmented:sub(index) or ""
end

local function find_raw_length_for_text(text, candidates)
    local text_length = #text
    for i = 1, #candidates do
        local candidate = candidates[i]
        if candidate.text:sub(1, text_length) == text then
            local state = candidate.path
            while state do
                if state.text_length == text_length then
                    return state.raw_length
                end
                state = state.previous
            end
        end
    end
    return 0
end

raw_lengths_for_proposal = function(proposal, candidates)
    local lengths = {}
    local prefix_by_length = {}
    local prefix = ""
    local chars = utf_chars(proposal)
    for i = 1, #chars do
        prefix = prefix .. chars[i]
        prefix_by_length[#prefix] = prefix
    end
    local found = 0
    for i = 1, #candidates do
        local candidate = candidates[i]
        local state = candidate.path
        while state do
            local wanted = prefix_by_length[state.text_length]
            if wanted and lengths[wanted] == nil and
                candidate.text:sub(1, state.text_length) == wanted then
                lengths[wanted] = state.raw_length
                found = found + 1
            end
            state = state.previous
        end
        if found == #chars then break end
    end
    return lengths
end

local function common_history_prefix(history)
    if #history == 0 then return "" end
    local common = utf_chars(history[1].proposal or "")
    for index = 2, #history do
        local chars = utf_chars(history[index].proposal or "")
        local count = math.min(#common, #chars)
        local matched = 0
        while matched < count and common[matched + 1] == chars[matched + 1] do
            matched = matched + 1
        end
        while #common > matched do common[#common] = nil end
        if #common == 0 then return "" end
    end
    return table.concat(common)
end

local function required_early_commit_history(history)
    if #history < 2 then return 3 end
    for i = 1, #history do
        if not history[i].strong then return 3 end
    end
    return 2
end

local function stable_history_raw_length(history, text, minimum_count)
    if text == "" or #history < (minimum_count or 3) then return 0 end
    local stable = 0
    for i = 1, #history do
        local raw_length = (history[i].raw_lengths or {})[text] or 0
        if raw_length <= 0 then return 0 end
        if stable == 0 then
            stable = raw_length
        elseif stable ~= raw_length then
            return 0
        end
    end
    return stable
end

local function constrain_early_commit_boundary(
    history, stable_proposal, committed_text, full_raw, minimum_count)
    local consumed = stable_history_raw_length(
        history, stable_proposal, minimum_count)
    while #stable_proposal > #committed_text and
        (consumed == 0 or
         #full_raw - consumed < early_commit_retained_raw_length) do
        local chars = utf_chars(stable_proposal)
        chars[#chars] = nil
        stable_proposal = table.concat(chars)
        consumed = stable_history_raw_length(
            history, stable_proposal, minimum_count)
    end
    return stable_proposal, consumed
end

confidence_proposal = function(candidates, threshold)
    if #candidates == 0 then
        return "", 0.0
    end
    local max_score = candidates[1].confidence_score or candidates[1].score
    for i = 2, #candidates do
        local score = candidates[i].confidence_score or candidates[i].score
        if score > max_score then max_score = score end
    end
    local total = 0
    for i = 1, #candidates do
        total = total + math.exp((candidates[i].confidence_score or candidates[i].score) - max_score)
    end

    local prefix_mass = {}
    local prefix_length = {}
    local prefix_order = {}
    for i = 1, #candidates do
        local weight = math.exp((candidates[i].confidence_score or candidates[i].score) - max_score)
        local chars = utf_chars(candidates[i].text)
        local prefix = ""
        for length = 1, #chars do
            prefix = prefix .. chars[length]
            if prefix_mass[prefix] == nil then
                prefix_mass[prefix] = 0
                prefix_length[prefix] = length
                prefix_order[#prefix_order + 1] = prefix
            end
            prefix_mass[prefix] = prefix_mass[prefix] + weight
        end
    end

    local proposal = ""
    local proposal_length = 0
    local proposal_share = 0.0
    for index = 1, #prefix_order do
        local prefix = prefix_order[index]
        local length = prefix_length[prefix]
        local share = prefix_mass[prefix] / total
        if share >= threshold and length > proposal_length then
            proposal = prefix
            proposal_length = length
            proposal_share = share
        end
    end
    return proposal, proposal_share
end

local function has_selection_suffix(raw)
    return raw and raw:find("[;'0-9]") ~= nil
end

local function group_rank_allowed(candidate, raw)
    return has_selection_suffix(raw) or (candidate.max_rank or 1) <= 1
end

local function implicit_rank_allowed(candidate, raw, continuation_after_auto_commit)
    return not continuation_after_auto_commit or group_rank_allowed(candidate, raw)
end

local function capture_empty_code_candidate(
    full_raw, committed_text, continuation_after_auto_commit)
    local decoded = decode(full_raw, false, committed_text)
    local captured = nil
    local count = 0
    for i = 1, #decoded do
        local candidate = decoded[i]
        -- Whole-input non-first ranks are visible for explicit selection, but
        -- are not legal implicit segments after the appended key makes the
        -- edge dead. Do not count them as empty-code ambiguity.
        if group_rank_allowed(candidate, full_raw) and
            candidate.text and candidate.text ~= "" and
            candidate.text:sub(1, #committed_text) == committed_text and
            #candidate.text > #committed_text then
            count = count + 1
            if count > 1 then return nil end
            local previous = candidate.path and candidate.path.previous
            captured = {
                candidate_text = candidate.text,
                committed_text = committed_text,
                base_raw_length = #full_raw,
                last_segment_start = previous and previous.raw_length or 0
            }
        end
    end
    return count == 1 and captured or nil
end

local function cycle_candidate(context, step)
    if not context:has_menu() then return false end
    local composition = context.composition
    if not composition or composition:empty() then return false end
    local segment = composition:back()
    local menu = segment and segment.menu
    if not menu then return false end
    local count = menu:candidate_count()
    if not count or count <= 0 then return false end
    local selected = segment.selected_index or 0
    context:select((selected + step) % count)
    return true
end

local function reset_decode_cache_values()
    decode_cache.raw = nil
    decode_cache.states = nil
    decode_cache.result = nil
    decode_cache.includes_early_commit = false
    decode_cache.required_text_prefix = ""
end

local function try_empty_code_commit(env, state, full_before, appended_letter)
    local context = env.engine.context
    if state.suspended then
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        return false
    end
    local pending = state.empty_code_pending or
        capture_empty_code_candidate(
            full_before,
            state.committed_text,
            state.continuation_after_auto_commit)
    local full_raw = full_before .. appended_letter
    state.empty_code_pending = pending

    if not pending then
        save_transient_state(context, state, env)
        return false
    end

    if has_complete_candidate(full_raw, state.committed_text) then
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        return false
    end
    if not pending or
        pending.committed_text ~= state.committed_text or
        pending.base_raw_length < 0 or
        pending.base_raw_length >= #full_raw or
        pending.last_segment_start < 0 or
        pending.last_segment_start >= #full_raw then
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        return false
    end

    local extended_last_segment = full_raw:sub(pending.last_segment_start + 1)
    if proper_code_prefixes[extended_last_segment] then
        save_transient_state(context, state, env)
        return false
    end

    local commit = pending.candidate_text:sub(#pending.committed_text + 1)
    local retained_raw = full_raw:sub(pending.base_raw_length + 1)
    -- Preserve the committed prefix as decoder context. Restarting from only
    -- retained_raw makes the following sentence decode locally and can
    -- stabilize a plausible continuation that disagrees with the text which
    -- has already been committed.
    state.committed_text = pending.candidate_text
    state.committed_raw = full_raw:sub(1, pending.base_raw_length)
    state.proposal = ""
    state.stable = 0
    state.evidence_raw = ""
    state.history = {}
    state.suspended = false
    state.empty_code_pending = nil
    state.continuation_after_auto_commit = true
    env.engine:commit_text(commit)
    context:clear()
    save_sentence_state(context, state, env)
    reset_decode_cache_values()
    if retained_raw ~= "" then context:push_input(retained_raw) end
    return true
end

local function try_early_commit(env)
    local context = env.engine.context
    local state = sentence_state(context, env)
    local live_raw = context.input or ""

    if not context:get_option("tiger_sentence_early_commit") then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        save_transient_state(context, state, env)
        return
    end

    if state.suspended then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        save_transient_state(context, state, env)
        return
    end

    -- The first four raw encoding keys are never counted as stable evidence.
    if #live_raw + #state.committed_raw <= 4 then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        save_transient_state(context, state, env)
        return
    end

    local full_raw = state.committed_raw .. live_raw
    local decoded = decode(full_raw, true, state.committed_text)
    local early_commit_evidence = decoded.early_commit_evidence or {}
    if early_commit_evidence.confidence_truncated then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        save_transient_state(context, state, env)
        return
    end
    local proposal = early_commit_evidence.proposal or ""
    if proposal == "" then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        save_transient_state(context, state, env)
        return
    end

    local visible_top = nil
    for i = 1, #decoded do
        if state.committed_text == "" or
            decoded[i].text:sub(1, #state.committed_text) == state.committed_text then
            visible_top = decoded[i]
            break
        end
    end
    if visible_top and (visible_top.supplement_score or 0.0) > 0.0 then
        local supplement_top = visible_top.text
        while #proposal > #state.committed_text and
            supplement_top:sub(1, #proposal) ~= proposal do
            local chars = utf_chars(proposal)
            chars[#chars] = nil
            proposal = table.concat(chars)
        end
    end
    if proposal == "" or #proposal <= #state.committed_text then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        save_transient_state(context, state, env)
        return
    end

    local history = state.history or {}
    local previous_raw = #history > 0 and history[#history].raw or ""
    local extends_evidence = previous_raw ~= "" and
        #full_raw == #previous_raw + 1 and
        full_raw:sub(1, #previous_raw) == previous_raw
    if not extends_evidence then
        history = {}
    end
    history[#history + 1] = {
        proposal = proposal,
        raw = full_raw,
        raw_lengths = early_commit_evidence.raw_lengths or {},
        strong = (early_commit_evidence.proposal_share or 0.0) >=
            early_commit_strong_share
    }
    while #history > 3 do table.remove(history, 1) end
    state.history = history
    state.proposal = proposal
    state.stable = #history
    state.evidence_raw = full_raw
    save_transient_state(context, state, env)
    local required_history = required_early_commit_history(history)
    if #history < required_history then return end

    local stable_proposal, consumed = constrain_early_commit_boundary(
        history,
        common_history_prefix(history),
        state.committed_text,
        full_raw,
        required_history)
    if consumed <= #state.committed_raw or
        consumed > #full_raw or
        #full_raw - consumed < early_commit_retained_raw_length then return end
    local commit = stable_proposal:sub(#state.committed_text + 1)
    if #utf_chars(commit) < 1 or #live_raw < 3 then return end
    state.committed_text = stable_proposal
    state.committed_raw = full_raw:sub(1, consumed)
    state.proposal = stable_proposal
    state.stable = 0
    state.evidence_raw = ""
    state.history = {}
    state.continuation_after_auto_commit = true
    env.engine:commit_text(commit)
    context:clear()
    save_sentence_state(context, state, env)
    local remaining = full_raw:sub(consumed + 1)
    if remaining ~= "" then context:push_input(remaining) end
end

local function reset_decode_cache()
    decode_cache.raw = nil
    decode_cache.states = nil
    decode_cache.result = nil
    decode_cache.includes_early_commit = false
    decode_cache.required_text_prefix = ""
end

local function is_plain_char_key(key_event, repr)
    if key_event:ctrl() or key_event:alt() or key_event:super() then
        return nil
    end
    if #repr == 1 and repr:match("[a-z]") then
        return repr
    end
    if repr == "semicolon" then
        return ";"
    end
    if repr == "apostrophe" then
        return "'"
    end
    if repr:match("^[0-9]$") then
        return repr
    end
    local kp = repr:match("^KP_([0-9])$")
    if kp then
        return kp
    end
    return nil
end

local function processor(key_event, env)
    if key_event:release() then
        return 2
    end
    local context = env.engine.context
    local state = sentence_state(context, env)
    local repr = key_event:repr()
    local ch = is_plain_char_key(key_event, repr)
    if ch then
        if not context:is_composing() and
            (state.committed_raw ~= "" or state.proposal ~= "" or
             state.evidence_raw ~= "" or state.suspended or
             state.continuation_after_auto_commit) then
            reset_sentence_state(context, env)
            state = sentence_state(context, env)
        end
        if #(context.input or "") >= max_raw_length then
            return 1
        end
        -- Digits are rank suffixes only while composing. Idle Chinese mode
        -- should commit 0-9 like a normal Rime schema (including 全角).
        if ch:match("%d") and not context:is_composing() then
            if #repr == 1 then
                return 2
            end
            if context:get_option("full_shape") then
                local full = { "０", "１", "２", "３", "４", "５", "６", "７", "８", "９" }
                env.engine:commit_text(full[tonumber(ch) + 1])
            else
                env.engine:commit_text(ch)
            end
            return 1
        end
        local is_letter = ch:match("^[a-z]$") ~= nil
        local full_before = state.committed_raw .. (context.input or "")
        if not is_letter then
            state.empty_code_pending = nil
            save_transient_state(context, state, env)
        end
        context:push_input(ch)
        if is_letter and try_empty_code_commit(env, state, full_before, ch) then
            return 1
        end
        try_early_commit(env)
        return 1
    end
    if not context:is_composing() then
        return 2
    end
    if repr == "Return" or repr == "KP_Enter" then
        env.engine:commit_text(context.input)
        context:clear()
        reset_sentence_state(context, env)
        return 1
    end
    if repr == "Escape" then
        context:clear()
        reset_sentence_state(context, env)
        return 1
    end
    if repr == "BackSpace" or repr == "Delete" then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        return 2
    end
    if repr == "Tab" or repr == "ISO_Left_Tab" or repr == "Shift+Tab" then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        state.suspended = true
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        if cycle_candidate(context, repr == "Tab" and 1 or -1) then return 1 end
        return 2
    end
    if repr == "Up" or repr == "Down" or repr == "Page_Up" or repr == "Page_Down" then
        state.proposal = ""
        state.stable = 0
        state.evidence_raw = ""
        state.history = {}
        state.suspended = true
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        return 2
    end
    if repr == "space" then
        if context:has_menu() then
            context:confirm_current_selection()
        end
        reset_sentence_state(context, env)
        return 1
    end
    return 2
end

local function translator(input, seg, env)
    local context = env.engine.context
    local state = sentence_state(context, env)
    local committed_text = state.committed_text
    local committed_raw = state.committed_raw
    local raw = committed_raw .. input
    local results = decode(raw)
    local yielded = 0
    for i = 1, #results do
        local item = results[i]
        if implicit_rank_allowed(
                item, raw, state.continuation_after_auto_commit) and
            (committed_text == "" or
             item.text:sub(1, #committed_text) == committed_text) then
            local text = committed_text == "" and item.text or item.text:sub(#committed_text + 1)
            local preedit = committed_raw == "" and item.segmented or
                trim_segmented_after_raw_prefix(item.segmented, #committed_raw)
            if text ~= "" then
                local cand = Candidate("sentence", seg.start, seg._end, text, "")
                cand.preedit = preedit
                yield(cand)
                yielded = yielded + 1
                if yielded >= candidate_limit then return end
            end
        end
    end
end

local M = {}
M.decode = decode
M.decode_full = decode_full
M.reset_decode_cache = reset_decode_cache
M.results_equal = results_equal
M.model_status = model_status
M.load_ngram_model = kn_reader.load
M.supplement_status = function()
    return {
        path = supplement_matcher.path,
        count = supplement_matcher.count or 0,
        error = supplement_matcher.error
    }
end
M.find_raw_length_for_text = find_raw_length_for_text
M.confidence_proposal = confidence_proposal
M.required_early_commit_history = required_early_commit_history
M.constrain_early_commit_boundary = constrain_early_commit_boundary
M.capture_empty_code_candidate = capture_empty_code_candidate
M.performance_status = function()
    return {
        current = {
            decode_calls = performance.decode_calls,
            decode_total_ms = performance.decode_total_ms,
            decode_max_ms = performance.decode_max_ms,
            page_misses = performance.page_misses,
            page_bytes = performance.page_bytes,
            early_evidence_builds = performance.early_evidence_builds,
            isolation_hits = performance.isolation_hits,
            isolation_misses = performance.isolation_misses
        },
        last = performance.last
    }
end
M.processor = processor
M.translator = translator
return M
