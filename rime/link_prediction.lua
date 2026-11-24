-- Only post-commit menus use an empty input with one of these segment tags.
return function(ctx, key)
    if ctx.input ~= "" then return false end
    local segment = ctx.composition:back()
    if not segment or not (segment:has_tag("prediction") or segment:has_tag("link_personal")) then
        return false
    end

    if key and key.keycode >= 97 and key.keycode <= 122
       and not (key:ctrl() or key:alt() or key:super() or key:shift()) then
        -- The same key will start a fresh spelling. Remove the placeholder without
        -- breaking word-to-word learning or firing an empty-composition update.
        ctx.composition:pop_back()
    else
        ctx:set_property("link_break", "1")
        -- A "thru" record prevents the predictor from recreating the old menu when
        -- a host subsequently refreshes composition or the editor selection.
        ctx.commit_history:push("thru", "")
        ctx:clear()
    end
    return true
end
