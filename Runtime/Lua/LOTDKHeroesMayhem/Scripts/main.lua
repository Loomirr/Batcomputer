-- Settings: edit these while the game is closed. Requires the included native helper.
local ENABLED_ON_STARTUP = true
local DEBUG_LOGGING = false
local enabled = ENABLED_ON_STARTUP
local prefix = "[LOTDKHeroesMayhem] "
if type(LOTDK_SetCrossModeAccess) ~= "function" then
    print(prefix .. "INACTIVE: install/enable LOTDKModeAccess, then restart the game.\n")
    return
end
if type(LOTDK_SetCrossModeDebugLogging) == "function" then LOTDK_SetCrossModeDebugLogging(DEBUG_LOGGING) end
local function apply(startup)
    local active = LOTDK_SetCrossModeAccess(enabled)
    local status = enabled and "ON" or "OFF"
    local note = startup and "F10 toggles this session." or "Return to title and reload to refresh the roster. Restart for a clean reset."
    print(prefix .. status .. (active and ". " or " requested (helper not active yet). ") .. note .. "\n")
end
apply(true)
if type(RegisterKeyBind) ~= "function" or not Key then
    print(prefix .. "Shortcut unavailable; change ENABLED_ON_STARTUP or disable this mod before launch.\n")
    return
end
if type(IsKeyBindRegistered) == "function" and IsKeyBindRegistered(Key.F10) then
    print(prefix .. "F10 is already registered; shortcut not installed.\n")
    return
end
RegisterKeyBind(Key.F10, function()
    enabled = not enabled
    apply(false)
end)
