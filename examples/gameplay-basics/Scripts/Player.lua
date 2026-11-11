-- Player.lua: a third-person character. WASD moves relative to the camera, LeftShift sprints, Space jumps, the mouse
-- turns the camera (Tab frees / locks the mouse, the wheel zooms).
-- It steps aside while Flight.lua flies it or while it sits in a vehicle (the camera then chases the vehicle), it
-- walks slower while a status effect lowers its "SpeedMultiplier" attribute, and a knockback ("KnockbackUntil")
-- briefly takes the controls away so the push is not cancelled by walking.

local WALK, SPRINT, JUMP = 12, 22, 17
local GROUND_ACCEL, AIR_ACCEL = 14, 3
local SENSITIVITY = 0.15
local MIN_PITCH, MAX_PITCH = -35, 70

local yaw, pitch, distance = 270, 15, 16 -- camera yaw 270 looks along -Z
local cameraPosition
local spawnPoint

local function flat(v)
  local f = Vector3.new(v.x, 0, v.z)
  if Vector3.magnitude(f) < 1e-4 then
    return Vector3.new(0, 0, -1)
  end
  return Vector3.normalize(f)
end

local function flying()
  local flight = self:GetScript("Flight")
  return flight ~= nil and flight.IsFlying()
end

function Start()
  self.RotationLocked = true -- physics never tips the character over
  local p = self.Transform.Position
  spawnPoint = Vector3.new(p.x, p.y, p.z)
  Shared.player = self
  Input.SetMouseLocked(true)

  -- Status.lua says when Health reaches 0: back to the start after 2 seconds, healed.
  Events.On("Died", function(info)
    if info.object == self then
      Events.Emit("Hint", "You fainted...")
      Timer.After(2, function()
        if Shared.vehicle ~= nil then
          Shared.vehicle:GetScript("CarSeat").Interact(self) -- get out first
        end
        self.Transform.Position = spawnPoint
        self.LinearVelocity = Vector3.new(0, 0, 0)
        self:SetAttribute("Health", 100)
        Events.Emit("Hint", "Respawned")
      end)
    end
  end)
end

function Update(dt)
  if Input.IsKeyPressed("Tab") then
    Input.SetMouseLocked(not Input.MouseLocked)
  end
  if Input.MouseLocked then
    yaw = (yaw + Input.mouseDeltaX * SENSITIVITY) % 360
    pitch = math.clamp(pitch + Input.mouseDeltaY * SENSITIVITY, MIN_PITCH, MAX_PITCH)
  end
  distance = math.clamp(distance - Input.scrollDelta * 2, 6, 40)

  if Shared.vehicle ~= nil or flying() then
    return -- the car or Flight.lua is in charge
  end
  if Time.elapsedTime < (self:GetAttribute("KnockbackUntil") or 0) then
    return -- being knocked back: no steering for a moment
  end

  -- Camera-relative movement: W goes where the camera looks, flattened onto the ground.
  local forward, right = flat(Camera.Forward), flat(Camera.Right)
  local move = forward * Input.GetAxis("S", "W") + right * Input.GetAxis("A", "D")
  if Vector3.magnitude(move) > 0 then
    move = Vector3.normalize(move)
  end
  local speed = (Input.IsKeyDown("LeftShift") and SPRINT or WALK) * (self:GetAttribute("SpeedMultiplier") or 1)
  local target = move * speed

  local grounded = self:IsGrounded()
  local v = self.LinearVelocity
  local blend = math.min(1, (grounded and GROUND_ACCEL or AIR_ACCEL) * dt) -- frame-rate independent
  local vy = v.y
  if grounded and Input.IsKeyPressed("Space") then
    vy = JUMP
  end
  self.LinearVelocity = Vector3.new(v.x + (target.x - v.x) * blend, vy, v.z + (target.z - v.z) * blend)

  if Vector3.magnitude(move) > 0 then
    -- Face the way we walk: the Part's Forward (local -Z) along the movement.
    self.Transform.Rotation = Vector3.new(0, math.deg(math.atan(-move.x, -move.z)), 0)
  end
end

function LateUpdate(dt)
  local desired, pivot
  local vehicle = Shared.vehicle
  if vehicle ~= nil and vehicle.Exists then
    -- Chase camera: behind and above the vehicle, looking where it goes.
    pivot = vehicle.Transform.Position + Vector3.new(0, 1.5, 0)
    desired = pivot - flat(vehicle.Forward) * (distance + 4) + Vector3.new(0, 5, 0)
  else
    pivot = self.Transform.Position + Vector3.new(0, 2, 0)
    local y, p = math.rad(yaw), math.rad(pitch)
    local look = Vector3.new(math.cos(p) * math.cos(y), -math.sin(p), math.cos(p) * math.sin(y))
    desired = pivot - look * distance
  end

  -- Keep the camera out of walls: pull it in front of whatever is between it and the target.
  local hit = Physics.Raycast(pivot, desired - pivot, Vector3.distance(pivot, desired),
    { Ignore = { self, vehicle }, IgnoreTriggers = true, IgnoreTags = { "Drop" } })
  if hit then
    desired = pivot + Vector3.normalize(desired - pivot) * math.max(1, hit.Distance - 0.5)
  end

  -- Smoothing that does not depend on the frame rate.
  cameraPosition = cameraPosition and Vector3.lerp(cameraPosition, desired, 1 - math.exp(-12 * dt)) or desired
  Camera.Position = cameraPosition
  Camera.LookAt(pivot)
end

return { Spawn = function() return spawnPoint end }
