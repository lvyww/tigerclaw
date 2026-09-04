-- Benchmark the production Rime Lua sentence decoder.
-- Usage:
--   lua tools/bench_tiger_sentence_lua.lua [repo_root]
--       [--mode auto|mobile|none] [--repeat N] [--require-model]

local repo = arg[1] or "."
local mode = "auto"
local repeats = 50
local require_model = false
for i = 2, #arg do
    if arg[i] == "--mode" then
        mode = arg[i + 1] or mode
    elseif arg[i] == "--repeat" then
        repeats = math.max(1, tonumber(arg[i + 1]) or repeats)
    elseif arg[i] == "--require-model" then
        require_model = true
    end
end
if mode ~= "auto" and mode ~= "mobile" and mode ~= "none" then
    io.stderr:write("unknown mode: " .. mode .. "\n")
    os.exit(2)
end

package.path = repo .. "/rime/tiger_sentence/lua/?.lua;" .. package.path
rime_api = {
    get_user_data_dir = function()
        return repo .. "/rime/tiger_sentence"
    end
}

local sentence = require("tiger_sentence")
sentence.ensure_lexicon(nil)
sentence.set_model_enabled(mode ~= "none")
local model = sentence.model_status()
if require_model and not model.loaded then
    io.stderr:write("sentence model is required: " .. tostring(model.error) .. "\n")
    os.exit(2)
end
if mode == "mobile" and model.loaded and model.format ~= "TCSKNM02" then
    io.stderr:write("mobile mode loaded unexpected model format: " ..
        tostring(model.format) .. "\n")
    os.exit(2)
end

local cases = {
    { name = "是", raw = "ot" },
    { name = "的是", raw = "ueot" },
    { name = "我不是", raw = "tucbot" },
    { name = "我们现在还没有", raw = "tujanengcukrnv" },
    { name = "买椟还珠", raw = "awmenamcunta" },
    { name = "今天早上我吃了两个面包", raw = "jaefmonyftuderlmljgbmnvs" },
    { name = "这个问题其实没有那么复杂", raw = "vujgadkotwzhwwkrnvautkeohke" },
    { name = "他把那本书放在桌子上面然后离开了", raw = "jeumbauefaalhngyoehiyfbmvmxfzbflrl" },
}

local function percentile(sorted, share)
    return sorted[math.max(1, math.ceil(#sorted * share))]
end

local function summarize(times, gc_total)
    local sorted = {}
    local total, maximum = 0.0, 0.0
    for i = 1, #times do
        sorted[i] = times[i]
        total = total + times[i]
        maximum = math.max(maximum, times[i])
    end
    table.sort(sorted)
    return {
        mean = total / #times,
        p50 = percentile(sorted, 0.50),
        p95 = percentile(sorted, 0.95),
        maximum = maximum,
        gc_kib = gc_total / #times
    }
end

local function measure(run)
    local times = {}
    local gc_total = 0.0
    local result
    for _ = 1, repeats do
        collectgarbage("collect")
        local before_gc = collectgarbage("count")
        local started = os.clock()
        result = run()
        times[#times + 1] = (os.clock() - started) * 1000
        gc_total = gc_total + math.max(0.0, collectgarbage("count") - before_gc)
    end
    return result, summarize(times, gc_total)
end

local function run_full(raw, evidence)
    return function()
        sentence.reset_decode_cache()
        return sentence.decode_full(raw, evidence, "")
    end
end

local function run_incremental(raw, evidence)
    return function()
        sentence.reset_decode_cache()
        local result = {}
        for length = 1, #raw do
            result = sentence.decode(raw:sub(1, length), evidence, "")
        end
        return result
    end
end

local function performance_snapshot()
    local current = sentence.performance_status().current
    return {
        decode_calls = current.decode_calls,
        page_misses = current.page_misses,
        page_bytes = current.page_bytes,
        evidence_builds = current.early_evidence_builds
    }
end

local function print_result(case, kind, result, stats, metrics)
    local top = result[1] and result[1].text or ""
    io.write(string.format(
        "%-18s %-11s keys=%2d cands=%2d mean=%7.2fms p50=%7.2fms " ..
        "p95=%7.2fms max=%7.2fms gc=%7.1fKiB calls=%5.1f ev=%5.1f " ..
        "miss=%5.1f read=%7.1fKiB top=%s\n",
        case.name, kind, #case.raw, #result, stats.mean, stats.p50,
        stats.p95, stats.maximum, stats.gc_kib, metrics.decode_calls,
        metrics.evidence_builds, metrics.page_misses, metrics.page_kib, top))
end

local function benchmark(case, kind, run)
    local before = performance_snapshot()
    local result, stats = measure(run)
    local after = performance_snapshot()
    local metrics = {
        decode_calls = (after.decode_calls - before.decode_calls) / repeats,
        evidence_builds = (after.evidence_builds - before.evidence_builds) / repeats,
        page_misses = (after.page_misses - before.page_misses) / repeats,
        page_kib = (after.page_bytes - before.page_bytes) / repeats / 1024
    }
    print_result(case, kind, result, stats, metrics)
    return result
end

io.write(string.format(
    "engine=%s mode=%s repeats=%d model=%s path=%s\n",
    (_VERSION or "?") .. (jit and ("/" .. jit.version) or ""),
    mode, repeats, model.loaded and tostring(model.format) or "none",
    tostring(model.path or model.error or "")))

for _, case in ipairs(cases) do
    sentence.reset_decode_cache()
    sentence.decode_full(case.raw, true, "")

    local full = benchmark(case, "full", run_full(case.raw, false))
    local incremental = benchmark(case, "incremental", run_incremental(case.raw, false))
    local evidence = benchmark(case, "evidence", run_incremental(case.raw, true))

    if not sentence.results_equal(incremental, sentence.decode_full(case.raw, false, "")) or
        not sentence.results_equal(evidence, sentence.decode_full(case.raw, true, "")) then
        io.stderr:write("incremental/full mismatch for " .. case.raw .. "\n")
        os.exit(1)
    end
end
