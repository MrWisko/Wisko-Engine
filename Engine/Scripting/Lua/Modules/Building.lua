-- Wisko.Building: optional placement previews with grid, surface and rotation snapping, collision checks, and helpers to
-- remove, duplicate, align, describe and rebuild what was built.
--
--   local Building = require("Wisko.Building")
--   local placer = Building.Placer({ Grid = 1, RotationStep = 90, Ignore = { player } })
--   placer:Begin({ Type = "Block", Size = Vector3.new(4, 1, 4), Color = "#C8A060", Name = "Floor" })
--   -- every frame: placer:Update()            -- follows the middle of the screen (or placer:SetTarget(point))
--   -- R: placer:Rotate(1)   Left mouse: placer:Confirm()   Escape: placer:Cancel()
--
-- The preview is a Play-only Part (no collisions, tagged "BuildPreview", recoloured green or red as placement is valid
-- or not); the scene cannot be saved while playing, so previews can never become saved objects. Confirm checks the
-- place again (Physics.OverlapBox) and creates the real Part(s) (tagged "Built"); CanPlace(info) adds the game's own
-- rules. A blueprint may be a list of pieces placed together (Offset each, relative to the first). Ordinary Lua on
-- Wisko's public API: copy it to Assets/Scripts/Wisko/Building.lua to change it.

local Signal = require("Wisko.Signal")

local Building = {}

local Placer = {}
Placer.__index = Placer

local PREVIEW_TAG, BUILT_TAG = "BuildPreview", "Built"

local function snap(value, step)
  if step == nil or step <= 0 then
    return value
  end
  return math.floor(value / step + 0.5) * step
end

-- Options: Grid (studs; 0: no grid snapping, default 1), RotationStep (degrees, 90), MaxDistance (from the camera, 40),
-- SurfaceSnap (true: rest on what the aim hits), Ignore (objects the aim and the collision check skip), ValidColor,
-- InvalidColor, CanPlace(info) -> ok, reason (info: { Position, Rotation, Blueprint, Hit }), Tag (BUILT tag, "Built").
function Building.Placer(options)
  options = options or {}
  return setmetatable({
    Grid = options.Grid == nil and 1 or options.Grid,
    RotationStep = options.RotationStep or 90,
    MaxDistance = options.MaxDistance or 40,
    SurfaceSnap = options.SurfaceSnap ~= false,
    Ignore = options.Ignore or {},
    ValidColor = options.ValidColor or "#4CD964",
    InvalidColor = options.InvalidColor or "#FF453A",
    CanPlace = options.CanPlace,
    Tag = options.Tag or BUILT_TAG,
    Active = false,
    Valid = false,
    Reason = nil,
    Yaw = 0,
    Position = nil,
    _previews = {},
    _placed = Signal.new(),
    _cancelled = Signal.new(),
  }, Placer)
end

local function pieces(blueprint)
  if type(blueprint) ~= "table" then
    error("a blueprint is { Type, Size, Color, Name, Tags, Attributes, Offset } or a list of them", 3)
  end
  if blueprint.Type ~= nil or blueprint.Size ~= nil then
    return { blueprint }
  end
  if #blueprint == 0 then
    error("an empty blueprint", 3)
  end
  return blueprint
end

-- Starts placing a blueprint: the previews appear (where the last aim was).
function Placer:Begin(blueprint)
  self:Cancel()
  self.Blueprint = pieces(blueprint)
  self.Active = true
  for i, piece in ipairs(self.Blueprint) do
    self._previews[i] = Scene.Create(piece.Type or "Block", {
      Name = (piece.Name or "Piece") .. "Preview", Anchored = true, CanCollide = false, Size = piece.Size or Vector3.new(4, 4, 4),
      Color = self.ValidColor, Position = Vector3.new(0, -1000, 0), Tags = { PREVIEW_TAG } })
  end
end

-- Turns the preview by steps of RotationStep (negative: the other way).
function Placer:Rotate(steps)
  self.Yaw = (self.Yaw + (steps or 1) * self.RotationStep) % 360
  if self._lastPoint ~= nil then
    -- Placed again from the same aim (the snapped position already includes the half height).
    self:SetTarget(self._lastPoint, self._lastHit)
  end
end

local function rotateY(v, degrees)
  local r = math.rad(degrees)
  local c, s = math.cos(r), math.sin(r)
  return Vector3.new(v.x * c + v.z * s, v.y, -v.x * s + v.z * c)
end

-- Places the preview at a point (snapped to the grid; with hit, resting on the surface hit). Returns valid, reason.
function Placer:SetTarget(point, hit)
  if not self.Active then
    return false, "nothing is being placed"
  end
  self._lastHit = hit
  self._lastPoint = Vector3.new(point.x, point.y, point.z)
  local first = self.Blueprint[1]
  local size = first.Size or Vector3.new(4, 4, 4)
  local turned = (self.Yaw % 180) ~= 0
  local sx, sz = turned and size.z or size.x, turned and size.x or size.z
  -- Grid: a piece's corner sits on grid lines (its centre snaps by half its size when that is odd).
  local function grid(value, extent)
    if self.Grid <= 0 then
      return value
    end
    local offset = (extent / self.Grid) % 2 == 1 and self.Grid / 2 or 0
    return snap(value - offset, self.Grid) + offset
  end
  local x, z = grid(point.x, sx), grid(point.z, sz)
  local y = point.y + size.y / 2
  if hit ~= nil and self.SurfaceSnap and hit.Normal ~= nil and hit.Normal.y > 0.7 then
    y = hit.Point.y + size.y / 2
  end
  self.Position = Vector3.new(x, y, z)
  self.Rotation = Vector3.new(0, self.Yaw, 0)
  for i, piece in ipairs(self.Blueprint) do
    local preview = self._previews[i]
    if preview ~= nil and preview.Exists then
      preview.Transform.Position = self.Position + rotateY(piece.Offset or Vector3.new(0, 0, 0), self.Yaw)
      preview.Transform.Rotation = self.Rotation
    end
  end
  return self:Validate()
end

-- Aims from the camera through the middle of the screen (or a screen point x, y) and places the preview there.
function Placer:Update(x, y)
  if not self.Active then
    return false
  end
  local origin, direction = Camera.ScreenPointToRay(x or Camera.ViewportWidth / 2, y or Camera.ViewportHeight / 2)
  local ignore = { table.unpack(self.Ignore) }
  for _, preview in ipairs(self._previews) do
    ignore[#ignore + 1] = preview
  end
  local hit = Physics.Raycast(origin, direction, self.MaxDistance, { Ignore = ignore, IgnoreTriggers = true })
  if hit == nil then
    self.Valid, self.Reason = false, "aim at the ground or something to build on"
    self:_paint()
    return false, self.Reason
  end
  return self:SetTarget(hit.Point, hit)
end

-- Whether the current place is free (and the game's CanPlace agrees): valid, reason.
function Placer:Validate()
  if not self.Active or self.Position == nil then
    self.Valid, self.Reason = false, "nothing is being placed"
    return false, self.Reason
  end
  local ignore = { table.unpack(self.Ignore) }
  for _, preview in ipairs(self._previews) do
    ignore[#ignore + 1] = preview
  end
  self.Valid, self.Reason = true, nil
  for i, piece in ipairs(self.Blueprint) do
    local size = piece.Size or Vector3.new(4, 4, 4)
    local center = self.Position + rotateY(piece.Offset or Vector3.new(0, 0, 0), self.Yaw)
    -- A little smaller than the piece: resting on the ground or touching a neighbour is allowed.
    local check = Vector3.new(math.max(0.05, size.x - 0.1), math.max(0.05, size.y - 0.1), math.max(0.05, size.z - 0.1))
    local blocking = Physics.OverlapBox(center, check, self.Rotation, { Ignore = ignore, IgnoreTriggers = true, IgnoreEnvironment = true })
    if #blocking > 0 then
      self.Valid, self.Reason = false, "blocked by " .. blocking[1].Name
      break
    end
  end
  if self.Valid and self.CanPlace ~= nil then
    local ok, reason = self.CanPlace({ Position = self.Position, Rotation = self.Rotation, Blueprint = self.Blueprint, Hit = self._lastHit })
    if not ok then
      self.Valid, self.Reason = false, reason or "not allowed here"
    end
  end
  self:_paint()
  return self.Valid, self.Reason
end

function Placer:_paint()
  for _, preview in ipairs(self._previews) do
    if preview.Exists then
      preview.Color = self.Valid and self.ValidColor or self.InvalidColor
    end
  end
end

-- Builds it: the real Part(s) at the preview's place. Returns the list of new objects, or nil and why.
function Placer:Confirm()
  local valid, reason = self:Validate()
  if not valid then
    return nil, reason
  end
  local made = {}
  for i, piece in ipairs(self.Blueprint) do
    local tags = { self.Tag }
    for _, tag in ipairs(piece.Tags or {}) do
      tags[#tags + 1] = tag
    end
    made[i] = Scene.Create(piece.Type or "Block", {
      Name = piece.Name or "Piece", Anchored = piece.Anchored ~= false, Size = piece.Size or Vector3.new(4, 4, 4),
      Color = piece.Color or "#C8A060", Position = self.Position + rotateY(piece.Offset or Vector3.new(0, 0, 0), self.Yaw),
      Rotation = self.Rotation, Tags = tags, Attributes = piece.Attributes })
  end
  self._placed:Fire(made, self)
  Events.Emit("Built", { count = #made, position = self.Position })
  return made
end

-- Stops placing: the previews go.
function Placer:Cancel()
  local wasActive = self.Active
  for _, preview in ipairs(self._previews) do
    if preview.Exists then
      preview:Destroy()
    end
  end
  self._previews = {}
  self.Active = false
  self.Position = nil
  if wasActive then
    self._cancelled:Fire(self)
  end
end

function Placer:OnPlaced(f) return self._placed:Connect(f) end
function Placer:OnCancelled(f) return self._cancelled:Connect(f) end

-- Removes something built (only objects with the Built tag unless force). Returns true, or false and why.
function Building.Remove(object, force)
  if object == nil or not object.Exists then
    return false, "it no longer exists"
  end
  if not force and not object:HasTag(BUILT_TAG) then
    return false, object.Name .. " was not built (pass force = true to remove it anyway)"
  end
  object:Destroy()
  return true
end

-- A copy of an object, moved by offset (a vector).
function Building.Duplicate(object, offset, name)
  local copy = object:Clone(name)
  if offset ~= nil then
    copy.Transform.Position = object.Transform.Position + offset
  end
  return copy
end

-- Lines objects up along an axis ("x", "y" or "z") at the first one's value ("Min" sides, "Center" or "Max" sides).
function Building.Align(objects, axis, mode)
  if #objects < 2 then
    return
  end
  mode = mode or "Center"
  local function edge(object)
    local low, high = object:GetBounds()
    if mode == "Min" then return low[axis] end
    if mode == "Max" then return high[axis] end
    return (low[axis] + high[axis]) / 2
  end
  local target = edge(objects[1])
  for i = 2, #objects do
    local p = objects[i].Transform.Position
    local delta = target - edge(objects[i])
    objects[i].Transform.Position = Vector3.new(p.x + (axis == "x" and delta or 0), p.y + (axis == "y" and delta or 0), p.z + (axis == "z" and delta or 0))
  end
end

-- Plain data for GameSave: what each built object is and where (rebuilt with Building.Rebuild).
function Building.Describe(objects)
  local list = {}
  for _, object in ipairs(objects or Scene.FindAllByTag(BUILT_TAG)) do
    if object.Exists and not object:HasTag(PREVIEW_TAG) then
      local t = object.Transform
      list[#list + 1] = { Type = object.Type, Name = object.Name, Color = object.Color, Position = t.Position + Vector3.new(0, 0, 0),
        Rotation = t.Rotation + Vector3.new(0, 0, 0), Size = t.Scale + Vector3.new(0, 0, 0), Anchored = object.Anchored }
    end
  end
  return list
end

function Building.Rebuild(list)
  local made = {}
  for i, entry in ipairs(list or {}) do
    made[i] = Scene.Create(entry.Type or "Block", { Name = entry.Name, Color = entry.Color, Position = entry.Position, Rotation = entry.Rotation,
      Size = entry.Size, Anchored = entry.Anchored ~= false, Tags = { BUILT_TAG } })
  end
  return made
end

return Building
