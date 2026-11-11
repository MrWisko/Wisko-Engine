-- Clock.lua (on the "Clock" marker): a day and night cycle with Wisko.DayNight (a day lasts 3 minutes), street lamps that
-- switch on after dusk and house windows that glow at night.
--   N  jump to 22:00 (night)   M  jump to 7:00 (morning)   P  pause or resume the clock
-- The lamps are parts named "Lamp ..." with a point light; the windows are parts named "Window ..." with a glow.

local DayNight = require("Wisko.DayNight")
local lamps, windows = {}, {}
local lit

local function setNight(night)
  if night == lit then
    return
  end
  lit = night
  for _, lamp in ipairs(lamps) do
    lamp.Light.Enabled = night
    lamp.Material.EmissiveIntensity = night and 6 or 0
  end
  for _, window in ipairs(windows) do
    window.Material.EmissiveIntensity = night and 3 or 0
  end
  print(night and "Clock: lamps on" or "Clock: lamps off")
end

function Start()
  for _, object in ipairs(Scene.GetAll()) do
    if string.sub(object.Name, 1, 4) == "Lamp" and object.Light ~= nil then
      lamps[#lamps + 1] = object
    elseif string.sub(object.Name, 1, 6) == "Window" then
      windows[#windows + 1] = object
    end
  end
  GameTime.DayLength = 180
  GameTime.Set(1, 15)
  DayNight.Start()
end

function Update()
  if Input.IsKeyPressed("N") then
    GameTime.Set(GameTime.Day, 22)
  elseif Input.IsKeyPressed("M") then
    GameTime.Set(GameTime.Day + 1, 7)
  elseif Input.IsKeyPressed("P") then
    if GameTime.Paused then
      GameTime.Resume()
    else
      GameTime.Pause()
    end
  end
  local hour = GameTime.TimeOfDay * 24
  setNight(hour >= 18.5 or hour < 6.5)
end
