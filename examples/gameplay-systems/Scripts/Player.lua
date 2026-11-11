-- Player.lua (on the invisible "PlayerBody"): the showcase's player. A Block body moved by physics carries the
-- RobotExpressive model ("PlayerRobot"), which shows the animation (as in the Animation demo). On top of that:
--   * E uses the nearest interactable thing (any script registers one in Shared.interactions).
--   * Q equips the crafted axe in the robot's right hand (Wisko.Equipment on the "Palm2.R" bone).
--   * Running makes footstep noises (Perception.EmitNoise) the guard can hear.
--   * B builds (Wisko.Building): a floor, a wall, then off; R turns the piece, the left mouse button places it.
-- WASD walk, Shift run, Space jump; the mouse turns the camera (Tab frees / locks the mouse).

local Equipment = require("Wisko.Equipment")
local Building = require("Wisko.Building")

local WALK, RUN, JUMP = 8, 16, 18
local SENSITIVITY = 0.15
local FADE = 0.2
local WALK_CLIP_SPEED, RUN_CLIP_SPEED = 5.5, 12

local robot, gear, axe, placer
local yaw, pitch, distance = 270, 14, 15
local cameraPosition
local jumpedAt
local busyUntil = 0            -- a one-shot clip (punch) is playing
local stepClock = 0
local buildIndex = 0
local current                  -- the interactable in reach

local PIECES = {
  { Name = "Floor", Type = "Block", Size = Vector3.new(4, 0.5, 4), Color = "#B98A55", Cost = 1 },
  { Name = "Wall", Type = "Block", Size = Vector3.new(4, 3, 0.5), Color = "#9C6B3E", Cost = 1 },
}

Shared.interactions = Shared.interactions or {}

local function flat(v)
  local f = Vector3.new(v.x, 0, v.z)
  if Vector3.magnitude(f) < 1e-4 then
    return Vector3.new(0, 0, -1)
  end
  return Vector3.normalize(f)
end

-- The axe: a handle with a head (two Parts, the head attached to the handle), made when first equipped.
local function makeAxe()
  local handle = Scene.Create("Block", { Name = "AxeHandle", Size = Vector3.new(0.25, 2, 0.25), Color = "#6B4A2B", CanCollide = false })
  local head = Scene.Create("Block", { Name = "AxeHead", Size = Vector3.new(0.9, 0.55, 0.2), Color = "#B8C0CC", CanCollide = false })
  head:SetParent(handle, Vector3.new(0.35, 0.8, 0))
  return handle
end

local function setEquipped(on)
  if on and Shared.game.inventory.Count("Axe") < 1 then
    Shared.game.Hint("You have no axe: craft one at the workbench", 2)
    return
  end
  if on then
    axe = axe or makeAxe()
    gear:Equip("RightHand", axe)
  elseif gear:Get("RightHand") ~= nil then
    local item = gear:Unequip("RightHand")
    -- Put away: hidden on the back instead of dropped.
    item:SetParent(Shared.player, Vector3.new(0, 0.6, 1.1), Vector3.new(0, 0, 70))
  end
  Shared.equipped = on
end

-- The interactable in reach: the nearest object with the Interactable tag that a script registered.
local function nearestInteractable()
  local best, bestDistance
  local here = self.Transform.Position
  for _, object in ipairs(Physics.OverlapSphere(here, 6, { OnlyTags = "Interactable", IgnoreEnvironment = true })) do
    local entry = Shared.interactions[object.Id]
    if entry ~= nil then
      local d = Vector3.distance(here, object.Transform.Position)
      if bestDistance == nil or d < bestDistance then
        best, bestDistance = object, d
      end
    end
  end
  return best
end

local function promptOf(object)
  local entry = Shared.interactions[object.Id]
  local prompt = entry.Prompt
  if type(prompt) == "function" then
    prompt = prompt(self)
  end
  return prompt
end

-- B: the next piece to build, then off.
local function cycleBuild()
  buildIndex = buildIndex % (#PIECES + 1) + 1
  if buildIndex > #PIECES then
    buildIndex = 0
    placer:Cancel()
    Shared.game.Hint("Building off", 1.5)
    return
  end
  local piece = PIECES[buildIndex]
  placer:Begin(piece)
  Shared.game.Hint("Building: " .. piece.Name .. " (" .. piece.Cost .. " plank). R turns, left click places, B next", 3)
end

local function place()
  local piece = PIECES[buildIndex]
  if Shared.game.inventory.Count("Plank") < piece.Cost then
    Shared.game.Hint("You need " .. piece.Cost .. " plank (craft planks from wood at the workbench)", 2.5)
    return
  end
  local made, reason = placer:Confirm()
  if made == nil then
    Shared.game.Hint("Cannot build here: " .. tostring(reason), 2)
    return
  end
  Shared.game.inventory.Remove("Plank", piece.Cost)
  Shared.game.quests:Progress("builder", "place")
end

function Start()
  Shared.player = self
  self.RotationLocked = true
  self.Visible = false
  self:AddTag("Player")
  robot = Scene.Find("PlayerRobot")
  robot:SetParent(self, Vector3.new(0, -2.25, 0), Vector3.new(0, 180, 0))
  robot:PlayAnimation("Idle")
  gear = Equipment.new(robot, { Owner = self, Slots = {
    RightHand = { Bone = "Palm2.R", Offset = Vector3.new(0, 0.05, 0), Rotation = Vector3.new(0, 0, 90) } } })
  placer = Building.Placer({ Grid = 2, RotationStep = 90, Ignore = { self, robot }, MaxDistance = 30,
    CanPlace = function(info)
      -- Not on the roads of the village plaza, not too far from the player.
      if Vector3.distance(info.Position, self.Transform.Position) > 24 then
        return false, "too far away"
      end
      return true
    end })
  robot:OnAnimationFinished(function(name)
    if name == "Punch" then
      busyUntil = 0
    end
  end)
  Events.On("GameLoaded", function(info)
    setEquipped(info.equipped)
  end)
  Events.On("Caught", function()
    robot:CrossFadeAnimation("No", 0.1, { Loop = false })
    busyUntil = Time.elapsedTime + 1.2
  end)
  Input.SetMouseLocked(true)
end

-- Called by the things that are used with a swing (trees, rocks): plays the punch.
function Swing()
  busyUntil = Time.elapsedTime + 0.6
  robot:CrossFadeAnimation("Punch", 0.08, { Loop = false, Restart = true })
end

function Update(dt)
  local cinematic = Shared.cinematic == true
  if Input.IsKeyPressed("Tab") then
    Input.SetMouseLocked(not Input.MouseLocked)
  end
  if Input.MouseLocked and not cinematic then
    yaw = (yaw + Input.mouseDeltaX * SENSITIVITY) % 360
    pitch = math.clamp(pitch + Input.mouseDeltaY * SENSITIVITY, -30, 70)
  end
  distance = math.clamp(distance - Input.scrollDelta * 2, 6, 40)

  -- Interaction.
  current = nearestInteractable()
  Shared.game.Prompt(current and promptOf(current) or "")
  if not cinematic and current ~= nil and Input.IsKeyPressed("E") then
    Shared.interactions[current.Id].Use(self)
  end
  if not cinematic and Input.IsKeyPressed("Q") then
    setEquipped(not Shared.equipped)
  end

  -- Building.
  if not cinematic and Input.IsKeyPressed("B") then
    cycleBuild()
  end
  if buildIndex > 0 then
    placer:Update()
    if Input.IsKeyPressed("R") then placer:Rotate(1) end
    if Input.IsMouseButtonPressed("Left") then place() end
  end

  -- Movement (as the Animation demo's character).
  local grounded = self:IsGrounded()
  if jumpedAt ~= nil and grounded and Time.elapsedTime - jumpedAt > 0.25 then
    jumpedAt = nil
  end
  local busy = Time.elapsedTime < busyUntil
  local forward, right = flat(Camera.Forward), flat(Camera.Right)
  local move = forward * Input.GetAxis("S", "W") + right * Input.GetAxis("A", "D")
  if cinematic or busy then
    move = Vector3.new(0, 0, 0)
  elseif Vector3.magnitude(move) > 0 then
    move = Vector3.normalize(move)
  end
  local running = Input.IsKeyDown("LeftShift")
  local target = move * (running and RUN or WALK)
  local v = self.LinearVelocity
  local blend = math.min(1, (grounded and 12 or 3) * dt)
  local vy = v.y
  if grounded and jumpedAt == nil and not busy and not cinematic and Input.IsKeyPressed("Space") then
    vy = JUMP
    jumpedAt = Time.elapsedTime
    robot:CrossFadeAnimation("Jump", 0.1, { Loop = false })
  end
  self.LinearVelocity = Vector3.new(v.x + (target.x - v.x) * blend, vy, v.z + (target.z - v.z) * blend)
  if Vector3.magnitude(move) > 0 then
    self.Transform.Rotation = Vector3.new(0, math.deg(math.atan(-move.x, -move.z)), 0)
  end

  local speed = math.sqrt(v.x * v.x + v.z * v.z)
  if jumpedAt == nil and not busy then
    if speed < 0.6 then
      robot:CrossFadeAnimation("Idle", FADE)
    elseif speed < (WALK + RUN) / 2 then
      robot:CrossFadeAnimation("Walking", FADE, { Speed = speed / WALK_CLIP_SPEED })
    else
      robot:CrossFadeAnimation("Running", FADE, { Speed = speed / RUN_CLIP_SPEED })
    end
  end

  -- Running on the ground is loud: footsteps the guard can hear (developer-defined noise, not acoustics).
  stepClock = stepClock + dt
  if grounded and speed > (WALK + RUN) / 2 and stepClock > 0.35 then
    stepClock = 0
    Perception.EmitNoise(self.Transform.Position, { Radius = 26, Kind = "Footstep", Source = self })
  end

  -- Fell off the world (into the gap below the sky island): back to the last checkpoint.
  if self.Transform.Position.y < -20 and Shared.checkpoints then
    Shared.checkpoints:Respawn(self)
    Shared.game.Hint("Back to the last checkpoint", 2)
  end
end

function LateUpdate(dt)
  if Shared.cinematic then
    cameraPosition = nil
    return
  end
  local pivot = self.Transform.Position + Vector3.new(0, 1.5, 0)
  local y, p = math.rad(yaw), math.rad(pitch)
  local look = Vector3.new(math.cos(p) * math.cos(y), -math.sin(p), math.cos(p) * math.sin(y))
  local desired = pivot - look * distance
  local hit = Physics.Raycast(pivot, desired - pivot, Vector3.distance(pivot, desired),
    { Ignore = { self, robot }, IgnoreTriggers = true, IgnoreTags = { "BuildPreview" } })
  if hit then
    desired = pivot + Vector3.normalize(desired - pivot) * math.max(1, hit.Distance - 0.5)
  end
  cameraPosition = cameraPosition and Vector3.lerp(cameraPosition, desired, 1 - math.exp(-12 * dt)) or desired
  Camera.Position = cameraPosition
  Camera.LookAt(pivot)
end

return { Swing = Swing, SetEquipped = setEquipped }
