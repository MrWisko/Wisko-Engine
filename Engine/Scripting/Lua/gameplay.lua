-- Wisko's gameplay prelude.
--
-- Trusted setup code, run once per Lua runtime by the sandbox prelude (LuaSandbox.Prelude) with its helpers. It returns
-- (1) the extra methods every scene object wrapper gets (Teleport, GetBones, HasBone, GetBoneTransform,
-- OnTriggerEntered, OnTriggerExited) and (2) extendEnvironment(env, wrap), which adds the gameplay globals to one script's
-- environment: Navigation, Perception, Random, Noise, Path, GameTime, GameSave, Cameras, Particles, DebugDraw and Chat
-- (Time.SetTimeScale and Time.OnUpdate are added to the shared Time values here).
--
-- Rules, as for the rest of the sandbox: scripts only ever hold locked tables (handles keep a number; the host looks it
-- up again on every use), wrong arguments raise errors at the script's line, answers that are "not possible now" are
-- nil/false and a reason, and nothing here keeps a script's state alive after it stops (connections and handles belong
-- to the script that made them). Code here never uses s:method() string syntax: string methods come from the running
-- script's own (possibly changed) string library.

local internals = ...
local hostGameplay, hostScene, hostEvents = internals.hostGameplay, internals.hostScene, internals.hostEvents
local newVector3, vectorParts, isFinite, invalidMember = internals.newVector3, internals.vectorParts, internals.isFinite, internals.invalidMember
local objectIds, newConnection, timeValues = internals.objectIds, internals.newConnection, internals.timeValues

local error, type, select, next, ipairs, pairs, tostring, tonumber = error, type, select, next, ipairs, pairs, tostring, tonumber
local setmetatable, getmetatable, rawequal, rawget, rawset, rawlen, pcall = setmetatable, getmetatable, rawequal, rawget, rawset, rawlen, pcall
local floor, ceil, sqrt, abs, min, max = math.floor, math.ceil, math.sqrt, math.abs, math.min, math.max
local sin, cos, atan, pi, huge, mathType = math.sin, math.cos, math.atan, math.pi, math.huge, math.type
local format, sub, find, byte, gsub, rep, lower = string.format, string.sub, string.find, string.byte, string.gsub, string.rep, string.lower
local concat, sort, insert, remove, unpack = table.concat, table.sort, table.insert, table.remove, table.unpack
local utf8len = utf8.len
local osTime, osClock = os.time, os.clock

-- ---------------------------------------------------------------------------------------------------------------------
-- Shared helpers

-- A locked library table: members (functions and constants), values (key -> getter) and setters (key -> function(value)).
-- Unknown members are errors, as everywhere in the API.
local function library(name, members, values, setters)
  return setmetatable({}, {
    __index = function(_, key)
      local member = members[key]
      if member ~= nil then
        return member
      end
      local getter = values and values[key]
      if getter ~= nil then
        return getter()
      end
      invalidMember(name, key)
    end,
    __newindex = function(_, key, value)
      local setter = setters and setters[key]
      if setter ~= nil then
        setter(value)
        return
      end
      if members[key] ~= nil then
        error(name .. "." .. tostring(key) .. " cannot be replaced", 2)
      end
      if values and values[key] ~= nil then
        error(name .. "." .. tostring(key) .. " is read-only", 2)
      end
      invalidMember(name, key)
    end,
    __tostring = function() return name end,
    __metatable = name,
  })
end

-- A handle object (a camera, an emitter, an agent...): getters (key -> function()), setters (key -> function(value)) and
-- methods; the same rules as libraries. describe() gives its __tostring.
local function handleObject(typeName, getters, setters, methods, describe)
  return setmetatable({}, {
    __index = function(_, key)
      local method = methods[key]
      if method ~= nil then
        return method
      end
      local getter = getters[key]
      if getter ~= nil then
        return getter()
      end
      invalidMember(typeName, key)
    end,
    __newindex = function(_, key, value)
      local setter = setters[key]
      if setter ~= nil then
        setter(value)
        return
      end
      if methods[key] ~= nil then
        error(tostring(key) .. " is a method and cannot be replaced", 2)
      end
      if getters[key] ~= nil then
        error(tostring(key) .. " is read-only", 2)
      end
      invalidMember(typeName, key)
    end,
    __tostring = describe or function() return typeName end,
    __metatable = typeName,
  })
end

-- A Lua-side Connection (same members as the engine's): disconnect() is called once.
local function luaConnection(disconnect)
  local connected = true
  local connection
  local function doDisconnect(self)
    if not rawequal(self, connection) then
      error("call it with a colon: connection:Disconnect()", 2)
    end
    if connected then
      connected = false
      disconnect()
    end
  end
  connection = setmetatable({}, {
    __index = function(_, key)
      if key == "Connected" then
        return connected
      end
      if key == "Disconnect" then
        return doDisconnect
      end
      invalidMember("Connection", key)
    end,
    __newindex = function(_, key)
      if key == "Connected" then
        error("Connected is read-only", 2)
      end
      invalidMember("Connection", key)
    end,
    __tostring = function() return "Connection(" .. (connected and "connected" or "disconnected") .. ")" end,
    __metatable = "Connection",
  })
  return connection, function() connected = false end
end

-- Listeners kept in a list; each gets a Lua Connection. fire(...) calls them in order (a copy: listeners may disconnect).
local function newSignal()
  local listeners = {}
  local signal = {}
  function signal.connect(f)
    local entry = { f = f }
    listeners[#listeners + 1] = entry
    local connection = luaConnection(function()
      for i = #listeners, 1, -1 do
        if listeners[i] == entry then
          remove(listeners, i)
        end
      end
    end)
    return connection
  end
  function signal.fire(...)
    if #listeners == 0 then
      return
    end
    local copy = { unpack(listeners) }
    for i = 1, #copy do
      copy[i].f(...)
    end
  end
  function signal.count()
    return #listeners
  end
  return signal
end

local function checkFunction(value, usage, level)
  if type(value) ~= "function" then
    error(usage .. " expects a function, got a " .. type(value) .. " value", level + 1)
  end
end

local function checkNumber(value, what, level, low, high)
  if not isFinite(value) then
    error(what .. " must be a finite number, got a " .. type(value) .. " value", level + 1)
  end
  if (low ~= nil and value < low) or (high ~= nil and value > high) then
    if high == nil then
      error(what .. " must be " .. tostring(low) .. " or more", level + 1)
    end
    error(what .. " must be from " .. tostring(low) .. " to " .. tostring(high), level + 1)
  end
  return value
end

local function checkVector(value, what, level)
  local x, y, z = vectorParts(value)
  if x == nil or not (isFinite(x) and isFinite(y) and isFinite(z)) then
    error(what .. " must be a vector (finite x, y, z), got a " .. type(value) .. " value", level + 1)
  end
  return x, y, z
end

local function checkBoolean(value, what, level)
  if type(value) ~= "boolean" then
    error(what .. " must be true or false, got a " .. type(value) .. " value", level + 1)
  end
  return value
end

-- Options tables: every key must be one of the allowed ones (typos fail loudly).
local function checkOptions(options, allowed, usage, level)
  if options == nil then
    return {}
  end
  if type(options) ~= "table" or getmetatable(options) ~= nil then
    error(usage .. " expects an options table, got a " .. type(options) .. " value", level + 1)
  end
  for key in next, options do
    if allowed[key] == nil then
      local names = {}
      for name in next, allowed do
        names[#names + 1] = name
      end
      sort(names)
      error(usage .. ": " .. tostring(key) .. " is not an option (" .. concat(names, ", ") .. ")", level + 1)
    end
  end
  return options
end

-- "#RRGGBB" -> r, g, b (0..1); nil -> the default.
local function parseColor(value, default, what, level)
  if value == nil then
    return default[1], default[2], default[3]
  end
  if type(value) ~= "string" or not find(value, "^#?%x%x%x%x%x%x$") then
    error(what .. " must be a colour \"#RRGGBB\" (six hex digits)", level + 1)
  end
  local hex = gsub(value, "#", "")
  return tonumber(sub(hex, 1, 2), 16) / 255, tonumber(sub(hex, 3, 4), 16) / 255, tonumber(sub(hex, 5, 6), 16) / 255
end

local function toHex(r, g, b)
  local function channel(v)
    return max(0, min(255, floor(v * 255 + 0.5)))
  end
  return format("#%02X%02X%02X", channel(r), channel(g), channel(b))
end

-- A position argument: a vector, or a scene object (its position).
local function positionOf(value, what, level)
  if objectIds[value] ~= nil then
    local position = value.Transform.Position
    return position.x, position.y, position.z
  end
  return checkVector(value, what, level + 1)
end

-- A point for the navigation host: (object id, 0, 0, 0) or (0, x, y, z).
local function pointSpec(value, what, level)
  local objectId = objectIds[value]
  if objectId ~= nil then
    return objectId, 0, 0, 0
  end
  local x, y, z = checkVector(value, what .. " (a vector or a scene object)", level + 1)
  return 0, x, y, z
end

local function distance3(ax, ay, az, bx, by, bz)
  local dx, dy, dz = bx - ax, by - ay, bz - az
  return sqrt(dx * dx + dy * dy + dz * dz)
end

-- ---------------------------------------------------------------------------------------------------------------------
-- Time.SetTimeScale, Time.OnUpdate (shared by every script: Time's values are one table)

-- The whole simulation's speed: 1 normal, 0.5 half, 0 paused (Update still runs, with dt = 0). Lasts until Stop.
timeValues.SetTimeScale = function(scale)
  if not isFinite(scale) then
    error("Time.SetTimeScale expects a number (1: normal speed, 0: paused), got a " .. type(scale) .. " value", 2)
  end
  local ok, message = hostGameplay("settimescale", scale)
  if not ok then
    error(message, 2)
  end
end

-- f(dt) every frame, after every script's Update and before physics; owned by the calling script.
timeValues.OnUpdate = function(f)
  checkFunction(f, "Time.OnUpdate", 1)
  local id, message = hostGameplay("onupdate", f)
  if not id then
    error(message, 2)
  end
  return newConnection(hostEvents, id)
end

-- ---------------------------------------------------------------------------------------------------------------------
-- DebugDraw (shared)

local defaultDebugColor = { 1, 0.84, 0.04 }

local function debugSeconds(seconds, level)
  if seconds == nil then
    return 0
  end
  return checkNumber(seconds, "DebugDraw: seconds", level + 1, 0)
end

local function drawLine(ax, ay, az, bx, by, bz, r, g, b, seconds)
  return hostGameplay("dline", ax, ay, az, bx, by, bz, r, g, b, seconds) == true
end

local DebugDraw = library("DebugDraw", {
  -- A line from a to b (vectors), "#RRGGBB" (default yellow), for seconds of real time (0: this frame).
  Line = function(a, b, color, seconds)
    local ax, ay, az = checkVector(a, "DebugDraw.Line: the start", 1)
    local bx, by, bz = checkVector(b, "DebugDraw.Line: the end", 1)
    local r, g, bl = parseColor(color, defaultDebugColor, "DebugDraw.Line: the colour", 1)
    return drawLine(ax, ay, az, bx, by, bz, r, g, bl, debugSeconds(seconds, 1))
  end,
  -- From origin along direction (its length is the line's).
  Ray = function(origin, direction, color, seconds)
    local ox, oy, oz = checkVector(origin, "DebugDraw.Ray: the origin", 1)
    local dx, dy, dz = checkVector(direction, "DebugDraw.Ray: the direction", 1)
    local r, g, b = parseColor(color, defaultDebugColor, "DebugDraw.Ray: the colour", 1)
    return drawLine(ox, oy, oz, ox + dx, oy + dy, oz + dz, r, g, b, debugSeconds(seconds, 1))
  end,
  Sphere = function(center, radius, color, seconds)
    local x, y, z = checkVector(center, "DebugDraw.Sphere: the centre", 1)
    checkNumber(radius, "DebugDraw.Sphere: the radius", 1, 0)
    local r, g, b = parseColor(color, defaultDebugColor, "DebugDraw.Sphere: the colour", 1)
    return hostGameplay("dsphere", x, y, z, radius, r, g, b, debugSeconds(seconds, 1)) == true
  end,
  -- A box's edges; rotation in Euler degrees (default none).
  Box = function(center, size, color, seconds, rotation)
    local x, y, z = checkVector(center, "DebugDraw.Box: the centre", 1)
    local sx, sy, sz = checkVector(size, "DebugDraw.Box: the size", 1)
    local rx, ry, rz = 0, 0, 0
    if rotation ~= nil then
      rx, ry, rz = checkVector(rotation, "DebugDraw.Box: the rotation", 1)
    end
    local r, g, b = parseColor(color, defaultDebugColor, "DebugDraw.Box: the colour", 1)
    return hostGameplay("dbox", x, y, z, sx, sy, sz, rx, ry, rz, r, g, b, debugSeconds(seconds, 1)) == true
  end,
  -- A small cross (size: its width, default 0.5).
  Point = function(point, size, color, seconds)
    local x, y, z = checkVector(point, "DebugDraw.Point: the point", 1)
    local h = (size == nil and 0.5 or checkNumber(size, "DebugDraw.Point: the size", 1, 0)) / 2
    local r, g, b = parseColor(color, defaultDebugColor, "DebugDraw.Point: the colour", 1)
    seconds = debugSeconds(seconds, 1)
    drawLine(x - h, y, z, x + h, y, z, r, g, b, seconds)
    drawLine(x, y - h, z, x, y + h, z, r, g, b, seconds)
    return drawLine(x, y, z - h, x, y, z + h, r, g, b, seconds)
  end,
  -- A list of points (vectors), a Path, or a navigation path ({ Points = ... }).
  Path = function(points, color, seconds)
    local r, g, b = parseColor(color, defaultDebugColor, "DebugDraw.Path: the colour", 1)
    seconds = debugSeconds(seconds, 1)
    if getmetatable(points) == "Path" then
      return points:Draw(color, seconds)
    end
    if type(points) == "table" and getmetatable(points) == nil and rawget(points, "Points") ~= nil then
      points = rawget(points, "Points")
    end
    if type(points) ~= "table" then
      error("DebugDraw.Path expects a list of points, a Path or a navigation path, got a " .. type(points) .. " value", 2)
    end
    local px, py, pz
    for i = 1, rawlen(points) do
      local x, y, z = checkVector(rawget(points, i), "DebugDraw.Path: point " .. i, 1)
      if px ~= nil then
        drawLine(px, py, pz, x, y, z, r, g, b, seconds)
      end
      px, py, pz = x, y, z
    end
    return true
  end,
  Clear = function()
    hostGameplay("dclear")
  end,
}, {
  -- Lines shown now (at most 20,000).
  Count = function() return hostGameplay("dcount") or 0 end,
})

-- ---------------------------------------------------------------------------------------------------------------------
-- Random (shared): xoshiro256** on Lua's 64-bit integers, seeded with splitmix64. The same seed gives the same sequence
-- on every platform and every run.

local function rotl(x, k)
  return (x << k) | (x >> (64 - k))
end

local function splitmix(state)
  state = state + 0x9E3779B97F4A7C15
  local z = state
  z = (z ~ (z >> 30)) * 0xBF58476D1CE4E5B9
  z = (z ~ (z >> 27)) * 0x94D049BB133111EB
  return state, z ~ (z >> 31)
end

-- A string seed: FNV-1a over its bytes (64 bits).
local function hashString(text)
  local hash = 0xCBF29CE484222325
  for i = 1, #text do
    hash = (hash ~ byte(text, i)) * 0x100000001B3
  end
  return hash
end

local twoTo53 = 9007199254740992.0

local newRandom

newRandom = function(seed, state)
  local s0, s1, s2, s3
  if state ~= nil then
    s0, s1, s2, s3 = state[1], state[2], state[3], state[4]
  else
    local mix = seed
    if type(seed) == "string" then
      mix = hashString(seed)
    elseif mathType(seed) == "float" then
      mix = hashString(format("%.17g", seed))
    end
    mix, s0 = splitmix(mix)
    mix, s1 = splitmix(mix)
    mix, s2 = splitmix(mix)
    mix, s3 = splitmix(mix)
    if s0 == 0 and s1 == 0 and s2 == 0 and s3 == 0 then
      s0 = 1
    end
  end

  local function nextInteger()
    local result = rotl(s1 * 5, 7) * 9
    local t = s1 << 17
    s2 = s2 ~ s0
    s3 = s3 ~ s1
    s1 = s1 ~ s2
    s0 = s0 ~ s3
    s2 = s2 ~ t
    s3 = rotl(s3, 45)
    return result
  end

  -- 0 <= value < 1, 53 random bits.
  local function nextFloat()
    return (nextInteger() >> 11) / twoTo53
  end

  local generator
  local function check(self, usage)
    if not rawequal(self, generator) then
      error("call it with a colon: random:" .. usage, 3)
    end
  end

  local function integerBetween(low, high)
    return low + floor(nextFloat() * (high - low + 1))
  end

  local methods = {
    -- Number() in 0..1 (1 excluded); Number(max) in 0..max; Number(min, max) in min..max.
    Number = function(self, low, high)
      check(self, "Number([min[, max]])")
      local value = nextFloat()
      if low == nil then
        return value
      end
      if high == nil then
        low, high = 0, low
      end
      checkNumber(low, "Random:Number: min", 1)
      checkNumber(high, "Random:Number: max", 1)
      return low + (high - low) * value
    end,
    -- A whole number from min to max, both included.
    Integer = function(self, low, high)
      check(self, "Integer(min, max)")
      if mathType(low) ~= "integer" and not (isFinite(low) and floor(low) == low) then
        error("Random:Integer expects whole numbers min and max", 2)
      end
      if mathType(high) ~= "integer" and not (isFinite(high) and floor(high) == high) then
        error("Random:Integer expects whole numbers min and max", 2)
      end
      if high < low then
        error("Random:Integer expects min <= max", 2)
      end
      if high - low >= 2 ^ 53 then
        error("Random:Integer: the range is too large (at most 2^53 values)", 2)
      end
      return floor(integerBetween(floor(low), floor(high)))
    end,
    -- true with probability p (default 0.5).
    Bool = function(self, p)
      check(self, "Bool([probability])")
      if p ~= nil then
        checkNumber(p, "Random:Bool: the probability", 1, 0, 1)
      end
      return nextFloat() < (p or 0.5)
    end,
    -- One item of a list (nil for an empty list).
    Choice = function(self, list)
      check(self, "Choice(list)")
      if type(list) ~= "table" then
        error("Random:Choice expects a list, got a " .. type(list) .. " value", 2)
      end
      local count = rawlen(list)
      if count == 0 then
        return nil
      end
      return rawget(list, integerBetween(1, count))
    end,
    -- An item by weight: a list of { item, weight } pairs or { Item = ..., Weight = ... }, or a map item -> weight
    -- (its keys are sorted first, so the choice is the same on every run).
    Weighted = function(self, entries)
      check(self, "Weighted(entries)")
      if type(entries) ~= "table" then
        error("Random:Weighted expects a table of weights, got a " .. type(entries) .. " value", 2)
      end
      local items, weights, total = {}, {}, 0
      local function add(item, weight, where)
        if not isFinite(weight) or weight < 0 then
          error("Random:Weighted: the weight of " .. where .. " must be a finite number of 0 or more", 3)
        end
        items[#items + 1] = item
        weights[#weights + 1] = weight
        total = total + weight
      end
      if rawlen(entries) > 0 then
        for i = 1, rawlen(entries) do
          local entry = rawget(entries, i)
          if type(entry) ~= "table" then
            error("Random:Weighted: entry " .. i .. " must be { item, weight } or { Item = ..., Weight = ... }", 2)
          end
          if rawget(entry, "Weight") ~= nil then
            add(rawget(entry, "Item"), rawget(entry, "Weight"), "entry " .. i)
          else
            add(rawget(entry, 1), rawget(entry, 2), "entry " .. i)
          end
        end
      else
        local keys = {}
        for key in next, entries do
          keys[#keys + 1] = key
        end
        sort(keys, function(a, b)
          local ta, tb = type(a), type(b)
          if ta ~= tb then
            return ta < tb
          end
          if ta == "number" or ta == "string" then
            return a < b
          end
          return tostring(a) < tostring(b)
        end)
        for _, key in ipairs(keys) do
          add(key, rawget(entries, key), tostring(key))
        end
      end
      if total <= 0 then
        return nil
      end
      local pick = nextFloat() * total
      for i = 1, #items do
        pick = pick - weights[i]
        if pick < 0 then
          return items[i]
        end
      end
      return items[#items]
    end,
    -- Shuffles a list in place (and returns it).
    Shuffle = function(self, list)
      check(self, "Shuffle(list)")
      if type(list) ~= "table" then
        error("Random:Shuffle expects a list, got a " .. type(list) .. " value", 2)
      end
      for i = rawlen(list), 2, -1 do
        local j = integerBetween(1, i)
        local a, b = rawget(list, i), rawget(list, j)
        rawset(list, i, b)
        rawset(list, j, a)
      end
      return list
    end,
    -- A direction of length 1, uniform over the sphere.
    UnitVector = function(self)
      check(self, "UnitVector()")
      local z = nextFloat() * 2 - 1
      local angle = nextFloat() * 2 * pi
      local r = sqrt(max(0, 1 - z * z))
      return newVector3(r * cos(angle), r * sin(angle), z)
    end,
    -- A point inside a box given by two corners.
    PointInBox = function(self, low, high)
      check(self, "PointInBox(min, max)")
      local ax, ay, az = checkVector(low, "Random:PointInBox: min", 1)
      local bx, by, bz = checkVector(high, "Random:PointInBox: max", 1)
      return newVector3(ax + (bx - ax) * nextFloat(), ay + (by - ay) * nextFloat(), az + (bz - az) * nextFloat())
    end,
    -- A point on the ground plane (x, 0, z) within radius of the origin, uniform over the disc.
    PointInCircle = function(self, radius)
      check(self, "PointInCircle(radius)")
      checkNumber(radius, "Random:PointInCircle: the radius", 1, 0)
      local r = sqrt(nextFloat()) * radius
      local angle = nextFloat() * 2 * pi
      return newVector3(r * cos(angle), 0, r * sin(angle))
    end,
    -- An independent generator that continues from this one's current state.
    Clone = function(self)
      check(self, "Clone()")
      return newRandom(seed, { s0, s1, s2, s3 })
    end,
    -- The state as four integers (plain data: GameSave can keep it), and back.
    GetState = function(self)
      check(self, "GetState()")
      return { s0, s1, s2, s3 }
    end,
    SetState = function(self, saved)
      check(self, "SetState(state)")
      if type(saved) ~= "table" then
        error("Random:SetState expects the table GetState gave", 2)
      end
      for i = 1, 4 do
        if mathType(rawget(saved, i)) ~= "integer" then
          error("Random:SetState expects the table GetState gave (four integers)", 2)
        end
      end
      s0, s1, s2, s3 = saved[1], saved[2], saved[3], saved[4]
    end,
  }
  generator = handleObject("Random", { Seed = function() return seed end }, {}, methods,
    function() return "Random(" .. tostring(seed) .. ")" end)
  return generator
end

local Random = library("Random", {
  -- Random.new(seed): seed is a whole number or a string (the same seed, the same numbers); nil picks one from the
  -- clock (not reproducible).
  new = function(seed)
    if seed == nil then
      seed = floor(osTime() * 1000 + (osClock() * 1000000) % 1000)
    elseif type(seed) ~= "string" and not isFinite(seed) then
      error("Random.new expects a seed: a number or a string (or nothing), got a " .. type(seed) .. " value", 2)
    end
    if type(seed) == "number" and floor(seed) == seed and seed >= -2 ^ 63 and seed < 2 ^ 63 then
      seed = floor(seed)
      if mathType(seed) ~= "integer" then
        seed = tonumber(format("%.0f", seed)) // 1
      end
    end
    return newRandom(seed)
  end,
})

-- ---------------------------------------------------------------------------------------------------------------------
-- Noise (shared): Perlin's improved noise with a permutation shuffled from a seed; Fractal sums octaves.

local permutations = {}
local permutationOrder = {}

local function permutationFor(seed)
  local cached = permutations[seed]
  if cached ~= nil then
    return cached
  end
  local generator = newRandom(seed)
  local p = {}
  for i = 0, 255 do
    p[i] = i
  end
  for i = 255, 1, -1 do
    local j = generator:Integer(0, i)
    p[i], p[j] = p[j], p[i]
  end
  for i = 0, 255 do
    p[i + 256] = p[i]
  end
  permutations[seed] = p
  permutationOrder[#permutationOrder + 1] = seed
  if #permutationOrder > 32 then
    permutations[remove(permutationOrder, 1)] = nil
  end
  return p
end

local function fade(t)
  return t * t * t * (t * (t * 6 - 15) + 10)
end

local function grad(hash, x, y, z)
  local h = hash & 15
  local u = h < 8 and x or y
  local v = h < 4 and y or ((h == 12 or h == 14) and x or z)
  return ((h & 1) == 0 and u or -u) + ((h & 2) == 0 and v or -v)
end

local function perlin(p, x, y, z)
  local fx, fy, fz = floor(x), floor(y), floor(z)
  local X, Y, Z = fx & 255, fy & 255, fz & 255
  x, y, z = x - fx, y - fy, z - fz
  local u, v, w = fade(x), fade(y), fade(z)
  local A = p[X] + Y
  local AA, AB = p[A] + Z, p[A + 1] + Z
  local B = p[X + 1] + Y
  local BA, BB = p[B] + Z, p[B + 1] + Z
  local function lerp(t, a, b) return a + t * (b - a) end
  return lerp(w,
    lerp(v, lerp(u, grad(p[AA], x, y, z), grad(p[BA], x - 1, y, z)),
      lerp(u, grad(p[AB], x, y - 1, z), grad(p[BB], x - 1, y - 1, z))),
    lerp(v, lerp(u, grad(p[AA + 1], x, y, z - 1), grad(p[BA + 1], x - 1, y, z - 1)),
      lerp(u, grad(p[AB + 1], x, y - 1, z - 1), grad(p[BB + 1], x - 1, y - 1, z - 1))))
end

local function noiseSeed(seed, usage)
  if seed == nil then
    return 0
  end
  if type(seed) ~= "string" and not isFinite(seed) then
    error(usage .. " expects the seed to be a number or a string", 3)
  end
  return seed
end

local Noise = library("Noise", {
  -- Smooth noise in about -1..1 at a point (y and z default to 0); the same seed always gives the same field.
  Perlin = function(x, y, z, seed)
    checkNumber(x, "Noise.Perlin: x", 1)
    if y ~= nil then checkNumber(y, "Noise.Perlin: y", 1) end
    if z ~= nil then checkNumber(z, "Noise.Perlin: z", 1) end
    return perlin(permutationFor(noiseSeed(seed, "Noise.Perlin")), x, y or 0, z or 0)
  end,
  -- Octaves of Perlin noise added together (normalised to about -1..1). Options: Octaves (4), Persistence (0.5),
  -- Lacunarity (2), Scale (1: multiplies the coordinates), Seed (0).
  Fractal = function(x, y, z, options)
    checkNumber(x, "Noise.Fractal: x", 1)
    y = y == nil and 0 or checkNumber(y, "Noise.Fractal: y", 1)
    z = z == nil and 0 or checkNumber(z, "Noise.Fractal: z", 1)
    options = checkOptions(options, { Octaves = true, Persistence = true, Lacunarity = true, Scale = true, Seed = true }, "Noise.Fractal", 1)
    local octaves = options.Octaves == nil and 4 or checkNumber(options.Octaves, "Noise.Fractal: Octaves", 1, 1, 16)
    local persistence = options.Persistence == nil and 0.5 or checkNumber(options.Persistence, "Noise.Fractal: Persistence", 1, 0, 1)
    local lacunarity = options.Lacunarity == nil and 2 or checkNumber(options.Lacunarity, "Noise.Fractal: Lacunarity", 1, 1, 8)
    local scale = options.Scale == nil and 1 or checkNumber(options.Scale, "Noise.Fractal: Scale", 1)
    local p = permutationFor(noiseSeed(options.Seed, "Noise.Fractal"))
    local total, amplitude, frequency, range = 0, 1, scale, 0
    for _ = 1, floor(octaves) do
      total = total + perlin(p, x * frequency, y * frequency, z * frequency) * amplitude
      range = range + amplitude
      amplitude = amplitude * persistence
      frequency = frequency * lacunarity
    end
    return range > 0 and total / range or 0
  end,
})

-- ---------------------------------------------------------------------------------------------------------------------
-- Path (shared): curves through control points, measured by distance along them.

local pathKinds = { Linear = true, CatmullRom = true, Bezier = true }

local newPath

newPath = function(xs, ys, zs, kind, closed)
  local count = #xs
  local segments
  if kind == "Bezier" then
    segments = closed and count // 3 or (count - 1) // 3
  else
    segments = closed and count or count - 1
  end

  local function point(i)
    if closed then
      i = (i - 1) % count + 1
    else
      i = max(1, min(count, i))
    end
    return xs[i], ys[i], zs[i]
  end

  -- Position and derivative of segment s (1-based) at t (0..1).
  local function evaluate(s, t)
    if kind == "Linear" then
      local ax, ay, az = point(s)
      local bx, by, bz = point(s + 1)
      return ax + (bx - ax) * t, ay + (by - ay) * t, az + (bz - az) * t, bx - ax, by - ay, bz - az
    elseif kind == "CatmullRom" then
      local p0x, p0y, p0z = point(s - 1)
      local p1x, p1y, p1z = point(s)
      local p2x, p2y, p2z = point(s + 1)
      local p3x, p3y, p3z = point(s + 2)
      if not closed then
        -- Ends: mirror the neighbour so the curve starts and ends where the points do, without a kink.
        if s == 1 then
          p0x, p0y, p0z = 2 * p1x - p2x, 2 * p1y - p2y, 2 * p1z - p2z
        end
        if s == segments then
          p3x, p3y, p3z = 2 * p2x - p1x, 2 * p2y - p1y, 2 * p2z - p1z
        end
      end
      local t2, t3 = t * t, t * t * t
      local function axis(a, b, c, d)
        local value = 0.5 * ((2 * b) + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3)
        local slope = 0.5 * ((-a + c) + 2 * (2 * a - 5 * b + 4 * c - d) * t + 3 * (-a + 3 * b - 3 * c + d) * t2)
        return value, slope
      end
      local x, dx = axis(p0x, p1x, p2x, p3x)
      local y, dy = axis(p0y, p1y, p2y, p3y)
      local z, dz = axis(p0z, p1z, p2z, p3z)
      return x, y, z, dx, dy, dz
    else
      local base = (s - 1) * 3 + 1
      local ax, ay, az = point(base)
      local bx, by, bz = point(base + 1)
      local cx, cy, cz = point(base + 2)
      local dx, dy, dz = point(base + 3)
      local u = 1 - t
      local function axis(a, b, c, d)
        return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d,
          3 * u * u * (b - a) + 6 * u * t * (c - b) + 3 * t * t * (d - c)
      end
      local x, ex = axis(ax, bx, cx, dx)
      local y, ey = axis(ay, by, cy, dy)
      local z, ez = axis(az, bz, cz, dz)
      return x, y, z, ex, ey, ez
    end
  end

  -- Arc length: samples per segment, cumulative distances.
  local samples = kind == "Linear" and 1 or 32
  local lengths = { [0] = 0 }
  local total = 0
  local px, py, pz = evaluate(1, 0)
  for s = 1, segments do
    for i = 1, samples do
      local x, y, z = evaluate(s, i / samples)
      total = total + distance3(px, py, pz, x, y, z)
      lengths[(s - 1) * samples + i] = total
      px, py, pz = x, y, z
    end
  end
  local sampleCount = segments * samples

  -- (segment, t) at a distance along the path (wrapped when closed, clamped otherwise).
  local function locate(d)
    if total <= 0 then
      return 1, 0
    end
    if closed then
      d = d % total
    else
      d = max(0, min(total, d))
    end
    local low, high = 0, sampleCount
    while high - low > 1 do
      local middle = (low + high) // 2
      if lengths[middle] < d then
        low = middle
      else
        high = middle
      end
    end
    local span = lengths[high] - lengths[low]
    local fraction = span > 0 and (d - lengths[low]) / span or 0
    local sample = low + fraction
    local s = min(segments, floor(sample / samples) + 1)
    return s, (sample - (s - 1) * samples) / samples
  end

  local path
  local function check(self, usage)
    if not rawequal(self, path) then
      error("call it with a colon: path:" .. usage, 3)
    end
  end

  local function tangentAt(d)
    local s, t = locate(d)
    local _, _, _, dx, dy, dz = evaluate(s, t)
    local length = sqrt(dx * dx + dy * dy + dz * dz)
    if length < 1e-9 then
      -- A degenerate spot (two equal points): look a little ahead.
      local ax, ay, az = evaluate(locate(d - 0.01))
      local bx, by, bz = evaluate(locate(d + 0.01))
      dx, dy, dz = bx - ax, by - ay, bz - az
      length = sqrt(dx * dx + dy * dy + dz * dz)
      if length < 1e-9 then
        return 0, 0, 1
      end
    end
    return dx / length, dy / length, dz / length
  end

  local methods = {
    GetPointAtDistance = function(self, d)
      check(self, "GetPointAtDistance(distance)")
      checkNumber(d, "path:GetPointAtDistance: the distance", 1)
      local x, y, z = evaluate(locate(d))
      return newVector3(x, y, z)
    end,
    GetTangentAtDistance = function(self, d)
      check(self, "GetTangentAtDistance(distance)")
      checkNumber(d, "path:GetTangentAtDistance: the distance", 1)
      return newVector3(tangentAt(d))
    end,
    -- t from 0 (start) to 1 (end), by distance (so equal steps of t are equal distances).
    GetPointAt = function(self, t)
      check(self, "GetPointAt(t)")
      checkNumber(t, "path:GetPointAt: t", 1)
      local x, y, z = evaluate(locate(t * total))
      return newVector3(x, y, z)
    end,
    GetTangentAt = function(self, t)
      check(self, "GetTangentAt(t)")
      checkNumber(t, "path:GetTangentAt: t", 1)
      return newVector3(tangentAt(t * total))
    end,
    -- The distance along the path of the point nearest to this one, and that point.
    GetClosestDistance = function(self, value)
      check(self, "GetClosestDistance(point)")
      local x, y, z = checkVector(value, "path:GetClosestDistance: the point", 1)
      local best, bestD = huge, 0
      local steps = max(64, sampleCount * 2)
      for i = 0, steps do
        local d = total * i / steps
        local cx, cy, cz = evaluate(locate(d))
        local gap = distance3(cx, cy, cz, x, y, z)
        if gap < best then
          best, bestD = gap, d
        end
      end
      -- Refine around the best sample.
      local step = total / steps
      for _ = 1, 12 do
        step = step / 2
        for _, d in ipairs({ bestD - step, bestD + step }) do
          if closed or (d >= 0 and d <= total) then
            local cx, cy, cz = evaluate(locate(d))
            local gap = distance3(cx, cy, cz, x, y, z)
            if gap < best then
              best, bestD = gap, d
            end
          end
        end
      end
      if closed then
        bestD = bestD % total
      end
      local cx, cy, cz = evaluate(locate(bestD))
      return bestD, newVector3(cx, cy, cz)
    end,
    GetControlPoints = function(self)
      check(self, "GetControlPoints()")
      local list = {}
      for i = 1, count do
        list[i] = newVector3(xs[i], ys[i], zs[i])
      end
      return list
    end,
    -- The same curve, travelled the other way.
    Reverse = function(self)
      check(self, "Reverse()")
      local rx, ry, rz = {}, {}, {}
      for i = 1, count do
        rx[i], ry[i], rz[i] = xs[count + 1 - i], ys[count + 1 - i], zs[count + 1 - i]
      end
      if closed and kind == "Bezier" then
        -- A closed Bezier's list starts with an anchor; reversed, the anchor must stay first.
        local fx, fy, fz = remove(rx), remove(ry), remove(rz)
        insert(rx, 1, fx)
        insert(ry, 1, fy)
        insert(rz, 1, fz)
      end
      return newPath(rx, ry, rz, kind, closed)
    end,
    -- Debug lines along the curve (default colour cyan).
    Draw = function(self, color, seconds)
      check(self, "Draw([color[, seconds]])")
      local r, g, b = parseColor(color, { 0.2, 0.85, 1 }, "path:Draw: the colour", 1)
      seconds = debugSeconds(seconds, 1)
      local steps = max(1, min(400, sampleCount))
      local ax, ay, az = evaluate(locate(0))
      for i = 1, steps do
        local bx, by, bz = evaluate(locate(total * i / steps))
        drawLine(ax, ay, az, bx, by, bz, r, g, b, seconds)
        ax, ay, az = bx, by, bz
      end
      for i = 1, count do
        local h = 0.25
        drawLine(xs[i] - h, ys[i], zs[i], xs[i] + h, ys[i], zs[i], r, g, b, seconds)
        drawLine(xs[i], ys[i] - h, zs[i], xs[i], ys[i] + h, zs[i], r, g, b, seconds)
      end
      return true
    end,
  }
  path = handleObject("Path", {
    Length = function() return total end,
    Closed = function() return closed end,
    Type = function() return kind end,
    Count = function() return count end,
  }, {}, methods, function() return format("Path(%s, %d points, %.2f long)", kind, count, total) end)
  return path
end

-- object -> its follower's stop function (one follower per object and script at a time).
local Path

-- Moves an object along a path every frame (Time.OnUpdate, owned by the calling script).
local function followPath(object, path, options)
  if objectIds[object] == nil then
    error("Path.Follow expects a scene object, got a " .. type(object) .. " value", 2)
  end
  if getmetatable(path) ~= "Path" then
    error("Path.Follow expects a Path (Path.new), got a " .. type(path) .. " value", 2)
  end
  options = checkOptions(options, { Speed = true, Loop = true, PingPong = true, Orient = true, StartDistance = true,
    Reverse = true, Mode = true, Offset = true }, "Path.Follow", 1)
  local speed = options.Speed == nil and 5 or checkNumber(options.Speed, "Path.Follow: Speed", 1, 0)
  local loop = options.Loop
  if loop == nil then
    loop = path.Closed
  else
    checkBoolean(loop, "Path.Follow: Loop", 1)
  end
  local pingPong = options.PingPong ~= nil and checkBoolean(options.PingPong, "Path.Follow: PingPong", 1) or false
  local orient = options.Orient ~= nil and checkBoolean(options.Orient, "Path.Follow: Orient", 1) or false
  local direction = (options.Reverse ~= nil and checkBoolean(options.Reverse, "Path.Follow: Reverse", 1)) and -1 or 1
  local length = path.Length
  local distance = options.StartDistance == nil and (direction > 0 and 0 or length)
    or checkNumber(options.StartDistance, "Path.Follow: StartDistance", 1)
  local ox, oy, oz = 0, 0, 0
  if options.Offset ~= nil then
    ox, oy, oz = checkVector(options.Offset, "Path.Follow: Offset", 1)
  end
  local mode = options.Mode or "Auto"
  if mode ~= "Auto" and mode ~= "Velocity" and mode ~= "Transform" then
    error("Path.Follow: Mode must be \"Auto\", \"Velocity\" or \"Transform\", got " .. tostring(mode), 2)
  end
  if mode == "Auto" then
    -- An anchored Part physics moves (a platform) is driven by velocity, so what stands on it rides along.
    mode = object.Anchored and object.CanCollide and "Velocity" or "Transform"
  end

  local paused, finished = false, false
  local finishedSignal, loopedSignal = newSignal(), newSignal()
  local connection
  local follower

  local function stopMoving()
    if mode == "Velocity" and object.Exists then
      pcall(function() object.LinearVelocity = newVector3(0, 0, 0) end)
    end
  end

  local function place(dt, first)
    local target = path:GetPointAtDistance(distance)
    local tx, ty, tz = target.x + ox, target.y + oy, target.z + oz
    if mode == "Velocity" and not first then
      local position = object.Transform.Position
      if dt > 0 then
        local ok = pcall(function()
          object.LinearVelocity = newVector3((tx - position.x) / dt, (ty - position.y) / dt, (tz - position.z) / dt)
        end)
        if not ok then
          -- Not moved by physics after all (e.g. a model): place it instead from now on.
          mode = "Transform"
        end
      else
        object.LinearVelocity = newVector3(0, 0, 0)
      end
    end
    if mode == "Transform" or first then
      object.Transform.Position = newVector3(tx, ty, tz)
    end
    if orient then
      local tangent = path:GetTangentAtDistance(distance)
      local s = direction
      object:LookAt(newVector3(tx + tangent.x * s, ty + tangent.y * s, tz + tangent.z * s))
    end
  end

  local function finish()
    finished = true
    if mode == "Velocity" and object.Exists then
      -- The last step's velocity is cleared below before physics uses it: put the object exactly at the end instead.
      local target = path:GetPointAtDistance(distance)
      pcall(function() object.Transform.Position = newVector3(target.x + ox, target.y + oy, target.z + oz) end)
    end
    stopMoving()
    if connection ~= nil then
      connection:Disconnect()
    end
    finishedSignal.fire(follower)
  end

  local function tick(dt)
    if not object.Exists then
      if connection ~= nil then
        connection:Disconnect()
      end
      finished = true
      return
    end
    if paused or finished then
      return
    end
    distance = distance + speed * dt * direction
    if not path.Closed or not loop then
      if distance >= length or distance <= 0 then
        if pingPong then
          distance = distance >= length and 2 * length - distance or -distance
          direction = -direction
          loopedSignal.fire(follower)
        elseif loop then
          distance = distance % length
          loopedSignal.fire(follower)
        else
          distance = max(0, min(length, distance))
          place(dt, false)
          finish()
          return
        end
      end
    elseif distance >= length or distance < 0 then
      distance = distance % length
      loopedSignal.fire(follower)
    end
    place(dt, false)
  end

  local function check(self, usage)
    if not rawequal(self, follower) then
      error("call it with a colon: follower:" .. usage, 3)
    end
  end

  follower = handleObject("PathFollower", {
    Speed = function() return speed end,
    Distance = function() return distance end,
    Direction = function() return direction end,
    Paused = function() return paused end,
    Finished = function() return finished end,
    Object = function() return object end,
    Path = function() return path end,
  }, {
    Speed = function(value) speed = checkNumber(value, "follower.Speed", 2, 0) end,
    Distance = function(value) distance = checkNumber(value, "follower.Distance", 2) end,
  }, {
    Pause = function(self)
      check(self, "Pause()")
      paused = true
      stopMoving()
    end,
    Resume = function(self)
      check(self, "Resume()")
      paused = false
    end,
    -- Ends it for good (the object stays where it is).
    Stop = function(self)
      check(self, "Stop()")
      if not finished then
        finished = true
        stopMoving()
        if connection ~= nil then
          connection:Disconnect()
        end
      end
    end,
    -- Turns round where it is.
    Reverse = function(self)
      check(self, "Reverse()")
      direction = -direction
    end,
    OnFinished = function(self, f)
      check(self, "OnFinished(function)")
      checkFunction(f, "follower:OnFinished", 1)
      return finishedSignal.connect(f)
    end,
    OnLooped = function(self, f)
      check(self, "OnLooped(function)")
      checkFunction(f, "follower:OnLooped", 1)
      return loopedSignal.connect(f)
    end,
  })

  place(0, true)
  connection = timeValues.OnUpdate(tick)
  return follower
end

Path = library("Path", {
  -- Path.new(points[, { Type = "CatmullRom" | "Linear" | "Bezier", Closed = false }]): points are vectors (at least 2;
  -- a Bezier path is anchor, handle, handle, anchor, ... : 3n + 1 points, or 3n when closed).
  new = function(points, options)
    if type(points) ~= "table" or getmetatable(points) ~= nil then
      error("Path.new expects a list of points (vectors), got a " .. type(points) .. " value", 2)
    end
    options = checkOptions(options, { Type = true, Closed = true }, "Path.new", 1)
    local kind = options.Type or "CatmullRom"
    if not pathKinds[kind] then
      error("Path.new: Type must be \"CatmullRom\", \"Linear\" or \"Bezier\", got " .. tostring(kind), 2)
    end
    local closed = options.Closed ~= nil and checkBoolean(options.Closed, "Path.new: Closed", 1) or false
    local xs, ys, zs = {}, {}, {}
    local count = rawlen(points)
    if count > 10000 then
      error("Path.new: at most 10,000 points", 2)
    end
    for i = 1, count do
      xs[i], ys[i], zs[i] = checkVector(rawget(points, i), "Path.new: point " .. i, 1)
    end
    if count < 2 then
      error("Path.new needs at least 2 points", 2)
    end
    if kind == "Bezier" then
      if closed and (count < 3 or count % 3 ~= 0) then
        error("Path.new: a closed Bezier path has 3n points (anchor, handle, handle, ...), got " .. count, 2)
      elseif not closed and (count < 4 or (count - 1) % 3 ~= 0) then
        error("Path.new: a Bezier path has 3n + 1 points (anchor, handle, handle, anchor, ...), got " .. count, 2)
      end
    end
    if closed and count < 3 then
      error("Path.new: a closed path needs at least 3 points", 2)
    end
    return newPath(xs, ys, zs, kind, closed)
  end,
  Follow = followPath,
})

-- ---------------------------------------------------------------------------------------------------------------------
-- Particles (shared): emitters the renderer draws while playing.

local emitterHandles = setmetatable({}, { __mode = "v" })
local emitterVectors = { Position = true, Area = true, Velocity = true }
local emitterNumbers = { Rate = { 0, 100000 }, Lifetime = { 0.01, 60 }, Spread = { 0, 10 }, Gravity = { -100, 100 }, Size = { 0.005, 50 },
  Length = { 0, 100 }, MaxParticles = { 1, 20000 } }
local emitterBooleans = { FollowCamera = true, Enabled = true }

local function emitterHandle(id)
  local handle = emitterHandles[id]
  if handle ~= nil then
    return handle
  end
  local function alive()
    return hostGameplay("pget", id, "Exists") == true
  end
  local function request(...)
    local ok, message = hostGameplay(...)
    if ok == false then
      error(message or "that particle emitter no longer exists", 4)
    end
    return ok, message
  end
  local getters = { Exists = alive }
  local setters = {}
  for key in next, emitterVectors do
    getters[key] = function()
      local x, y, z = hostGameplay("pget", id, key)
      if x == false then
        error(y, 3)
      end
      return newVector3(x, y, z)
    end
    setters[key] = function(value)
      local x, y, z = checkVector(value, "ParticleEmitter." .. key, 2)
      request("pset", id, key, x, y, z)
    end
  end
  for key, range in next, emitterNumbers do
    getters[key] = function()
      local value, message = hostGameplay("pget", id, key)
      if value == false then
        error(message, 3)
      end
      return value
    end
    setters[key] = function(value)
      checkNumber(value, "ParticleEmitter." .. key, 2, range[1], range[2])
      request("pset", id, key, value)
    end
  end
  for key in next, emitterBooleans do
    getters[key] = function()
      local value, message = hostGameplay("pget", id, key)
      if value == false and message ~= nil then
        error(message, 3)
      end
      return value
    end
    setters[key] = function(value)
      checkBoolean(value, "ParticleEmitter." .. key, 2)
      request("pset", id, key, value)
    end
  end
  getters.Count = function()
    local value = hostGameplay("pget", id, "Count")
    return value == false and 0 or value
  end
  getters.Color = function()
    local r, g, b = hostGameplay("pget", id, "Color")
    if r == false then
      error(g, 3)
    end
    return toHex(r, g, b)
  end
  setters.Color = function(value)
    local r, g, b = parseColor(value, nil, "ParticleEmitter.Color", 2)
    request("pset", id, "Color", r, g, b)
  end
  local function check(self, usage)
    if not rawequal(self, handle) then
      error("call it with a colon: emitter:" .. usage, 3)
    end
  end
  handle = handleObject("ParticleEmitter", getters, setters, {
    -- Spawns this many particles now (within MaxParticles); returns how many were made.
    Burst = function(self, count)
      check(self, "Burst(count)")
      checkNumber(count, "emitter:Burst: the count", 1, 0)
      local made, message = hostGameplay("pburst", id, count)
      if made == false then
        error(message, 2)
      end
      return made
    end,
    Clear = function(self)
      check(self, "Clear()")
      hostGameplay("pclear", id)
    end,
    Destroy = function(self)
      check(self, "Destroy()")
      hostGameplay("pdestroy", id)
    end,
  }, function() return "ParticleEmitter(" .. (alive() and "active" or "destroyed") .. ")" end)
  emitterHandles[id] = handle
  return handle
end

local emitterOptions = { Position = true, Area = true, Rate = true, Lifetime = true, Velocity = true, Spread = true, Gravity = true,
  Color = true, Size = true, Length = true, FollowCamera = true, MaxParticles = true, Enabled = true }

local Particles = library("Particles", {
  -- A new emitter; every option is a property of it (see ParticleEmitter). It belongs to this script.
  CreateEmitter = function(options)
    options = checkOptions(options, emitterOptions, "Particles.CreateEmitter", 1)
    -- Check everything before anything is made.
    for key, value in next, options do
      if emitterVectors[key] then
        checkVector(value, "Particles.CreateEmitter: " .. key, 1)
      elseif emitterNumbers[key] then
        checkNumber(value, "Particles.CreateEmitter: " .. key, 1, emitterNumbers[key][1], emitterNumbers[key][2])
      elseif emitterBooleans[key] then
        checkBoolean(value, "Particles.CreateEmitter: " .. key, 1)
      elseif key == "Color" then
        parseColor(value, nil, "Particles.CreateEmitter: Color", 1)
      end
    end
    local id, message = hostGameplay("pnew")
    if not id then
      error(message, 2)
    end
    local emitter = emitterHandle(id)
    for key, value in next, options do
      emitter[key] = value
    end
    return emitter
  end,
}, {
  -- Particles alive in the whole session.
  Count = function() return hostGameplay("pcount") or 0 end,
})

-- ---------------------------------------------------------------------------------------------------------------------
-- Cameras (shared): named cameras, the active one, blends, shake, feeds.

local cameraHandles = setmetatable({}, { __mode = "v" })
local cameraHandle

local function cameraRequest(...)
  local ok, message = hostGameplay(...)
  if ok == false then
    error(message or "that camera no longer exists", 4)
  end
  return ok
end

-- camera:PlayPath(path, options): moves the camera along a Path (Time.OnUpdate, owned by the calling script).
local function playCameraPath(camera, id, path, options)
  if getmetatable(path) ~= "Path" then
    error("camera:PlayPath expects a Path (Path.new), got a " .. type(path) .. " value", 3)
  end
  options = checkOptions(options, { Duration = true, Speed = true, LookAt = true, Loop = true, Easing = true }, "camera:PlayPath", 2)
  local length = path.Length
  local speed
  if options.Duration ~= nil then
    local duration = checkNumber(options.Duration, "camera:PlayPath: Duration", 2, 0.01)
    speed = length / duration
  else
    speed = options.Speed == nil and 5 or checkNumber(options.Speed, "camera:PlayPath: Speed", 2, 0.001)
  end
  local loop = options.Loop ~= nil and checkBoolean(options.Loop, "camera:PlayPath: Loop", 2) or false
  local easing = options.Easing or "Smooth"
  if easing ~= "Smooth" and easing ~= "Linear" then
    error("camera:PlayPath: Easing must be \"Smooth\" or \"Linear\"", 3)
  end
  local look = options.LookAt
  local lookIsObject = objectIds[look] ~= nil
  if look ~= nil and not lookIsObject then
    checkVector(look, "camera:PlayPath: LookAt (a point or a scene object)", 2)
  end
  local travelled = 0
  local finishedSignal = newSignal()
  local connection
  local playback

  local function stop()
    if connection ~= nil and connection.Connected then
      connection:Disconnect()
    end
  end

  local function place()
    if not camera.Exists then
      stop()
      return
    end
    local t = length > 0 and min(1, travelled / length) or 1
    if easing == "Smooth" and not loop then
      t = t * t * (3 - 2 * t)
    end
    local d = t * length
    local position = path:GetPointAtDistance(d)
    camera.Position = position
    if look ~= nil then
      if lookIsObject then
        if look.Exists then
          camera:LookAt(look.Transform.Position)
        end
      else
        camera:LookAt(look)
      end
    else
      local tangent = path:GetTangentAtDistance(d)
      camera:LookAt(position + tangent)
    end
  end

  local function tick()
    -- Real time: a paused game can still play its cinematics.
    travelled = travelled + speed * timeValues.unscaledDeltaTime
    if travelled >= length then
      if loop then
        travelled = travelled % length
      else
        travelled = length
        place()
        stop()
        finishedSignal.fire(camera)
        return
      end
    end
    place()
  end

  playback = handleObject("CameraPlayback", {
    Playing = function() return connection ~= nil and connection.Connected end,
    Progress = function() return length > 0 and min(1, travelled / length) or 1 end,
  }, {}, {
    Stop = function(self)
      if not rawequal(self, playback) then
        error("call it with a colon: playback:Stop()", 2)
      end
      stop()
    end,
    OnFinished = function(self, f)
      if not rawequal(self, playback) then
        error("call it with a colon: playback:OnFinished(function)", 2)
      end
      checkFunction(f, "playback:OnFinished", 1)
      return finishedSignal.connect(f)
    end,
  })
  place()
  connection = timeValues.OnUpdate(tick)
  return playback
end

cameraHandle = function(id)
  local handle = cameraHandles[id]
  if handle ~= nil then
    return handle
  end
  local function get(key)
    local a, b, c = hostGameplay("camget", id, key)
    if a == false and key ~= "Exists" and key ~= "Active" and key ~= "Following" and key ~= "HasFeed" then
      error(b or "that camera no longer exists", 3)
    end
    return a, b, c
  end
  local function check(self, usage)
    if not rawequal(self, handle) then
      error("call it with a colon: camera:" .. usage, 3)
    end
  end
  local name = (hostGameplay("camget", id, "Name"))
  local currentPlayback
  handle = handleObject("GameCamera", {
    Name = function() return name end,
    Position = function() return newVector3(get("Position")) end,
    -- x: pitch (up positive, -89..89), y: yaw (0 looks along +X, 90 along +Z); the camera never rolls.
    Rotation = function()
      local pitch, yaw = get("Rotation")
      return newVector3(pitch, yaw, 0)
    end,
    Forward = function() return newVector3(get("Forward")) end,
    FieldOfView = function() return (get("FieldOfView")) end,
    Active = function() return (get("Active")) == true end,
    Exists = function() return (get("Exists")) == true end,
    Following = function() return (get("Following")) == true end,
    HasFeed = function() return (get("HasFeed")) == true end,
  }, {
    Position = function(value)
      local x, y, z = checkVector(value, "camera.Position", 2)
      cameraRequest("camset", id, "Position", x, y, z)
    end,
    Rotation = function(value)
      local x, y = checkVector(value, "camera.Rotation (x: pitch, y: yaw)", 2)
      cameraRequest("camset", id, "Rotation", x, y)
    end,
    FieldOfView = function(value)
      checkNumber(value, "camera.FieldOfView", 2, 10, 120)
      cameraRequest("camset", id, "FieldOfView", value)
    end,
  }, {
    -- Turns it to look at a point (or an object).
    LookAt = function(self, point)
      check(self, "LookAt(point)")
      local x, y, z = positionOf(point, "camera:LookAt: the point", 1)
      cameraRequest("camlook", id, x, y, z)
    end,
    -- Follows an object every frame: Offset in the object's own axes (default 0, 6, 12: behind and above), LookAt
    -- (default true) at the object plus LookOffset, Smoothing (0: exact; e.g. 8 to catch up smoothly).
    Follow = function(self, object, options)
      check(self, "Follow(object[, options])")
      local objectId = objectIds[object]
      if objectId == nil then
        error("camera:Follow expects a scene object, got a " .. type(object) .. " value", 2)
      end
      options = checkOptions(options, { Offset = true, LookAt = true, LookOffset = true, Smoothing = true }, "camera:Follow", 1)
      local ox, oy, oz = 0, 6, 12
      if options.Offset ~= nil then
        ox, oy, oz = checkVector(options.Offset, "camera:Follow: Offset", 1)
      end
      local lx, ly, lz = 0, 0, 0
      if options.LookOffset ~= nil then
        lx, ly, lz = checkVector(options.LookOffset, "camera:Follow: LookOffset", 1)
      end
      local lookAt = options.LookAt == nil and true or checkBoolean(options.LookAt, "camera:Follow: LookAt", 1)
      local smoothing = options.Smoothing == nil and 0 or checkNumber(options.Smoothing, "camera:Follow: Smoothing", 1, 0)
      cameraRequest("camfollow", id, objectId, ox, oy, oz, lookAt, lx, ly, lz, smoothing)
    end,
    StopFollowing = function(self)
      check(self, "StopFollowing()")
      cameraRequest("camfollow", id, 0)
    end,
    -- Moves it along a Path; returns a playback (Stop, OnFinished, Playing, Progress).
    PlayPath = function(self, path, options)
      check(self, "PlayPath(path[, options])")
      if currentPlayback ~= nil then
        currentPlayback:Stop()
      end
      currentPlayback = playCameraPath(handle, id, path, options)
      return currentPlayback
    end,
    StopPath = function(self)
      check(self, "StopPath()")
      if currentPlayback ~= nil then
        currentPlayback:Stop()
        currentPlayback = nil
      end
    end,
    -- Renders it into an image every frame for GUI Image elements whose Image is "camera:<Name>".
    CreateFeed = function(self, options)
      check(self, "CreateFeed([options])")
      options = checkOptions(options, { Width = true, Height = true }, "camera:CreateFeed", 1)
      local width = options.Width == nil and 480 or checkNumber(options.Width, "camera:CreateFeed: Width", 1, 16, 1024)
      local height = options.Height == nil and 270 or checkNumber(options.Height, "camera:CreateFeed: Height", 1, 16, 1024)
      local ok, message = hostGameplay("camfeed", id, floor(width), floor(height))
      if not ok then
        return false, message
      end
      return true
    end,
    Destroy = function(self)
      check(self, "Destroy()")
      if currentPlayback ~= nil then
        currentPlayback:Stop()
      end
      hostGameplay("camdestroy", id)
    end,
  }, function() return "GameCamera(" .. tostring(name) .. ")" end)
  cameraHandles[id] = handle
  return handle
end

local function findCamera(name)
  local id = hostGameplay("camfind", name)
  return id and cameraHandle(id) or nil
end

local Cameras = library("Cameras", {
  -- A named camera (unique name). Options: Position, Rotation (x pitch, y yaw), LookAt (a point or object), FieldOfView
  -- (default 70). It belongs to this script; the gameplay camera (Camera) stays the default view.
  Create = function(name, options)
    if type(name) ~= "string" or name == "" or #name > 64 then
      error("Cameras.Create expects a name (a string of 1 to 64 characters), got a " .. type(name) .. " value", 2)
    end
    options = checkOptions(options, { Position = true, Rotation = true, LookAt = true, FieldOfView = true }, "Cameras.Create", 1)
    local px, py, pz = 0, 10, 0
    if options.Position ~= nil then
      px, py, pz = checkVector(options.Position, "Cameras.Create: Position", 1)
    end
    local pitch, yaw = 0, 0
    if options.Rotation ~= nil then
      pitch, yaw = checkVector(options.Rotation, "Cameras.Create: Rotation", 1)
    end
    local fov = options.FieldOfView == nil and 70 or checkNumber(options.FieldOfView, "Cameras.Create: FieldOfView", 1, 10, 120)
    local lx, ly, lz
    if options.LookAt ~= nil then
      lx, ly, lz = positionOf(options.LookAt, "Cameras.Create: LookAt", 1)
    end
    local id, message = hostGameplay("camnew", name, px, py, pz, pitch, yaw, fov)
    if not id then
      error(message, 2)
    end
    local camera = cameraHandle(id)
    if lx ~= nil then
      cameraRequest("camlook", id, lx, ly, lz)
    end
    return camera
  end,
  Find = function(name)
    if type(name) ~= "string" then
      error("Cameras.Find expects a name (a string), got a " .. type(name) .. " value", 2)
    end
    return findCamera(name)
  end,
  GetAll = function()
    local ids = hostGameplay("camall") or {}
    local list = {}
    for i = 1, #ids do
      list[i] = cameraHandle(ids[i])
    end
    return list
  end,
  -- Shows this camera (nil: the gameplay camera, Camera). Options: Blend (seconds, default 0), Easing ("Smooth" or
  -- "Linear"). Blends run in real time.
  SetActive = function(camera, options)
    local id = 0
    if camera ~= nil then
      if getmetatable(camera) ~= "GameCamera" then
        error("Cameras.SetActive expects a camera (Cameras.Create) or nil for the gameplay camera, got a " .. type(camera) .. " value", 2)
      end
      local found = findCamera(camera.Name)
      if found == nil then
        error("Cameras.SetActive: that camera no longer exists", 2)
      end
      id = hostGameplay("camfind", camera.Name)
    end
    options = checkOptions(options, { Blend = true, Easing = true }, "Cameras.SetActive", 1)
    local blend = options.Blend == nil and 0 or checkNumber(options.Blend, "Cameras.SetActive: Blend", 1, 0, 60)
    local easing = options.Easing or "Smooth"
    if easing ~= "Smooth" and easing ~= "Linear" then
      error("Cameras.SetActive: Easing must be \"Smooth\" or \"Linear\", got " .. tostring(easing), 2)
    end
    local ok, message = hostGameplay("camactive", id, blend, easing == "Linear")
    if not ok then
      error(message, 2)
    end
  end,
  -- The camera shown (nil: the gameplay camera).
  GetActive = function()
    local id = hostGameplay("camgetactive")
    return id ~= nil and id ~= 0 and cameraHandle(id) or nil
  end,
  -- Shakes what is shown: intensity in studs (0.3 is a bump, 1.5 an explosion), duration seconds (default 0.5),
  -- frequency per second (default 12); it fades out.
  Shake = function(intensity, duration, frequency)
    checkNumber(intensity, "Cameras.Shake: the intensity", 1, 0, 50)
    duration = duration == nil and 0.5 or checkNumber(duration, "Cameras.Shake: the duration", 1, 0, 60)
    frequency = frequency == nil and 12 or checkNumber(frequency, "Cameras.Shake: the frequency", 1, 0.1, 100)
    hostGameplay("camshake", intensity, duration, frequency)
  end,
  -- f(camera, previous) at the start of the frame after the shown camera changes (nil: the gameplay camera).
  OnActivated = function(f)
    checkFunction(f, "Cameras.OnActivated", 1)
    local id, message = hostGameplay("camactivated", function(name, previous)
      f(name and findCamera(name) or nil, previous and findCamera(previous) or nil)
    end)
    if not id then
      error(message, 2)
    end
    return newConnection(hostEvents, id)
  end,
  CreateFeed = function(camera, options)
    if getmetatable(camera) ~= "GameCamera" then
      error("Cameras.CreateFeed expects a camera (Cameras.Create), got a " .. type(camera) .. " value", 2)
    end
    return camera:CreateFeed(options)
  end,
}, {
  -- Whether this renderer can draw camera feeds into GUI images (OpenGL: yes).
  FeedsSupported = function() return hostGameplay("camfeeds") == true end,
})

-- ---------------------------------------------------------------------------------------------------------------------
-- GameSave (shared): plain data to and from <project>/Saves/<slot>.json.

local saveLimitDepth, saveLimitValues = 64, 200000

local function saveKeyLabel(key)
  if type(key) == "string" and find(key, "^[%a_][%w_]*$") then
    return "." .. key
  end
  if type(key) == "string" then
    return "[" .. format("%q", sub(key, 1, 40)) .. "]"
  end
  return "[" .. tostring(key) .. "]"
end

local saveRule = " (saves hold booleans, numbers, strings, Vector3 values and plain tables of those)"

-- Every value counts (tables, and what is in them), so one long list cannot pass the limit either.
local function countSaveValue(counter)
  local count = counter[1] + 1
  counter[1] = count
  if count > saveLimitValues then
    error("the data has more than " .. saveLimitValues .. " values", 0)
  end
end

-- The tagged plain copy the host writes: vectors -> { ["$vector"] = { x, y, z } }; tables whose keys are not all strings
-- (and not a list) -> { ["$map"] = { { key, value }, ... } }; string keys starting with "$" -> "$$...". Errors name the place.
local function toSaveValue(value, where, open, counter, depth)
  local kind = type(value)
  if kind == "boolean" or kind == "string" then
    countSaveValue(counter)
    return value
  end
  if kind == "number" then
    if value ~= value or value == huge or value == -huge then
      error(where .. " is not a finite number (NaN and infinity cannot be saved)", 0)
    end
    countSaveValue(counter)
    return value
  end
  if kind ~= "table" then
    error(where .. " is a " .. kind .. " value" .. saveRule, 0)
  end
  if objectIds[value] ~= nil then
    error(where .. " is a scene object: save what you need to recreate it (its Name, Id, position...)" , 0)
  end
  local metatable = getmetatable(value)
  if metatable == "Vector3" then
    local x, y, z = vectorParts(value)
    if not (isFinite(x) and isFinite(y) and isFinite(z)) then
      error(where .. " is a vector that is not finite", 0)
    end
    countSaveValue(counter)
    return { ["$vector"] = { x, y, z } }
  end
  if metatable ~= nil then
    error(where .. " is a " .. (type(metatable) == "string" and metatable or "table with a metatable") .. saveRule, 0)
  end
  if open[value] then
    error(where .. " contains itself (a table cycle cannot be saved)", 0)
  end
  if depth >= saveLimitDepth then
    error(where .. " is nested more than " .. saveLimitDepth .. " levels deep", 0)
  end
  countSaveValue(counter)
  open[value] = true
  local length = rawlen(value)
  local keys, strings = 0, true
  for key in next, value do
    keys = keys + 1
    if type(key) ~= "string" then
      strings = false
    end
  end
  local copy = {}
  if length > 0 and keys == length then
    for i = 1, length do
      copy[i] = toSaveValue(rawget(value, i), where .. "[" .. i .. "]", open, counter, depth + 1)
    end
  elseif strings then
    for key, item in next, value do
      local stored = sub(key, 1, 1) == "$" and "$" .. key or key
      copy[stored] = toSaveValue(item, where .. saveKeyLabel(key), open, counter, depth + 1)
    end
  else
    local pairsList = {}
    for key, item in next, value do
      local keyKind = type(key)
      if keyKind ~= "string" and keyKind ~= "number" and keyKind ~= "boolean" then
        error(where .. " has a key that is a " .. keyKind .. " value (keys must be strings, numbers or booleans)", 0)
      end
      if keyKind == "number" and (key ~= key or key == huge or key == -huge) then
        error(where .. " has a key that is not a finite number", 0)
      end
      pairsList[#pairsList + 1] = { key, toSaveValue(item, where .. saveKeyLabel(key), open, counter, depth + 1) }
    end
    copy["$map"] = pairsList
  end
  open[value] = nil
  return copy
end

-- Back from the host's plain tables: $vector, $map and $$ keys undone. Anything unexpected is a damaged save.
local function fromSaveValue(value, depth)
  if type(value) ~= "table" then
    return value
  end
  if depth > saveLimitDepth + 2 then
    error("the save is damaged (nested too deeply)", 0)
  end
  local vector = rawget(value, "$vector")
  if vector ~= nil then
    if type(vector) ~= "table" or type(vector[1]) ~= "number" or type(vector[2]) ~= "number" or type(vector[3]) ~= "number" then
      error("the save is damaged (a broken vector)", 0)
    end
    return newVector3(vector[1], vector[2], vector[3])
  end
  local map = rawget(value, "$map")
  if map ~= nil then
    if type(map) ~= "table" then
      error("the save is damaged (a broken table)", 0)
    end
    local result = {}
    for i = 1, rawlen(map) do
      local entry = rawget(map, i)
      if type(entry) ~= "table" or rawget(entry, 1) == nil then
        error("the save is damaged (a broken table entry)", 0)
      end
      result[rawget(entry, 1)] = fromSaveValue(rawget(entry, 2), depth + 1)
    end
    return result
  end
  local result = {}
  for key, item in next, value do
    if type(key) == "string" and sub(key, 1, 1) == "$" then
      if sub(key, 2, 2) ~= "$" then
        error("the save is damaged (an unknown entry " .. sub(key, 1, 20) .. ")", 0)
      end
      key = sub(key, 2)
    end
    result[key] = fromSaveValue(item, depth + 1)
  end
  return result
end

local function checkSlot(slot, usage)
  if type(slot) ~= "string" then
    error(usage .. " expects a slot name (a string such as \"Slot1\"), got a " .. type(slot) .. " value", 3)
  end
  local problem = hostGameplay("slotcheck", slot)
  if problem ~= nil then
    error(usage .. ": " .. problem, 3)
  end
end

local function saveInfo(slot, version, savedAt, metadata, recovered)
  local ok, value = pcall(fromSaveValue, metadata, 0)
  return { Slot = slot, Version = version, SavedAt = savedAt, Metadata = ok and value or nil, Recovered = recovered == true }
end

local GameSave = library("GameSave", {
  -- Saves plain data in a slot. Options: Version (a whole number, default 1: your data's version, for migrating old
  -- saves), Metadata (a small plain table shown by List and GetInfo, e.g. a level name or play time). Returns true, or
  -- false and why (e.g. the disk is full); data that cannot be saved is an error naming the place.
  Save = function(slot, data, options)
    checkSlot(slot, "GameSave.Save")
    options = checkOptions(options, { Version = true, Metadata = true }, "GameSave.Save", 1)
    local version = 1
    if options.Version ~= nil then
      version = options.Version
      if mathType(version) ~= "integer" and not (isFinite(version) and floor(version) == version) or version < 1 then
        error("GameSave.Save: Version must be a whole number of 1 or more", 2)
      end
      version = floor(version)
    end
    if data == nil then
      error("GameSave.Save expects data to save (a table, or a single value), got nil", 2)
    end
    local counter = { 0 }
    local ok, plain = pcall(toSaveValue, data, "data", {}, counter, 0)
    if not ok then
      error("GameSave.Save: " .. tostring(plain), 2)
    end
    local metadata
    if options.Metadata ~= nil then
      if type(options.Metadata) ~= "table" then
        error("GameSave.Save: Metadata must be a table", 2)
      end
      local metaCounter = { 0 }
      local metaOk, metaPlain = pcall(toSaveValue, options.Metadata, "Metadata", {}, metaCounter, 0)
      if not metaOk then
        error("GameSave.Save: " .. tostring(metaPlain), 2)
      end
      if metaCounter[1] > 1000 then
        error("GameSave.Save: Metadata is for a few values (at most 1,000); keep the rest in the data", 2)
      end
      metadata = metaPlain
    end
    local saved, message = hostGameplay("save", slot, plain, metadata, version)
    if saved ~= true then
      return false, message
    end
    return true
  end,
  -- The data saved in a slot and its info { Slot, Version, SavedAt, Metadata, Recovered }; nil and why when there is no
  -- such save or it is damaged (a damaged save whose previous version is intact gives that version, Recovered = true).
  Load = function(slot)
    checkSlot(slot, "GameSave.Load")
    local ok, data, metadata, version, savedAt, recovered = hostGameplay("load", slot)
    if ok ~= true then
      return nil, data
    end
    local decoded, value = pcall(fromSaveValue, data, 0)
    if not decoded then
      return nil, "the save \"" .. slot .. "\" is damaged (" .. tostring(value) .. ")"
    end
    return value, saveInfo(slot, version, savedAt, metadata, recovered)
  end,
  Exists = function(slot)
    checkSlot(slot, "GameSave.Exists")
    return hostGameplay("exists", slot) == true
  end,
  -- Removes a slot (and its previous version). Returns true (also when there was nothing), or false and why.
  Delete = function(slot)
    checkSlot(slot, "GameSave.Delete")
    local ok, existed = hostGameplay("delete", slot)
    if ok ~= true then
      return false, existed
    end
    return true, existed
  end,
  -- Every slot's info, by name.
  List = function()
    local list = hostGameplay("list")
    if type(list) ~= "table" then
      return {}
    end
    local result = {}
    for i = 1, #list do
      local entry = list[i]
      result[i] = saveInfo(entry.Slot, entry.Version, entry.SavedAt, entry.Metadata, false)
    end
    return result
  end,
  GetInfo = function(slot)
    checkSlot(slot, "GameSave.GetInfo")
    local ok, version, savedAt, metadata = hostGameplay("info", slot)
    if ok ~= true then
      return nil, version
    end
    return saveInfo(slot, version, savedAt, metadata, false)
  end,
}, {
  -- Whether saving is possible (the game has a project folder).
  Available = function() return hostGameplay("savesavailable") == true end,
})

-- ---------------------------------------------------------------------------------------------------------------------
-- GameTime (per script: its schedules are its own)

local clockKeys = { Day = true, Hour = true, Minute = true, TimeOfDay = true, TotalHours = true, DayLength = true, Scale = true, Paused = true }

local function clockValue(key)
  local value, message = hostGameplay("clock", key)
  if value == false and key ~= "Paused" then
    error(message, 3)
  end
  return value
end

local function newGameTime()
  local schedules = {}
  local lastSeen
  local ticking

  local function tick()
    local now = hostGameplay("clock", "TotalHours")
    if type(now) ~= "number" then
      return
    end
    if lastSeen == nil or now < lastSeen then
      -- First tick, or the clock was set back: nothing has passed.
      lastSeen = now
      return
    end
    local from = lastSeen
    lastSeen = now
    if now == from then
      return
    end
    local copy = { unpack(schedules) }
    for _, entry in ipairs(copy) do
      if entry.active then
        local kind = entry.kind
        if kind == "at" then
          local k = floor((from - entry.hour) / 24) + 1
          local t = k * 24 + entry.hour
          local fired = 0
          while t <= now and fired < 48 and entry.active do
            entry.f(floor(t / 24) + 1)
            fired = fired + 1
            t = t + 24
          end
        elseif kind == "after" then
          if now >= entry.due then
            entry.active = false
            entry.f()
          end
        elseif kind == "every" then
          local fired = 0
          while now >= entry.due and fired < 48 and entry.active do
            entry.due = entry.due + entry.interval
            fired = fired + 1
            entry.f()
          end
        elseif kind == "hour" then
          local h = floor(from) + 1
          local last = floor(now)
          local fired = 0
          while h <= last and fired < 48 and entry.active do
            entry.f(h % 24, h // 24 + 1)
            h = h + 1
            fired = fired + 1
          end
        elseif kind == "day" then
          local d = floor(from / 24) + 1
          local last = floor(now / 24)
          local fired = 0
          while d <= last and fired < 48 and entry.active do
            entry.f(d + 1)
            d = d + 1
            fired = fired + 1
          end
        end
      end
    end
    for i = #schedules, 1, -1 do
      if not schedules[i].active then
        remove(schedules, i)
      end
    end
  end

  local function schedule(entry)
    if ticking == nil or not ticking.Connected then
      ticking = timeValues.OnUpdate(tick)
      lastSeen = hostGameplay("clock", "TotalHours")
    end
    entry.active = true
    schedules[#schedules + 1] = entry
    return luaConnection(function()
      entry.active = false
    end)
  end

  local function hourArgument(hour, usage)
    if not isFinite(hour) or hour < 0 or hour >= 24 then
      error(usage .. " expects an hour from 0 up to 24 (e.g. 6.5 for 06:30), got " .. tostring(hour), 3)
    end
    return hour
  end

  return library("GameTime", {
    Pause = function() hostGameplay("clockset", "Paused", true) end,
    Resume = function() hostGameplay("clockset", "Paused", false) end,
    -- Jumps to a day (from 1) and hour (0 up to 24).
    Set = function(day, hour)
      local ok, message = hostGameplay("clockjump", day, hour)
      if not ok then
        error(message, 2)
      end
      lastSeen = hostGameplay("clock", "TotalHours")
    end,
    -- f(day) each time the clock passes that hour (every day).
    At = function(hour, f)
      hourArgument(hour, "GameTime.At")
      checkFunction(f, "GameTime.At", 1)
      return schedule({ kind = "at", hour = hour, f = f })
    end,
    -- f() once, after that many game hours.
    After = function(hours, f)
      checkNumber(hours, "GameTime.After: hours", 1, 0)
      checkFunction(f, "GameTime.After", 1)
      local now = hostGameplay("clock", "TotalHours")
      return schedule({ kind = "after", due = now + hours, f = f })
    end,
    -- f() every that many game hours.
    Every = function(hours, f)
      checkNumber(hours, "GameTime.Every: hours", 1, 1 / 3600)
      checkFunction(f, "GameTime.Every", 1)
      local now = hostGameplay("clock", "TotalHours")
      return schedule({ kind = "every", due = now + hours, interval = hours, f = f })
    end,
    -- f(hour, day) when a new hour begins.
    OnHourChanged = function(f)
      checkFunction(f, "GameTime.OnHourChanged", 1)
      return schedule({ kind = "hour", f = f })
    end,
    -- f(day) when a new day begins.
    OnDayChanged = function(f)
      checkFunction(f, "GameTime.OnDayChanged", 1)
      return schedule({ kind = "day", f = f })
    end,
    -- Whether the time of day is from a up to b (hours; wraps past midnight: IsBetween(20, 6) is the night).
    IsBetween = function(a, b)
      hourArgument(a, "GameTime.IsBetween")
      if not isFinite(b) or b < 0 or b > 24 then
        error("GameTime.IsBetween expects hours from 0 to 24", 2)
      end
      local hour = hostGameplay("clock", "Hour")
      if a <= b then
        return hour >= a and hour < b
      end
      return hour >= a or hour < b
    end,
    -- "HH:MM".
    Clock = function()
      local hour = hostGameplay("clock", "Hour")
      local minutes = floor(hour * 60 + 1e-9)
      return format("%02d:%02d", minutes // 60, minutes % 60)
    end,
    -- Plain data for GameSave: { Day, Hour, DayLength, Scale, Paused }.
    GetState = function()
      return { Day = hostGameplay("clock", "Day"), Hour = hostGameplay("clock", "Hour"), DayLength = hostGameplay("clock", "DayLength"),
        Scale = hostGameplay("clock", "Scale"), Paused = hostGameplay("clock", "Paused") == true }
    end,
    SetState = function(state)
      if type(state) ~= "table" then
        error("GameTime.SetState expects the table GetState gave", 2)
      end
      local ok, message = hostGameplay("clockjump", state.Day, state.Hour)
      if not ok then
        error("GameTime.SetState: " .. tostring(message), 2)
      end
      if state.DayLength ~= nil then
        ok, message = hostGameplay("clockset", "DayLength", state.DayLength)
        if not ok then error("GameTime.SetState: " .. tostring(message), 2) end
      end
      if state.Scale ~= nil then
        ok, message = hostGameplay("clockset", "Scale", state.Scale)
        if not ok then error("GameTime.SetState: " .. tostring(message), 2) end
      end
      if state.Paused ~= nil then
        hostGameplay("clockset", "Paused", state.Paused == true)
      end
      lastSeen = hostGameplay("clock", "TotalHours")
    end,
  }, {
    Day = function() return clockValue("Day") end,
    Hour = function() return clockValue("Hour") end,
    Minute = function() return clockValue("Minute") end,
    TimeOfDay = function() return clockValue("TimeOfDay") end,
    TotalHours = function() return clockValue("TotalHours") end,
    DayLength = function() return clockValue("DayLength") end,
    Scale = function() return clockValue("Scale") end,
    Paused = function() return clockValue("Paused") == true end,
  }, {
    DayLength = function(value)
      if not isFinite(value) then
        error("GameTime.DayLength must be a number of seconds", 3)
      end
      local ok, message = hostGameplay("clockset", "DayLength", value)
      if not ok then
        error(message, 3)
      end
    end,
    Scale = function(value)
      if not isFinite(value) then
        error("GameTime.Scale must be a number (1: normal, 0: stopped)", 3)
      end
      local ok, message = hostGameplay("clockset", "Scale", value)
      if not ok then
        error(message, 3)
      end
    end,
    Paused = function(value)
      checkBoolean(value, "GameTime.Paused", 2)
      hostGameplay("clockset", "Paused", value)
    end,
  })
end

-- ---------------------------------------------------------------------------------------------------------------------
-- Navigation (per script: agents' callbacks are the script's)

local function pathFromHost(complete, length, flat)
  local points = {}
  for i = 1, #flat // 3 do
    points[i] = newVector3(flat[i * 3 - 2], flat[i * 3 - 1], flat[i * 3])
  end
  return { Points = points, Length = length, Complete = complete }
end

local function newNavigation(wrap)
  local navigation

  local function request(...)
    local ok, message = hostGameplay(...)
    if ok == false then
      error(message, 3)
    end
    return ok, message
  end

  local function newAgent(agentId, object)
    local arrived, failed, waypoints = newSignal(), newSignal(), newSignal()
    local patrol
    local agent

    local function check(self, usage)
      if not rawequal(self, agent) then
        error("call it with a colon: agent:" .. usage, 3)
      end
    end

    local function agentRequest(...)
      local ok, message = hostGameplay(...)
      if ok == false then
        error(message, 4)
      end
      return ok, message
    end

    local function goTo(x, y, z)
      local ok, message = hostGameplay("agentdest", agentId, x, y, z)
      if ok == false and message ~= nil and find(message, "no longer exists") then
        error(message, 4)
      end
      return ok == true, message
    end

    local function patrolNext()
      local count = #patrol.points
      if patrol.pingPong then
        local nextIndex = patrol.index + patrol.direction
        if nextIndex < 1 or nextIndex > count then
          patrol.direction = -patrol.direction
          nextIndex = patrol.index + patrol.direction
        end
        patrol.index = max(1, min(count, nextIndex))
      elseif patrol.index >= count then
        if not patrol.loop then
          patrol = nil
          return
        end
        patrol.index = 1
      else
        patrol.index = patrol.index + 1
      end
      local point = patrol.points[patrol.index]
      goTo(point.x, point.y, point.z)
    end

    agent = handleObject("NavAgent", {
      Object = function() return object end,
      Exists = function() return hostGameplay("agentget", agentId, "Exists") == true end,
      Status = function() return (select(1, agentRequest("agentget", agentId, "Status"))) end,
      RemainingDistance = function() return (select(1, agentRequest("agentget", agentId, "RemainingDistance"))) end,
      FailureReason = function() return (select(1, hostGameplay("agentget", agentId, "FailureReason"))) end,
      Velocity = function()
        local x, y, z = hostGameplay("agentget", agentId, "Velocity")
        if x == false then error(y, 3) end
        return newVector3(x, y, z)
      end,
      Destination = function()
        local x, y, z = hostGameplay("agentget", agentId, "Destination")
        if x == false then error(y, 3) end
        if x == nil then return nil end
        return newVector3(x, y, z)
      end,
      Target = function()
        local id = hostGameplay("agentget", agentId, "Target")
        return id ~= nil and id ~= false and id ~= 0 and wrap(id) or nil
      end,
      Speed = function() return (agentRequest("agentget", agentId, "Speed")) end,
      Acceleration = function() return (agentRequest("agentget", agentId, "Acceleration")) end,
      StoppingDistance = function() return (agentRequest("agentget", agentId, "StoppingDistance")) end,
      Radius = function() return (agentRequest("agentget", agentId, "Radius")) end,
      Patrolling = function() return patrol ~= nil end,
    }, {
      Speed = function(value) checkNumber(value, "agent.Speed", 2, 0, 1000) agentRequest("agentset", agentId, "Speed", value) end,
      Acceleration = function(value) checkNumber(value, "agent.Acceleration", 2, 0.01) agentRequest("agentset", agentId, "Acceleration", value) end,
      StoppingDistance = function(value) checkNumber(value, "agent.StoppingDistance", 2, 0) agentRequest("agentset", agentId, "StoppingDistance", value) end,
      Radius = function(value) checkNumber(value, "agent.Radius", 2, 0.01) agentRequest("agentset", agentId, "Radius", value) end,
    }, {
      -- Finds a path there now: true, or false and why (the agent's Status is then "Failed").
      SetDestination = function(self, point)
        check(self, "SetDestination(point)")
        local x, y, z = positionOf(point, "agent:SetDestination: the destination", 1)
        patrol = nil
        return goTo(x, y, z)
      end,
      -- Follows a (moving) object, stopping about distance studs from it (default 3); it re-paths as the object moves.
      Follow = function(self, target, distance)
        check(self, "Follow(object[, distance])")
        local targetId = objectIds[target]
        if targetId == nil then
          error("agent:Follow expects a scene object, got a " .. type(target) .. " value", 2)
        end
        if distance ~= nil then
          checkNumber(distance, "agent:Follow: the distance", 1, 0)
        end
        patrol = nil
        local ok, message = hostGameplay("agentfollow", agentId, targetId, distance or 3)
        if ok == false and message ~= nil and find(message, "no longer exists") then
          error(message, 2)
        end
        return ok == true, message
      end,
      -- Walks a route of points (vectors). Options: Loop (default true), PingPong (back and forth), Wait (seconds at
      -- each point, default 0), StartIndex (1).
      Patrol = function(self, points, options)
        check(self, "Patrol(points[, options])")
        if type(points) ~= "table" or rawlen(points) == 0 then
          error("agent:Patrol expects a list of points (vectors)", 2)
        end
        options = checkOptions(options, { Loop = true, PingPong = true, Wait = true, StartIndex = true }, "agent:Patrol", 1)
        local copy = {}
        for i = 1, rawlen(points) do
          local x, y, z = positionOf(rawget(points, i), "agent:Patrol: point " .. i, 1)
          copy[i] = newVector3(x, y, z)
        end
        local start = options.StartIndex == nil and 1 or checkNumber(options.StartIndex, "agent:Patrol: StartIndex", 1, 1, #copy)
        patrol = {
          points = copy, index = floor(start), direction = 1,
          loop = options.Loop == nil and true or checkBoolean(options.Loop, "agent:Patrol: Loop", 1),
          pingPong = options.PingPong ~= nil and checkBoolean(options.PingPong, "agent:Patrol: PingPong", 1) or false,
          wait = options.Wait == nil and 0 or checkNumber(options.Wait, "agent:Patrol: Wait", 1, 0),
        }
        local point = copy[patrol.index]
        return goTo(point.x, point.y, point.z)
      end,
      Stop = function(self)
        check(self, "Stop()")
        patrol = nil
        agentRequest("agentstop", agentId)
      end,
      -- One step (call it every frame from Update; dt defaults to Time.deltaTime): returns the status ("Idle", "Moving",
      -- "Arrived", "Failed") and the velocity it wants. Move "Velocity" / "Transform" applies it; OnArrived, OnFailed and
      -- OnWaypoint callbacks run from here.
      Update = function(self, dt)
        check(self, "Update([dt])")
        if dt == nil then
          dt = timeValues.deltaTime
        else
          checkNumber(dt, "agent:Update: dt", 1, 0)
        end
        if patrol ~= nil and patrol.waitUntil ~= nil and timeValues.elapsedTime >= patrol.waitUntil then
          patrol.waitUntil = nil
          patrolNext()
        end
        local status, vx, vy, vz, remaining, arrivedNow, failedNow, waypoint, reason = hostGameplay("agentupdate", agentId, dt)
        if status == false then
          error(vx, 2)
        end
        if waypoint ~= nil and waypoint > 0 then
          waypoints.fire(waypoint, agent)
        end
        if arrivedNow then
          arrived.fire(agent)
          if patrol ~= nil then
            if patrol.wait > 0 then
              patrol.waitUntil = timeValues.elapsedTime + patrol.wait
            else
              patrolNext()
            end
          end
        end
        if failedNow then
          failed.fire(reason, agent)
        end
        return status, newVector3(vx, vy, vz)
      end,
      -- The current path's points (copies).
      GetPath = function(self)
        check(self, "GetPath()")
        local flat = hostGameplay("agentpath", agentId)
        if flat == false then
          return {}
        end
        return pathFromHost(true, 0, flat).Points
      end,
      OnArrived = function(self, f)
        check(self, "OnArrived(function)")
        checkFunction(f, "agent:OnArrived", 1)
        return arrived.connect(f)
      end,
      OnFailed = function(self, f)
        check(self, "OnFailed(function)")
        checkFunction(f, "agent:OnFailed", 1)
        return failed.connect(f)
      end,
      OnWaypoint = function(self, f)
        check(self, "OnWaypoint(function)")
        checkFunction(f, "agent:OnWaypoint", 1)
        return waypoints.connect(f)
      end,
      -- Draws its path (debug lines, this frame unless seconds given).
      DrawPath = function(self, color, seconds)
        check(self, "DrawPath([color[, seconds]])")
        local flat = hostGameplay("agentpath", agentId)
        if type(flat) == "table" and #flat >= 6 then
          DebugDraw.Path(pathFromHost(true, 0, flat).Points, color or "#33FF66", seconds)
        end
      end,
      Destroy = function(self)
        check(self, "Destroy()")
        patrol = nil
        hostGameplay("agentdestroy", agentId)
      end,
    }, function() return "NavAgent(" .. tostring(hostGameplay("agentget", agentId, "Status") or "destroyed") .. ")" end)
    return agent
  end

  local buildOptions = { Min = true, Max = true, CellSize = true, AgentRadius = true, AgentHeight = true, MaxClimb = true,
    MaxSlope = true, IgnoreTags = true, UseHighestSurface = true, IncludeUnanchored = true }
  local agentOptions = { Speed = true, Acceleration = true, StoppingDistance = true, Radius = true, RepathInterval = true,
    Move = true, FaceMovement = true, Avoidance = true, FaceYawOffset = true }

  navigation = library("Navigation", {
    -- Bakes the walkable grid now. Options: Min, Max (the area; default: around every anchored Part), CellSize (1),
    -- AgentRadius (0.8), AgentHeight (4), MaxClimb (1.1), MaxSlope (46 degrees), IgnoreTags (tags of Parts that are
    -- not obstacles), UseHighestSurface (false: the lowest surface with headroom), IncludeUnanchored (false).
    Build = function(options)
      options = checkOptions(options, buildOptions, "Navigation.Build", 1)
      local hasBounds = options.Min ~= nil or options.Max ~= nil
      local ax, ay, az, bx, by, bz = 0, 0, 0, 0, 0, 0
      if hasBounds then
        if options.Min == nil or options.Max == nil then
          error("Navigation.Build: give both Min and Max (or neither)", 2)
        end
        ax, ay, az = checkVector(options.Min, "Navigation.Build: Min", 1)
        bx, by, bz = checkVector(options.Max, "Navigation.Build: Max", 1)
        if bx <= ax or by <= ay or bz <= az then
          error("Navigation.Build: Max must be greater than Min on every axis", 2)
        end
      end
      local function option(key, default, low, high)
        return options[key] == nil and default or checkNumber(options[key], "Navigation.Build: " .. key, 2, low, high)
      end
      local tags = {}
      if options.IgnoreTags ~= nil then
        if type(options.IgnoreTags) == "string" then
          tags[1] = options.IgnoreTags
        elseif type(options.IgnoreTags) == "table" then
          for i = 1, rawlen(options.IgnoreTags) do
            local tag = rawget(options.IgnoreTags, i)
            if type(tag) ~= "string" then
              error("Navigation.Build: IgnoreTags must be a tag or a list of tags", 2)
            end
            tags[i] = tag
          end
        else
          error("Navigation.Build: IgnoreTags must be a tag or a list of tags", 2)
        end
      end
      local ok, info = hostGameplay("navbuild", hasBounds, ax, ay, az, bx, by, bz,
        option("CellSize", 1, 0.25, 8), option("AgentRadius", 0.8, 0, 20), option("AgentHeight", 4, 0.2, 100),
        option("MaxClimb", 1.1, 0, 50), option("MaxSlope", 46, 0, 89),
        options.UseHighestSurface ~= nil and checkBoolean(options.UseHighestSurface, "Navigation.Build: UseHighestSurface", 1) or false,
        options.IncludeUnanchored ~= nil and checkBoolean(options.IncludeUnanchored, "Navigation.Build: IncludeUnanchored", 1) or false,
        tags)
      if not ok then
        error(info, 2)
      end
      return info
    end,
    -- The grid's size and counts (it is baked with the defaults on first use).
    GetInfo = function()
      local ok, info = hostGameplay("navinfo")
      if not ok then
        error(info, 2)
      end
      return info
    end,
    -- Bakes the cells in a box again (a door opened, something was built or removed).
    Update = function(center, size)
      local x, y, z = checkVector(center, "Navigation.Update: the centre", 1)
      local sx, sy, sz = checkVector(size, "Navigation.Update: the size", 1)
      request("navupdate", x, y, z, sx, sy, sz)
    end,
    -- Marks a box as not walkable (blocked = true) or walkable again (false), whatever is there.
    SetBlocked = function(center, size, blocked)
      local x, y, z = checkVector(center, "Navigation.SetBlocked: the centre", 1)
      local sx, sy, sz = checkVector(size, "Navigation.SetBlocked: the size", 1)
      request("navblock", x, y, z, sx, sy, sz, blocked ~= false)
    end,
    -- A path between two points or objects: { Points = {Vector3...}, Length, Complete }, or nil and why. Options:
    -- AllowPartial (false: an unreachable destination gives nil; true: the path to the closest reachable place).
    FindPath = function(from, to, options)
      local fid, fx, fy, fz = pointSpec(from, "Navigation.FindPath: the start", 1)
      local tid, tx, ty, tz = pointSpec(to, "Navigation.FindPath: the destination", 1)
      options = checkOptions(options, { AllowPartial = true }, "Navigation.FindPath", 1)
      local partial = options.AllowPartial ~= nil and checkBoolean(options.AllowPartial, "Navigation.FindPath: AllowPartial", 1) or false
      local ok, complete, length, flat = hostGameplay("navpath", fid, fx, fy, fz, tid, tx, ty, tz, partial)
      if not ok then
        return nil, complete
      end
      return pathFromHost(complete, length, flat)
    end,
    -- Whether a walk from one point (or object) to another exists: true, or false and why.
    IsReachable = function(from, to)
      local fid, fx, fy, fz = pointSpec(from, "Navigation.IsReachable: the start", 1)
      local tid, tx, ty, tz = pointSpec(to, "Navigation.IsReachable: the destination", 1)
      local ok, reason = hostGameplay("navreach", fid, fx, fy, fz, tid, tx, ty, tz)
      return ok == true, reason
    end,
    -- Whether a point is on walkable ground (and that ground's height).
    IsWalkable = function(point)
      local x, y, z = positionOf(point, "Navigation.IsWalkable: the point", 1)
      local ok, height = hostGameplay("navwalk", x, y, z)
      if ok == true then
        return true, height
      end
      if ok == false and height ~= nil then
        error(height, 2)
      end
      return false
    end,
    -- The nearest walkable point within maxDistance (default 10), or nil.
    GetNearestWalkable = function(point, maxDistance)
      local x, y, z = positionOf(point, "Navigation.GetNearestWalkable: the point", 1)
      maxDistance = maxDistance == nil and 10 or checkNumber(maxDistance, "Navigation.GetNearestWalkable: maxDistance", 1, 0)
      local ok, px, py, pz = hostGameplay("navnear", x, y, z, maxDistance)
      if ok == true then
        return newVector3(px, py, pz)
      end
      if ok == false and px ~= nil then
        error(px, 2)
      end
      return nil
    end,
    -- Whether a straight walk from a to b stays on walkable ground.
    Raycast = function(from, to)
      local ax, ay, az = positionOf(from, "Navigation.Raycast: the start", 1)
      local bx, by, bz = positionOf(to, "Navigation.Raycast: the end", 1)
      local ok, message = hostGameplay("navray", ax, ay, az, bx, by, bz)
      if ok == false and message ~= nil then
        error(message, 2)
      end
      return ok == true
    end,
    DrawPath = function(path, color, seconds)
      return DebugDraw.Path(path, color or "#33FF66", seconds)
    end,
    -- The walkable cells around a point as small green crosses (debug lines).
    DrawGrid = function(center, radius, seconds)
      local x, y, z = positionOf(center, "Navigation.DrawGrid: the centre", 1)
      radius = radius == nil and 12 or checkNumber(radius, "Navigation.DrawGrid: the radius", 1, 0, 200)
      local drawn, message = hostGameplay("navdrawgrid", x, y, z, radius, debugSeconds(seconds, 1))
      if drawn == false then
        error(message, 2)
      end
      return drawn
    end,
    -- An agent that walks an object along paths. Options: Speed (8), Acceleration (40), StoppingDistance (0.5), Radius
    -- (1: for keeping apart from other agents), RepathInterval (0.5 s, while following), Move ("Velocity": the body's
    -- horizontal velocity; "Transform": an anchored object's position; "None": nothing, read Update's velocity),
    -- FaceMovement (true), FaceYawOffset (degrees added to the facing: 180 for models that face +Z, as glTF models do),
    -- Avoidance (true). It belongs to this script and goes when its object is destroyed.
    CreateAgent = function(object, options)
      local objectId = objectIds[object]
      if objectId == nil then
        error("Navigation.CreateAgent expects a scene object, got a " .. type(object) .. " value", 2)
      end
      options = checkOptions(options, agentOptions, "Navigation.CreateAgent", 1)
      local function option(key, default, low, high)
        return options[key] == nil and default or checkNumber(options[key], "Navigation.CreateAgent: " .. key, 2, low, high)
      end
      local move = options.Move or "Velocity"
      if move ~= "Velocity" and move ~= "Transform" and move ~= "None" then
        error("Navigation.CreateAgent: Move must be \"Velocity\", \"Transform\" or \"None\", got " .. tostring(move), 2)
      end
      local id, message = hostGameplay("agentnew", objectId, option("Speed", 8, 0, 1000), option("Acceleration", 40, 0.01),
        option("StoppingDistance", 0.5, 0), option("Radius", 1, 0.01, 100), option("RepathInterval", 0.5, 0.05, 60), move,
        options.FaceMovement == nil and true or checkBoolean(options.FaceMovement, "Navigation.CreateAgent: FaceMovement", 1),
        options.Avoidance == nil and true or checkBoolean(options.Avoidance, "Navigation.CreateAgent: Avoidance", 1),
        option("FaceYawOffset", 0, -360, 360))
      if not id then
        error(message, 2)
      end
      return newAgent(id, object)
    end,
  })
  return navigation
end

-- ---------------------------------------------------------------------------------------------------------------------
-- Perception (per script: sensors' callbacks are the script's)

local function newPerception(wrap, physics, events)
  local raycast, overlapSphere = physics.Raycast, physics.OverlapSphere

  -- An observer's eye: its position plus an offset (world axes).
  local function eyeOf(observer, offset)
    local p = observer.Transform.Position
    if offset == nil then
      return p.x, p.y, p.z
    end
    return p.x + offset[1], p.y + offset[2], p.z + offset[3]
  end

  -- Raycast options for occlusion: never the observer or the target, triggers skipped unless asked.
  local function occlusionFilter(filter, observer, target)
    local ignore = {}
    if objectIds[observer] ~= nil then ignore[#ignore + 1] = observer end
    if objectIds[target] ~= nil then ignore[#ignore + 1] = target end
    local options = { Ignore = ignore, IgnoreTriggers = true }
    if filter ~= nil then
      for key, value in next, filter do
        if key == "Ignore" then
          if objectIds[value] ~= nil then
            ignore[#ignore + 1] = value
          elseif type(value) == "table" then
            for i = 1, rawlen(value) do
              ignore[#ignore + 1] = rawget(value, i)
            end
          end
        else
          options[key] = value
        end
      end
    end
    return options
  end

  local function clearLine(ax, ay, az, bx, by, bz, options)
    local dx, dy, dz = bx - ax, by - ay, bz - az
    local length = sqrt(dx * dx + dy * dy + dz * dz)
    if length < 1e-4 then
      return true, nil
    end
    local hit = raycast(newVector3(ax, ay, az), newVector3(dx, dy, dz), length, options)
    if hit == nil or hit.Distance >= length - 0.05 then
      return true, nil
    end
    return false, hit
  end

  -- The points of a target that may be seen: its middle and near its top.
  local function targetPoints(target)
    if objectIds[target] == nil then
      local x, y, z = checkVector(target, "Perception: the target (a scene object or a point)", 3)
      return { { x, y, z } }
    end
    local p = target.Transform.Position
    local low, high = target:GetBounds()
    local top = high.y - (high.y - low.y) * 0.15
    return { { p.x, p.y, p.z }, { p.x, top, p.z } }
  end

  local function forwardOf(observer)
    if objectIds[observer] ~= nil then
      local f = observer.Forward
      return f.x, f.y, f.z
    end
    error("Perception: the observer must be a scene object", 3)
  end

  local function angleTo(fx, fy, fz, dx, dy, dz)
    local length = sqrt(dx * dx + dy * dy + dz * dz)
    if length < 1e-6 then
      return 0, 0
    end
    local dot = (fx * dx + fy * dy + fz * dz) / length
    return math.deg(math.acos(max(-1, min(1, dot)))), length
  end

  local canSeeOptions = { FieldOfView = true, Range = true, EyeOffset = true, Filter = true, LineOfSight = true }

  local function readSight(options, usage, level)
    options = checkOptions(options, canSeeOptions, usage, level + 1)
    local fov = options.FieldOfView == nil and 120 or checkNumber(options.FieldOfView, usage .. ": FieldOfView", level + 1, 0, 360)
    local range = options.Range == nil and 50 or checkNumber(options.Range, usage .. ": Range", level + 1, 0)
    local eye
    if options.EyeOffset ~= nil then
      eye = { checkVector(options.EyeOffset, usage .. ": EyeOffset", level + 1) }
    end
    if options.Filter ~= nil and (type(options.Filter) ~= "table" or getmetatable(options.Filter) ~= nil) then
      error(usage .. ": Filter must be a query options table (IgnoreTags, CollisionGroup, ...)", level + 1)
    end
    local los = options.LineOfSight == nil and true or checkBoolean(options.LineOfSight, usage .. ": LineOfSight", level + 1)
    return fov, range, eye, options.Filter, los
  end

  -- seen, info { Distance, Angle, InRange, InView, Visible, Blocker }.
  local function canSee(observer, target, fov, range, eye, filter, los)
    local ex, ey, ez = eyeOf(observer, eye)
    local fx, fy, fz = forwardOf(observer)
    local points = targetPoints(target)
    local first = points[1]
    local angle, distance = angleTo(fx, fy, fz, first[1] - ex, first[2] - ey, first[3] - ez)
    local info = { Distance = distance, Angle = angle, InRange = distance <= range, InView = angle <= fov / 2 }
    if not info.InRange or not info.InView then
      info.Visible = false
      return false, info
    end
    if not los then
      info.Visible = true
      return true, info
    end
    local options = occlusionFilter(filter, observer, target)
    for _, point in ipairs(points) do
      local clear, hit = clearLine(ex, ey, ez, point[1], point[2], point[3], options)
      if clear then
        info.Visible = true
        return true, info
      end
      info.Blocker = hit.Object
    end
    info.Visible = false
    return false, info
  end

  local sensorOptions = { Range = true, FieldOfView = true, EyeOffset = true, OnlyTags = true, IgnoreTags = true, LineOfSight = true,
    Interval = true, Memory = true, Debug = true, Filter = true }

  local perception
  perception = library("Perception", {
    -- Whether target (an object or a point) is within the observer's view cone (degrees, the full angle) and range
    -- (default: any): inView, angle (degrees off its Forward), distance.
    InFieldOfView = function(observer, target, degrees, range)
      if objectIds[observer] == nil then
        error("Perception.InFieldOfView expects an observer (a scene object), got a " .. type(observer) .. " value", 2)
      end
      checkNumber(degrees, "Perception.InFieldOfView: the angle", 1, 0, 360)
      if range ~= nil then
        checkNumber(range, "Perception.InFieldOfView: the range", 1, 0)
      end
      local ex, ey, ez = eyeOf(observer, nil)
      local tx, ty, tz = positionOf(target, "Perception.InFieldOfView: the target", 1)
      local fx, fy, fz = forwardOf(observer)
      local angle, distance = angleTo(fx, fy, fz, tx - ex, ty - ey, tz - ez)
      return angle <= degrees / 2 and (range == nil or distance <= range), angle, distance
    end,
    -- Whether nothing solid is between two points or objects (objects at the ends are not obstacles): clear, and the
    -- RaycastResult of what is in the way. filter: query options (IgnoreTags, CollisionGroup, Ignore...).
    HasLineOfSight = function(from, to, filter)
      local ax, ay, az = positionOf(from, "Perception.HasLineOfSight: the start", 1)
      local bx, by, bz = positionOf(to, "Perception.HasLineOfSight: the end", 1)
      if filter ~= nil and (type(filter) ~= "table" or getmetatable(filter) ~= nil) then
        error("Perception.HasLineOfSight expects filter to be a query options table", 2)
      end
      return clearLine(ax, ay, az, bx, by, bz, occlusionFilter(filter, from, to))
    end,
    -- Range, view cone and line of sight together. Options: FieldOfView (120), Range (50), EyeOffset (vector from the
    -- observer's position), Filter (occluder query options), LineOfSight (true). Returns seen, info.
    CanSee = function(observer, target, options)
      if objectIds[observer] == nil then
        error("Perception.CanSee expects an observer (a scene object), got a " .. type(observer) .. " value", 2)
      end
      if target == nil then
        error("Perception.CanSee expects a target (a scene object or a point)", 2)
      end
      local fov, range, eye, filter, los = readSight(options, "Perception.CanSee", 1)
      if objectIds[target] ~= nil and not target.Exists then
        return false, { Visible = false }
      end
      return canSee(observer, target, fov, range, eye, filter, los)
    end,
    -- The objects the observer sees now (candidates: within Range; OnlyTags / IgnoreTags narrow them).
    FindVisible = function(observer, options)
      if objectIds[observer] == nil then
        error("Perception.FindVisible expects an observer (a scene object), got a " .. type(observer) .. " value", 2)
      end
      local onlyTags, ignoreTags
      if options ~= nil and type(options) == "table" then
        onlyTags, ignoreTags = rawget(options, "OnlyTags"), rawget(options, "IgnoreTags")
        local copy = {}
        for key, value in next, options do
          if key ~= "OnlyTags" and key ~= "IgnoreTags" then
            copy[key] = value
          end
        end
        options = copy
      end
      local fov, range, eye, filter, los = readSight(options, "Perception.FindVisible", 1)
      local ex, ey, ez = eyeOf(observer, eye)
      local found = overlapSphere(newVector3(ex, ey, ez), max(range, 0.01), { Ignore = observer, OnlyTags = onlyTags, IgnoreTags = ignoreTags, IgnoreEnvironment = true })
      local visible = {}
      for _, candidate in ipairs(found) do
        if canSee(observer, candidate, fov, range, eye, filter, los) then
          visible[#visible + 1] = candidate
        end
      end
      return visible
    end,
    -- A sensor that watches for objects the observer can see and tells when they appear and disappear. Options:
    -- Range (30), FieldOfView (120), EyeOffset, OnlyTags, IgnoreTags, LineOfSight (true), Interval (0.1 s between
    -- looks), Memory (seconds an object stays detected after it is lost from view, 0), Debug (draw the view cone),
    -- Filter (occluders). It looks by itself every Interval (Time.OnUpdate); it belongs to this script.
    CreateSensor = function(observer, options)
      if objectIds[observer] == nil then
        error("Perception.CreateSensor expects an observer (a scene object), got a " .. type(observer) .. " value", 2)
      end
      options = checkOptions(options, sensorOptions, "Perception.CreateSensor", 1)
      local range = options.Range == nil and 30 or checkNumber(options.Range, "Perception.CreateSensor: Range", 1, 0)
      local fov = options.FieldOfView == nil and 120 or checkNumber(options.FieldOfView, "Perception.CreateSensor: FieldOfView", 1, 0, 360)
      local eye
      if options.EyeOffset ~= nil then
        eye = { checkVector(options.EyeOffset, "Perception.CreateSensor: EyeOffset", 1) }
      end
      local interval = options.Interval == nil and 0.1 or checkNumber(options.Interval, "Perception.CreateSensor: Interval", 1, 0)
      local memory = options.Memory == nil and 0 or checkNumber(options.Memory, "Perception.CreateSensor: Memory", 1, 0)
      local los = options.LineOfSight == nil and true or checkBoolean(options.LineOfSight, "Perception.CreateSensor: LineOfSight", 1)
      local debug = options.Debug ~= nil and checkBoolean(options.Debug, "Perception.CreateSensor: Debug", 1) or false
      local onlyTags, ignoreTags, filter = options.OnlyTags, options.IgnoreTags, options.Filter
      local detected = {}   -- id -> { object = wrapper, lastSeen = elapsed time }
      local order = {}      -- ids in detection order
      local detectedSignal, lostSignal = newSignal(), newSignal()
      local enabled, destroyed = true, false
      local clock = 0
      local connection
      local sensor

      local function lose(id)
        local entry = detected[id]
        detected[id] = nil
        for i = #order, 1, -1 do
          if order[i] == id then
            remove(order, i)
          end
        end
        lostSignal.fire(entry.object, sensor)
      end

      local function look()
        if not observer.Exists then
          return
        end
        local now = timeValues.elapsedTime
        local ex, ey, ez = eyeOf(observer, eye)
        local seen = {}
        local candidates = overlapSphere(newVector3(ex, ey, ez), max(range, 0.01),
          { Ignore = observer, OnlyTags = onlyTags, IgnoreTags = ignoreTags, IgnoreEnvironment = true })
        for _, candidate in ipairs(candidates) do
          if canSee(observer, candidate, fov, range, eye, filter, los) then
            local id = candidate.Id
            seen[id] = true
            local entry = detected[id]
            if entry == nil then
              detected[id] = { object = candidate, lastSeen = now }
              order[#order + 1] = id
              detectedSignal.fire(candidate, sensor)
            else
              entry.lastSeen = now
            end
          end
        end
        for i = #order, 1, -1 do
          local id = order[i]
          local entry = detected[id]
          if entry ~= nil and not seen[id] and (not entry.object.Exists or now - entry.lastSeen >= memory) then
            lose(id)
          end
        end
      end

      local function drawDebug()
        if not observer.Exists then
          return
        end
        local ex, ey, ez = eyeOf(observer, eye)
        local fx, fy, fz = forwardOf(observer)
        local flat = sqrt(fx * fx + fz * fz)
        if flat < 1e-6 then
          return
        end
        local yaw = atan(fz, fx)
        local half = math.rad(min(fov, 359) / 2)
        local color = #order > 0 and "#FF453A" or "#FFD60A"
        local r, g, b = parseColor(color, nil, "", 1)
        local steps = 12
        local px, pz
        for i = 0, steps do
          local a = yaw - half + (2 * half) * i / steps
          local x, z = ex + cos(a) * range, ez + sin(a) * range
          if i == 0 or i == steps then
            drawLine(ex, ey, ez, x, ey, z, r, g, b, 0)
          end
          if px ~= nil then
            drawLine(px, ey, pz, x, ey, z, r, g, b, 0)
          end
          px, pz = x, z
        end
        for _, id in ipairs(order) do
          local target = detected[id].object
          if target.Exists then
            local t = target.Transform.Position
            drawLine(ex, ey, ez, t.x, t.y, t.z, 1, 0.27, 0.23, 0)
          end
        end
      end

      local function tick(dt)
        if destroyed then
          return
        end
        if not observer.Exists then
          sensor:Destroy()
          return
        end
        if debug then
          drawDebug()
        end
        if not enabled then
          return
        end
        clock = clock + dt
        if clock >= interval then
          clock = 0
          look()
        end
      end

      local function check(self, usage)
        if not rawequal(self, sensor) then
          error("call it with a colon: sensor:" .. usage, 3)
        end
      end

      sensor = handleObject("Sensor", {
        Observer = function() return observer end,
        Enabled = function() return enabled end,
        Debug = function() return debug end,
        Range = function() return range end,
        FieldOfView = function() return fov end,
        Exists = function() return not destroyed end,
        Count = function() return #order end,
      }, {
        Enabled = function(value)
          enabled = checkBoolean(value, "sensor.Enabled", 2)
          if not enabled then
            for i = #order, 1, -1 do
              lose(order[i])
            end
          end
        end,
        Debug = function(value) debug = checkBoolean(value, "sensor.Debug", 2) end,
        Range = function(value) range = checkNumber(value, "sensor.Range", 2, 0) end,
        FieldOfView = function(value) fov = checkNumber(value, "sensor.FieldOfView", 2, 0, 360) end,
      }, {
        -- f(object, sensor) when an object comes into view.
        OnDetected = function(self, f)
          check(self, "OnDetected(function)")
          checkFunction(f, "sensor:OnDetected", 1)
          return detectedSignal.connect(f)
        end,
        -- f(object, sensor) when it is lost (out of view for Memory seconds, or destroyed).
        OnLost = function(self, f)
          check(self, "OnLost(function)")
          checkFunction(f, "sensor:OnLost", 1)
          return lostSignal.connect(f)
        end,
        GetDetected = function(self)
          check(self, "GetDetected()")
          local list = {}
          for i, id in ipairs(order) do
            list[i] = detected[id].object
          end
          return list
        end,
        IsDetected = function(self, object)
          check(self, "IsDetected(object)")
          local id = objectIds[object]
          return id ~= nil and detected[id] ~= nil
        end,
        -- Looks now (instead of waiting for the next interval).
        Update = function(self)
          check(self, "Update()")
          if not destroyed and enabled then
            look()
          end
        end,
        Destroy = function(self)
          check(self, "Destroy()")
          if destroyed then
            return
          end
          destroyed = true
          if connection ~= nil then
            connection:Disconnect()
          end
        end,
      })
      connection = timeValues.OnUpdate(tick)
      return sensor
    end,
    -- A noise anyone listening can hear (Perception.OnNoise) within Radius studs, delivered at the start of the next
    -- frame. Options: Radius (20), Kind (a name, default "Noise"), Source (an object), Loudness (1), Data (a plain table).
    EmitNoise = function(position, options)
      local x, y, z = positionOf(position, "Perception.EmitNoise: the position", 1)
      options = checkOptions(options, { Radius = true, Kind = true, Source = true, Loudness = true, Data = true }, "Perception.EmitNoise", 1)
      local radius = options.Radius == nil and 20 or checkNumber(options.Radius, "Perception.EmitNoise: Radius", 1, 0)
      local kind = options.Kind or "Noise"
      if type(kind) ~= "string" then
        error("Perception.EmitNoise: Kind must be a string", 2)
      end
      if options.Source ~= nil and objectIds[options.Source] == nil then
        error("Perception.EmitNoise: Source must be a scene object", 2)
      end
      local loudness = options.Loudness == nil and 1 or checkNumber(options.Loudness, "Perception.EmitNoise: Loudness", 1, 0)
      events.Emit("Wisko.Noise", { Position = newVector3(x, y, z), Radius = radius, Kind = kind, Source = options.Source,
        Loudness = loudness, Data = options.Data })
    end,
    -- f(noise, listener) for every noise the listener hears: within the noise's Radius times Hearing (default 1).
    -- noise: { Position, Distance, Radius, Kind, Source, Loudness, Data }. Returns the Events connection.
    OnNoise = function(listener, f, options)
      if objectIds[listener] == nil then
        error("Perception.OnNoise expects a listener (a scene object), got a " .. type(listener) .. " value", 2)
      end
      checkFunction(f, "Perception.OnNoise", 1)
      options = checkOptions(options, { Hearing = true }, "Perception.OnNoise", 1)
      local hearing = options.Hearing == nil and 1 or checkNumber(options.Hearing, "Perception.OnNoise: Hearing", 1, 0)
      local connection
      connection = events.On("Wisko.Noise", function(noise)
        if not listener.Exists then
          connection:Disconnect()
          return
        end
        local p = listener.Transform.Position
        local position = noise.Position
        local d = distance3(p.x, p.y, p.z, position.x, position.y, position.z)
        if d <= noise.Radius * hearing and noise.Source ~= listener then
          noise.Distance = d
          f(noise, listener)
        end
      end)
      return connection
    end,
  })
  return perception
end

-- ---------------------------------------------------------------------------------------------------------------------
-- Object methods

local function objectMethods(id, object, fail, checkSelf, request)
  local function teleport(self, position, rotation, options)
    checkSelf(self, "Teleport(position[, rotation][, options])")
    local x, y, z = vectorParts(position)
    if x == nil or not (isFinite(x) and isFinite(y) and isFinite(z)) then
      error("Teleport expects a position: a vector (finite x, y, z)", 2)
    end
    -- Teleport(position, options): a table that is not a vector is the options.
    if options == nil and type(rotation) == "table" and getmetatable(rotation) == nil and vectorParts(rotation) == nil then
      options, rotation = rotation, nil
    end
    local hasRotation, rx, ry, rz = false, 0, 0, 0
    if rotation ~= nil then
      rx, ry, rz = vectorParts(rotation)
      if rx == nil or not (isFinite(rx) and isFinite(ry) and isFinite(rz)) then
        error("Teleport expects rotation to be a vector of Euler degrees (or nil to keep it)", 2)
      end
      hasRotation = true
    end
    options = checkOptions(options, { KeepVelocity = true, RotateVelocity = true }, "Teleport", 1)
    local keep = options.KeepVelocity ~= nil and checkBoolean(options.KeepVelocity, "Teleport: KeepVelocity", 1) or false
    local turn = options.RotateVelocity == nil and true or checkBoolean(options.RotateVelocity, "Teleport: RotateVelocity", 1)
    request("teleport", id, x, y, z, hasRotation, rx, ry, rz, keep, turn)
    return true
  end

  local function getBones(self)
    checkSelf(self, "GetBones()")
    local ok, bones = hostScene("bones", id)
    if not ok then
      fail(bones)
    end
    return bones
  end

  local function hasBone(self, name)
    checkSelf(self, "HasBone(name)")
    if type(name) ~= "string" then
      error("HasBone expects a bone name (a string), got a " .. type(name) .. " value", 2)
    end
    local ok, has = hostScene("hasbone", id, name)
    if not ok then
      fail(has)
    end
    return has
  end

  -- The bone's world position and rotation (Euler degrees) in the pose shown now; nil and why when there is no such bone.
  local function getBoneTransform(self, name)
    checkSelf(self, "GetBoneTransform(name)")
    if type(name) ~= "string" then
      error("GetBoneTransform expects a bone name (a string), got a " .. type(name) .. " value", 2)
    end
    local ok, px, py, pz, rx, ry, rz = hostScene("bone", id, name)
    if not ok then
      if px == nil then
        fail(nil)
      end
      return nil, px
    end
    return newVector3(px, py, pz), newVector3(rx, ry, rz)
  end

  local function triggerConnection(entered, usage)
    return function(self, f)
      checkSelf(self, usage .. "(function)")
      checkFunction(f, usage, 1)
      local handlerId, message = hostScene("ontrigger", id, entered, f)
      if not handlerId then
        fail(message)
      end
      return newConnection(hostEvents, handlerId)
    end
  end

  return {
    Teleport = teleport,
    GetBones = getBones,
    HasBone = hasBone,
    GetBoneTransform = getBoneTransform,
    -- f(other, trigger) when something starts / stops overlapping this trigger (other: nil for the environment); any
    -- script may connect, not only the trigger's own. Runs with the frame's collision callbacks.
    OnTriggerEntered = triggerConnection(true, "OnTriggerEntered"),
    OnTriggerExited = triggerConnection(false, "OnTriggerExited"),
  }
end

-- ---------------------------------------------------------------------------------------------------------------------
-- Chat (per script: the default UI is the script's own require("Wisko.ChatUI")). The text chat of this Play session,
-- on this machine only: Wisko has no networking. Messages sent in a frame are delivered at the start of the next one,
-- after the filters; history, mutes and blocks last until Stop.

local defaultChannel = "General"

-- A message as scripts get it: a fresh plain table for every receiver (changing it changes nobody else's). Data holds
-- the developer's own values (from the key, value pairs after the eight fixed ones); empty when it has none.
local function chatMessage(id, channel, text, senderId, senderName, kind, time, timestamp, ...)
  local data = {}
  for i = 1, select("#", ...), 2 do
    local key, value = select(i, ...)
    data[key] = value
  end
  return {
    Id = id, Channel = channel, Text = text, Kind = kind, Time = time, Timestamp = timestamp,
    Sender = { Id = senderId, Name = senderName }, Data = data,
  }
end

-- The host's "not while editing" answer becomes an error, as elsewhere in the gameplay API; other answers pass through.
local function chatAnswer(ok, message, level)
  if ok == false and type(message) == "string" and find(message, "only available while the game plays", 1, true) then
    error("Chat is only available while the game plays", level + 1)
  end
  return ok, message
end

local function checkChatText(value, usage, level)
  if type(value) == "number" then
    return tostring(value)
  end
  if type(value) ~= "string" then
    error(usage .. " expects the message text (a string), got a " .. type(value) .. " value", level + 1)
  end
  return value
end

-- A message's Data: a table of names to strings, numbers or booleans (at most 16), for the developer's own styling.
local function checkChatData(value, usage, level)
  if value == nil then
    return nil
  end
  if type(value) ~= "table" or getmetatable(value) ~= nil then
    error(usage .. ": Data must be a table of names to strings, numbers or booleans, got a " .. type(value) .. " value", level + 1)
  end
  local count = 0
  for key, item in next, value do
    count = count + 1
    if type(key) ~= "string" or not find(key, "^%a[%w_]*$") or #key > 32 then
      error(usage .. ": Data keys are names (a letter, then letters, digits or _, at most 32 characters), not " .. tostring(key), level + 1)
    end
    local kind = type(item)
    if kind ~= "string" and kind ~= "number" and kind ~= "boolean" then
      error(usage .. ": Data." .. key .. " must be a string, a number or a boolean, not a " .. kind .. " value", level + 1)
    end
    if kind == "number" and not isFinite(item) then
      error(usage .. ": Data." .. key .. " must be a finite number", level + 1)
    end
  end
  if count > 16 then
    error(usage .. ": Data can hold at most 16 values", level + 1)
  end
  return value
end

local function checkChannelName(value, usage, level)
  if value ~= nil and type(value) ~= "string" then
    error(usage .. ": a channel is named by a string, got a " .. type(value) .. " value", level + 1)
  end
  return value
end

-- A sender id from "id" or a message's Sender table.
local function senderIdOf(value, usage, level)
  if type(value) == "table" and type(rawget(value, "Id")) == "string" then
    return rawget(value, "Id")
  end
  if type(value) ~= "string" then
    error(usage .. " expects a sender id (message.Sender.Id) or a message's Sender, got a " .. type(value) .. " value", level + 1)
  end
  return value
end

-- Send / Post: the new message's id, or nil and why.
local function chatSend(request, text, channel, senderName, data, level)
  local id, message = hostGameplay(request, text, channel, senderName, data)
  if id == false then
    chatAnswer(false, message, level + 1)
    return nil, message
  end
  return id
end

local function chatOnMessage(f, channel, usage, level)
  checkFunction(f, usage, level + 1)
  local id, message = hostGameplay("chatlisten", channel, function(...)
    f(chatMessage(...))
  end)
  if not id then
    chatAnswer(false, message, level + 1)
    error(usage .. ": " .. tostring(message), level + 1)
  end
  return newConnection(hostEvents, id)
end

local function chatHistory(channel, count, usage, level)
  if count ~= nil then
    checkNumber(count, usage .. ": the count", level + 1, 0)
  end
  local ok, flat = hostGameplay("chathistory", channel, count)
  if not ok then
    chatAnswer(false, flat, level + 1)
    error(usage .. ": " .. tostring(flat), level + 1)
  end
  -- Per message: its eight values, the number of its Data pairs, the pairs.
  local result = {}
  local i = 1
  while i <= #flat do
    local pairCount = flat[i + 8]
    local message = chatMessage(flat[i], flat[i + 1], flat[i + 2], flat[i + 3], flat[i + 4], flat[i + 5], flat[i + 6], flat[i + 7])
    for p = i + 9, i + 8 + pairCount * 2, 2 do
      message.Data[flat[p]] = flat[p + 1]
    end
    result[#result + 1] = message
    i = i + 9 + pairCount * 2
  end
  return result
end

-- A yes / no question about a sender (muted, blocked).
local function chatAsk(request, sender, usage, level)
  local answer, message = hostGameplay(request, senderIdOf(sender, usage, level + 1))
  chatAnswer(answer, message, level + 1)
  return answer == true
end

-- Mute / Unmute / Block / Unblock.
local function chatHide(request, sender, hidden, usage, level)
  local ok, message = hostGameplay(request, senderIdOf(sender, usage, level + 1), hidden)
  if not ok then
    chatAnswer(false, message, level + 1)
    error(usage .. ": " .. tostring(message), level + 1)
  end
end

-- A channel's handle: its name, limits and the channel-bound versions of Send, Post, OnMessage and GetHistory.
local function chatChannel(name)
  local handle
  local function info(index)
    local values = { hostGameplay("chatchannel", name) }
    if values[1] == nil or values[1] == false then
      error("the chat channel \"" .. name .. "\" is gone (chat ends at Stop)", 3)
    end
    return values[index]
  end
  local function checkHandle(self, usage)
    if not rawequal(self, handle) then
      error("call it with a colon: channel:" .. usage, 3)
    end
  end
  handle = handleObject("ChatChannel", {
    Name = function() return name end,
    MaxLength = function() return info(2) end,
    HistorySize = function() return info(3) end,
    PlayersCanSend = function() return info(4) end,
    Count = function() return info(5) end,
  }, {}, {
    Send = function(self, text, options)
      checkHandle(self, "Send(\"text\", options?)")
      checkOptions(options, { Data = true }, "channel:Send", 1)
      local data = options and checkChatData(options.Data, "channel:Send", 1)
      local id, message = chatSend("chatsend", checkChatText(text, "channel:Send", 1), name, nil, data, 1)
      return id, message
    end,
    Post = function(self, text, options)
      checkHandle(self, "Post(\"text\", options?)")
      checkOptions(options, { SenderName = true, Data = true }, "channel:Post", 1)
      if options ~= nil and options.SenderName ~= nil and type(options.SenderName) ~= "string" then
        error("channel:Post: SenderName must be a string", 2)
      end
      local data = options and checkChatData(options.Data, "channel:Post", 1)
      local id, message = chatSend("chatpost", checkChatText(text, "channel:Post", 1), name, options and options.SenderName, data, 1)
      return id, message
    end,
    OnMessage = function(self, f)
      checkHandle(self, "OnMessage(function(message) ... end)")
      local connection = chatOnMessage(f, name, "channel:OnMessage", 1)
      return connection
    end,
    GetHistory = function(self, count)
      checkHandle(self, "GetHistory(count?)")
      local messages = chatHistory(name, count, "channel:GetHistory", 1)
      return messages
    end,
  }, function() return "ChatChannel(" .. name .. ")" end)
  return handle
end

local function newChat(require)
  local defaultUi
  return library("Chat", {
    -- The local player says text: the message's id, or nil and why (too fast, empty, players cannot send there).
    -- options: a channel name (default "General"), or { Channel, Data } (Data: the developer's own values for
    -- styling). It is always the local player's: no option changes the sender or the kind.
    Send = function(text, options)
      local channel, data
      if type(options) == "table" then
        checkOptions(options, { Channel = true, Data = true }, "Chat.Send", 1)
        channel = checkChannelName(options.Channel, "Chat.Send", 1)
        data = checkChatData(options.Data, "Chat.Send", 1)
      else
        channel = checkChannelName(options, "Chat.Send", 1)
      end
      local id, message = chatSend("chatsend", checkChatText(text, "Chat.Send", 1), channel, nil, data, 1)
      return id, message
    end,
    -- The game's own message (kind "System"): an announcement, or an NPC's line with SenderName. options: a channel
    -- name, or { Channel, SenderName, Data }. The message's id, or nil and why.
    Post = function(text, options)
      local channel, senderName, data
      if type(options) == "string" then
        channel = options
      elseif options ~= nil then
        checkOptions(options, { Channel = true, SenderName = true, Data = true }, "Chat.Post", 1)
        channel = checkChannelName(options.Channel, "Chat.Post", 1)
        data = checkChatData(options.Data, "Chat.Post", 1)
        senderName = options.SenderName
        if senderName ~= nil and type(senderName) ~= "string" then
          error("Chat.Post: SenderName must be a string", 2)
        end
      end
      local id, message = chatSend("chatpost", checkChatText(text, "Chat.Post", 1), channel, senderName, data, 1)
      return id, message
    end,
    -- f(message) for every delivered message (of one channel when given), at the start of the frame after it was
    -- sent. message: { Id, Text, Channel, Sender = { Id, Name }, Kind = "Player" | "System", Time, Timestamp, Data }.
    OnMessage = function(f, channel)
      local connection = chatOnMessage(f, checkChannelName(channel, "Chat.OnMessage", 1), "Chat.OnMessage", 1)
      return connection
    end,
    -- f(message) sees every message before anyone else: nil or true keeps it, false (and a reason) withholds it, a
    -- string replaces its text. Filters run in the order they were set. A filter that raises an error closes chat
    -- for the rest of the session (nothing unfiltered gets through).
    SetFilter = function(f)
      checkFunction(f, "Chat.SetFilter", 1)
      local id, message = hostGameplay("chatfilter", function(messageId, ...)
        local verdict, reason = f(chatMessage(messageId, ...))
        if verdict == nil or verdict == true then
          return
        end
        if verdict == false then
          hostGameplay("chatverdict", messageId, false, type(reason) == "string" and reason or nil)
        elseif type(verdict) == "string" then
          hostGameplay("chatverdict", messageId, verdict)
        else
          error("Chat.SetFilter: a filter returns nil or true (keep the message), false (withhold it) or a string (its new text), not a "
            .. type(verdict) .. " value", 2)
        end
      end)
      if not id then
        chatAnswer(false, message, 1)
        error("Chat.SetFilter: " .. tostring(message), 2)
      end
      return newConnection(hostEvents, id)
    end,
    -- A new channel: options { MaxLength (1 to 1000, default 200), HistorySize (1 to 500, default 100),
    -- PlayersCanSend (default true) }. Answers its handle.
    CreateChannel = function(name, options)
      if type(name) ~= "string" then
        error("Chat.CreateChannel expects a name (a string), got a " .. type(name) .. " value", 2)
      end
      checkOptions(options, { MaxLength = true, HistorySize = true, PlayersCanSend = true }, "Chat.CreateChannel", 1)
      options = options or {}
      if options.MaxLength ~= nil then
        checkNumber(options.MaxLength, "Chat.CreateChannel: MaxLength", 1, 1, 1000)
      end
      if options.HistorySize ~= nil then
        checkNumber(options.HistorySize, "Chat.CreateChannel: HistorySize", 1, 1, 500)
      end
      if options.PlayersCanSend ~= nil then
        checkBoolean(options.PlayersCanSend, "Chat.CreateChannel: PlayersCanSend", 1)
      end
      local ok, message = hostGameplay("chatcreate", name, options.MaxLength, options.HistorySize, options.PlayersCanSend)
      if not ok then
        chatAnswer(false, message, 1)
        error("Chat.CreateChannel: " .. tostring(message), 2)
      end
      return chatChannel(name)
    end,
    -- A channel's handle, or nil when there is none by that name.
    GetChannel = function(name)
      if type(name) ~= "string" then
        error("Chat.GetChannel expects a name (a string), got a " .. type(name) .. " value", 2)
      end
      local found, message = hostGameplay("chatchannel", name)
      chatAnswer(found, message, 1)
      return found and chatChannel(found) or nil
    end,
    -- Every channel, in the order they were made ("General" first).
    GetChannels = function()
      local names, message = hostGameplay("chatchannels")
      chatAnswer(names, message, 1)
      local result = {}
      for i = 1, #names do
        result[i] = chatChannel(names[i])
      end
      return result
    end,
    -- The last count (default: all kept) messages of a channel (default "General"), oldest first.
    GetHistory = function(channel, count)
      local messages = chatHistory(checkChannelName(channel, "Chat.GetHistory", 1) or defaultChannel, count, "Chat.GetHistory", 1)
      return messages
    end,
    -- Hides a sender's messages from the local player (Unmute shows them again). A sender id or a message's Sender.
    Mute = function(sender)
      chatHide("chatmute", sender, true, "Chat.Mute", 1)
    end,
    Unmute = function(sender)
      chatHide("chatmute", sender, false, "Chat.Unmute", 1)
    end,
    IsMuted = function(sender)
      local muted = chatAsk("chatismuted", sender, "Chat.IsMuted", 1)
      return muted
    end,
    -- As Mute, and the transport is told (a networked one would also stop them seeing the local player's messages).
    Block = function(sender)
      chatHide("chatblock", sender, true, "Chat.Block", 1)
    end,
    Unblock = function(sender)
      chatHide("chatblock", sender, false, "Chat.Unblock", 1)
    end,
    IsBlocked = function(sender)
      local blocked = chatAsk("chatisblocked", sender, "Chat.IsBlocked", 1)
      return blocked
    end,
    -- The local player's name in chat (1 to 32 visible characters; not "System", "Admin" or another reserved name):
    -- true, or false and why.
    SetPlayerName = function(name)
      if type(name) ~= "string" then
        error("Chat.SetPlayerName expects a name (a string), got a " .. type(name) .. " value", 2)
      end
      local ok, message = hostGameplay("chatsetname", name)
      chatAnswer(ok, message, 1)
      return ok == true, message
    end,
    -- How fast the local player may send: a burst of messages, then perSecond more each second (default 5 and 1).
    SetRateLimit = function(burst, perSecond)
      checkNumber(burst, "Chat.SetRateLimit: the burst", 1, 1, 100)
      checkNumber(perSecond, "Chat.SetRateLimit: messages per second", 1, 0.05, 100)
      local ok, message = hostGameplay("chatrate", burst, perSecond)
      if not ok then
        chatAnswer(false, message, 1)
        error("Chat.SetRateLimit: " .. tostring(message), 2)
      end
    end,
    -- The ready-made chat window (Wisko.ChatUI: a log and a text box; "/" starts typing, Enter sends). options:
    -- Channel, FontSize, Width, Height, FocusKey, Placeholder, Position, Anchor, BackgroundColor, BackgroundOpacity,
    -- InputColor, PlayerColor, SystemColor, NoticeColor. One script at a time has it; it ends with that script.
    EnableDefaultUI = function(options)
      if options ~= nil and type(options) ~= "table" then
        error("Chat.EnableDefaultUI expects an options table or nothing, got a " .. type(options) .. " value", 2)
      end
      local ok, message = hostGameplay("chatui", true)
      if not ok then
        chatAnswer(false, message, 1)
        error("Chat.EnableDefaultUI: " .. tostring(message), 2)
      end
      if defaultUi == nil then
        local loaded, module = pcall(require, "Wisko.ChatUI")
        if not loaded or type(module) ~= "table" or type(module.Enable) ~= "function" then
          hostGameplay("chatui", false)
          error("Chat.EnableDefaultUI: Wisko.ChatUI could not be loaded (" .. tostring(module) .. ")", 2)
        end
        defaultUi = module
      end
      local enabled, problem = pcall(defaultUi.Enable, options)
      if not enabled then
        hostGameplay("chatui", false)
        error("Chat.EnableDefaultUI: " .. (gsub(tostring(problem), "^[^:]*:%d+: ", "")), 2)
      end
    end,
    DisableDefaultUI = function()
      if defaultUi ~= nil and type(defaultUi.Disable) == "function" then
        defaultUi.Disable()
      end
      hostGameplay("chatui", false)
    end,
  }, {
    -- { Id = "local", Name } of the person playing.
    LocalPlayer = function()
      local id, name = hostGameplay("chatplayer")
      chatAnswer(id, name, 1)
      return { Id = id, Name = name }
    end,
    -- "Local": messages never leave this machine.
    Transport = function()
      local name, networked = hostGameplay("chattransport")
      chatAnswer(name, networked, 1)
      return name
    end,
    IsNetworked = function()
      local name, networked = hostGameplay("chattransport")
      chatAnswer(name, networked, 1)
      return networked == true
    end,
    DefaultChannel = function() return defaultChannel end,
  })
end

-- ---------------------------------------------------------------------------------------------------------------------
-- The globals of one environment

-- A per-script library made on first use: until a script touches it, it costs one small table. The first access builds
-- the library and puts its metamethods on this table (later accesses go straight to them); that first access reaches
-- them by tail calls, so an error still names the script's line.
local debugGetmetatable = debug.getmetatable

local function lazyLibrary(name, make)
  local metatable = {}
  local function install()
    local real = debugGetmetatable(make())
    for key, value in next, real do
      metatable[key] = value
    end
    return real
  end
  metatable.__index = function(t, key)
    local real = install()
    return real.__index(t, key)
  end
  metatable.__newindex = function(t, key, value)
    local real = install()
    return real.__newindex(t, key, value)
  end
  metatable.__tostring = function() return name end
  metatable.__metatable = name
  return setmetatable({}, metatable)
end

local function extendEnvironment(env, wrap)
  -- The script's own Physics and Events, as they were made (a script that later replaces its globals does not break
  -- these libraries).
  local physics, events = env.Physics, env.Events
  env.Navigation = lazyLibrary("Navigation", function() return newNavigation(wrap) end)
  env.Perception = lazyLibrary("Perception", function() return newPerception(wrap, physics, events) end)
  env.GameTime = lazyLibrary("GameTime", newGameTime)
  env.Random = Random
  env.Noise = Noise
  env.Path = Path
  env.GameSave = GameSave
  env.Cameras = Cameras
  env.Particles = Particles
  env.DebugDraw = DebugDraw
  -- The script's own require as it was made (the default chat UI loads through it, so a project can replace it).
  local require = env.require
  env.Chat = lazyLibrary("Chat", function() return newChat(require) end)
end

return objectMethods, extendEnvironment
