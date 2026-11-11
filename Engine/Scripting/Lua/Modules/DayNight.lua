-- Wisko.DayNight: an optional day/night cycle that drives the Atmosphere's sun, sky and ambient light from GameTime.
--
--   local DayNight = require("Wisko.DayNight")
--   GameTime.DayLength = 240                     -- a game day lasts 4 minutes
--   local cycle = DayNight.Start()              -- the sun rises at 6, sets at 18; the sky follows
--   -- with Wisko.Weather: DayNight.Start({ Weather = weather }) dims the sun under clouds and rain
--
-- What it changes (each frame, only the Atmosphere's existing settings): SunElevation, SunHeading, SunIntensity,
-- SunColor, SkyColor, HorizonColor, AmbientColor, AmbientIntensity, SkyIntensity. Wisko has no separate night-sky
-- renderer (no stars or moon): night is a dark blue sky and dim ambient light with the sun below the horizon.
-- Keyframes (hour -> look) are plain tables; give your own with Keyframes = {...}. Ordinary Lua on Wisko's public API.

local DayNight = {}

-- Looks at hours of the day; values blend between neighbouring keyframes (and from 21 round to 5).
DayNight.Keyframes = {
  { Hour = 0, SkyColor = "#0B1430", HorizonColor = "#1C2748", SunColor = "#8FA4FF", AmbientColor = "#5A6A9A", AmbientIntensity = 0.16, SunIntensity = 0.0, SkyIntensity = 0.45 },
  { Hour = 5, SkyColor = "#1B2550", HorizonColor = "#3A3F6A", SunColor = "#FF9E6B", AmbientColor = "#6C76A8", AmbientIntensity = 0.18, SunIntensity = 0.05, SkyIntensity = 0.55 },
  { Hour = 6.5, SkyColor = "#5C7FC4", HorizonColor = "#F2A97C", SunColor = "#FFB27A", AmbientColor = "#B49AA8", AmbientIntensity = 0.26, SunIntensity = 0.6, SkyIntensity = 0.8 },
  { Hour = 9, SkyColor = "#4E8AE2", HorizonColor = "#B0D0F0", SunColor = "#FFF2DC", AmbientColor = "#CCDBFF", AmbientIntensity = 0.34, SunIntensity = 1.0, SkyIntensity = 1.0 },
  { Hour = 15, SkyColor = "#4E8AE2", HorizonColor = "#B0D0F0", SunColor = "#FFF2DC", AmbientColor = "#CCDBFF", AmbientIntensity = 0.36, SunIntensity = 1.0, SkyIntensity = 1.0 },
  { Hour = 17.5, SkyColor = "#5A78C0", HorizonColor = "#F4A26E", SunColor = "#FFA060", AmbientColor = "#C29C94", AmbientIntensity = 0.28, SunIntensity = 0.7, SkyIntensity = 0.85 },
  { Hour = 19, SkyColor = "#26305E", HorizonColor = "#6A4A6A", SunColor = "#FF7A50", AmbientColor = "#7A72A0", AmbientIntensity = 0.2, SunIntensity = 0.1, SkyIntensity = 0.6 },
  { Hour = 21, SkyColor = "#0B1430", HorizonColor = "#1C2748", SunColor = "#8FA4FF", AmbientColor = "#5A6A9A", AmbientIntensity = 0.16, SunIntensity = 0.0, SkyIntensity = 0.45 },
}

local colorKeys = { "SkyColor", "HorizonColor", "SunColor", "AmbientColor" }
local numberKeys = { "AmbientIntensity", "SunIntensity", "SkyIntensity" }

local function rgb(hex)
  hex = string.gsub(hex, "#", "")
  return tonumber(string.sub(hex, 1, 2), 16), tonumber(string.sub(hex, 3, 4), 16), tonumber(string.sub(hex, 5, 6), 16)
end

local function mix(a, b, t)
  local ar, ag, ab = rgb(a)
  local br, bg, bb = rgb(b)
  return string.format("#%02X%02X%02X", math.floor(ar + (br - ar) * t + 0.5), math.floor(ag + (bg - ag) * t + 0.5), math.floor(ab + (bb - ab) * t + 0.5))
end

-- The look at an hour: the keyframes around it, blended.
function DayNight.Sample(keyframes, hour)
  local count = #keyframes
  local before, after = keyframes[count], keyframes[1]
  for i = 1, count do
    if keyframes[i].Hour <= hour then
      before = keyframes[i]
      after = keyframes[i + 1] or keyframes[1]
    end
  end
  local span = (after.Hour - before.Hour) % 24
  local t = span > 0 and ((hour - before.Hour) % 24) / span or 0
  local look = {}
  for _, key in ipairs(colorKeys) do
    look[key] = mix(before[key], after[key], t)
  end
  for _, key in ipairs(numberKeys) do
    look[key] = before[key] + (after[key] - before[key]) * t
  end
  return look
end

-- The sun at an hour: elevation (up to MaxElevation at noon, below the horizon at night) and heading (east to west).
function DayNight.Sun(hour, sunrise, sunset, maxElevation)
  local day = (hour - sunrise) / (sunset - sunrise)
  local elevation
  if day >= 0 and day <= 1 then
    elevation = math.sin(day * math.pi) * maxElevation
  else
    elevation = -10
  end
  local heading = (90 + day * 180) % 360
  return elevation, heading
end

-- Starts the cycle. Options: Keyframes, Sunrise (6), Sunset (18), MaxElevation (70 degrees), Weather (a Wisko.Weather:
-- its Light dims the sun), Sun (true: move the sun), Colors (true: change the sky and light colours).
function DayNight.Start(options)
  options = options or {}
  local cycle = {
    Keyframes = options.Keyframes or DayNight.Keyframes,
    Sunrise = options.Sunrise or 6,
    Sunset = options.Sunset or 18,
    MaxElevation = options.MaxElevation or 70,
    Weather = options.Weather,
  }
  local moveSun, changeColors = options.Sun ~= false, options.Colors ~= false
  local lastHour, lastLight

  function cycle:Apply(hour)
    local light = self.Weather and self.Weather.Light or 1
    if hour == lastHour and light == lastLight then
      return
    end
    lastHour, lastLight = hour, light
    local look = DayNight.Sample(self.Keyframes, hour)
    if moveSun then
      local elevation, heading = DayNight.Sun(hour, self.Sunrise, self.Sunset, self.MaxElevation)
      Atmosphere.SunElevation = elevation
      Atmosphere.SunHeading = heading
    end
    if changeColors then
      for _, key in ipairs(colorKeys) do
        Atmosphere[key] = look[key]
      end
      Atmosphere.AmbientIntensity = look.AmbientIntensity * (0.6 + 0.4 * light)
      Atmosphere.SkyIntensity = look.SkyIntensity * (0.7 + 0.3 * light)
    end
    Atmosphere.SunIntensity = look.SunIntensity * light
  end

  -- The Atmosphere changes in steps of a game minute (and when the weather's light changes).
  cycle._tick = Time.OnUpdate(function()
    local hour = math.floor(GameTime.Hour * 60) / 60
    cycle:Apply(hour)
  end)

  function cycle:Stop()
    self._tick:Disconnect()
  end

  cycle:Apply(GameTime.Hour)
  return cycle
end

return DayNight
