local function has_han(text)
    for _, code in utf8.codes(text) do
        if (code >= 0x3400 and code <= 0x9FFF) or
           (code >= 0xF900 and code <= 0xFAFF) or
           (code >= 0x20000 and code <= 0x323AF) then
            return true
        end
    end
    return false
end

return function(translation)
    for candidate in translation:iter() do
        if candidate:get_genuine().type ~= "prediction" or has_han(candidate.text) then
            yield(candidate)
        end
    end
end
