local prediction = {}

local function current_segment(ctx)
    if ctx.input ~= "" then return nil end
    local segment = ctx.composition:back()
    if segment and (segment:has_tag("prediction") or segment:has_tag("link_personal")) then
        return segment
    end
end

local function clear(ctx)
    ctx:set_property("link_break", "1")
    -- A "thru" record prevents the predictor from recreating the old menu when
    -- a host subsequently refreshes composition or the editor selection.
    ctx.commit_history:push("thru", "")
    ctx:clear()
end

function prediction.dismiss(ctx)
    if not current_segment(ctx) then return false end
    clear(ctx)
    return true
end

function prediction.process_key(ctx, key, schema)
    local segment = current_segment(ctx)
    if not segment then return false end
    local code = key.keycode
    local plain = not (key:ctrl() or key:alt() or key:super() or key:shift())
    if plain and not ctx:get_option("link_private") then
        local digit
        if code >= 48 and code <= 57 then digit = code - 48
        elseif code >= 0xffb0 and code <= 0xffb9 then digit = code - 0xffb0 end
        if digit then
            local slot = (digit + 9) % 10 -- Labels run from 1 to 9, then 0.
            local size = schema.page_size
            local index = segment.selected_index - segment.selected_index % size + slot
            if slot < size and segment:get_candidate_at(index) then
                ctx:select(index)
                return true
            end
        end
    end
    if plain and code >= 97 and code <= 122 then
        -- The same key will start a fresh spelling. Remove the placeholder without
        -- breaking word-to-word learning or firing an empty-composition update.
        ctx.composition:pop_back()
    else
        clear(ctx)
    end
    return false
end

return prediction
