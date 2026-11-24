-- Generated from public words at build time. One shared lookup table per Lua VM.
local loaded, entries = pcall(require, "link_public_words_data")
local public = {}

function public.following(history_text, last_word)
    if not loaded then return {} end
    local text = history_text and history_text ~= "" and history_text or last_word
    if not text or text == "" then return {} end
    local length = utf8.len(text)
    if not length then return {} end
    for n = math.min(length, 6), 2, -1 do
        local offset = utf8.offset(text, -n)
        local words = entries[text:sub(offset)]
        if words then
            local result = {}
            for word in words:gmatch("%S+") do result[#result + 1] = word end
            return result
        end
    end
    return {}
end

return public
