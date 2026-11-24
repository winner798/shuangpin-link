return function(segments)
    if segments.input:sub(1, 1) ~= "@" or segments:get_current_start_position() ~= 0 then
        return true
    end
    local segment = Segment(0, #segments.input)
    segment.tags = Set({"link_email"})
    segments:add_segment(segment)
    return false
end
