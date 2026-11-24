-- Counts and short continuations are shared by all Rime sessions in this process.
-- Editor context and its timeout belong to one filter instance only.
local db, users = nil, 0
local MAX_CHOICES, MAX_CHARS, TIMEOUT = 16, 16, 90
local MAX_CONTEXT_WORDS, MAX_CONTEXT_CHARS = 4, 24
local filter = {}
local dismiss_prediction = require("link_prediction")
local public_words = require("link_public_words")

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
    env.history, env.last_time, env.pending, env.suggestions = {}, nil, false, nil
end

local function chinese_context(ctx)
    return not ctx:get_option("ascii_mode") and not ctx:get_option("link_private")
end

local function learning(ctx, env)
    return env.enabled and db and chinese_context(ctx) and not ctx:get_option("incognito_mode")
end

local function expire(env)
    if env.last_time then
        local age = os.time() - env.last_time
        if age < 0 or age > TIMEOUT then reset(env) end
    end
end

-- Index contiguous text so different commit boundaries can match the same history.
-- Longest first, with the most recent word providing the last fallback.
local function history_keys(history)
    local keys, text = {}, ""
    for i = #history, 1, -1 do
        text = history[i] .. text
        if utf8.len(text) > MAX_CONTEXT_CHARS then break end
        table.insert(keys, 1, "after/" .. text)
    end
    return keys
end

local function remember(ctx, env)
    local text = ctx:get_commit_text()
    if not chinese_context(ctx) or not chinese_word(text) then reset(env) return end
    expire(env)
    if learning(ctx, env) then
        local input = ctx.input
        if #input <= 64 and input:match("^[a-z;']+$") then
            count_choice(env.scheme .. input, text)
        end
        for _, key in ipairs(history_keys(env.history)) do count_choice(key, text) end
        -- Recall the untyped suffix of phrases previously selected in one operation.
        for i = 1, math.min(8, utf8.len(text) - 1) do
            local cut = utf8.offset(text, i + 1)
            count_choice("after/" .. text:sub(1, cut - 1), text:sub(cut))
        end
    end
    if #env.history == MAX_CONTEXT_WORDS then table.remove(env.history, 1) end
    table.insert(env.history, text)
    env.last_time, env.pending, env.suggestions = os.time(), true, nil
end

local function continuations(ctx, env)
    expire(env)
    if env.suggestions then return env.suggestions end
    local history = env.history
    local result = {personal = {}, counts = {}, public = {}}
    env.suggestions = result
    if #history == 0 then return result end
    result.public = public_words.following(table.concat(history), history[#history])
    if not learning(ctx, env) then return result end
    local keys = history_keys(history)
    if #history >= 2 then
        -- 0.4.1 stored some longer two-word contexts only in this namespace.
        table.insert(keys, math.max(1, #keys), "pair/" .. history[#history - 1] .. "/" .. history[#history])
    end
    for _, key in ipairs(keys) do
        for _, item in ipairs(read_choices(key)) do
            if not result.counts[item.word] then
                table.insert(result.personal, item.word)
                result.counts[item.word] = item.count
            end
        end
    end
    return result
end

function filter.init(env)
    reset(env)
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
    env.option = ctx.option_update_notifier:connect(function(context, name)
        if name == "link_dismiss_prediction" then
            if not dismiss_prediction(context) and context.input == "" then reset(env) end
        elseif name == "link_new_editor" or name == "link_private" or name == "incognito_mode" or name == "ascii_mode" then
            reset(env)
        end
    end)
    env.property = ctx.property_update_notifier:connect(function(_, name)
        if name == "link_break" then reset(env) end
    end)
    env.update = ctx.update_notifier:connect(function(context)
        if not env.pending or context.input ~= "" then return end
        env.pending = false
        if not chinese_context(context) or not context:get_option("prediction")
           or not context.composition:empty() then return end
        local suggestions = continuations(context, env)
        if #suggestions.personal == 0 and #suggestions.public == 0 then return end
        -- predict.db doesn't create a placeholder for every word in the other sources.
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
        if not chinese_context(ctx) or not ctx:get_option("prediction") then return end
        local seen = {}
        local suggestions = continuations(ctx, env)
        local function offer(word)
            if not seen[word] then
                seen[word] = true
                yield(Candidate("prediction", first.start, first._end, word, ""))
            end
        end
        -- Reserve first-page room for public suggestions and the built-in language model.
        for i = 1, math.min(3, #suggestions.personal) do offer(suggestions.personal[i]) end
        for _, word in ipairs(suggestions.public) do offer(word) end
        local cand = first
        local native = 0
        while cand do
            if chinese_word(cand.text) and not seen[cand.text] then
                seen[cand.text] = true
                yield(cand)
                native = native + 1
                if native == 2 then
                    for i = 4, #suggestions.personal do offer(suggestions.personal[i]) end
                end
            end
            cand = iter(stream)
        end
        if native < 2 then
            for i = 4, #suggestions.personal do offer(suggestions.personal[i]) end
        end
        return
    end
    local counts = {}
    local contextual = {}
    if learning(ctx, env) and #input <= 64 and input:match("^[a-z;']+$") then
        for _, item in ipairs(read_choices(env.scheme .. input)) do counts[item.word] = item.count end
        contextual = continuations(ctx, env).counts
    end
    if not next(counts) and not next(contextual) then
        yield(first)
    else
        local ranked = {}
        local cand = first
        for i = 1, 24 do
            if not cand then break end
            local score = 0
            if cand._end == #input then
                -- A contextual match breaks homophone ties; well-established spelling habits still count.
                score = (counts[cand.text] or 0) + 2 * math.min(4, contextual[cand.text] or 0)
            end
            ranked[i] = {candidate = cand, index = i, count = score}
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
