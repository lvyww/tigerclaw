-- Fixed-seed editing stress: compare warm incremental decoding to a cold full
-- decode after each operation. Runs only against explicitly supplied test data.
local pack, data = assert(arg[1]), assert(arg[2])
package.path = pack .. '/lua/?.lua;' .. package.path
rime_api = {get_user_data_dir=function() return data end}
local s = require('tiger_sentence')
s.ensure_lexicon(); s.set_memory_profile('compact')
assert(s.model_status().loaded)
-- Historical full/incremental parity uses an unlimited reference budget.
-- Production exhaustion contracts are exercised by test_key_correction_perf.lua.
if s.correction.configure then s.correction.configure('reference') end
s.correction.set_enabled(true)
local seed, checks = 20261001, 0
local function random(n)
    seed = (seed * 48271) % 2147483647
    return seed % n + 1
end
local function compare(raw)
    local a, b = s.decode(raw,true,''), s.decode_full(raw,true,'')
    assert(s.results_equal(a,b), 'incremental mismatch: ' .. raw)
    for i=1,#a do
        assert(a[i].correction_count == b[i].correction_count and
            a[i].corrected_raw == b[i].corrected_raw, 'correction metadata mismatch')
    end
    checks = checks + 1
end
local lengths={}
for value in (arg[3] or '8,16,32,64,128'):gmatch('%d+') do lengths[#lengths+1]=tonumber(value) end
local maximum=0
for _,length in ipairs(lengths) do
    maximum=math.max(maximum,length+1)
    local raw = ('kospfifyiejryfenahbmsp'):rep(8):sub(1,length)
    s.reset_decode_cache(); compare(raw)
    for _, action in ipairs({'append','backspace','insert','delete','replace','toggle'}) do
        local at = random(#raw)
        local letter = string.char(96 + random(26))
        if action == 'append' then raw = raw .. letter
        elseif action == 'backspace' then raw = raw:sub(1,-2)
        elseif action == 'insert' then raw = raw:sub(1,at-1) .. letter .. raw:sub(at)
        elseif action == 'delete' then raw = raw:sub(1,at-1) .. raw:sub(at+1)
        elseif action == 'replace' then raw = raw:sub(1,at-1) .. letter .. raw:sub(at+1)
        else s.correction.set_enabled(false); s.decode(raw); s.correction.set_enabled(true) end
        compare(raw)
    end
    print('passed length ' .. length); io.stdout:flush()
end
print(string.format('{"random_edit_checks":%d,"seed":20261001,"maximum_keys":%d}',checks,maximum))
