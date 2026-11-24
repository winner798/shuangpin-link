local domains = require("link_domains")

return function(input, segment)
    if segment:has_tag("link_personal") then
        -- Gives the final filter a stream when the bundled predictor has no entry.
        yield(Candidate("link_anchor", 0, 0, "", ""))
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
