return function(segments, env)
    if segments.input:sub(1, 1) ~= "@" or segments:get_current_start_position() ~= 0
       or env.engine.context:get_option("link_private") then
        return true
    end
    local segment = Segment(0, #segments.input)
    segment.tags = Set({"link_email"})
    segments:add_segment(segment)
    return false
end
