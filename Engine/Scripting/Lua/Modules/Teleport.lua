-- Wisko.Teleport: optional teleport pads, linked portals and checkpoints, on object:Teleport and trigger connections.
--
--   local Teleport = require("Wisko.Teleport")
--   local a = Teleport.Pad(Scene.Find("PadA"), { Filter = function(o) return o:HasTag("Player") end })
--   local b = Teleport.Pad(Scene.Find("PadB"), { Filter = ... })
--   Teleport.Link(a, b)                 -- two-way: step on A, arrive at B (and back)
--   a:OnTeleported(function(object, from, to) print(object.Name .. " used the portal") end)
--
--   local checkpoints = Teleport.Checkpoints({ Spawn = Vector3.new(0, 3, 0) })
--   checkpoints:Watch(Scene.Find("Flag1"))       -- touching it makes it the respawn point (of whoever touched it)
--   checkpoints:Respawn(player)                  -- back to the last checkpoint
--
-- A traveller arriving on a pad is not sent back by it until it has left that pad's trigger (no ping-pong), and each
-- pad has a cooldown per traveller. Pads are trigger Parts (IsTrigger = true). Ordinary Lua on Wisko's public API.

local Signal = require("Wisko.Signal")

local Teleport = {}

local Pad = {}
Pad.__index = Pad

local function checkObject(object, usage)
  if type(object) ~= "table" or object.Id == nil then
    error(usage .. " expects a scene object, got a " .. type(object) .. " value", 3)
  end
end

-- A pad (a trigger Part). Options: Destination (a pad, a scene object or a point; set later with Link or SetDestination),
-- Offset (where on the destination one arrives, default 0, 3, 0), Cooldown (seconds per traveller, 1), KeepVelocity
-- (false), FaceDestination (true: arrive turned like the destination object), Filter(object) -> bool (who may use it),
-- Enabled (true).
function Teleport.Pad(object, options)
  checkObject(object, "Teleport.Pad")
  options = options or {}
  local pad = setmetatable({
    Object = object,
    Destination = options.Destination,
    Offset = options.Offset or Vector3.new(0, 3, 0),
    Cooldown = options.Cooldown or 1,
    KeepVelocity = options.KeepVelocity == true,
    FaceDestination = options.FaceDestination ~= false,
    Filter = options.Filter,
    Enabled = options.Enabled ~= false,
    _arrived = {},   -- id -> elapsed time it arrived here (cleared when it leaves the trigger)
    _lastUse = {},   -- id -> elapsed time of the last teleport from here
    _teleported = Signal.new(),
  }, Pad)
  pad._enter = object:OnTriggerEntered(function(other)
    if other ~= nil then
      pad:_entered(other)
    end
  end)
  pad._exit = object:OnTriggerExited(function(other)
    if other ~= nil then
      pad._arrived[other.Id] = nil
    end
  end)
  return pad
end

function Pad:SetDestination(destination)
  self.Destination = destination
end

-- f(object, fromPad, toPositon) after each teleport from this pad.
function Pad:OnTeleported(f)
  return self._teleported:Connect(f)
end

-- Where a traveller arrives: the destination (pad, object or point) plus Offset, and the rotation to arrive with.
function Pad:_target()
  local destination = self.Destination
  if destination == nil then
    return nil
  end
  if getmetatable(destination) == Pad then
    destination = destination.Object
  end
  if type(destination) == "table" and destination.Id ~= nil then
    if not destination.Exists then
      return nil
    end
    local rotation = self.FaceDestination and destination.Transform.Rotation or nil
    local offset = destination:VectorToWorld(self.Offset)
    return destination.Transform.Position + offset, rotation and Vector3.new(0, rotation.y, 0) or nil
  end
  return destination + self.Offset, nil
end

function Pad:_entered(other)
  local id = other.Id
  local arrived = self._arrived[id]
  if not self.Enabled or arrived ~= nil and Time.elapsedTime - arrived < 1.5 then
    return
  end
  if self.Filter ~= nil and not self.Filter(other) then
    return
  end
  local last = self._lastUse[id]
  if last ~= nil and Time.elapsedTime - last < self.Cooldown then
    return
  end
  self:Send(other)
end

-- Sends an object to the destination now (the filter and cooldown are not asked). Returns true, or false and why.
function Pad:Send(other)
  local position, rotation = self:_target()
  if position == nil then
    return false, "this pad has no destination"
  end
  local id = other.Id
  self._lastUse[id] = Time.elapsedTime
  -- Arriving on a linked pad: it ignores the traveller until it has stepped off.
  local destination = self.Destination
  if getmetatable(destination) == Pad then
    destination._arrived[id] = Time.elapsedTime
    destination._lastUse[id] = Time.elapsedTime
  end
  local ok, problem = pcall(function()
    other:Teleport(position, rotation, { KeepVelocity = self.KeepVelocity })
  end)
  if not ok then
    return false, problem
  end
  self._teleported:Fire(other, self, position)
  Events.Emit("Teleported", { object = other, from = self.Object, position = position })
  return true
end

function Pad:Destroy()
  self._enter:Disconnect()
  self._exit:Disconnect()
end

-- Links two pads both ways.
function Teleport.Link(a, b)
  if getmetatable(a) ~= Pad or getmetatable(b) ~= Pad then
    error("Teleport.Link expects two pads (Teleport.Pad)", 2)
  end
  a.Destination = b
  b.Destination = a
end

-- Respawn points per object. Options: Spawn (where everyone starts, a point), Offset (above a checkpoint object, 0, 3, 0),
-- Filter(object) -> bool (who checkpoints count for), KeepRotation (false).
local Checkpoints = {}
Checkpoints.__index = Checkpoints

function Teleport.Checkpoints(options)
  options = options or {}
  return setmetatable({
    Spawn = options.Spawn or Vector3.new(0, 4, 0),
    Offset = options.Offset or Vector3.new(0, 3, 0),
    Filter = options.Filter,
    _points = {},       -- id -> { position, name, order }
    _connections = {},
    _activated = Signal.new(),
    _respawned = Signal.new(),
  }, Checkpoints)
end

-- Touching this trigger Part activates it for whoever touched it (order: a later checkpoint never goes back to an
-- earlier one unless AllowBacktrack).
function Checkpoints:Watch(object, order)
  checkObject(object, "checkpoints:Watch")
  order = order or (#self._connections + 1)
  self._connections[#self._connections + 1] = object:OnTriggerEntered(function(other)
    if other ~= nil and (self.Filter == nil or self.Filter(other)) then
      self:Activate(object, other, order)
    end
  end)
end

-- Makes checkpoint (an object or a point) the respawn point of who. Returns true when it was new for them.
function Checkpoints:Activate(checkpoint, who, order)
  local current = self._points[who.Id]
  order = order or 0
  if current ~= nil and not self.AllowBacktrack and order < current.order then
    return false
  end
  local position, name
  if type(checkpoint) == "table" and checkpoint.Id ~= nil then
    position = checkpoint.Transform.Position + self.Offset
    name = checkpoint.Name
  else
    position = checkpoint
    name = "point"
  end
  if current ~= nil and current.name == name and Vector3.distance(current.position, position) < 1e-3 then
    return false
  end
  self._points[who.Id] = { position = Vector3.new(position.x, position.y, position.z), name = name, order = order }
  self._activated:Fire(who, name, position)
  Events.Emit("CheckpointActivated", { object = who, checkpoint = name })
  return true
end

-- Where who respawns now.
function Checkpoints:GetRespawn(who)
  local point = self._points[who.Id]
  return point and point.position or self.Spawn, point and point.name or nil
end

-- Back to the respawn point, stopped.
function Checkpoints:Respawn(who)
  local position = self:GetRespawn(who)
  who:Teleport(position)
  self._respawned:Fire(who, position)
  return position
end

function Checkpoints:OnActivated(f) return self._activated:Connect(f) end
function Checkpoints:OnRespawned(f) return self._respawned:Connect(f) end

-- Plain data for GameSave: { [object name] = { x, y, z, name, order } } (by name: Ids change between sessions).
function Checkpoints:Save(objects)
  local data = {}
  for _, object in ipairs(objects) do
    local point = self._points[object.Id]
    if point ~= nil then
      data[object.Name] = { point.position.x, point.position.y, point.position.z, point.name, point.order }
    end
  end
  return data
end

function Checkpoints:Load(data, objects)
  for _, object in ipairs(objects) do
    local saved = data[object.Name]
    if saved ~= nil then
      self._points[object.Id] = { position = Vector3.new(saved[1], saved[2], saved[3]), name = saved[4], order = saved[5] or 0 }
    end
  end
end

function Checkpoints:Destroy()
  for _, connection in ipairs(self._connections) do
    connection:Disconnect()
  end
end

return Teleport
