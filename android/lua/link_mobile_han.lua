local data = require("link_mobile_han_data")
local filter = {}

local function single_han(text)
    if utf8.len(text) ~= 1 then return false end
    local code = utf8.codepoint(text)
    return (code >= 0x3400 and code <= 0x9fff) or (code >= 0xf900 and code <= 0xfaff)
        or (code >= 0x20000 and code <= 0x323af)
end

function filter.groups_for(schema)
    local rule = schema.config:get_string("speller/algebra/@0") or ""
    return data[schema.schema_id .. (rule:match("^fuzz/") and "_fuzzy" or "_exact")]
end

function filter.init(env)
    env.groups = filter.groups_for(env.engine.schema)
    env.simplify = Opencc("t2s.json")
end

function filter.func(translation, env)
    local ctx = env.engine.context
    local input = ctx.input
    local group = env.groups and env.groups[input]
    if not group or ctx:get_option("ascii_mode") or ctx.caret_pos ~= #input then
        for cand in translation:iter() do yield(cand) end
        return
    end
    local segment = ctx.composition:back()
    if not segment or segment.start ~= 0 or segment._end ~= #input or not segment:has_tag("abc") then
        for cand in translation:iter() do yield(cand) end
        return
    end

    local iter, stream = translation:iter()
    local seen, deferred = {}, {}
    -- Keep native picks (including personalized ordering and their learning metadata).
    -- Limit skipped entries, not characters delivered. Dense native character
    -- runs keep their original weights and selection metadata across every page.
    local skipped = 0
    while skipped < 64 do
        local cand = iter(stream)
        if not cand then break end
        local original = cand:get_genuine().text
        if cand.start == 0 and cand._end == #input and single_han(cand.text)
           and (group:find(cand.text, 1, true) or (utf8.len(original) == 1 and group:find(original, 1, true))) then
            if not seen[cand.text] then
                seen[cand.text] = true
                yield(cand)
            else
                skipped = skipped + 1
            end
        else
            skipped = skipped + 1
            deferred[#deferred + 1] = cand
        end
    end
    -- Consult only this reading's finite character list. The main phrase stream
    -- stays lazy even when a rare matching character is buried thousands of rows down.
    for _, point in utf8.codes(group) do
        local ch = utf8.char(point)
        if not seen[ch] then
            local text = env.simplify:convert(ch)
            if not seen[text] then
                seen[text] = true
                yield(Candidate("link_mobile_han", 0, #input, text, ""))
            end
        end
    end
    for _, cand in ipairs(deferred) do
        if not seen[cand.text] then yield(cand) end
    end
    for cand in iter, stream do
        if not seen[cand.text] then yield(cand) end
    end
end

return filter
