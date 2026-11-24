local index = require("link_initials_data")
-- Several near-identical corpus counts crowd everyday short forms off the first page.
index.zg = "中国 这个 最高"
index.nh = "你好 女孩"

return function(translation, env)
    local ctx = env.engine.context
    local input = ctx.input
    local words = index[input]
    local first = true
    for cand in translation:iter() do
        yield(cand)
        if first and words and not ctx:get_option("ascii_mode") and not ctx:get_option("link_private")
           and cand.start == 0 and cand:get_genuine().type ~= "prediction" then
            for word in words:gmatch("%S+") do
                if word ~= cand.text then yield(Candidate("link_initial", 0, #input, word, "")) end
            end
        end
        first = false
    end
end
