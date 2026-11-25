return function(segments, env)
    if segments:get_current_start_position() ~= 0 then return true end
    local ctx = env.engine.context
    if ctx:get_option("link_private") then return true end
    local input = segments.input
    local tags
    if input:sub(1, 1) == "@" then tags = {"link_email"}
    elseif not ctx:get_option("ascii_mode") and input:match("^[A-Z][A-Za-z]*%d[A-Za-z0-9]*$") then
        tags = {"link_identifier"}
        -- The original converters can still be picked explicitly after the literal.
        if input:match("^R%d+$") then tags[2] = "number"
        elseif input:match("^U[0-9a-f]+$") then tags[2] = "unicode"
        elseif #input <= 9 and input:match("^N%d+$") then tags[2] = "gregorian_to_lunar" end
    else return true end
    local segment = Segment(0, #input)
    segment.tags = Set(tags)
    segments:add_segment(segment)
    return false
end
