local PREFIX = "[EverwindTrainer] "

local function log(message)
    print(PREFIX .. message .. "\n")
end

-- Feature hooks are populated from the reflection dump produced by
-- EverwindDiscovery. Keeping the loader valid during discovery makes install
-- and update testing deterministic.
log("Runtime loaded; awaiting verified Everwind property mappings")

