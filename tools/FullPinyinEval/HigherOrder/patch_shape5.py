"""Patch only an owned offline copy of the frozen shape decoder; never production."""
from pathlib import Path
import argparse
p=argparse.ArgumentParser();p.add_argument('source',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
s=a.source.read_text()
def replace(old,new,n=1):
 global s
 assert s.count(old)==n,(old,s.count(old),n)
 s=s.replace(old,new)
replace('local function trailing_selector_span(raw)', '''-- Offline fivegram experiment; old trigram remains only for isolation priors.
local shape5_model = os.getenv("SHAPE5_MODEL")
local shape5, shape5_begin
local shape5_cache, shape5_keys, shape5_next = {}, {}, 1
if shape5_model then
    shape5 = assert(package.loadlib(assert(os.getenv("SHAPE5_LIB")), "luaopen_shape5"))()
    shape5_begin = shape5.load(shape5_model)
end
local function search_logp(history, prev2, prev1, target)
    if not shape5 then return logp(prev2, prev1, target), history end
    assert(history, "missing fivegram history")
    local key = history .. target
    local cached = shape5_cache[key]
    if cached then return cached[1], cached[2] end
    local score, next_state = shape5.step(history, target)
    local old = shape5_keys[shape5_next]
    if old then shape5_cache[old] = nil end
    shape5_keys[shape5_next] = key
    shape5_next = shape5_next % 8192 + 1
    shape5_cache[key] = {score, next_state}
    return score, next_state
end

local function trailing_selector_span(raw)''')
replace('        prev2 = BOS,\n        prev1 = BOS,','        prev2 = BOS,\n        prev1 = BOS,\n        history5 = shape5_begin,')
replace('local prev2, prev1 = item.prev2, item.prev1','local prev2, prev1 = item.prev2, item.prev1\n                                    local history5 = item.history5')
replace('score = score + logp(prev2, prev1, chars[ci])','local probability\n                                        probability, history5 = search_logp(history5, prev2, prev1, chars[ci])\n                                        score = score + probability')
replace('                                        prev2 = prev2,','                                        history5 = history5,\n                                        prev2 = prev2,')
replace('local eos_score = logp(item.prev2, item.prev1, EOS)','local eos_score = search_logp(item.history5, item.prev2, item.prev1, EOS)')
replace('        prev2 = item.prev2,','        history5 = item.history5,\n        prev2 = item.prev2,')
replace('left.text ~= right.text or left.prev2 ~= right.prev2 or left.prev1 ~= right.prev1 or','left.text ~= right.text or left.prev2 ~= right.prev2 or left.prev1 ~= right.prev1 or\n            left.history5 ~= right.history5 or')
replace('local seed = { text = "", prev2 = BOS, prev1 = BOS, score = 0, mass_score = 0,','local seed = { text = "", prev2 = BOS, prev1 = BOS, history5 = shape5_begin, score = 0, mass_score = 0,')
replace('prev2 = seed.prev2, prev1 = seed.prev1, score = seed.score,','prev2 = seed.prev2, prev1 = seed.prev1, history5 = seed.history5, score = seed.score,')
replace('item.score = item.score + logp(item.prev2, item.prev1, ch) + emitted_character_reward','local probability\n                    probability, item.history5 = search_logp(item.history5, item.prev2, item.prev1, ch)\n                    item.score = item.score + probability + emitted_character_reward')
# The frozen module is near Lua's 200-local limit; keep adapter locals in a
# closed scope and expose only fields on its existing ranking-prior namespace.
s=s.replace("local shape5_model =", "do\nlocal shape5_model =")
s=s.replace("local shape5, shape5_begin", "local shape5")
s=s.replace("shape5_begin", "ranking_prior.shape5_begin")
s=s.replace("local function search_logp", "function search_logp")
import re
s=re.sub(r"(?<![.\w])search_logp\(", "ranking_prior.search_logp(", s)
s=s.replace("local function trailing_selector_span(raw)", "end\n\nlocal function trailing_selector_span(raw)")
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(s)
