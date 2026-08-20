-- TigerClaw-style sentence lattice for Rime, with optional Lua KN scoring.
local lexicon = require("tiger_sentence_lexicon")
local kn_reader = require("tiger_sentence_kn")
local ranks = require("tiger_sentence_ranks")

local beam_width = 200
local candidate_limit = 20
local rank_penalty = 0.03
local isolation_threshold = 3000
local isolation_lambda = 2.0
local BOS = kn_reader.BOS
local EOS = kn_reader.EOS
local kn_model = false
local max_code_len = 1
for i = 1, #lexicon.lengths do
    if lexicon.lengths[i] > max_code_len then
        max_code_len = lexicon.lengths[i]
    end
end

local logp_cache_limit = 32768
local logp_cache = {}
local logp_cache_keys = {}
local logp_cache_next = 1
local decode_cache = {
    raw = nil,
    states = nil,
    result = nil
}

-- Keep early-commit state in Rime context properties. The Lua binding may
-- create a different userdata wrapper for the same context on each callback,
-- so a weak table keyed by that wrapper cannot preserve the stability count.
local state_keys = {
    committed_text = "tiger_sentence_committed_text",
    committed_raw = "tiger_sentence_committed_raw",
    proposal = "tiger_sentence_proposal",
    stable = "tiger_sentence_stable"
}

local function sentence_state(context)
    return {
        committed_text = context:get_property(state_keys.committed_text) or "",
        committed_raw = context:get_property(state_keys.committed_raw) or "",
        proposal = context:get_property(state_keys.proposal) or "",
        stable = tonumber(context:get_property(state_keys.stable)) or 0
    }
end

local function save_sentence_state(context, state)
    context:set_property(state_keys.committed_text, state.committed_text or "")
    context:set_property(state_keys.committed_raw, state.committed_raw or "")
    context:set_property(state_keys.proposal, state.proposal or "")
    context:set_property(state_keys.stable, tostring(state.stable or 0))
end

local function reset_sentence_state(context)
    save_sentence_state(context, {
        committed_text = "",
        committed_raw = "",
        proposal = "",
        stable = 0
    })
end

local function ensure_kn()
    if kn_model ~= false then
        return kn_model
    end
    kn_model = kn_reader.try_load() or nil
    return kn_model
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
    chars[1] = text
    return chars
end

local function isolation_penalty(text)
    local model = ensure_kn()
    if not model or not model.has_observed_bigram or not text or text == "" then
        return 0
    end
    local chars = utf_chars(text)
    local penalty = 0
    for index = 1, #chars do
        local rank = ranks.rank(chars[index])
        if rank > isolation_threshold then
            local left_hit = index > 1 and model.has_observed_bigram(chars[index - 1], chars[index])
            local right_hit = index < #chars and model.has_observed_bigram(chars[index], chars[index + 1])
            if not left_hit and not right_hit then
                penalty = penalty + isolation_lambda
            end
        end
    end
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

local function dedup_limit(states, limit)
    if not states or #states == 0 then
        return {}
    end
    local best = {}
    local order = {}
    for i = 1, #states do
        local item = states[i]
        local previous = best[item.text]
        local item_rank = item.max_rank or 1
        if not previous then
            order[#order + 1] = item.text
            best[item.text] = item
        else
            local previous_rank = previous.max_rank or 1
            if item_rank < previous_rank or (item_rank == previous_rank and item.score > previous.score) then
                best[item.text] = item
            end
        end
    end
    local result = {}
    for i = 1, #order do
        result[#result + 1] = best[order[i]]
    end
    table.sort(result, function(left, right)
        local left_rank = left.max_rank or 1
        local right_rank = right.max_rank or 1
        if left_rank ~= right_rank then
            return left_rank < right_rank
        end
        if left.score == right.score then
            return left.text < right.text
        end
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

local function new_states(length)
    local states = {}
    for index = 0, length do
        states[index] = {}
    end
    states[0][1] = { score = 0, text = "", segmented = "", prev2 = BOS, prev1 = BOS, max_rank = 1 }
    return states
end

local function expand_range(raw, states, from_pos, length)
    local allow_all_ranks = length <= 4
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
                        if not (length > 1 and consumed_end - position < 2) then
                            for c = 1, #current do
                                local item = current[c]
                                for k = 1, #candidates do
                                    local candidate = candidates[k]
                                    local rank_ok = selected_rank > 0 and candidate.r == selected_rank
                                        or selected_rank == 0 and (allow_all_ranks or candidate.r == 1)
                                    if rank_ok then
                                        local score = item.score
                                        local prev2, prev1 = item.prev2, item.prev1
                                        local chars = utf_chars(candidate.t)
                                        for ci = 1, #chars do
                                            score = score + logp(prev2, prev1, chars[ci])
                                            prev2 = prev1
                                            prev1 = chars[ci]
                                        end
                                        if selected_rank == 0 then
                                            score = score - rank_penalty * math.log(1.0 + candidate.r - 1)
                                        end
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
                                            prev1 = prev1,
                                            max_rank = math.max(item.max_rank or 1, candidate.r)
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
end

local function emit(states, length)
    local completed = dedup_limit(states[length], beam_width)
    local result = {}
    for i = 1, #completed do
        local item = completed[i]
        result[i] = {
            score = item.score + logp(item.prev2, item.prev1, EOS) - isolation_penalty(item.text),
            text = item.text,
            segmented = item.segmented,
            prev2 = item.prev2,
            prev1 = item.prev1,
            max_rank = math.max(1, item.max_rank or 1)
        }
    end
    table.sort(result, function(left, right)
        if left.max_rank ~= right.max_rank then
            return left.max_rank < right.max_rank
        end
        if left.score == right.score then
            return left.text < right.text
        end
        return left.score > right.score
    end)
    if #result > candidate_limit then
        local trimmed = {}
        for i = 1, candidate_limit do
            trimmed[i] = result[i]
        end
        return trimmed
    end
    return result
end

local function results_equal(left, right)
    if #left ~= #right then
        return false
    end
    for i = 1, #left do
        if left[i].text ~= right[i].text
            or left[i].segmented ~= right[i].segmented
            or left[i].score ~= right[i].score then
            return false
        end
    end
    return true
end

local function decode_full(raw_code)
    local raw = normalize(raw_code)
    if raw == "" or not has_letter(raw) then
        return {}
    end
    local length = #raw
    local states = new_states(length)
    expand_range(raw, states, 0, length)
    return emit(states, length)
end

local function decode(raw_code)
    local raw = normalize(raw_code)
    if raw == "" or not has_letter(raw) then
        decode_cache.raw = raw
        decode_cache.states = nil
        decode_cache.result = {}
        return decode_cache.result
    end
    if decode_cache.raw == raw and decode_cache.result then
        return decode_cache.result
    end

    local length = #raw
    local states = nil
    local old_raw = decode_cache.raw
    local old_states = decode_cache.states
    if old_states and type(old_raw) == "string" and old_raw ~= "" then
        local old_n = #old_raw
        -- A one-key segment is legal only when the whole input is one key.
        -- Crossing that boundary, or the four-key all-rank boundary, changes
        -- which edges exist in the reused prefix.
        if old_n == 1 or length == 1 or (old_n <= 4) ~= (length <= 4) then
            states = nil
        elseif length > old_n and raw:sub(1, old_n) == old_raw then
            local max_consume = max_code_len + trailing_selector_span(raw)
            local from_pos = math.max(0, old_n + 1 - max_consume)
            states = old_states
            for index = old_n + 1, length do
                states[index] = {}
            end
            expand_range(raw, states, from_pos, length)
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

    local result = emit(states, length)
    decode_cache.raw = raw
    decode_cache.states = states
    decode_cache.result = result
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

local function find_raw_length_for_text(text, raw, candidates)
    for i = 1, #candidates do
        local candidate = candidates[i]
        if candidate.text:sub(1, #text) == text and candidate.segmented ~= "" then
            local consumed = 0
            for piece in candidate.segmented:gmatch("[^ ]+") do
                consumed = consumed + #piece
                if consumed <= #raw then
                    local prefix = decode_full(raw:sub(1, consumed))
                    for p = 1, #prefix do
                        if prefix[p].text == text then
                            return consumed
                        end
                    end
                end
            end
        end
    end
    return 0
end

local function try_early_commit(env)
    local context = env.engine.context
    local state = sentence_state(context)
    local live_raw = context.input or ""

    -- The first four raw encoding keys are never counted as stable evidence.
    if #live_raw + #state.committed_raw <= 4 then
        state.proposal = ""
        state.stable = 0
        save_sentence_state(context, state)
        return
    end

    local full_raw = state.committed_raw .. live_raw
    local decoded = decode(full_raw)
    local candidates = {}
    for i = 1, #decoded do
        local candidate = decoded[i]
        if state.committed_text == "" or
            candidate.text:sub(1, #state.committed_text) == state.committed_text then
            candidates[#candidates + 1] = candidate
        end
    end
    if #candidates == 0 then
        state.proposal = ""
        state.stable = 0
        save_sentence_state(context, state)
        return
    end

    local max_score = candidates[1].score
    for i = 2, #candidates do
        if candidates[i].score > max_score then max_score = candidates[i].score end
    end
    local total = 0
    for i = 1, #candidates do total = total + math.exp(candidates[i].score - max_score) end

    local proposal = ""
    for i = 1, #candidates do
        local chars = utf_chars(candidates[i].text)
        for length = 1, #chars - 1 do
            local prefix = table.concat(chars, "", 1, length)
            local mass = 0
            for j = 1, #candidates do
                if candidates[j].text:sub(1, #prefix) == prefix then
                    mass = mass + math.exp(candidates[j].score - max_score)
                end
            end
            if mass / total >= 0.995 and #utf_chars(prefix) > #utf_chars(proposal) then
                proposal = prefix
            end
        end
    end
    local proposal_chars = utf_chars(proposal)
    if #proposal_chars > 1 then
        proposal = table.concat(proposal_chars, "", 1, #proposal_chars - 1)
    end
    if proposal == "" or #proposal <= #state.committed_text then
        state.proposal = ""
        state.stable = 0
        save_sentence_state(context, state)
        return
    end

    if proposal == state.proposal then
        state.stable = state.stable + 1
    else
        state.proposal = proposal
        state.stable = 1
    end
    save_sentence_state(context, state)
    if state.stable < 2 then return end

    local consumed = find_raw_length_for_text(proposal, full_raw, candidates)
    if consumed <= #state.committed_raw or consumed > #full_raw then return end
    local commit = proposal:sub(#state.committed_text + 1)
    state.committed_text = proposal
    state.committed_raw = full_raw:sub(1, consumed)
    state.proposal = proposal
    state.stable = 0
    env.engine:commit_text(commit)
    context:clear()
    save_sentence_state(context, state)
    local remaining = full_raw:sub(consumed + 1)
    if remaining ~= "" then context:push_input(remaining) end
end

local function reset_decode_cache()
    decode_cache.raw = nil
    decode_cache.states = nil
    decode_cache.result = nil
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
    local state = sentence_state(context)
    local repr = key_event:repr()
    local ch = is_plain_char_key(key_event, repr)
    if ch then
        if not context:is_composing() and state.committed_raw ~= "" then
            reset_sentence_state(context)
            state = sentence_state(context)
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
        context:push_input(ch)
        try_early_commit(env)
        return 1
    end
    if not context:is_composing() then
        return 2
    end
    if repr == "Return" or repr == "KP_Enter" then
        env.engine:commit_text(context.input)
        context:clear()
        reset_sentence_state(context)
        return 1
    end
    if repr == "Escape" then
        context:clear()
        reset_sentence_state(context)
        return 1
    end
    if repr == "space" then
        if context:has_menu() then
            context:confirm_current_selection()
        end
        reset_sentence_state(context)
        return 1
    end
    return 2
end

local function translator(input, seg, env)
    local context = env.engine.context
    local state = sentence_state(context)
    local committed_text = state.committed_text or ""
    local committed_raw = state.committed_raw or ""
    local raw = committed_raw .. input
    local results = decode(raw)
    for i = 1, #results do
        local item = results[i]
        if committed_text == "" or item.text:sub(1, #committed_text) == committed_text then
            local text = committed_text == "" and item.text or item.text:sub(#committed_text + 1)
            local preedit = committed_raw == "" and item.segmented or
                trim_segmented_after_raw_prefix(item.segmented, #committed_raw)
            if text ~= "" then
                local cand = Candidate("sentence", seg.start, seg._end, text, "")
                cand.preedit = preedit
                yield(cand)
            end
        end
    end
end

local M = {}
M.decode = decode
M.decode_full = decode_full
M.reset_decode_cache = reset_decode_cache
M.results_equal = results_equal
M.processor = processor
M.translator = translator
return M
