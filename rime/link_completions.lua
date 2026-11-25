local domains = require("link_domains")

return function(input, segment)
    if segment:has_tag("link_personal") then
        -- Gives the final filter a stream when the bundled predictor has no entry.
        yield(Candidate("link_anchor", 0, 0, "", ""))
    elseif segment:has_tag("link_identifier") then
        local literal = Candidate("link_identifier", 0, #input, input, "")
        literal.quality = 100000 -- The bundled lunar converter assigns its date 99999.
        yield(literal)
    elseif segment:has_tag("link_email") then
        yield(Candidate("link_email", 0, #input, input, ""))
        local suffix = input:sub(2):lower()
        for _, domain in ipairs(domains) do
            if domain:sub(1, #suffix) == suffix and domain ~= suffix then
                yield(Candidate("link_email", 0, #input, "@" .. domain, ""))
            end
        end
    end
end
