-- Counts and short continuations are shared by all Rime sessions in this process.
-- Editor context and its timeout belong to one filter instance only.
local db, users = nil, 0
local MAX_CHOICES, MAX_CHARS, TIMEOUT = 16, 16, 90
local filter = {}

local function is_han(code)
    return (code >= 0x3400 and code <= 0x9FFF) or (code >= 0xF900 and code <= 0xFAFF)
        or (code >= 0x20000 and code <= 0x323AF)
end

local function chinese_word(text)
    if text == "" or #text > 4 * MAX_CHARS or utf8.len(text) > MAX_CHARS then return false end
    for _, code in utf8.codes(text) do
        if not is_han(code) then return false end
    end
    return true
end

local function read_choices(key)
    local items = {}
    for word, count in (db:fetch(key) or ""):gmatch("([^:%s]+):(%d+)") do
        items[#items + 1] = {word = word, count = tonumber(count)}
    end
    return items
end

local function count_choice(key, word)
    local items = read_choices(key)
    local count = 1
    for i, entry in ipairs(items) do
        if entry.word == word then
            count = math.min(entry.count + 1, 65535)
            table.remove(items, i)
            break
        end
    end
    local at = 1
    while at <= #items and items[at].count > count do at = at + 1 end
    -- Preserve room for recently seen alternatives even when the list is full.
    table.insert(items, math.min(at, MAX_CHOICES), {word = word, count = count})
    local parts = {}
    for i = 1, math.min(#items, MAX_CHOICES) do parts[i] = items[i].word .. ":" .. items[i].count end
    db:update(key, table.concat(parts, " "))
end

local function reset(env)
    env.previous, env.older, env.last_time, env.pending = nil, nil, nil, false
end

local function active(ctx, env)
    return env.enabled and db and not ctx:get_option("ascii_mode") and not ctx:get_option("link_private")
        and not ctx:get_option("incognito_mode")
end

local function remember(ctx, env)
    local text = ctx:get_commit_text()
    if not active(ctx, env) or not chinese_word(text) then reset(env) return end
    local now = os.time()
    if env.last_time and (now < env.last_time or now - env.last_time > TIMEOUT) then reset(env) end
    local input = ctx.input
    if #input > 0 and #input <= 64 and input:match("^[a-z;']+$") then
        count_choice(env.scheme .. input, text)
    end
    if env.previous then count_choice("after/" .. env.previous, text) end
    if env.older then
        count_choice("pair/" .. env.older .. "/" .. env.previous, text)
        local combined = env.older .. env.previous
        if utf8.len(combined) <= MAX_CHARS then count_choice("after/" .. combined, text) end
    end
    -- A sentence can also be selected in one operation. Its leading characters can
    -- recall the untyped suffix on a later visit, even when the bundled model cannot.
    for i = 1, math.min(8, utf8.len(text) - 1) do
        local cut = utf8.offset(text, i + 1)
        count_choice("after/" .. text:sub(1, cut - 1), text:sub(cut))
    end
    env.older, env.previous, env.last_time, env.pending = env.previous, text, now, true
end

local function continuations(env)
    if not env.previous or os.time() - env.last_time > TIMEOUT then return {} end
    local result = {}
    local seen = {}
    local function add(key)
        for _, item in ipairs(read_choices(key)) do
            if not seen[item.word] then
                result[#result + 1] = item.word
                seen[item.word] = true
            end
        end
    end
    if env.older then add("pair/" .. env.older .. "/" .. env.previous) end
    add("after/" .. env.previous)
    return result
end

function filter.init(env)
    env.scheme = env.engine.schema.schema_id .. "/"
    env.enabled = env.engine.schema.config:get_bool("translator/enable_user_dict") ~= false
    if env.enabled then
        if not db then
            local storage = LevelDb("link_habits/choices")
            if storage and storage:open() then db = storage end
        end
        users = users + 1
    end
    local ctx = env.engine.context
    env.commit = ctx.commit_notifier:connect(function(context) remember(context, env) end)
    env.option = ctx.option_update_notifier:connect(function(_, name)
        if name == "link_new_editor" or name == "link_private" or name == "incognito_mode" or name == "ascii_mode" then
            reset(env)
        end
    end)
    env.property = ctx.property_update_notifier:connect(function(_, name)
        if name == "link_break" then reset(env) end
    end)
    env.update = ctx.update_notifier:connect(function(context)
        if not env.pending or context.input ~= "" then return end
        env.pending = false
        if not active(context, env) or not context:get_option("prediction")
           or not context.composition:empty() or #continuations(env) == 0 then return end
        -- Public prediction only creates a placeholder when predict.db knows the last word.
        local slot = Segment(0, 0)
        slot.tags = Set({"placeholder", "link_personal"})
        context.composition:push_back(slot)
        context.input = ""
    end)
end

function filter.fini(env)
    env.commit:disconnect()
    env.option:disconnect()
    env.property:disconnect()
    env.update:disconnect()
    if env.enabled then
        users = users - 1
        if users == 0 and db then db:close() db = nil end
    end
end

function filter.func(translation, env)
    local ctx = env.engine.context
    local input = ctx.input
    local iter, stream = translation:iter()
    local first = iter(stream)
    if not first then return end
    local kind = first:get_genuine().type
    if kind == "prediction" or kind == "link_anchor" then
        if ctx:get_option("link_private") or ctx:get_option("ascii_mode") then return end
        local seen = {}
        if active(ctx, env) then
            for _, word in ipairs(continuations(env)) do
                seen[word] = true
                yield(Candidate("prediction", first.start, first._end, word, ""))
            end
        end
        local cand = first
        while cand do
            if chinese_word(cand.text) and not seen[cand.text] then
                seen[cand.text] = true
                yield(cand)
            end
            cand = iter(stream)
        end
        return
    end
    local counts = {}
    if active(ctx, env) and #input <= 64 and input:match("^[a-z;']+$") then
        for _, item in ipairs(read_choices(env.scheme .. input)) do counts[item.word] = item.count end
    end
    if not next(counts) then
        yield(first)
    else
        local ranked = {}
        local cand = first
        for i = 1, 24 do
            if not cand then break end
            ranked[i] = {candidate = cand, index = i, count = cand._end == #input and (counts[cand.text] or 0) or 0}
            if i < 24 then cand = iter(stream) end
        end
        table.sort(ranked, function(a, b)
            if a.count == b.count then return a.index < b.index end
            return a.count > b.count
        end)
        for _, item in ipairs(ranked) do yield(item.candidate) end
    end
    for candidate in iter, stream do yield(candidate) end
end

return filter
