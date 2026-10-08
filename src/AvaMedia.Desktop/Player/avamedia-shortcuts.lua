local mp = require "mp"
local options = { language = "zh" }
require("mp.options").read_options(options, "avamedia-shortcuts")

local last_speed = 1
local help_visible = false
local help = options.language == "en" and
    "Space Play/Pause · Enter/Alt+Enter/F11 Fullscreen · Esc Exit fullscreen\n" ..
    "Left/Right 5 s · Shift+Left/Right 30 s · Ctrl/Cmd+Left/Right 60 s\n" ..
    "Up/Down Volume · M Mute · X/C Slower/Faster · Z Normal/Last speed\n" ..
    "D/F Previous/Next frame · Backspace/Home Start · F4 Stop\n" ..
    "Page Up/Down Previous/Next file · F6 Playlist · F1 Shortcuts\n" ..
    "Ctrl+E / Cmd+E Capture PNG to video folder · Q Close player" or
    "Space 播放/暂停 · Enter/Alt+Enter/F11 全屏 · Esc 退出全屏\n" ..
    "←/→ 5秒 · Shift+←/→ 30秒 · Ctrl/Cmd+←/→ 60秒\n" ..
    "↑/↓ 音量 · M 静音 · X/C 减速/加速 · Z 正常/上次速度\n" ..
    "D/F 前/后一帧 · Backspace/Home 起点 · F4 停止\n" ..
    "Page Up/Down 上一/下一文件 · F6 播放列表 · F1 快捷键\n" ..
    "Ctrl+E / Cmd+E 截图为 PNG，保存到视频目录 · Q 关闭播放器"

local binding_index = 0
local function bind(keys, action, repeatable)
    for _, key in ipairs(keys) do
        binding_index = binding_index + 1
        mp.add_key_binding(key, "avamedia-" .. binding_index, function()
            if help_visible then
                help_visible = false
                mp.osd_message("")
            end
            action()
        end, { repeatable = repeatable == true })
    end
end

local function command(...)
    local args = { ... }
    return function() mp.commandv(unpack(args)) end
end

local function set_speed(speed)
    speed = math.max(0.25, math.min(4, math.floor(speed * 100 + 0.5) / 100))
    mp.set_property_number("speed", speed)
    mp.osd_message((options.language == "en" and "Speed " or "速度 ") .. string.format("%g×", speed))
end

bind({ "SPACE" }, command("cycle", "pause"))
bind({ "ENTER", "Alt+ENTER", "F11" }, command("cycle", "fullscreen"))
bind({ "LEFT" }, command("osd-msg", "seek", -5, "relative+exact"), true)
bind({ "RIGHT" }, command("osd-msg", "seek", 5, "relative+exact"), true)
bind({ "Shift+LEFT" }, command("osd-msg", "seek", -30, "relative+exact"), true)
bind({ "Shift+RIGHT" }, command("osd-msg", "seek", 30, "relative+exact"), true)
bind({ "Ctrl+LEFT", "Meta+LEFT" }, command("osd-msg", "seek", -60, "relative+exact"), true)
bind({ "Ctrl+RIGHT", "Meta+RIGHT" }, command("osd-msg", "seek", 60, "relative+exact"), true)
bind({ "UP" }, command("osd-msg", "add", "volume", 5), true)
bind({ "DOWN" }, command("osd-msg", "add", "volume", -5), true)
bind({ "m" }, command("osd-msg", "cycle", "mute"))
bind({ "x" }, function() set_speed(mp.get_property_number("speed", 1) - 0.1) end, true)
bind({ "c" }, function() set_speed(mp.get_property_number("speed", 1) + 0.1) end, true)
bind({ "z" }, function()
    local current = mp.get_property_number("speed", 1)
    if math.abs(current - 1) < 0.001 then set_speed(last_speed)
    else last_speed = current; set_speed(1) end
end)
bind({ "d" }, command("frame-back-step"))
bind({ "f" }, command("frame-step"))
bind({ "BS", "HOME" }, command("osd-msg", "seek", 0, "absolute+exact"))
bind({ "PGUP" }, command("playlist-prev", "weak"))
bind({ "PGDWN" }, command("playlist-next", "weak"))
bind({ "F4" }, function()
    mp.set_property_bool("pause", true)
    mp.commandv("osd-msg", "seek", 0, "absolute+exact")
end)
bind({ "Ctrl+e", "Meta+e" }, command("osd-msg", "screenshot", "video"))
bind({ "F6" }, function() mp.osd_message(mp.get_property_osd("playlist"), 5) end)

-- Help toggles independently; Esc dismisses it before leaving fullscreen.
mp.add_key_binding("F1", "avamedia-help", function()
    help_visible = not help_visible
    mp.osd_message(help_visible and help or "", help_visible and 3600 or 0)
end)
mp.add_key_binding("ESC", "avamedia-dismiss", function()
    if help_visible then help_visible = false; mp.osd_message("")
    else mp.set_property_bool("fullscreen", false) end
end)
