local accepted, noop = 1, 2
local domains = require("link_domains")
local dismiss_prediction = require("link_prediction")

local function domain_prefix(prefix)
    for _, domain in ipairs(domains) do
        if domain:sub(1, #prefix) == prefix then return true end
    end
    return false
end

return function(key, env)
    if key:release() then return noop end
    local ctx = env.engine.context
    local code = key.keycode
    dismiss_prediction(ctx, key)
    if ctx:get_option("link_private") then return noop end
    local segment = ctx.composition:back()
    if segment and segment:has_tag("link_email") then
        if key:ctrl() or key:alt() or key:super() then
            env.engine:commit_text(ctx.input)
            ctx:clear()
            return noop
        end
        if code == 0xff08 then -- Backspace
            if #ctx.input == 1 then ctx:clear() else ctx:pop_input(1) end
            return accepted
        elseif code == 0xff1b then
            ctx:clear()
            return accepted
        elseif code == 0xff09 then -- Tab accepts the first matching domain.
            if segment:get_candidate_at(1) then ctx:select(1) else ctx:commit() end
            return accepted
        elseif code == 32 or code == 0xff0d then
            if segment.selected_index > 0 then
                ctx:select(segment.selected_index)
            else
                env.engine:commit_text(ctx.input .. (code == 32 and " " or ""))
                ctx:clear()
            end
            return accepted
        elseif code == 0xff52 or code == 0xff54 then -- Up / Down, including in ASCII mode.
            local index = math.max(0, segment.selected_index + (code == 0xff54 and 1 or -1))
            if segment:get_candidate_at(index) then segment.selected_index = index end
            return accepted
        elseif code >= 32 and code < 128 and string.char(code):match("[A-Za-z0-9.%-]") and #ctx.input < 64
           and (ctx:get_option("ascii_mode") or domain_prefix((ctx.input:sub(2) .. string.char(code)):lower())) then
            ctx:push_input(string.char(code))
            return accepted
        elseif code >= 97 and code <= 122 and ctx.input:match("^@[a-z]*$") and not ctx:get_option("ascii_mode") then
            local pending = ctx.input:sub(2)
            env.engine:commit_text("@")
            ctx:clear()
            for i = 1, #pending do env.engine:process_key(KeyEvent(pending:byte(i), 0)) end
            return noop
        else
            -- Keep ordinary @mentions, custom domains and punctuation intact.
            env.engine:commit_text(ctx.input)
            ctx:clear()
            segment = nil
        end
    end
    if not key:ctrl() and not key:alt() and not key:super() and code == 64 then
        ctx:set_property("link_break", "1")
        -- In Chinese mode a Latin username may still be in the preedit.
        if ctx.input ~= "" then env.engine:commit_text(ctx.input) end
        ctx:clear()
        ctx:push_input("@")
        return accepted
    end
    if key:ctrl() or key:alt() or key:super() or
       code == 0xff0d or code == 0xff1b or code == 0xff09 or
       (ctx.input == "" and ((code >= 32 and code < 127 and (code < 97 or code > 122)) or
         code == 0xff08 or code == 0xffff or (code >= 0xff50 and code <= 0xff57))) then
        ctx:set_property("link_break", "1")
    end
    return noop
end
