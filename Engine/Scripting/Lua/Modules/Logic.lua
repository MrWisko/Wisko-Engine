-- Wisko.Logic: optional building blocks for switches, pressure plates, timers, counters, conditions and sequences.
--
-- Everything here is a "signal": a table with a Value, OnChanged(f(value, old, signal)) and, for inputs, ways to change
-- it. Signals combine (All, Any, Not, Hold, Window), so a puzzle is a few lines that say what opens what; no puzzle is
-- built in. Switches and plates mirror their value in an attribute of their object (default "On" / "Pressed"), so
-- other scripts, OnAttributeChanged and the HUD can follow them too.
--
--   local Logic = require("Wisko.Logic")
--   local a = Logic.Switch(Scene.Find("SwitchA"))
--   local b = Logic.Switch(Scene.Find("SwitchB"))
--   local plate = Logic.PressurePlate(Scene.Find("Plate"), { Filter = function(o) return o:HasTag("Player") end })
--   local open = Logic.All(a, b, Logic.Hold(plate, 5))     -- the plate counts for 5 s after you step off
--   open:OnChanged(function(isOpen) door:Set(isOpen) end)
--
-- Ordinary Lua on Wisko's public API: copy it to Assets/Scripts/Wisko/Logic.lua to change it.

local Signal = require("Wisko.Signal")

local Logic = {}

local Value = {}
Value.__index = Value

local function newValue(initial, name)
  return setmetatable({ Value = initial, Name = name, _changed = Signal.new() }, Value)
end

function Value:OnChanged(f)
  return self._changed:Connect(f)
end

-- Calls f(value) now and every time it changes.
function Value:Bind(f)
  f(self.Value, nil, self)
  return self._changed:Connect(f)
end

function Value:_set(value)
  if value == self.Value then
    return false
  end
  local old = self.Value
  self.Value = value
  self._changed:Fire(value, old, self)
  return true
end

local function isSignal(value)
  return type(value) == "table" and getmetatable(value) ~= nil and value._changed ~= nil
end

local function mirror(object, attribute, value)
  if object ~= nil and attribute and object.Exists then
    object:SetAttribute(attribute, value)
  end
end

local function checkObject(object, usage)
  if object ~= nil and (type(object) ~= "table" or object.Id == nil) then
    error(usage .. " expects a scene object (or nil), got a " .. type(object) .. " value", 3)
  end
end

-- A switch (on or off). Options: On (start value, false), Attribute ("On"; false: no attribute), CanUse(user, switch)
-- -> ok[, reason] (who may use it), Toggle (true: Use flips it; false: Use turns it on).
function Logic.Switch(object, options)
  checkObject(object, "Logic.Switch")
  options = options or {}
  local switch = newValue(options.On == true, object and object.Name or "Switch")
  local attribute = options.Attribute == nil and "On" or options.Attribute
  local toggles = options.Toggle ~= false
  switch.Object = object
  mirror(object, attribute, switch.Value)

  function switch:Set(on)
    if self:_set(on == true) then
      mirror(object, attribute, self.Value)
    end
  end

  function switch:Toggle()
    self:Set(not self.Value)
  end

  -- An interaction by user (e.g. the player): checked with CanUse, then flipped (or turned on). Returns ok, reason.
  function switch:Use(user)
    if options.CanUse ~= nil then
      local ok, reason = options.CanUse(user, self)
      if not ok then
        return false, reason or "it cannot be used now"
      end
    end
    if toggles then
      self:Toggle()
    else
      self:Set(true)
    end
    return true
  end

  return switch
end

-- Whatever is inside a trigger Part (object.IsTrigger must be true): Value is how many objects the Filter accepts
-- (default: all), OnEntered(f(object)) / OnExited(f(object)).
function Logic.Volume(object, options)
  if type(object) ~= "table" or object.Id == nil then
    error("Logic.Volume expects a trigger Part (a scene object)", 2)
  end
  options = options or {}
  local volume = newValue(0, object.Name)
  local entered, exited = Signal.new(), Signal.new()
  local inside = {}
  volume.Object = object

  local function accepts(other)
    return other ~= nil and (options.Filter == nil or options.Filter(other))
  end

  volume._enter = object:OnTriggerEntered(function(other)
    if accepts(other) and not inside[other.Id] then
      inside[other.Id] = other
      volume:_set(volume.Value + 1)
      entered:Fire(other)
    end
  end)
  volume._exit = object:OnTriggerExited(function(other)
    if other ~= nil and inside[other.Id] then
      inside[other.Id] = nil
      volume:_set(math.max(0, volume.Value - 1))
      exited:Fire(other)
    end
  end)

  function volume:OnEntered(f) return entered:Connect(f) end
  function volume:OnExited(f) return exited:Connect(f) end

  function volume:GetOccupants()
    local list = {}
    for _, other in pairs(inside) do
      if other.Exists then
        list[#list + 1] = other
      end
    end
    table.sort(list, function(a, b) return a.Id < b.Id end)
    return list
  end

  function volume:Destroy()
    self._enter:Disconnect()
    self._exit:Disconnect()
  end

  return volume
end

-- A pressure plate: a trigger Part that is pressed while at least MinCount (1) accepted objects are on it. Options:
-- Filter(object) -> bool, MinCount, Attribute ("Pressed"; false: none).
function Logic.PressurePlate(object, options)
  options = options or {}
  local volume = Logic.Volume(object, { Filter = options.Filter })
  local minimum = options.MinCount or 1
  local attribute = options.Attribute == nil and "Pressed" or options.Attribute
  local plate = newValue(false, object.Name)
  plate.Object = object
  plate.Volume = volume
  mirror(object, attribute, false)
  volume:OnChanged(function(count)
    if plate:_set(count >= minimum) then
      mirror(object, attribute, plate.Value)
    end
  end)

  function plate:GetOccupants() return volume:GetOccupants() end
  function plate:OnEntered(f) return volume:OnEntered(f) end
  function plate:OnExited(f) return volume:OnExited(f) end
  function plate:Destroy() volume:Destroy() end

  return plate
end

-- A number that counts. Options: Start (0), Target (Reached becomes true at it). counter.Reached is a signal too.
function Logic.Counter(options)
  options = options or {}
  local counter = newValue(options.Start or 0, "Counter")
  local target = options.Target
  counter.Reached = newValue(target ~= nil and counter.Value >= target, "Reached")

  local function update()
    if target ~= nil then
      counter.Reached:_set(counter.Value >= target)
    end
  end

  function counter:Increment(amount)
    self:_set(self.Value + (amount or 1))
    update()
    return self.Value
  end

  function counter:Decrement(amount)
    self:_set(self.Value - (amount or 1))
    update()
    return self.Value
  end

  function counter:Set(value)
    self:_set(value)
    update()
  end

  function counter:Reset()
    self:Set(options.Start or 0)
  end

  return counter
end

local function combine(name, inputs, compute)
  for i, input in ipairs(inputs) do
    if not isSignal(input) then
      error("Logic." .. name .. " expects signals (switches, plates, counters, other conditions); argument " .. i .. " is a " .. type(input) .. " value", 3)
    end
  end
  local result = newValue(compute(inputs), name)
  result.Inputs = inputs
  for _, input in ipairs(inputs) do
    input:OnChanged(function()
      result:_set(compute(inputs))
    end)
  end
  return result
end

local function truthy(value)
  return value ~= nil and value ~= false and value ~= 0
end

-- True while every input is (a counter counts when it is not 0; use counter.Reached for a target).
function Logic.All(...)
  return combine("All", { ... }, function(inputs)
    for _, input in ipairs(inputs) do
      if not truthy(input.Value) then
        return false
      end
    end
    return true
  end)
end

-- True while any input is.
function Logic.Any(...)
  return combine("Any", { ... }, function(inputs)
    for _, input in ipairs(inputs) do
      if truthy(input.Value) then
        return true
      end
    end
    return false
  end)
end

function Logic.Not(input)
  return combine("Not", { input }, function(inputs)
    return not truthy(inputs[1].Value)
  end)
end

-- True while the input is, and for seconds after it stops being true (a timed pressure plate, a lingering switch).
function Logic.Hold(input, seconds)
  if not isSignal(input) then
    error("Logic.Hold expects a signal, got a " .. type(input) .. " value", 2)
  end
  if type(seconds) ~= "number" or seconds < 0 then
    error("Logic.Hold expects a number of seconds (0 or more)", 2)
  end
  local held = newValue(truthy(input.Value), "Hold")
  local timer
  held.Remaining = 0
  input:OnChanged(function(value)
    if timer ~= nil then
      timer:Cancel()
      timer = nil
    end
    if truthy(value) then
      held:_set(true)
    elseif seconds == 0 then
      held:_set(false)
    else
      timer = Timer.After(seconds, function()
        timer = nil
        held:_set(false)
      end)
    end
  end)
  function held:GetRemaining()
    return timer ~= nil and timer.Remaining or 0
  end
  return held
end

-- True for seconds after the input becomes true (a time window), whatever the input does meanwhile.
function Logic.Window(input, seconds)
  if not isSignal(input) then
    error("Logic.Window expects a signal, got a " .. type(input) .. " value", 2)
  end
  local window = newValue(false, "Window")
  local timer
  input:OnChanged(function(value)
    if truthy(value) then
      if timer ~= nil then
        timer:Cancel()
      end
      window:_set(true)
      timer = Timer.After(seconds, function()
        timer = nil
        window:_set(false)
      end)
    end
  end)
  function window:GetRemaining()
    return timer ~= nil and timer.Remaining or 0
  end
  return window
end

-- f() after seconds (a Timer handle: Cancel, Remaining).
function Logic.Delay(seconds, f)
  return Timer.After(seconds, f)
end

-- A sequence to enter in order (e.g. buttons, notes, levers). Options: Timeout (seconds between inputs before it starts
-- over; nil: none), OnComplete(f), OnWrong(f(expected, got)). sequence:Input(value) -> "progress", "complete" or "wrong".
-- Value is true once complete (Reset to try again).
function Logic.Sequence(expected, options)
  if type(expected) ~= "table" or #expected == 0 then
    error("Logic.Sequence expects a list of the inputs in order", 2)
  end
  options = options or {}
  local sequence = newValue(false, "Sequence")
  sequence.Progress = 0
  local lastInput = -math.huge

  function sequence:Input(value)
    if self.Value then
      return "complete"
    end
    if options.Timeout ~= nil and self.Progress > 0 and Time.elapsedTime - lastInput > options.Timeout then
      self.Progress = 0
    end
    lastInput = Time.elapsedTime
    local wanted = expected[self.Progress + 1]
    if value == wanted then
      self.Progress = self.Progress + 1
      if self.Progress == #expected then
        self:_set(true)
        if options.OnComplete then options.OnComplete(self) end
        return "complete"
      end
      return "progress"
    end
    if options.OnWrong then options.OnWrong(wanted, value, self) end
    -- A wrong input starts over (and may itself be the first of the sequence).
    self.Progress = value == expected[1] and 1 or 0
    return "wrong"
  end

  function sequence:Reset()
    self.Progress = 0
    self:_set(false)
  end

  return sequence
end

-- Moves an object between its closed place and closed + Offset (a door, a gate, a bridge) over Duration seconds.
-- An anchored Part physics moves is driven by velocity (what stands on it is carried); anything else is placed.
-- mover:Set(open), Open(), Close(); Value is true while open (or opening).
function Logic.Mover(object, options)
  if type(object) ~= "table" or object.Id == nil then
    error("Logic.Mover expects a scene object", 2)
  end
  options = options or {}
  local offset = options.Offset or Vector3.new(0, 4, 0)
  local duration = options.Duration or 1
  -- A copy: Transform.Position is a live view of where the object is now.
  local start = object.Transform.Position
  local closed = Vector3.new(start.x, start.y, start.z)
  local open = closed + offset
  local mover = newValue(false, object.Name)
  local progress = 0 -- 0 closed, 1 open
  local connection
  local byVelocity = object.Anchored and object.CanCollide

  local function place(t, dt)
    local target = Vector3.lerp(closed, open, t)
    if byVelocity and dt > 0 then
      local ok = pcall(function()
        object.LinearVelocity = (target - object.Transform.Position) / dt
      end)
      if not ok then
        byVelocity = false
      end
    end
    if not byVelocity then
      object.Transform.Position = target
    end
  end

  local function tick(dt)
    if not object.Exists then
      connection:Disconnect()
      connection = nil
      return
    end
    local goal = mover.Value and 1 or 0
    local step = duration <= 0 and 1 or dt / duration
    if progress < goal then
      progress = math.min(goal, progress + step)
    elseif progress > goal then
      progress = math.max(goal, progress - step)
    end
    place(progress, dt)
    if progress == goal then
      if byVelocity then
        object.LinearVelocity = Vector3.new(0, 0, 0)
        object.Transform.Position = Vector3.lerp(closed, open, progress)
      end
      connection:Disconnect()
      connection = nil
    end
  end

  function mover:Set(isOpen)
    self:_set(isOpen == true)
    if connection == nil then
      connection = Time.OnUpdate(tick)
    end
  end

  function mover:Open() self:Set(true) end
  function mover:Close() self:Set(false) end

  function mover:GetProgress()
    return progress
  end

  return mover
end

return Logic
