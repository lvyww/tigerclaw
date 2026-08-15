-- Benchmark the Rime Lua sentence lattice, with optional language-model scoring.
-- Usage:
--   lua tools/bench_tiger_sentence_lua.lua [repo_root] [--mode none|dummy|kn] [--repeat N]
--   luajit tools/bench_tiger_sentence_lua.lua ... --mode kn

local repo = arg[1] or "."
local mode = "none"
local repeats = 20
for i = 2, #arg do
    if arg[i] == "--mode" then
        mode = arg[i + 1] or mode
    elseif arg[i] == "--repeat" then
        repeats = tonumber(arg[i + 1]) or repeats
    end
end

package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
local lexicon = require("tiger_sentence_lexicon")

local beam_width = 200
local candidate_limit = 20
local BOS = "\2"
local EOS = "\3"

local function normalize(raw)
    return (raw or ""):lower():gsub("%s+", "")
end

local function utf_len(text)
    if utf8 and utf8.len then
        return utf8.len(text) or #text
    end
    return #text
end

local function utf_chars(text)
    local chars = {}
    if utf8 and utf8.codes then
        for _, cp in utf8.codes(text) do
            chars[#chars + 1] = utf8.char(cp)
        end
        return chars
    end
    chars[1] = text
    return chars
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

local function dedup_limit(states, limit)
    if not states or #states == 0 then
        return {}
    end
    local best = {}
    local order = {}
    for i = 1, #states do
        local item = states[i]
        local previous = best[item.text]
        if not previous then
            order[#order + 1] = item.text
            best[item.text] = item
        elseif item.score > previous.score then
            best[item.text] = item
        end
    end
    local result = {}
    for i = 1, #order do
        result[#result + 1] = best[order[i]]
    end
    table.sort(result, function(left, right)
        return left.score > right.score
    end)
    if #result > limit then
        local trimmed = {}
        for i = 1, limit do
            trimmed[i] = result[i]
        end
        return trimmed
    end
    return result
end

local logp = function()
    return 0
end

local function decode(raw_code)
    local raw = normalize(raw_code)
    if raw == "" or not raw:find("%a") then
        return {}, 0
    end
    local length = #raw
    local states = {}
    for index = 0, length do
        states[index] = {}
    end
    states[0][1] = { score = 0, text = "", segmented = "", prev2 = BOS, prev1 = BOS }
    local expansions = 0

    for position = 0, length - 1 do
        local current = dedup_limit(states[position], beam_width)
        if #current > 0 then
            for i = 1, #lexicon.lengths do
                local code_length = lexicon.lengths[i]
                if position + code_length <= length then
                    local code = raw:sub(position + 1, position + code_length)
                    local candidates = lexicon.codes[code]
                    if candidates then
                        local selected_rank, consumed_end = parse_selector(raw, position + code_length)
                        if not (length > 1 and consumed_end - position < 2) then
                            local required_rank = selected_rank > 0 and selected_rank or 1
                            for c = 1, #current do
                                local item = current[c]
                                for k = 1, #candidates do
                                    local candidate = candidates[k]
                                    if candidate.r == required_rank then
                                        local score = item.score
                                        local prev2, prev1 = item.prev2, item.prev1
                                        local chars = utf_chars(candidate.t)
                                        for ci = 1, #chars do
                                            score = score + logp(prev2, prev1, chars[ci])
                                            prev2 = prev1
                                            prev1 = chars[ci]
                                        end
                                        expansions = expansions + 1
                                        local piece = raw:sub(position + 1, consumed_end)
                                        local segmented = item.segmented
                                        if segmented == "" then
                                            segmented = piece
                                        else
                                            segmented = segmented .. " " .. piece
                                        end
                                        local next_states = states[consumed_end]
                                        next_states[#next_states + 1] = {
                                            score = score,
                                            text = item.text .. candidate.t,
                                            segmented = segmented,
                                            prev2 = prev2,
                                            prev1 = prev1
                                        }
                                    end
                                end
                            end
                        end
                    end
                end
            end
        end
    end

    local completed = dedup_limit(states[length], beam_width)
    for i = 1, #completed do
        completed[i].score = completed[i].score + logp(completed[i].prev2, completed[i].prev1, EOS)
    end
    table.sort(completed, function(a, b)
        return a.score > b.score
    end)
    if #completed > candidate_limit then
        local trimmed = {}
        for i = 1, candidate_limit do
            trimmed[i] = completed[i]
        end
        completed = trimmed
    end
    return completed, expansions
end

local function dummy_logp(prev2, prev1, target)
    local key = prev2 .. "\0" .. prev1 .. "\0" .. target
    local h = 2166136261
    for i = 1, #key do
        h = (h + key:byte(i)) * 16777619
        h = h % 4294967296
    end
    return math.log(((h % 100000) + 1) / 100000)
end

local function load_kn(path)
    local ffi = require("ffi")
    ffi.cdef[[
        int open(const char *pathname, int flags);
        int close(int fd);
        void *mmap(void *addr, size_t length, int prot, int flags, int fd, long offset);
        int munmap(void *addr, size_t length);
        long lseek(int fd, long offset, int whence);
    ]]
    local O_RDONLY = 0
    local PROT_READ = 1
    local MAP_PRIVATE = 2
    local SEEK_END = 2
    local fd = ffi.C.open(path, O_RDONLY)
    assert(fd >= 0, "cannot open model: " .. path)
    local length = tonumber(ffi.C.lseek(fd, 0, SEEK_END))
    local ptr = ffi.cast("const uint8_t*", ffi.C.mmap(nil, length, PROT_READ, MAP_PRIVATE, fd, 0))
    assert(ptr ~= nil and ptr ~= ffi.cast("const uint8_t*", -1), "mmap failed")

    local tmp32 = ffi.new("uint32_t[1]")
    local tmp64 = ffi.new("uint64_t[1]")
    local tmpf = ffi.new("float[1]")
    local function u32(off)
        ffi.copy(tmp32, ptr + off, 4)
        return tonumber(tmp32[0])
    end
    local function i32(off)
        local value = u32(off)
        if value >= 0x80000000 then
            return value - 0x100000000
        end
        return value
    end
    local function u64(off)
        ffi.copy(tmp64, ptr + off, 8)
        return tmp64[0]
    end
    local function f32(off)
        ffi.copy(tmpf, ptr + off, 4)
        return tonumber(tmpf[0])
    end

    assert(ffi.string(ptr, 8) == "TCSKNM01", "bad magic")
    assert(i32(8) == 1, "bad version")
    local uni_count = i32(12)
    local pos = 16
    local uni_off = pos
    pos = pos + uni_count * 8
    local bi_count = tonumber(u64(pos))
    pos = pos + 8
    local bi_off = pos
    pos = pos + bi_count * 12
    local bi_ctx_count = i32(pos)
    pos = pos + 4
    local bi_ctx_off = pos
    pos = pos + bi_ctx_count * 8
    local tri_count = tonumber(u64(pos))
    pos = pos + 8
    local tri_off = pos
    pos = pos + tri_count * 12
    local tri_ctx_count = tonumber(u64(pos))
    pos = pos + 8
    local tri_ctx_off = pos

    local unknown = f32(uni_off + 4)
    local SCALAR_BITS = 21
    local SCALAR_MASK = 0x1FFFFF

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

    local function scalar(token)
        if not token or token == "" then
            return 0
        end
        if token == BOS or token == EOS then
            return string.byte(token)
        end
        if utf8 and utf8.codepoint then
            return utf8.codepoint(token)
        end
        return 0
    end

    local SHIFT = ffi.cast("uint64_t", SCALAR_MASK + 1)
    local function pack2(first, second)
        return ffi.cast("uint64_t", first) * SHIFT + ffi.cast("uint64_t", second % (SCALAR_MASK + 1))
    end
    local function pack3(first, second, third)
        return pack2(first, second) * SHIFT + ffi.cast("uint64_t", third % (SCALAR_MASK + 1))
    end

    local function kn_logp(prev2, prev1, target)
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

    return {
        logp = kn_logp,
        length = length,
        uni = uni_count,
        bi = bi_count,
        tri = tri_count,
        close = function()
            ffi.C.munmap(ffi.cast("void*", ptr), length)
            ffi.C.close(fd)
        end
    }
end

local function load_kn_lua(path)
    local file = assert(io.open(path, "rb"))
    local data = file:read("*a")
    file:close()
    assert(data:sub(1, 8) == "TCSKNM01", "bad magic")
    local function i32(off)
        return (string.unpack("<i4", data, off + 1))
    end
    local function u64(off)
        return (string.unpack("<I8", data, off + 1))
    end
    local function f32(off)
        return (string.unpack("<f", data, off + 1))
    end
    assert(i32(8) == 1, "bad version")
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
    local SHIFT = 2097152
    local MASK = 2097151

    local function lookup_i32(offset, count, key, fallback)
        local low, high = 0, count
        while low < high do
            local middle = low + ((high - low) // 2)
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
            local middle = low + ((high - low) // 2)
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
        return first * SHIFT + (second & MASK)
    end
    local function pack3(first, second, third)
        return pack2(first, second) * SHIFT + (third & MASK)
    end

    local function kn_logp(prev2, prev1, target)
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

    return {
        logp = kn_logp,
        length = #data,
        uni = uni_count,
        bi = bi_count,
        tri = tri_count
    }
end

if mode == "dummy" then
    logp = dummy_logp
elseif mode == "knlua" then
    local t0 = os.clock()
    local kn = load_kn_lua("/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin")
    io.write(string.format(
        "loaded KN lua-string %.1f MiB in %.2fs  uni=%d bi=%d tri=%d\n",
        kn.length / 1048576, os.clock() - t0, kn.uni, kn.bi, kn.tri))
    logp = kn.logp
elseif mode == "kn" then
    local ok, kn_or_err = pcall(load_kn, "/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin")
    if not ok then
        io.stderr:write("KN mmap unavailable: " .. tostring(kn_or_err) .. "\n")
        os.exit(2)
    end
    logp = kn_or_err.logp
    io.write(string.format(
        "loaded KN mmap %.1f MiB  uni=%d bi=%d tri=%d\n",
        kn_or_err.length / 1048576, kn_or_err.uni, kn_or_err.bi, kn_or_err.tri))
elseif mode ~= "none" then
    io.stderr:write("unknown mode: " .. mode .. "\n")
    os.exit(2)
end

local cases = {
    { name = "是", raw = "ot" },
    { name = "的是", raw = "ueot" },
    { name = "我不是", raw = "tucbot" },
    { name = "我们现在还没有", raw = "tujanengcukrnv" },
    { name = "今天早上我吃了两个面包", raw = "jaefmonyftuderlmljgbmnvs" },
    { name = "这个问题其实没有那么复杂", raw = "vujgadkotwzhwwkrnvautkeohke" },
    { name = "他把那本书放在桌子上面然后离开了", raw = "jeumbauefaalhngyoehiyfbmvmxfzbflrl" },
}

local function median(values)
    table.sort(values)
    return values[math.ceil(#values / 2)]
end

io.write(string.format("engine=%s mode=%s repeats=%d beam=%d\n",
    (_VERSION or "?") .. (jit and ("/" .. jit.version) or ""),
    mode, repeats, beam_width))

for _, case in ipairs(cases) do
    local warmup, expansions = decode(case.raw)
    local times = {}
    for _ = 1, repeats do
        local t0 = os.clock()
        decode(case.raw)
        times[#times + 1] = (os.clock() - t0) * 1000
    end
    local sum = 0
    local max_ms = 0
    for i = 1, #times do
        sum = sum + times[i]
        if times[i] > max_ms then
            max_ms = times[i]
        end
    end
    local top = warmup[1] and warmup[1].text or ""
    io.write(string.format(
        "%-20s keys=%2d cands=%2d expand=%6d  mean=%7.2fms  med=%7.2fms  max=%7.2fms  top=%s\n",
        case.name, #case.raw, #warmup, expansions, sum / #times, median(times), max_ms, top))
end
