-- Neon.lua (on the "Neon" marker): materials and lights driven from Lua.
--   "Neon Pulse" glows brighter and dimmer (Material.EmissiveIntensity);
--   "Rainbow Orb" cycles its light and glow through the colours (Light.Color, Material.EmissiveColor).

local pulse, orb
local t = 0

local function hex(r, g, b)
  return string.format("#%02X%02X%02X", math.floor(r * 255 + 0.5), math.floor(g * 255 + 0.5), math.floor(b * 255 + 0.5))
end

-- A fully saturated colour around the colour wheel (hue 0-1).
local function hue(h)
  local r = math.max(0, math.min(1, math.abs(h * 6 - 3) - 1))
  local g = math.max(0, math.min(1, 2 - math.abs(h * 6 - 2)))
  local b = math.max(0, math.min(1, 2 - math.abs(h * 6 - 4)))
  return hex(r, g, b)
end

function Start()
  pulse = Scene.Find("Neon Pulse")
  orb = Scene.Find("Rainbow Orb")
end

function Update(dt)
  t = t + dt
  if pulse ~= nil then
    pulse.Material.EmissiveIntensity = 3 + 2.5 * math.sin(t * 3)
  end
  if orb ~= nil and orb.Light ~= nil then
    local color = hue((t * 0.12) % 1)
    orb.Light.Color = color
    orb.Material.EmissiveColor = color
  end
end
