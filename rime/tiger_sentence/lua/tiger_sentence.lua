-- TigerClaw-style sentence lattice for Rime, with optional Lua KN scoring.
-- Lexicon data is loaded from plain-text files (see load_lexicon_data below),
-- so users can edit or replace the code table without re-running exporters.
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

local reset_decode_cache -- forward declaration; assigned below
local clear_model_dependent_caches -- forward declaration; assigned below

local function clear_table(values)
    for key in pairs(values) do
        values[key] = nil
    end
end

-- Plain-text lexicon data.
--   tiger_sentence.codes.txt             "text\tcode", line order = rank
--   tiger_sentence.char_ranks.txt         one character per line, in rank order
--   tiger_sentence.full_code_whitelist.txt whitelist characters
-- Lookup order: user data directory, then shared data directory. Missing
-- ranks disable the isolation penalty and the common-character filter.

local function character_at(text, index)
    local byte = text:byte(index)
    if not byte then
        return nil, index
    end
    local length = 1
    if byte >= 0xF0 then
        length = 4
    elseif byte >= 0xE0 then
        length = 3
    elseif byte >= 0xC0 then
        length = 2
    end
    return text:sub(index, index + length - 1), index + length
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
        local character
        character, index = character_at(text, index)
        chars[#chars + 1] = character
    end
    return chars
end

local function utf_length(text)
    if utf8 and utf8.len then
        local length = utf8.len(text)
        if length then
            return length
        end
    end
    local count = 0
    local index = 1
    while index <= #text do
        _, index = character_at(text, index)
        count = count + 1
    end
    return count
end

local function utf_common_prefix(left, right)
    local left_index, right_index = 1, 1
    local last_left = 0
    while left_index <= #left and right_index <= #right do
        local left_character, next_left = character_at(left, left_index)
        local right_character, next_right = character_at(right, right_index)
        if left_character ~= right_character then
            break
        end
        last_left = next_left - 1
        left_index, right_index = next_left, next_right
    end
    return last_left > 0 and left:sub(1, last_left) or ""
end

local function is_single_character(text)
    return utf_length(text) == 1
end

local function normalize_text_content(content)
    if content:sub(1, 3) == "\239\187\191" then
        content = content:sub(4)
    end
    return (content:gsub("\r\n", "\n"):gsub("\r", "\n"))
end

local function each_content_line(content, callback)
    for line in content:gmatch("[^\n]+") do
        line = line:gsub("^%s+", ""):gsub("%s+$", "")
        if line ~= "" and line:sub(1, 1) ~= "#" then
            callback(line)
        end
    end
end

local function data_directories()
    local directories = {}
    if rime_api then
        if type(rime_api.get_user_data_dir) == "function" then
            local ok, path = pcall(rime_api.get_user_data_dir)
            if ok and type(path) == "string" and path ~= "" then
                directories[#directories + 1] = path
            end
        end
        if type(rime_api.get_shared_data_dir) == "function" then
            local ok, path = pcall(rime_api.get_shared_data_dir)
            if ok and type(path) == "string" and path ~= "" then
                directories[#directories + 1] = path
            end
        end
    end
    return directories
end

local function read_data_file(name)
    for _, directory in ipairs(data_directories()) do
        local path = directory .. "/" .. name
        local ok, handle = pcall(io.open, path, "rb")
        if ok and handle then
            local content = handle:read("*a")
            handle:close()
            if content then
                return content, path
            end
        end
    end
    return nil, nil
end

local function parse_codes_content(content)
    local entries = {}
    local seen = {}
    each_content_line(normalize_text_content(content), function(line)
        local word, code = line:match("^(%S+)%s+(%S+)")
        if word and code then
            code = code:lower()
            if code:match("^[a-z]+$") then
                local key = word .. "\0" .. code
                if not seen[key] then
                    seen[key] = true
                    entries[#entries + 1] = { word = word, code = code }
                end
            end
        end
    end)
    return entries
end

local function parse_ranks_content(content)
    local ranks = {}
    local count = 0
    each_content_line(normalize_text_content(content), function(line)
        local character = character_at(line, 1)
        if character and ranks[character] == nil then
            count = count + 1
            ranks[character] = count
        end
    end)
    return ranks, count
end

local function parse_whitelist_content(content)
    local characters = {}
    each_content_line(normalize_text_content(content), function(line)
        local index = 1
        while index <= #line do
            local character
            character, index = character_at(line, index)
            characters[character] = true
        end
    end)
    return characters
end

local default_high_freq_limit = 1500

local lexicon_state = {
    built = false,
    high_freq_limit = nil,
    codes = {},
    lengths = {},
    max_code_len = 1,
    proper_code_prefixes = {},
    character_ranks = nil,
    ranks_count = 0,
    unknown_character_rank = 20001,
    isolation_enabled = false,
    codes_path = nil,
    codes_entries = 0,
    codes_count = 0,
    ranks_path = nil,
    whitelist_path = nil,
    whitelist_count = 0,
    errors = {}
}

-- Port of SentenceLexiconIndex.Build: exact code table with line-order ranks,
-- per-character primary code (shortest wins, first rank preferred on ties),
-- then the high-frequency optimal-code filter with the full-code whitelist.
local function build_lexicon_index(entries, character_ranks, high_freq_limit, whitelist)
    local exact = {}
    local codes_by_character = {}
    for index = 1, #entries do
        local word = entries[index].word
        local code = entries[index].code
        local texts = exact[code]
        if not texts then
            texts = {}
            exact[code] = texts
        end
        local duplicate = false
        for j = 1, #texts do
            if texts[j] == word then
                duplicate = true
                break
            end
        end
        if not duplicate then
            texts[#texts + 1] = word
        end
        if is_single_character(word) then
            local character_codes = codes_by_character[word]
            if not character_codes then
                character_codes = {}
                codes_by_character[word] = character_codes
            end
            local code_duplicate = false
            for j = 1, #character_codes do
                if character_codes[j] == code then
                    code_duplicate = true
                    break
                end
            end
            if not code_duplicate then
                character_codes[#character_codes + 1] = code
            end
        end
    end

    local common = nil
    if character_ranks and high_freq_limit > 0 then
        local ordered = {}
        for character, rank in pairs(character_ranks) do
            ordered[#ordered + 1] = { character = character, rank = rank }
        end
        table.sort(ordered, function(a, b)
            return a.rank < b.rank
        end)
        common = {}
        local count = math.min(high_freq_limit, #ordered)
        for index = 1, count do
            common[ordered[index].character] = true
        end
    end

    local primary = {}
    for character, codes in pairs(codes_by_character) do
        local best_first, best_any
        for index = 1, #codes do
            local code = codes[index]
            if #code >= 2 then
                if not best_any or #code < #best_any then
                    best_any = code
                end
                local texts = exact[code]
                if texts and texts[1] == character then
                    if not best_first or #code < #best_first then
                        best_first = code
                    end
                end
            end
        end
        local chosen = best_first or best_any
        if chosen then
            primary[character] = chosen
        end
    end

    local filtered = {}
    local length_values = {}
    local max_len = 1
    local prefixes = {}
    for code, texts in pairs(exact) do
        local allowed = {}
        for index = 1, #texts do
            local text = texts[index]
            local allow_non_primary =
                #code == 1
                or not is_single_character(text)
                or not common
                or not common[text]
                or whitelist[text] == true
            if allow_non_primary or primary[text] == code then
                allowed[#allowed + 1] = { t = text, r = index }
            end
        end
        if #allowed > 0 then
            filtered[code] = allowed
            length_values[#length_values + 1] = #code
            if #code > max_len then
                max_len = #code
            end
            for length = 1, #code - 1 do
                prefixes[code:sub(1, length)] = true
            end
        end
    end

    table.sort(length_values)
    local lengths = {}
    local previous = 0
    for _, value in ipairs(length_values) do
        if value ~= previous then
            lengths[#lengths + 1] = value
            previous = value
        end
    end

    return {
        codes = filtered,
        lengths = lengths,
        max_code_len = max_len,
        proper_code_prefixes = prefixes
    }
end

local isolation_cache = {}
local isolation_cache_keys = {}
local isolation_cache_next = 1

local function rebuild_lexicon(limit)
    local errors = {}
    local entries = {}
    local codes_content, codes_path = read_data_file("tiger_sentence.codes.txt")
    if codes_content then
        entries = parse_codes_content(codes_content)
    else
        errors[#errors + 1] = "missing tiger_sentence.codes.txt"
    end

    local character_ranks = nil
    local ranks_count = 0
    local ranks_content, ranks_path = read_data_file("tiger_sentence.char_ranks.txt")
    if ranks_content then
        character_ranks, ranks_count = parse_ranks_content(ranks_content)
        if ranks_count == 0 then
            character_ranks = nil
        end
    end

    local whitelist = {}
    local whitelist_count = 0
    local whitelist_content, whitelist_path =
        read_data_file("tiger_sentence.full_code_whitelist.txt")
    if whitelist_content then
        whitelist = parse_whitelist_content(whitelist_content)
        for _ in pairs(whitelist) do
            whitelist_count = whitelist_count + 1
        end
    end

    local index = build_lexicon_index(entries, character_ranks, limit, whitelist)
    lexicon_state.built = true
    lexicon_state.high_freq_limit = limit
    lexicon_state.codes = index.codes
    lexicon_state.lengths = index.lengths
    lexicon_state.max_code_len = index.max_code_len
    lexicon_state.proper_code_prefixes = index.proper_code_prefixes
    lexicon_state.character_ranks = character_ranks
    lexicon_state.ranks_count = ranks_count
    lexicon_state.unknown_character_rank = ranks_count > 0 and ranks_count + 1 or 20001
    lexicon_state.isolation_enabled = character_ranks ~= nil
    lexicon_state.codes_path = codes_path
    lexicon_state.codes_entries = #entries
    lexicon_state.codes_count = 0
    for _ in pairs(index.codes) do
        lexicon_state.codes_count = lexicon_state.codes_count + 1
    end
    lexicon_state.ranks_path = ranks_path
    lexicon_state.whitelist_path = whitelist_path
    lexicon_state.whitelist_count = whitelist_count
    lexicon_state.errors = errors

    clear_model_dependent_caches()
end

local function configured_high_freq_limit(env)
    -- Mirror CoreRuntimeState.GetSentenceOptimalCodeHighFreqLimit: an absent
    -- key keeps the default; invalid or negative values mean 0 (no limit).
    local schema = env and env.engine and env.engine.schema
    local config = schema and schema.config
    if not config then
        return nil
    end
    local ok, value = pcall(function()
        return config:get_int("tiger_sentence/high_freq_limit")
    end)
    if not ok or type(value) ~= "number" then
        -- Absent or unreadable: keep the default. An explicit 0 in the
        -- schema still disables the optimal-code restriction.
        return nil
    end
    if value < 0 then
        return 0
    end
    return math.floor(value)
end

local function ensure_lexicon(env)
    local limit = configured_high_freq_limit(env)
    if lexicon_state.built then
        if limit == nil or limit == lexicon_state.high_freq_limit then
            return lexicon_state
        end
    else
        if limit == nil then
            limit = default_high_freq_limit
        end
    end
    rebuild_lexicon(limit)
    return lexicon_state
end

local function data_status()
    return {
        built = lexicon_state.built,
        high_freq_limit = lexicon_state.high_freq_limit,
        codes_path = lexicon_state.codes_path,
        codes_entries = lexicon_state.codes_entries,
        codes_count = lexicon_state.codes_count,
        ranks_path = lexicon_state.ranks_path,
        ranks_count = lexicon_state.ranks_count,
        isolation_enabled = lexicon_state.isolation_enabled,
        whitelist_path = lexicon_state.whitelist_path,
        whitelist_count = lexicon_state.whitelist_count,
        errors = lexicon_state.errors
    }
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

local beam_width = 200
-- A 200-wide beam protects ambiguity near the start of a sentence. Once a
-- long composition already has substantial left context, retaining all 200
-- states makes random/low-confidence input slower on every following key and
-- lets synchronous Rime translations queue behind physical input. Bound only
-- the long tail; deriving the limit from the lattice position keeps full and
-- incremental decoding identical.
local long_input_full_beam_length = 24
local long_input_beam_width = 48
local candidate_limit = 20
local max_raw_length = 128
local rank_penalty = 0.03
local emitted_character_reward = 2.0
local isolation_threshold = 3000
local isolation_lambda = 2.0
local early_commit_minimum_share = 0.995
-- Two consecutive generations may confirm only when dissenting Beam mass is
-- below 0.001%; every weaker history keeps the original three-key window.
local early_commit_strong_share = 0.99999
local early_commit_closed_boundary_share = 0.99999
local early_commit_required_evidence = 3
local early_commit_required_strong = 2
local early_commit_maximum_neutral_gap = 3
local early_commit_retained_raw_length = 3
-- 允许单字重码组句 defaults to on; the Rime switch only turns it off.
local allow_duplicate_single_option = "tiger_sentence_allow_duplicate_single"
local active_allow_duplicate_single = true
local BOS = kn_reader.BOS
local EOS = kn_reader.EOS
local kn_model = false
local kn_load_error = nil
-- Test/frontend hook: force-disable the n-gram model so the no-model
-- fallback ordering can be exercised deterministically.
local model_disabled = false
local supplement_matcher = supplement.load_default()
local has_supplements = (supplement_matcher.count or 0) > 0

local logp_cache_limit = 32768
local logp_cache = {}
local logp_cache_keys = {}
local logp_cache_next = 1
local observed_cache_limit = 32768
local observed_cache = {}
local observed_cache_keys = {}
local observed_cache_next = 1
local aggregate_during_expansion_threshold = 128
local decode_cache = {
    raw = nil,
    states = nil,
    result = nil,
    includes_early_commit = false,
    required_text_prefix = "",
    allow_duplicate = true
}
local state_separator = "\31"

local function beam_limit_at(raw_length)
    if raw_length > long_input_full_beam_length then
        return long_input_beam_width
    end
    return beam_width
end

clear_model_dependent_caches = function()
    clear_table(logp_cache)
    clear_table(logp_cache_keys)
    logp_cache_next = 1
    clear_table(observed_cache)
    clear_table(observed_cache_keys)
    observed_cache_next = 1
    clear_table(isolation_cache)
    clear_table(isolation_cache_keys)
    isolation_cache_next = 1
    if reset_decode_cache then
        reset_decode_cache()
    end
end

-- Only committed-prefix state must be visible to both the processor and
-- translator. Keep per-key confidence state on the processor environment so
-- it does not emit a Rime property update (and a redundant UI refresh) for
-- every physical key. The old properties are cleared once for live migration.
local state_keys = {
    committed = "tiger_sentence_committed",
    -- Read once during migration from the old two-property format.
    committed_text = "tiger_sentence_committed_text",
    committed_raw = "tiger_sentence_committed_raw",
    -- Cleared once during migration from the old proposal-history format.
    confidence = "tiger_sentence_confidence",
    proposal = "tiger_sentence_proposal",
    stable = "tiger_sentence_stable",
    evidence_raw = "tiger_sentence_evidence_raw"
}

local function fresh_transient_state()
    return {
        trackers = {},
        last_seen_raw = "",
        last_auto_commit_raw_length = 0,
        suspended = false,
        empty_code_pending = nil,
        continuation_after_auto_commit = false
    }
end

local function transient_state(context, env)
    if not env then
        return fresh_transient_state()
    end
    if not env._tiger_sentence_transient then
        -- The old single-proposal history cannot be migrated into
        -- independent (text, raw boundary) trackers; start clean.
        env._tiger_sentence_transient = fresh_transient_state()
    end
    return env._tiger_sentence_transient
end

local function set_property_if_changed(context, key, value)
    value = value or ""
    if (context:get_property(key) or "") ~= value then
        context:set_property(key, value)
    end
end

local function parse_committed_property(value)
    local separator = (value or ""):find("\t", 1, true)
    if not separator then
        return "", ""
    end
    return value:sub(1, separator - 1), value:sub(separator + 1)
end

local function sentence_state(context, env)
    local transient = transient_state(context, env)
    local combined = context:get_property(state_keys.committed) or ""
    local committed_raw, committed_text = parse_committed_property(combined)
    if combined == "" then
        -- One-time migration from the old two-property format.
        local old_raw = context:get_property(state_keys.committed_raw) or ""
        local old_text = context:get_property(state_keys.committed_text) or ""
        if old_raw ~= "" or old_text ~= "" then
            committed_raw, committed_text = old_raw, old_text
            set_property_if_changed(context, state_keys.committed,
                old_raw .. "\t" .. old_text)
            set_property_if_changed(context, state_keys.committed_raw, "")
            set_property_if_changed(context, state_keys.committed_text, "")
        end
    end
    transient.committed_text = committed_text
    transient.committed_raw = committed_raw
    transient.trackers = transient.trackers or {}
    transient.last_seen_raw = transient.last_seen_raw or ""
    transient.last_auto_commit_raw_length = transient.last_auto_commit_raw_length or 0
    transient.suspended = transient.suspended or false
    transient.continuation_after_auto_commit =
        transient.continuation_after_auto_commit or false
    return transient
end

local function save_transient_state(context, state, env)
    if env then
        env._tiger_sentence_transient = state
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
    set_property_if_changed(context, state_keys.committed,
        (state.committed_raw or "") .. "\t" .. (state.committed_text or ""))
    save_transient_state(context, state, env)
end

local function reset_sentence_state(context, env, continuation_after_auto_commit)
    finish_performance_sample()
    local transient = fresh_transient_state()
    transient.continuation_after_auto_commit = continuation_after_auto_commit or false
    save_sentence_state(context, {
        committed_text = "",
        committed_raw = "",
        trackers = transient.trackers,
        last_seen_raw = transient.last_seen_raw,
        last_auto_commit_raw_length = transient.last_auto_commit_raw_length,
        suspended = transient.suspended,
        empty_code_pending = transient.empty_code_pending,
        continuation_after_auto_commit = transient.continuation_after_auto_commit
    }, env)
end

local function ensure_kn()
    if model_disabled then
        return nil
    end
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

local function candidate_chars(candidate)
    if not candidate._chars then
        candidate._chars = utf_chars(candidate.t)
    end
    return candidate._chars
end

local function candidate_char_count(candidate)
    if candidate._char_count == nil then
        candidate._char_count = utf_length(candidate.t)
    end
    return candidate._char_count
end

local function candidate_is_single(candidate)
    if candidate._single == nil then
        candidate._single = candidate_char_count(candidate) == 1
    end
    return candidate._single
end

local function eligible_candidates(candidates, selected_rank, whole_input_edge, allow_duplicate_single)
    if selected_rank == 0 then
        if whole_input_edge then
            return candidates
        end
        if allow_duplicate_single then
            -- Rank one plus single-character duplicates; non-first
            -- multi-character words still need an explicit selector.
            local cached = candidates._duplicate
            if cached then
                return cached
            end
            local selected = {}
            for index = 1, #candidates do
                if candidates[index].r == 1 or candidate_is_single(candidates[index]) then
                    selected[#selected + 1] = candidates[index]
                end
            end
            candidates._duplicate = selected
            return selected
        end
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
    if not model or not model.has_observed_bigram or not text or text == "" or
        not lexicon_state.isolation_enabled then
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
        local rank = (lexicon_state.character_ranks[chars[index]] or
            lexicon_state.unknown_character_rank)
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
    if required == "" then
        local reachable = { [0] = true }
        for position = 0, #raw - 1 do
            if reachable[position] then
                for i = 1, #lexicon_state.lengths do
                    local code_end = position + lexicon_state.lengths[i]
                    if code_end <= #raw then
                        local candidates = lexicon_state.codes[raw:sub(position + 1, code_end)]
                        if candidates then
                            local selected_rank, consumed_end = parse_selector(raw, code_end)
                            local whole_input_edge = position == 0 and consumed_end == #raw
                            if not (#raw > 1 and consumed_end - position < 2) and
                                #eligible_candidates(candidates, selected_rank,
                                    whole_input_edge, active_allow_duplicate_single) > 0 then
                                reachable[consumed_end] = true
                            end
                        end
                    end
                end
            end
        end
        return reachable[#raw] or false
    end

    local states = {}
    for index = 0, #raw do states[index] = {} end
    states[0][0] = true
    for position = 0, #raw - 1 do
        if next(states[position]) then
            for i = 1, #lexicon_state.lengths do
                local code_length = lexicon_state.lengths[i]
                local code_end = position + code_length
                if code_end <= #raw then
                    local candidates = lexicon_state.codes[raw:sub(position + 1, code_end)]
                    if candidates then
                        local selected_rank, consumed_end = parse_selector(raw, code_end)
                        local whole_input_edge = position == 0 and consumed_end == #raw
                        if not (#raw > 1 and consumed_end - position < 2) then
                            local selected = eligible_candidates(
                                candidates, selected_rank, whole_input_edge,
                                active_allow_duplicate_single)
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

local function state_better_rank_first(left, right)
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

local function state_better_score_first(left, right)
    if left.score == right.score then
        local left_rank = left.max_rank or 1
        local right_rank = right.max_rank or 1
        if left_rank ~= right_rank then
            return left_rank < right_rank
        end
        return left.text < right.text
    end
    return left.score > right.score
end

-- Without an n-gram model the per-character reward dominates the score and
-- multi-segment concatenations would outrank direct code entries. Fall back
-- to lexicon rank first, then fewer lexicon edges, then score.
local function state_better_no_model(left, right)
    local left_rank = left.max_rank or 1
    local right_rank = right.max_rank or 1
    if left_rank ~= right_rank then
        return left_rank < right_rank
    end
    local left_edges = left.edge_count or 0
    local right_edges = right.edge_count or 0
    if left_edges ~= right_edges then
        return left_edges < right_edges
    end
    if left.score == right.score then
        return left.text < right.text
    end
    return left.score > right.score
end

-- Mirrors SentenceInputDecoder.GetBeamStateComparison. Resolve this once per
-- bucket instead of probing model availability for every heap comparison.
local function current_state_comparator()
    if not ensure_kn() then
        return state_better_no_model
    end
    if active_allow_duplicate_single then
        return state_better_score_first
    end
    return state_better_rank_first
end

local function duplicate_better(item, previous)
    local item_rank = item.max_rank or 1
    local previous_rank = previous.max_rank or 1
    if item_rank ~= previous_rank then
        return item_rank < previous_rank
    end
    if item.score ~= previous.score then
        return item.score > previous.score
    end
    return (item.edge_count or 0) < (previous.edge_count or 0)
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

local function sift_worst_up(heap, index, better)
    while index > 1 do
        local parent = math.floor(index / 2)
        if not better(heap[parent], heap[index]) then
            return
        end
        heap[parent], heap[index] = heap[index], heap[parent]
        index = parent
    end
end

local function sift_worst_down(heap, index, better)
    while true do
        local left = index * 2
        if left > #heap then
            return
        end
        local right = left + 1
        local worse = left
        if right <= #heap and better(heap[left], heap[right]) then
            worse = right
        end
        if not better(heap[index], heap[worse]) then
            return
        end
        heap[index], heap[worse] = heap[worse], heap[index]
        index = worse
    end
end

local function select_exact_top(values, limit, better)
    better = better or state_better_rank_first
    local heap = {}
    for i = 1, #values do
        local item = values[i]
        if #heap < limit then
            heap[#heap + 1] = item
            sift_worst_up(heap, #heap, better)
        elseif better(item, heap[1]) then
            heap[1] = item
            sift_worst_down(heap, 1, better)
        end
    end
    table.sort(heap, better)
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
    local better = current_state_comparator()
    if truncated_now then
        result = select_exact_top(result, limit, better)
    else
        table.sort(result, better)
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
        raw_length = 0,
        edge_count = 0
    })
    return states
end

local function expand_range(raw, states, from_pos, length, minimum_consumed_end)
    minimum_consumed_end = minimum_consumed_end or -1
    for position = from_pos, length - 1 do
        local current = dedup_limit(states[position], beam_limit_at(position))
        states[position] = current
        if #current > 0 then
            for i = 1, #lexicon_state.lengths do
                local code_length = lexicon_state.lengths[i]
                if position + code_length <= length then
                    local code = raw:sub(position + 1, position + code_length)
                    local candidates = lexicon_state.codes[code]
                    if candidates then
                        local selected_rank, consumed_end = parse_selector(raw, position + code_length)
                        local whole_input_edge = position == 0 and consumed_end == length
                        if consumed_end > minimum_consumed_end and
                            not (length > 1 and consumed_end - position < 2) then
                            local selected_candidates = eligible_candidates(
                                candidates, selected_rank, whole_input_edge,
                                active_allow_duplicate_single)
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
                                        raw_length = consumed_end,
                                        edge_count = (item.edge_count or 0) + 1
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
        edge_count = item.edge_count or 0,
        path = item
    }
end

local function add_early_commit_pool_candidate(pool, pool_index, candidate)
    if not candidate.text or candidate.text == "" then
        return
    end
    local raw_length = candidate.path and candidate.path.raw_length or 0
    local boundary = pool_index[raw_length]
    if not boundary then
        boundary = {}
        pool_index[raw_length] = boundary
    end
    local previous = boundary[candidate.text]
    if not previous then
        previous = {
            text = candidate.text,
            raw_length = raw_length,
            confidence_score = candidate.confidence_score,
            path = candidate.path
        }
        boundary[candidate.text] = previous
        pool[#pool + 1] = previous
        return
    end
    local combined = logsumexp(previous.confidence_score, candidate.confidence_score)
    if candidate.confidence_score > previous.confidence_score then
        previous.text = candidate.text
        previous.raw_length = raw_length
        previous.confidence_score = candidate.confidence_score
        previous.path = candidate.path
    end
    previous.confidence_score = combined
end

-- Per-(text prefix, raw boundary) evidence, mirroring
-- SentenceInputDecoder.BuildPrefixEvidence. Boundary mass is counted once per
-- candidate and raw boundary so negligible crossing paths cannot veto an
-- otherwise closed boundary.
local function build_prefix_evidence(pool)
    local prefixes = {}
    if #pool == 0 then
        return prefixes
    end
    local max_score = pool[1].confidence_score or pool[1].score
    for i = 2, #pool do
        local score = pool[i].confidence_score or pool[i].score
        if score > max_score then
            max_score = score
        end
    end
    local total = 0
    local weights = {}
    for i = 1, #pool do
        weights[i] = math.exp((pool[i].confidence_score or pool[i].score) - max_score)
        total = total + weights[i]
    end
    if total <= 0 then
        return prefixes
    end
    local mass_by_boundary = {}
    local order = {}
    local boundary_mass = {}
    for i = 1, #pool do
        local item = pool[i]
        local weight = weights[i]
        local state = item.path
        while state do
            local prefix_text = state.text
            if prefix_text == nil then
                local text_length = state.text_length or 0
                prefix_text = text_length > 0 and item.text:sub(1, text_length) or ""
            end
            if prefix_text ~= "" and #prefix_text <= #item.text then
                local boundary = mass_by_boundary[state.raw_length]
                if not boundary then
                    boundary = {}
                    mass_by_boundary[state.raw_length] = boundary
                end
                local entry = boundary[prefix_text]
                if not entry then
                    entry = {
                        text = prefix_text,
                        raw_length = state.raw_length,
                        weight = 0.0,
                        text_char_count = utf_length(prefix_text)
                    }
                    boundary[prefix_text] = entry
                    order[#order + 1] = entry
                end
                entry.weight = entry.weight + weight
                -- Raw lengths strictly increase along a path, so a candidate
                -- can visit each boundary only once.
                boundary_mass[state.raw_length] =
                    (boundary_mass[state.raw_length] or 0) + weight
            end
            state = state.previous
        end
    end
    for i = 1, #order do
        local entry = order[i]
        local boundary_share = (boundary_mass[entry.raw_length] or 0) / total
        prefixes[#prefixes + 1] = {
            text = entry.text,
            raw_length = entry.raw_length,
            share = entry.weight / total,
            boundary_share = boundary_share,
            boundary_closed = boundary_share >= early_commit_closed_boundary_share
        }
    end
    return prefixes
end

local function has_low_confidence_completed_generation(candidates)
    if #candidates == 0 then
        return false
    end
    local max_score = candidates[1].confidence_score or candidates[1].score
    for i = 2, #candidates do
        local score = candidates[i].confidence_score or candidates[i].score
        if score > max_score then
            max_score = score
        end
    end
    local total = 0
    for i = 1, #candidates do
        total = total + math.exp((candidates[i].confidence_score or candidates[i].score) - max_score)
    end
    return total > 0 and 1.0 / total < early_commit_minimum_share
end

local function incomplete_code_tail(tail)
    if tail == "" or not tail:match("^[A-Za-z]+$") or
        not lexicon_state.proper_code_prefixes[tail] then
        return false
    end
    return #tail < 2 or lexicon_state.codes[tail] == nil
end

local function truncated_early_commit_evidence()
    return {
        prefixes = {},
        proposal = "",
        proposal_share = 0.0,
        raw_lengths = {},
        neutral_incomplete_tail = false,
        merged_incomplete_tail = false,
        neutral_low_confidence = false,
        confidence_truncated = true
    }
end

local function build_early_commit_evidence(
    raw, states, completed, completed_truncated, required_text_prefix)
    performance.early_evidence_builds = performance.early_evidence_builds + 1
    -- A truncated confidence pool is never allowed to advance or preserve an
    -- early-commit tracker. Avoid materializing thousands of prefix records
    -- that try_early_commit would immediately discard.
    if completed_truncated then
        return truncated_early_commit_evidence()
    end
    local pool = {}
    local pool_index = {}
    local visible = {}
    for i = 1, #completed do
        local candidate = completed[i]
        if candidate.text and candidate.text ~= "" and
            (required_text_prefix == "" or
             candidate.text:sub(1, #required_text_prefix) == required_text_prefix) then
            visible[#visible + 1] = candidate
            add_early_commit_pool_candidate(pool, pool_index, candidate)
        end
    end

    local truncated = completed_truncated or false
    local merged_incomplete_tail = false
    local maximum_tail_length = math.min(lexicon_state.max_code_len - 1, #raw - 1)
    for tail_length = 1, maximum_tail_length do
        local consumed_length = #raw - tail_length
        local tail = raw:sub(consumed_length + 1)
        if incomplete_code_tail(tail) and states[consumed_length] then
            local partial = dedup_limit(
                states[consumed_length], beam_limit_at(consumed_length))
            states[consumed_length] = partial
            local added = false
            for i = 1, #partial do
                local candidate = evaluate_state(partial[i])
                if candidate.text and candidate.text ~= "" and
                    (required_text_prefix == "" or
                     candidate.text:sub(1, #required_text_prefix) == required_text_prefix) then
                    add_early_commit_pool_candidate(pool, pool_index, candidate)
                    added = true
                end
            end
            if added then
                merged_incomplete_tail = true
                if partial._truncated then
                    return truncated_early_commit_evidence()
                end
            end
        end
    end

    local prefixes = build_prefix_evidence(pool)

    local proposal = ""
    local proposal_share = 0.0
    local proposal_raw_length = 0
    local raw_lengths = {}
    for i = 1, #prefixes do
        local prefix = prefixes[i]
        if prefix.boundary_closed then
            local current = raw_lengths[prefix.text]
            if not current or prefix.share > current.share or
                (prefix.share == current.share and
                 prefix.raw_length < current.raw_length) then
                raw_lengths[prefix.text] = prefix
            end
            if prefix.share >= early_commit_minimum_share then
                local replace = proposal == ""
                if not replace then
                    local prefix_chars = prefix.text_char_count or utf_length(prefix.text)
                    local proposal_chars = utf_length(proposal)
                    if prefix_chars ~= proposal_chars then
                        replace = prefix_chars > proposal_chars
                    elseif prefix.share ~= proposal_share then
                        replace = prefix.share > proposal_share
                    else
                        replace = prefix.raw_length < proposal_raw_length
                    end
                end
                if replace then
                    proposal = prefix.text
                    proposal_share = prefix.share
                    proposal_raw_length = prefix.raw_length
                end
            end
        end
    end
    local raw_length_values = {}
    for text, prefix in pairs(raw_lengths) do
        raw_length_values[text] = prefix.raw_length
    end

    return {
        prefixes = prefixes,
        proposal = proposal,
        proposal_share = proposal_share,
        raw_lengths = raw_length_values,
        neutral_incomplete_tail = #visible == 0 and merged_incomplete_tail,
        merged_incomplete_tail = merged_incomplete_tail,
        neutral_low_confidence = has_low_confidence_completed_generation(visible),
        confidence_truncated = truncated
    }
end

-- With the duplicate single-character option enabled, segmented candidate
-- lists compete by score; whole-input single edges keep lexicon-rank order.
local function prefer_score_over_lexicon_rank(values)
    if not active_allow_duplicate_single then
        return false
    end
    for i = 1, #values do
        local path = values[i].path
        if path and path.previous and (path.previous.raw_length or 0) > 0 then
            return true
        end
    end
    return false
end

local function emit(raw, states, length, include_early_commit, required_text_prefix)
    local completed = dedup_limit(states[length], beam_limit_at(length))
    local all_candidates = {}
    for i = 1, #completed do
        all_candidates[i] = evaluate_state(completed[i])
    end
    local better
    if not ensure_kn() then
        better = state_better_no_model
    else
        better = prefer_score_over_lexicon_rank(all_candidates)
            and state_better_score_first
            or state_better_rank_first
    end
    local result = select_exact_top(all_candidates, candidate_limit, better)
    for i = 1, #result do
        result[i].segmented = segmented_from_path(raw, result[i].path)
    end
    result.early_commit_evidence = {
        prefixes = {},
        proposal = "",
        proposal_share = 0.0,
        raw_lengths = {},
        neutral_incomplete_tail = false,
        merged_incomplete_tail = false,
        neutral_low_confidence = false,
        confidence_truncated = completed._truncated or false
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

local function prefix_evidence_equal(left, right)
    local left_prefixes = left.prefixes or {}
    local right_prefixes = right.prefixes or {}
    if #left_prefixes ~= #right_prefixes then
        return false
    end
    local right_by_key = {}
    for i = 1, #right_prefixes do
        right_by_key[right_prefixes[i].text .. state_separator ..
            tostring(right_prefixes[i].raw_length)] = right_prefixes[i]
    end
    for i = 1, #left_prefixes do
        local item = left_prefixes[i]
        local other = right_by_key[item.text .. state_separator ..
            tostring(item.raw_length)]
        if not other or other.boundary_closed ~= item.boundary_closed or
            math.abs((other.share or 0.0) - (item.share or 0.0)) > 1e-9 or
            math.abs((other.boundary_share or 0.0) - (item.boundary_share or 0.0)) > 1e-9 then
            return false
        end
    end
    if (left.merged_incomplete_tail or false) ~= (right.merged_incomplete_tail or false) or
        (left.neutral_low_confidence or false) ~= (right.neutral_low_confidence or false) then
        return false
    end
    return true
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
        (right_evidence.confidence_truncated or false) or
        not prefix_evidence_equal(left_evidence, right_evidence) then
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
            or (left[i].max_rank or 1) ~= (right[i].max_rank or 1)
            or (left[i].edge_count or 0) ~= (right[i].edge_count or 0) then
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
    ensure_lexicon(nil)
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
    ensure_lexicon(nil)
    local raw = normalize(raw_code)
    required_text_prefix = required_text_prefix or ""
    if raw == "" or not has_letter(raw) then
        decode_cache.raw = raw
        decode_cache.states = nil
        decode_cache.result = {}
        decode_cache.includes_early_commit = include_early_commit or false
        decode_cache.required_text_prefix = required_text_prefix
        decode_cache.allow_duplicate = active_allow_duplicate_single
        return decode_cache.result
    end
    if decode_cache.raw == raw and decode_cache.result and
        decode_cache.allow_duplicate == active_allow_duplicate_single and
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
    if old_states and old_raw == raw and
        decode_cache.allow_duplicate == active_allow_duplicate_single then
        -- The translator normally cached this exact composition without
        -- confidence metadata. The processor may immediately request the same
        -- lattice with early-commit evidence before accepting the next key.
        -- Reuse the lattice; rebuilding a long composition here caused the
        -- visible pause after roughly forty uncommitted keys.
        states = old_states
    elseif old_states and type(old_raw) == "string" and old_raw ~= "" then
        local old_n = #old_raw
        -- Whole-input one-key edges and implicit non-first ranks may become
        -- segmented after an append. Rebuild the small four-code prefix so
        -- formerly legal states cannot leak into the longer input.
        if old_n <= 4 or length <= 4 then
            states = nil
        elseif length > old_n and raw:sub(1, old_n) == old_raw then
            local max_consume = lexicon_state.max_code_len + trailing_selector_span(raw)
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
    decode_cache.allow_duplicate = active_allow_duplicate_single
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

-- Independent per-(text prefix, raw boundary) trackers, mirroring
-- InputMethodEngine.TryAutoCommitSentencePrefix.
local function find_prefix_evidence(prefixes, text, raw_length)
    for i = 1, #prefixes do
        if prefixes[i].text == text and prefixes[i].raw_length == raw_length then
            return prefixes[i]
        end
    end
    return nil
end

local function common_text_prefix(left, right)
    return utf_common_prefix(left, right)
end

local function prefix_extends(left, right)
    return left:sub(1, #right) == right or right:sub(1, #left) == left
end

local function prefix_contradicted(tracker, prefixes)
    if #prefixes == 0 then
        return false
    end
    local self = find_prefix_evidence(prefixes, tracker.text, tracker.raw_length)
    local self_share = self and self.share or 0.0
    for i = 1, #prefixes do
        local prefix = prefixes[i]
        if prefix.text ~= "" and prefix.text ~= tracker.text and
            not prefix_extends(prefix.text, tracker.text) then
            local shared_stem = common_text_prefix(prefix.text, tracker.text)
            if shared_stem ~= "" and #shared_stem < #tracker.text then
                if self == nil or prefix.share > self_share then
                    return true
                end
            end
        end
    end
    return false
end

local function prefix_belongs_to_visible(prefix, visible)
    for i = 1, #visible do
        local candidate = visible[i]
        if candidate.text and candidate.text:sub(1, #prefix.text) == prefix.text then
            local state = candidate.path
            while state do
                if state.raw_length == prefix.raw_length and
                    (state.text == prefix.text or
                     (state.text == nil and state.text_length == #prefix.text)) then
                    return true
                end
                state = state.previous
            end
        end
    end
    return false
end

local function retain_trackers_without_counting(trackers, prefixes)
    local next_trackers = {}
    for key, tracker in pairs(trackers) do
        if not prefix_contradicted(tracker, prefixes) and
            find_prefix_evidence(prefixes, tracker.text, tracker.raw_length) then
            tracker.gap_count = tracker.gap_count + 1
            if tracker.gap_count <= early_commit_maximum_neutral_gap then
                local current = find_prefix_evidence(
                    prefixes, tracker.text, tracker.raw_length)
                if current then
                    tracker.last_share = current.share
                end
                next_trackers[key] = tracker
            end
        end
    end
    return next_trackers
end

local function tracker_better(left, right)
    local left_chars = left.text_char_count or utf_length(left.text)
    local right_chars = right.text_char_count or utf_length(right.text)
    if left_chars ~= right_chars then
        return left_chars > right_chars
    end
    if left.last_share ~= right.last_share then
        return left.last_share > right.last_share
    end
    return left.raw_length < right.raw_length
end

local function has_selection_suffix(raw)
    return raw and raw:find("[;'0-9]") ~= nil
end

local function implicit_rank_allowed(candidate, raw, continuation_after_auto_commit)
    if not continuation_after_auto_commit then
        return true
    end
    -- Continuations after empty-code auto commit keep implicit first ranks
    -- only; an explicit selector suffix still unlocks any rank.
    return has_selection_suffix(raw) or (candidate.max_rank or 1) <= 1
end

local function strong_empty_code_candidate(candidate, eligible, visible_top, pool_truncated)
    -- Confidence shares computed over a truncated pool are not trustworthy;
    -- Windows requires ConfidenceTruncated == false for the strong accept.
    if pool_truncated then
        return false
    end
    if not visible_top or candidate.text ~= visible_top.text then
        return false
    end
    local max_score = eligible[1].confidence_score or eligible[1].score
    for i = 2, #eligible do
        local score = eligible[i].confidence_score or eligible[i].score
        if score > max_score then
            max_score = score
        end
    end
    local total = 0
    local candidate_mass = 0
    for i = 1, #eligible do
        local mass = math.exp(
            (eligible[i].confidence_score or eligible[i].score) - max_score)
        total = total + mass
        if eligible[i] == candidate then
            candidate_mass = candidate_mass + mass
        end
    end
    return total > 0 and candidate_mass / total >= early_commit_strong_share
end

-- Mirrors InputMethodEngine.GetEmptyCodeAutoCommitCandidate: accept exactly
-- one group-eligible candidate, or a group-eligible visible top whose
-- untruncated confidence share reaches the strong threshold.
local function capture_empty_code_candidate(full_before, committed_text)
    local decoded = decode(full_before, false, committed_text)
    if #decoded == 0 then
        return nil
    end
    local visible_top = decoded[1]
    local eligible = {}
    local restrict = not has_selection_suffix(full_before)
    for i = 1, #decoded do
        -- Whole-input non-first ranks are visible for explicit selection, but
        -- are not legal implicit segments after the appended key makes the
        -- edge dead, so they never join the empty-code eligible group.
        if not restrict or (decoded[i].max_rank or 1) <= 1 then
            eligible[#eligible + 1] = decoded[i]
        end
    end
    if #eligible == 0 then
        return nil
    end
    local first = eligible[1]
    if not first.text or first.text == "" or
        first.text:sub(1, #committed_text) ~= committed_text or
        #first.text <= #committed_text then
        return nil
    end
    local pool_truncated =
        (decoded.early_commit_evidence or {}).confidence_truncated or false
    if #eligible > 1 and
        not strong_empty_code_candidate(first, eligible, visible_top, pool_truncated) then
        return nil
    end
    local previous = first.path and first.path.previous
    return {
        candidate_text = first.text,
        committed_text = committed_text,
        base_raw_length = #full_before,
        last_segment_start = previous and previous.raw_length or 0
    }
end

local function cycle_candidate_highlight(context, step)
    if not context:has_menu() then return false end
    local composition = context.composition
    if not composition or composition:empty() then return false end
    local segment = composition:back()
    local menu = segment and segment.menu
    if not menu then return false end
    local count = menu:candidate_count()
    if not count or count <= 0 then return false end
    local selected = segment.selected_index or 0
    local target = (selected + step) % count
    -- Highlight changes only the active index. Context:select(), by contrast,
    -- selects the candidate and may commit a whole-composition sentence.
    if type(context.highlight) == "function" and context:highlight(target) then
        return true
    end
    -- Compatibility fallback for a librime build predating Context::Highlight.
    -- librime-lua exposes Segment.selected_index as a writable field.
    segment.selected_index = target
    return segment.selected_index == target
end

reset_decode_cache = function()
    decode_cache.raw = nil
    decode_cache.states = nil
    decode_cache.result = nil
    decode_cache.includes_early_commit = false
    decode_cache.required_text_prefix = ""
    decode_cache.allow_duplicate = active_allow_duplicate_single
end

local function set_allow_duplicate_single(context)
    local allowed = true
    if context and type(context.get_option) == "function" then
        local ok, value = pcall(context.get_option, context, allow_duplicate_single_option)
        if ok and value == false then
            allowed = false
        end
    end
    if allowed ~= active_allow_duplicate_single then
        active_allow_duplicate_single = allowed
        reset_decode_cache()
    end
    return allowed
end

-- Replace the live composition with one atomic input mutation. clear() plus
-- push_input() emits an intermediate empty-composition notification that some
-- frontends render as a one-frame flash; assigning context.input triggers a
-- single change event carrying the final raw code. The set_input method hook
-- exists for tests; the clear+push fallback covers frontends whose context
-- does not accept direct input assignment.
local function restore_composition_input(context, value)
    if type(context.set_input) == "function" then
        context:set_input(value)
        return
    end
    local ok = pcall(function()
        context.input = value
    end)
    if ok and context.input == value then
        return
    end
    context:clear()
    if value ~= "" then
        context:push_input(value)
    end
end

local function get_min_retained_raw_length(env)
    local schema = env and env.engine and env.engine.schema
    local config = schema and schema.config
    if not config then
        return 0
    end
    local ok, value = pcall(function()
        return config:get_int("tiger_sentence/min_retained_raw_length")
    end)
    if ok and type(value) == "number" and value >= 0 then
        return math.floor(value)
    end
    return 0
end

-- Mirror InputMethodEngine.IsTextEndingWithDigit: both half- and
-- full-width digits arm the decimal-point follow-up.
local function ends_with_digit(text)
    if not text or text == "" then
        return false
    end
    local chars = utf_chars(text)
    local last = chars[#chars]
    return last:match("^[0-9]$") ~= nil or last:match("^\xEF\xBC[\x90-\x99]") ~= nil
end

-- Standalone modifier key events must not consume the armed decimal point.
local function is_modifier_repr(repr)
    return repr:match("^Shift") ~= nil or
        repr:match("^Control") ~= nil or
        repr:match("^Alt") ~= nil or
        repr:match("^Super") ~= nil or
        repr:match("^Meta") ~= nil or
        repr:match("^Caps_Lock$") ~= nil or
        repr:match("^Num_Lock$") ~= nil or
        repr:match("^ISO_Level") ~= nil or
        repr == "Mode_switch"
end

local function try_empty_code_commit(env, state, full_before, appended_letter)
    local context = env.engine.context
    -- Empty-code auto commit is part of 整句自动提前上屏; there is no
    -- separate switch, so the early-commit option gates it as well.
    if not context:get_option("tiger_sentence_early_commit") or state.suspended then
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        return false
    end
    local pending = state.empty_code_pending or
        capture_empty_code_candidate(full_before, state.committed_text)
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
    if lexicon_state.proper_code_prefixes[extended_last_segment] then
        save_transient_state(context, state, env)
        return false
    end

    local required_retain = get_min_retained_raw_length(env)
    if required_retain > 0 and #full_raw - pending.base_raw_length < required_retain then
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
    state.last_auto_commit_raw_length = pending.base_raw_length
    state.trackers = {}
    state.last_seen_raw = ""
    state.suspended = false
    state.empty_code_pending = nil
    state.continuation_after_auto_commit = true
    env.engine:commit_text(commit)
    if ends_with_digit(commit) then
        env._tiger_sentence_dot_armed = true
    end
    save_sentence_state(context, state, env)
    reset_decode_cache()
    restore_composition_input(context, retained_raw)
    return true
end

local function reset_early_evidence(state)
    state.trackers = {}
    state.last_seen_raw = ""
end

local function try_commit_mature_prefix(env, state, evidence_raw)
    local configured = get_min_retained_raw_length(env)
    local retain = configured > 0
        and math.max(early_commit_retained_raw_length, configured)
        or early_commit_retained_raw_length
    local selected = nil
    for _, tracker in pairs(state.trackers) do
        if (tracker.evidence_count >= early_commit_required_evidence or
            tracker.strong_count >= early_commit_required_strong) and
            tracker.raw_length > #state.committed_raw and
            tracker.raw_length <= #evidence_raw and
            #evidence_raw - tracker.raw_length >= retain and
            #tracker.text > #state.committed_text and
            tracker.text:sub(1, #state.committed_text) == state.committed_text then
            if not selected or tracker_better(tracker, selected) then
                selected = tracker
            end
        end
    end
    if not selected or
        #evidence_raw - state.last_auto_commit_raw_length <
            early_commit_retained_raw_length then
        return false
    end
    local commit = selected.text:sub(#state.committed_text + 1)
    if utf_length(commit) < 1 then
        return false
    end
    local context = env.engine.context
    state.committed_text = selected.text
    state.committed_raw = evidence_raw:sub(1, selected.raw_length)
    state.last_auto_commit_raw_length = selected.raw_length
    -- Probabilistic early commit chooses a boundary from complete sentence
    -- paths that have already competed in the language model. Keep those
    -- paths eligible after committing their common prefix; the stricter
    -- first-rank-only continuation rule is only for empty-code commit.
    state.continuation_after_auto_commit = false
    reset_early_evidence(state)
    save_sentence_state(context, state, env)
    env.engine:commit_text(commit)
    if ends_with_digit(commit) then
        env._tiger_sentence_dot_armed = true
    end
    restore_composition_input(context, evidence_raw:sub(selected.raw_length + 1))
    return true
end

local function try_early_commit(env)
    local context = env.engine.context
    local state = sentence_state(context, env)
    local live_raw = context.input or ""

    if not context:get_option("tiger_sentence_early_commit") or state.suspended then
        reset_early_evidence(state)
        save_transient_state(context, state, env)
        return
    end

    -- The first four raw encoding keys are never counted as stable evidence.
    local full_raw = state.committed_raw .. live_raw
    if #full_raw <= 4 then
        reset_early_evidence(state)
        save_transient_state(context, state, env)
        return
    end

    local decoded = decode(full_raw, true, state.committed_text)
    local early_commit_evidence = decoded.early_commit_evidence or {}
    if early_commit_evidence.confidence_truncated then
        reset_early_evidence(state)
        save_transient_state(context, state, env)
        return
    end
    -- The decode above ran synchronously for exactly this raw code, so the
    -- evidence generation always matches the live composition.
    local evidence_raw = full_raw

    if state.last_seen_raw == evidence_raw then
        try_commit_mature_prefix(env, state, evidence_raw)
        return
    end

    local extends_previous_generation = state.last_seen_raw == "" or
        (#evidence_raw == #state.last_seen_raw + 1 and
         evidence_raw:sub(1, #state.last_seen_raw) == state.last_seen_raw)
    if not extends_previous_generation then
        state.trackers = {}
    end
    state.last_seen_raw = evidence_raw

    local prefixes = early_commit_evidence.prefixes or {}
    local accepted_top = nil
    if #decoded > 0 and (decoded[1].supplement_score or 0.0) > 0.0 then
        accepted_top = decoded[1].text
    end

    local merged_incomplete_tail = early_commit_evidence.merged_incomplete_tail or false
    local qualifying = {}
    for i = 1, #prefixes do
        local prefix = prefixes[i]
        if prefix.text and prefix.text ~= "" and
            prefix.boundary_closed and
            prefix.share >= early_commit_minimum_share and
            prefix.raw_length > #state.committed_raw and
            #prefix.text > #state.committed_text and
            prefix.text:sub(1, #state.committed_text) == state.committed_text and
            (accepted_top == nil or accepted_top:sub(1, #prefix.text) == prefix.text) and
            (merged_incomplete_tail or prefix_belongs_to_visible(prefix, decoded)) then
            qualifying[prefix.text .. state_separator .. tostring(prefix.raw_length)] = prefix
        end
    end

    local retain_without_counting = next(qualifying) == nil and
        (early_commit_evidence.neutral_low_confidence or merged_incomplete_tail)
    if retain_without_counting then
        -- Comparison-only gap: keep supported trackers alive without letting
        -- them gain evidence, for at most three consecutive generations.
        state.trackers = retain_trackers_without_counting(state.trackers, prefixes)
        save_transient_state(context, state, env)
        try_commit_mature_prefix(env, state, evidence_raw)
        return
    end

    local next_trackers = {}
    for key, prefix in pairs(qualifying) do
        local tracker = state.trackers[key]
        if not tracker then
            tracker = {
                text = prefix.text,
                text_char_count = prefix.text_char_count or utf_length(prefix.text),
                raw_length = prefix.raw_length,
                evidence_count = 0,
                strong_count = 0,
                gap_count = 0,
                last_share = 0.0
            }
        end
        tracker.evidence_count = math.min(
            early_commit_required_evidence, tracker.evidence_count + 1)
        tracker.strong_count = prefix.share >= early_commit_strong_share
            and math.min(early_commit_required_strong, tracker.strong_count + 1)
            or 0
        tracker.gap_count = 0
        tracker.last_share = prefix.share
        next_trackers[key] = tracker
    end
    state.trackers = next_trackers
    save_transient_state(context, state, env)
    try_commit_mature_prefix(env, state, evidence_raw)
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
    ensure_lexicon(env)
    local context = env.engine.context
    local state = sentence_state(context, env)
    local repr = key_event:repr()
    -- Mirror InputMethodEngine._dotAfterDigitArmed: the period following a
    -- digit output becomes an ASCII decimal point; any non-modifier key
    -- consumes the arm.
    local dot_armed = env._tiger_sentence_dot_armed or false
    if not is_modifier_repr(repr) then
        env._tiger_sentence_dot_armed = false
    end
    local ch = is_plain_char_key(key_event, repr)
    if ch then
        if not context:is_composing() and
            (state.committed_raw ~= "" or state.last_seen_raw ~= "" or
             next(state.trackers or {}) ~= nil or state.suspended or
             state.continuation_after_auto_commit) then
            reset_sentence_state(context, env)
            state = sentence_state(context, env)
        end
        -- Semicolon and apostrophe are rank selectors only for an existing
        -- composition. At idle, leave them to punctuator so symbols.yaml can
        -- commit Chinese punctuation directly.
        if not context:is_composing() and (ch == ";" or ch == "'") then
            return 2
        end
        set_allow_duplicate_single(context)
        if #(context.input or "") >= max_raw_length then
            return 1
        end
        -- Digits are rank suffixes only while composing. Idle digits commit
        -- directly (half-width; full-width in full_shape mode). Returning
        -- them to Rime instead would push them into the speller alphabet
        -- and leave an unresolved composition behind.
        if ch:match("%d") and not context:is_composing() then
            if context:get_option("full_shape") then
                local full = { "０", "１", "２", "３", "４", "５", "６", "７", "８", "９" }
                env.engine:commit_text(full[tonumber(ch) + 1])
            else
                env.engine:commit_text(ch)
            end
            env._tiger_sentence_dot_armed = true
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
        if dot_armed and (repr == "." or repr == "KP_Decimal") and
            not key_event:shift() and not key_event:ctrl() and
            not key_event:alt() and not key_event:super() then
            env.engine:commit_text(".")
            return 1
        end
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
        reset_early_evidence(state)
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        return 2
    end
    if repr == "Tab" or repr == "ISO_Left_Tab" or repr == "Shift+Tab" then
        reset_early_evidence(state)
        state.suspended = true
        state.empty_code_pending = nil
        save_transient_state(context, state, env)
        if cycle_candidate_highlight(context, repr == "Tab" and 1 or -1) then
            return 1
        end
        -- Without a usable menu, leave the key to the schema's Down/Up
        -- bindings and the standard navigator.
        return 2
    end
    if repr == "Up" or repr == "Down" or repr == "Page_Up" or repr == "Page_Down" then
        reset_early_evidence(state)
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
    ensure_lexicon(env)
    local context = env.engine.context
    local state = sentence_state(context, env)
    set_allow_duplicate_single(context)
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
M.ensure_lexicon = ensure_lexicon
M.data_status = data_status
M.apply_high_freq_limit = function(limit)
    local value = math.floor(tonumber(limit) or 0)
    if value < 0 then
        value = 0
    end
    rebuild_lexicon(value)
end
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
M.lexicon_probe = function(code)
    local candidates = lexicon_state.codes[code]
    if not candidates then
        return nil
    end
    local copy = {}
    for index = 1, #candidates do
        copy[index] = { t = candidates[index].t, r = candidates[index].r }
    end
    return copy
end
M.lexicon_lengths = function()
    local copy = {}
    for index = 1, #lexicon_state.lengths do
        copy[index] = lexicon_state.lengths[index]
    end
    return copy
end
-- Read-only view for benchmarks and diagnostics; callers must not mutate.
M.lexicon_data_view = function()
    ensure_lexicon(nil)
    return {
        codes = lexicon_state.codes,
        lengths = lexicon_state.lengths
    }
end
M.capture_empty_code_candidate = capture_empty_code_candidate
M.set_allow_duplicate_single = set_allow_duplicate_single
M.build_prefix_evidence = build_prefix_evidence
M.find_prefix_evidence = find_prefix_evidence
M.common_text_prefix = common_text_prefix
M.strong_empty_code_candidate = strong_empty_code_candidate
M.set_model_enabled = function(enabled)
    local disabled = enabled == false
    if disabled == model_disabled then
        return
    end
    model_disabled = disabled
    kn_model = false
    kn_load_error = nil
    clear_model_dependent_caches()
end
M.prefix_extends = prefix_extends
M.prefix_contradicted = prefix_contradicted
M.retain_trackers_without_counting = retain_trackers_without_counting
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
