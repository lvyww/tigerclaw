-- Tab correction learning. Rime owns the LevelDb lock and durable records;
-- decoding uses only immutable in-memory indexes. No Windows receipt is implied.
local M = {}
local function chars(text)
    local result = {}
    for c in text:gmatch("[%z\1-\127\194-\244][\128-\191]*") do
        local a, b = c:byte(1, 2)
        local length = a < 128 and 1 or a < 224 and 2 or a < 240 and 3 or 4
        if #c ~= length or (a == 224 and b < 160) or (a == 237 and b >= 160) or
            (a == 240 and b < 144) or (a == 244 and b >= 144) then return {} end
        result[#result + 1] = c
    end
    if table.concat(result) ~= text then return {} end
    return result
end
local function static(text)
    local n = #chars(text)
    return n > 0 and n <= 16 and not text:find("[%z\1-\31\127{}]") and
        not text:find("\238[\128-\191][\128-\191]") and not text:find("\239[\128-\163][\128-\191]")
end
local function context(text)
    local c = chars(text)
    return table.concat(c, "", math.max(1, #c - 1))
end
local function key(...)
    local values = {...}
    for i, value in ipairs(values) do values[i] = #value .. ":" .. value end
    return table.concat(values)
end
local function frame(values) return key((table.unpack or unpack)(values)) end
local function unframe(value)
    local result, pos = {}, 1
    while pos <= #value do
        local a, b, n = value:find("^(%d+):", pos)
        if not a then return nil end
        n = tonumber(n)
        if not n or n > 8192 or b + n > #value then return nil end
        result[#result + 1] = value:sub(b + 1, b + n)
        pos = b + n + 1
    end
    return result
end
function M.hash(text)
    local a, b = 2166136261, 5381
    for i = 1, #text do
        a = (a * 65599 + text:byte(i)) % 4294967296
        b = (b * 33 + text:byte(i)) % 4294967296
    end
    return string.format("%08x%08x", a, b)
end
function M.build(events, now)
    now = now or os.time()
    local groups = {}
    for _, e in ipairs(events) do
        if e.mode and #e.mode > 0 and #e.mode <= 512 and e.code and #e.code > 0 and #e.code <= 128 and
            e.text and static(e.text) and e.context and (e.context == "" or #chars(e.context) > 0) and #chars(e.context) <= 2 and
            type(e.time) == "number" and e.time >= 0 then
            local k = key(e.code, e.mode, e.context)
            local group = groups[k] or {code=e.code, mode=e.mode, context=e.context, choices={}}
            groups[k] = group
            local time = math.min(now, e.time)
            for text, c in pairs(group.choices) do
                c.weight = c.weight * 2 ^ (-math.max(0, time - c.time) / (30 * 86400))
                c.time = math.max(time, c.time)
                if text ~= e.text then c.weight = c.weight * 0.25 end
            end
            local c = group.choices[e.text] or {weight=0, count=0, time=time}
            c.weight, c.count = math.min(3, c.weight + 1), math.min(3, c.count + 1)
            group.choices[e.text] = c
        end
    end
    local summaries = {}
    for _, g in pairs(groups) do
        for text, c in pairs(g.choices) do
            local k = key(g.code, g.mode, text)
            local s = summaries[k] or {code=g.code, mode=g.mode, text=text, exact={}, weight=0, count=0, contexts=0}
            summaries[k] = s
            local weight = c.weight * 2 ^ (-math.max(0, now - c.time) / (30 * 86400))
            s.exact[g.context] = math.min(10, 6 * math.min(1, weight) + 2 * math.max(0, weight - 1))
            s.weight, s.count = s.weight + weight, math.min(3, s.count + c.count)
            if g.context ~= "" and weight >= 0.1 then s.contexts = s.contexts + 1 end
        end
    end
    local index = {codes={}, exact={}, prefixes={}}
    local seen = {}
    for k, s in pairs(summaries) do
        s.general = #chars(s.text) > 1 and s.count >= 3 and s.contexts >= 2 and 2 * math.min(1, s.weight / 3) or 0
        index.exact[k] = s
        if not seen[s.code] then seen[s.code] = true; index.codes[#index.codes + 1] = s.code end
        local prefix, letters = "", chars(s.text)
        for i = 1, #letters - 1 do
            prefix = prefix .. letters[i]
            local pk = key(s.code, s.mode, prefix)
            local p = index.prefixes[pk] or {general=0, exact={}}
            index.prefixes[pk] = p
            p.general = math.max(p.general, s.general)
            for ctx, score in pairs(s.exact) do p.exact[ctx] = math.max(p.exact[ctx] or 0, score) end
        end
    end
    table.sort(index.codes)
    return index
end
local function score(s, ctx) return s and math.max(s.general, s.exact[ctx] or 0) or 0 end
function M.score(index, mode, code, text, ctx)
    return score(index.exact[key(code, mode, text)], ctx)
end
function M.prefix_score(index, mode, code, text, ctx)
    if code == "" or text == "" then return 0 end
    local codes, lo, hi = index.codes, 1, #index.codes + 1
    while lo < hi do
        local mid = math.floor((lo + hi) / 2)
        if codes[mid] < code then lo = mid + 1 else hi = mid end
    end
    local best = 0
    for i = lo, math.min(#codes, lo + 63) do
        local value = codes[i]
        if value:sub(1, #code) ~= code then break end
        if #value > #code then best = math.max(best, score(index.prefixes[key(value, mode, text)], ctx)) end
    end
    return best
end
function M.reward(index, mode, raw, text, finish, previous)
    local best, potential, start = previous.learning_score or 0, 0, previous
    if not index or #index.codes == 0 or mode == "" then return best, potential end
    while true do
        local t, r = start and start.text_length or 0, start and start.raw_length or 0
        local fragment = text:sub(t + 1)
        if #chars(fragment) > 16 then break end
        local code, ctx = raw:sub(r + 1, finish), context(text:sub(1, t))
        best = math.max(best, (start and start.learning_score or 0) + M.score(index, mode, code, fragment, ctx))
        potential = math.max(potential, M.prefix_score(index, mode, code, fragment, ctx))
        if not start or r == 0 then break end
        start = start.previous
    end
    return best, potential
end
function M.diff(raw, before, selected, floor, mode)
    local function boundaries(item)
        local map, ends, node = {[0]=0}, {}, item.path
        while node and (node.raw_length or 0) > 0 do
            map[node.raw_length] = node.text_length
            ends[#ends + 1] = node.raw_length
            node = node.previous
        end
        table.sort(ends)
        local r, t = 0, 0
        for _, last in ipairs(ends) do
            if last <= r or map[last] <= t or map[last] > #item.text then return nil end
            r, t = last, map[last]
        end
        if r ~= #raw or t ~= #item.text then return nil end
        return map, ends
    end
    if not before or not selected or before.text == selected.text then return {} end
    local a, ends = boundaries(before)
    local b = boundaries(selected)
    if not a or not b then return {} end
    local result, first = {}, 0
    for _, last in ipairs(ends) do
        if b[last] then
            local text = selected.text:sub(b[first] + 1, b[last])
            if first >= floor and text ~= before.text:sub(a[first] + 1, a[last]) and static(text) then
                result[#result + 1] = {time=os.time(), mode=mode, code=raw:sub(first + 1, last):lower(), text=text,
                    context=context(selected.text:sub(1, b[first])), raw_start=first, raw_end=last,
                    text_start=b[first], text_end=b[last]}
            end
            first = last
        end
    end
    return result
end

local stores = {}
function M.open(name)
    if stores[name] then return stores[name] end
    local store = {events={}, index=M.build({}), count=0, sequence=0, bytes=0, scored_at=os.time(), error=nil}
    stores[name] = store
    if type(LevelDb) ~= "function" then store.error = "LevelDb unavailable"; return store end
    local ok, err = pcall(function()
        local db = LevelDb(name)
        if not db or not db:open() then error("learning database is locked or unavailable") end
        store.db = db
        for k, v in db:query("e/"):iter() do
            if k:sub(1, 2) ~= "e/" then break end
            store.count, store.bytes = store.count + 1, store.bytes + #k + #v
            store.sequence = math.max(store.sequence, tonumber(k:sub(3)) or 0)
            if store.count > 10000 or store.bytes > 16 * 1024 * 1024 then error("learning database limit reached") end
            local f = unframe(v)
            if f and #f == 5 then
                store.events[#store.events + 1] = {time=tonumber(f[1]), mode=f[2], code=f[3], text=f[4], context=f[5]}
            end
        end
        store.index = M.build(store.events)
    end)
    if not ok then
        if store.db then pcall(function() store.db:close() end) end
        store.db, store.error = nil, tostring(err)
    end
    return store
end
function M.confirm(store, events)
    if not store or not store.db or #events == 0 then return false end
    local changed = false
    for _, e in ipairs(events) do
        if store.count >= 10000 then break end
        local k = string.format("e/%010d", store.sequence + 1)
        local value = frame({tostring(e.time), e.mode, e.code, e.text, e.context})
        if store.bytes + #k + #value > 16 * 1024 * 1024 then break end
        local ok, accepted = pcall(function() return store.db:update(k, value) end)
        if not ok or not accepted then store.error = "learning database write failed"; break end
        store.count, store.bytes = store.count + 1, store.bytes + #k + #value
        store.sequence = store.sequence + 1
        store.events[#store.events + 1] = e
        changed = true
    end
    if changed then store.index = M.build(store.events); store.scored_at = os.time() end
    return changed
end
function M.refresh_scores(store)
    if store and store.db and os.time() - store.scored_at >= 60 then
        store.index = M.build(store.events)
        store.scored_at = os.time()
    end
end
M.context = context
return M
