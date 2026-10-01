-- Compare a frozen evaluation decoder with the final runtime without learning.
-- Cases use eval_key_correction.py's six-column format; no parameter selection.
local pack, snapshot, data, cases = arg[1], arg[2], arg[3], arg[4]
assert(pack and snapshot and data and cases)
rime_api = {get_user_data_dir=function() return data end}
package.path = snapshot .. '/lua/?.lua;' .. package.path
local frozen = require('tiger_sentence')
frozen.ensure_lexicon()
for name in pairs(package.loaded) do
    if name:match('^tiger_sentence') then package.loaded[name] = nil end
end
package.path = pack .. '/lua/?.lua;' .. package.path
local current = require('tiger_sentence')
current.ensure_lexicon()
if arg[5]=='--reference' then current.correction.configure('reference') end
assert(current.model_status().loaded and frozen.model_status().loaded)
current.correction.set_enabled(true)
frozen.correction.set_enabled(true)
assert(current.correction.penalty == 8 and frozen.correction.penalty == 8)
local inputs, checks = 0, 0
for line in io.lines(cases) do
    local columns = {}
    for value in line:gmatch('[^\t]+') do columns[#columns+1] = value end
    for _, index in ipairs({3,5,6}) do
        local raw = assert(columns[index])
        if raw ~= '-' then
            current.reset_decode_cache(); frozen.reset_decode_cache()
            local a, b = current.decode(raw,false,''), frozen.decode(raw,false,'')
            assert(#a == #b, raw .. ': candidate count')
            for i=1,#a do
                assert(a[i].text == b[i].text and math.abs(a[i].score-b[i].score)<1e-9 and
                    a[i].segmented == b[i].segmented and a[i].correction_count == b[i].correction_count,
                    raw .. ': candidate ' .. i)
                checks = checks + 1
            end
            inputs = inputs + 1
        end
    end
end
print(string.format('{"snapshot_inputs":%d,"candidate_checks":%d,"penalty":8}',inputs,checks))
