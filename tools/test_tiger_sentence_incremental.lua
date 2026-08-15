-- Verify incremental lattice + exact logp cache match a full rebuild.
-- Usage: lua tools/test_tiger_sentence_incremental.lua [repo_root]

local repo = arg[1] or "."
package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path

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

print("loading KN + first full decode...")
local t0 = os.clock()
local first = sentence.decode_full(long_code)
print(string.format("KN ready in %.3fs  top=%s", os.clock() - t0, tops(first, 1)))

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
