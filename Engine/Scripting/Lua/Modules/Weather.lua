-- Wisko.Weather: optional weather states with smooth transitions of the Atmosphere's fog and light, rain and snow drawn
-- with particles, wind, and lightning flashes.
--
--   local Weather = require("Wisko.Weather")
--   local weather = Weather.new({ Initial = "Clear", WindTag = "Wind" })
--   weather:Set("Rain", 8)                  -- blends to rain over 8 seconds
--   weather:OnChanged(function(state, previous) print(previous .. " -> " .. state) end)
--
-- What is visible and what is only a value:
--   * Fog (density, colour, start and end), haze, saturation and light (exposure): the Atmosphere, drawn.
--   * Rain and snow: particle streaks / flakes around the camera, drawn (they do not collide or wet anything).
--   * Lightning: a short exposure flash (and an optional camera shake), drawn; no bolt geometry.
--   * Wind: a vector. It slants the rain and drifts the snow (visible), and pushes unanchored Parts tagged WindTag (a
--     force). Nothing else feels it unless a script reads weather.Wind.
--   * Clouds: there is no cloud renderer; "Cloudy" only greys and dims the light.
-- States are tables (the defaults are in Weather.States; give States = {...} for your own). Ordinary Lua on Wisko's
-- public API: copy it to Assets/Scripts/Wisko/Weather.lua to change it.

local Signal = require("Wisko.Signal")

local Weather = {}
Weather.__index = Weather

-- Numbers that blend, and colours (as "#RRGGBB").
local numbers = { "FogDensity", "FogStart", "FogEnd", "Haze", "Saturation", "Exposure" }
local colors = { "FogColor" }

-- Light: a multiplier others (Wisko.DayNight) may apply to the sun; Rain / Snow: particles per second; Wind: a vector.
Weather.States = {
  Clear = { Fog = false, FogDensity = 0.3, FogStart = 200, FogEnd = 1400, FogColor = "#C7D8E8", Haze = 0.2, Saturation = 1, Exposure = 0,
    Light = 1, Rain = 0, Snow = 0, Wind = Vector3.new(2, 0, 1) },
  Cloudy = { Fog = true, FogDensity = 0.35, FogStart = 120, FogEnd = 900, FogColor = "#B4BCC6", Haze = 0.45, Saturation = 0.8, Exposure = -0.2,
    Light = 0.7, Rain = 0, Snow = 0, Wind = Vector3.new(5, 0, 2) },
  Fog = { Fog = true, FogDensity = 0.85, FogStart = 6, FogEnd = 90, FogColor = "#C9CFD6", Haze = 0.7, Saturation = 0.75, Exposure = -0.1,
    Light = 0.6, Rain = 0, Snow = 0, Wind = Vector3.new(0.5, 0, 0.3) },
  Rain = { Fog = true, FogDensity = 0.55, FogStart = 30, FogEnd = 320, FogColor = "#8F9AA6", Haze = 0.55, Saturation = 0.7, Exposure = -0.35,
    Light = 0.5, Rain = 1400, Snow = 0, Wind = Vector3.new(6, 0, 3) },
  Storm = { Fog = true, FogDensity = 0.7, FogStart = 18, FogEnd = 200, FogColor = "#6F7884", Haze = 0.65, Saturation = 0.6, Exposure = -0.6,
    Light = 0.35, Rain = 2600, Snow = 0, Wind = Vector3.new(14, 0, 6), Lightning = 6 },
  Snow = { Fog = true, FogDensity = 0.6, FogStart = 20, FogEnd = 260, FogColor = "#E4E9F0", Haze = 0.6, Saturation = 0.7, Exposure = 0.1,
    Light = 0.75, Rain = 0, Snow = 700, Wind = Vector3.new(2, 0, 1) },
}

local function hexToRgb(hex)
  hex = string.gsub(hex, "#", "")
  return tonumber(string.sub(hex, 1, 2), 16), tonumber(string.sub(hex, 3, 4), 16), tonumber(string.sub(hex, 5, 6), 16)
end

local function mixColor(a, b, t)
  local ar, ag, ab = hexToRgb(a)
  local br, bg, bb = hexToRgb(b)
  return string.format("#%02X%02X%02X", math.floor(ar + (br - ar) * t + 0.5), math.floor(ag + (bg - ag) * t + 0.5), math.floor(ab + (bb - ab) * t + 0.5))
end

local function snapshot(state)
  local copy = {}
  for key, value in pairs(state) do
    copy[key] = value
  end
  return copy
end

-- What the Atmosphere shows now, as a state (so the first transition starts from the project's look).
local function current()
  local state = { Fog = Atmosphere.FogEnabled, FogColor = Atmosphere.FogColor, Light = 1, Rain = 0, Snow = 0, Wind = Vector3.new(0, 0, 0) }
  for _, key in ipairs(numbers) do
    state[key] = Atmosphere[key]
  end
  return state
end

-- Options: States (a table of states; default Weather.States), Initial (a state name, applied at once), WindTag (a tag:
-- unanchored Parts with it are pushed by the wind), WindForce (force per stud/s of wind and per unit of mass, 0.6),
-- Shake (true: lightning shakes the camera a little), Seed (for the lightning's timing).
function Weather.new(options)
  options = options or {}
  local weather = setmetatable({
    States = options.States or Weather.States,
    State = nil,
    Previous = nil,
    Wind = Vector3.new(0, 0, 0),
    Light = 1,
    Transitioning = false,
    WindTag = options.WindTag,
    WindForce = options.WindForce or 0.6,
    Shake = options.Shake ~= false,
    _random = Random.new(options.Seed or 7),
    _changed = Signal.new(),
    _lightning = Signal.new(),
    _from = current(),
    _to = nil,
    _t = 1,
    _duration = 0,
    _flash = 0,
    _nextLightning = nil,
  }, Weather)
  weather._rain = Particles.CreateEmitter({ FollowCamera = true, Position = Vector3.new(0, 22, 0), Area = Vector3.new(70, 6, 70), Rate = 0,
    Lifetime = 0.9, Velocity = Vector3.new(0, -55, 0), Spread = 0.04, Color = "#A9BCD6", Size = 0.05, Length = 1.4, MaxParticles = 6000 })
  weather._snow = Particles.CreateEmitter({ FollowCamera = true, Position = Vector3.new(0, 18, 0), Area = Vector3.new(60, 6, 60), Rate = 0,
    Lifetime = 6, Velocity = Vector3.new(0, -4, 0), Spread = 0.5, Color = "#F4F7FB", Size = 0.16, Length = 0, MaxParticles = 5000 })
  weather._tick = Time.OnUpdate(function(dt) weather:_update(dt) end)
  if options.Initial ~= nil then
    weather:Set(options.Initial, 0)
  end
  return weather
end

-- Blends to a state over seconds (0: at once). Returns true, or false and why.
function Weather:Set(name, seconds)
  local target = self.States[name]
  if target == nil then
    local names = {}
    for key in pairs(self.States) do
      names[#names + 1] = key
    end
    table.sort(names)
    return false, "there is no weather called \"" .. tostring(name) .. "\" (" .. table.concat(names, ", ") .. ")"
  end
  self._from = self:_blended()
  self._to = snapshot(target)
  self._duration = math.max(0, seconds or 0)
  self._t = self._duration > 0 and 0 or 1
  self.Transitioning = self._duration > 0
  local previous = self.State
  self.Previous = previous
  self.State = name
  self._nextLightning = target.Lightning and Time.elapsedTime + self._random:Number(1, target.Lightning) or nil
  self:_apply(self._t)
  self._changed:Fire(name, previous, self)
  Events.Emit("WeatherChanged", { state = name, previous = previous, seconds = self._duration })
  return true
end

function Weather:GetState()
  return self.State
end

-- The blend of the last state and the target at the transition's progress (a plain state table).
function Weather:_blended()
  if self._to == nil then
    return snapshot(self._from)
  end
  local t = self._t
  local result = {}
  for _, key in ipairs(numbers) do
    result[key] = (self._from[key] or 0) + ((self._to[key] or 0) - (self._from[key] or 0)) * t
  end
  for _, key in ipairs(colors) do
    result[key] = mixColor(self._from[key] or "#FFFFFF", self._to[key] or "#FFFFFF", t)
  end
  result.Light = (self._from.Light or 1) + ((self._to.Light or 1) - (self._from.Light or 1)) * t
  result.Rain = (self._from.Rain or 0) + ((self._to.Rain or 0) - (self._from.Rain or 0)) * t
  result.Snow = (self._from.Snow or 0) + ((self._to.Snow or 0) - (self._from.Snow or 0)) * t
  result.Wind = Vector3.lerp(self._from.Wind or Vector3.new(0, 0, 0), self._to.Wind or Vector3.new(0, 0, 0), t)
  -- Fog switches on at the start of a transition into fog, and off at the end of one out of it.
  result.Fog = (self._to.Fog and true) or ((self._from.Fog and t < 1) and true) or false
  return result
end

function Weather:_apply()
  local state = self:_blended()
  Atmosphere.FogEnabled = state.Fog
  Atmosphere.FogColor = state.FogColor
  Atmosphere.FogDensity = state.FogDensity
  Atmosphere.FogEnd = math.max(state.FogEnd, state.FogStart + 1)
  Atmosphere.FogStart = state.FogStart
  Atmosphere.Haze = state.Haze
  Atmosphere.Saturation = state.Saturation
  Atmosphere.Exposure = math.min(3, state.Exposure + self._flash)
  self.Light = state.Light
  self.Wind = state.Wind
  self.RainRate, self.SnowRate = state.Rain, state.Snow
  if self._rain.Exists then
    self._rain.Rate = state.Rain
    -- The streaks fall with the wind.
    self._rain.Velocity = Vector3.new(state.Wind.x * 1.5, -55, state.Wind.z * 1.5)
  end
  if self._snow.Exists then
    self._snow.Rate = state.Snow
    self._snow.Velocity = Vector3.new(state.Wind.x * 0.8, -4, state.Wind.z * 0.8)
  end
end

-- A lightning flash now (an exposure spike fading in about 0.4 s, and a light camera shake).
function Weather:Lightning()
  self._flash = 1.6
  if self.Shake then
    Cameras.Shake(0.15, 0.4, 18)
  end
  self._lightning:Fire(self)
  Events.Emit("Lightning", { state = self.State })
end

function Weather:_update(dt)
  -- The Atmosphere is only written while something changes (a transition, a flash fading).
  local changing = self.Transitioning or self._flash > 0
  if self.Transitioning then
    self._t = math.min(1, self._t + (self._duration > 0 and dt / self._duration or 1))
    if self._t >= 1 then
      self.Transitioning = false
    end
  end
  if self._flash > 0 then
    self._flash = math.max(0, self._flash - dt * 4)
  end
  local target = self._to
  if target ~= nil and target.Lightning and self._nextLightning ~= nil and Time.elapsedTime >= self._nextLightning then
    self:Lightning()
    self._nextLightning = Time.elapsedTime + self._random:Number(1, target.Lightning)
    changing = true
  end
  if changing then
    self:_apply()
  end
  -- Wind pushes unanchored Parts with the wind tag (more on heavier ones: the same drift whatever their mass).
  if self.WindTag ~= nil and Vector3.magnitude(self.Wind) > 0 then
    for _, object in ipairs(Scene.FindAllByTag(self.WindTag)) do
      if not object.Anchored then
        object:ApplyForce(self.Wind * (self.WindForce * object:GetMass()))
      end
    end
  end
end

function Weather:OnChanged(f) return self._changed:Connect(f) end
function Weather:OnLightning(f) return self._lightning:Connect(f) end

-- Plain data for GameSave.
function Weather:Save()
  return { State = self.State }
end

function Weather:Load(data)
  if type(data) == "table" and data.State ~= nil then
    self:Set(data.State, 0)
  end
end

-- Stops it: the particles go; the Atmosphere stays as it is now.
function Weather:Destroy()
  self._tick:Disconnect()
  self._rain:Destroy()
  self._snow:Destroy()
end

return Weather
