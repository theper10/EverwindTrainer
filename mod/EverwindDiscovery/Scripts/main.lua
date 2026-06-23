local PREFIX = "[EverwindDiscovery] "

local function log(message)
    print(PREFIX .. message .. "\n")
end

local ran_dump = false

local function run_discovery(label)
    if ran_dump then
        log("Skipping duplicate dump request: " .. tostring(label))
        return
    end

    ran_dump = true
    log("Starting UObject and reflected property dump (" .. tostring(label) .. ")")

    local ok, err = pcall(function()
        DumpAllObjects()
    end)

    if ok then
        log("Object dump complete")
    else
        log("Object dump failed: " .. tostring(err))
    end
end

log("Loaded; discovery dump attempts scheduled")

ExecuteInGameThreadWithDelay(5000, function()
    run_discovery("5s")
end)

ExecuteInGameThreadWithDelay(30000, function()
    run_discovery("30s")
end)

RegisterBeginPlayPostHook(function(Actor)
    run_discovery("BeginPlay")
end)
