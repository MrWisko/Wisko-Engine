-- Character.lua (on the invisible "PlayerBody"): a third-person character whose imported model ("Robot",
-- RobotExpressive.glb) animates from what the body actually does. WASD moves relative to the camera, LeftShift runs,
-- Space jumps, E waves; the mouse turns the camera (Tab frees / locks the mouse, the wheel zooms).
-- The body is a plain Block that physics moves; the model follows it (SetParent) and only shows the animation:
--   standing still -> Idle, walking -> Walking, running -> Running (crossfades, clip speed matched to the real speed),
--   in the air -> Jump (once), E -> Wave (once; OnAnimationFinished brings the movement clips back).
local WALK, RUN, JUMP = 7, 16, 18
local GROUND_ACCEL, AIR_ACCEL = 12, 3
local SENSITIVITY = 0.15
local MIN_PITCH, MAX_PITCH = -35, 70
local FADE = 0.2 -- crossfade between the movement clips, in seconds

-- RobotExpressive.glb's clip names (robot:GetAnimations() lists them).
local IDLE, WALKING, RUNNING, JUMPING, WAVE = "Idle", "Walking", "Running", "Jump", "Wave"

-- How fast the walk and run cycles move at speed 1 (studs per second): the clips play faster or slower to match the
-- body's real speed, so the feet do not slide.
local WALK_CLIP_SPEED, RUN_CLIP_SPEED = 5.5, 12

local robot, hud
local yaw, pitch, distance = 270, 12, 14 -- camera yaw 270 looks along -Z
local cameraPosition
local waving = false
local jumpedAt -- Time.elapsedTime of the last jump, while in the air

local function flat(v)
  local f = Vector3.new(v.x, 0, v.z)
  if Vector3.magnitude(f) < 1e-4 then
    return Vector3.new(0, 0, -1)
  end
  return Vector3.normalize(f)
end

function Start()
  self.RotationLocked = true -- physics never tips the body over
  self.Visible = false       -- the model is what we see
  robot = Scene.Find("Robot")
  -- The model's origin is at its feet: put them at the bottom of the 4.5 tall body. glTF characters face +Z and a
  -- Wisko object's Forward is -Z, hence the half turn.
  robot:SetParent(self, Vector3.new(0, -2.25, 0), Vector3.new(0, 180, 0))
  robot:PlayAnimation(IDLE)
  robot:OnAnimationFinished(function(name)
    if name == WAVE then
      waving = false -- the movement clips take over again (next Update)
    end
  end)
  Input.SetMouseLocked(true)
  hud = UI.Create("Text", { Name = "AnimationHud", Position = { 0, 16, 0, 16 }, Size = { 0, 560, 0, 24 },
    Text = "", FontSize = 18, TextColor = "#F0F2F8" })
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

  local grounded = self:IsGrounded()
  if jumpedAt ~= nil and grounded and Time.elapsedTime - jumpedAt > 0.25 then
    jumpedAt = nil -- landed
  end

  -- E: a one-shot clip; the character stands still until it ends.
  if Input.IsKeyPressed("E") and grounded and jumpedAt == nil and not waving then
    waving = true
    robot:CrossFadeAnimation(WAVE, 0.15, { Loop = false })
  end

  local forward, right = flat(Camera.Forward), flat(Camera.Right)
  local move = forward * Input.GetAxis("S", "W") + right * Input.GetAxis("A", "D")
  if waving then
    move = Vector3.new(0, 0, 0)
  elseif Vector3.magnitude(move) > 0 then
    move = Vector3.normalize(move)
  end
  local target = move * (Input.IsKeyDown("LeftShift") and RUN or WALK)

  local v = self.LinearVelocity
  local blend = math.min(1, (grounded and GROUND_ACCEL or AIR_ACCEL) * dt) -- frame-rate independent
  local vy = v.y
  if grounded and jumpedAt == nil and not waving and Input.IsKeyPressed("Space") then
    vy = JUMP
    jumpedAt = Time.elapsedTime
    robot:CrossFadeAnimation(JUMPING, 0.1, { Loop = false })
  end
  self.LinearVelocity = Vector3.new(v.x + (target.x - v.x) * blend, vy, v.z + (target.z - v.z) * blend)
  if Vector3.magnitude(move) > 0 then
    self.Transform.Rotation = Vector3.new(0, math.deg(math.atan(-move.x, -move.z)), 0)
  end

  -- The movement clips follow the body's real horizontal speed (what physics made of it), not the keys pressed.
  -- Calling CrossFadeAnimation every frame is fine: a clip that is already playing just keeps playing (its speed
  -- follows the Speed given).
  local speed = math.sqrt(v.x * v.x + v.z * v.z)
  if jumpedAt == nil and not waving then
    if speed < 0.6 then
      robot:CrossFadeAnimation(IDLE, FADE)
    elseif speed < (WALK + RUN) / 2 then
      robot:CrossFadeAnimation(WALKING, FADE, { Speed = speed / WALK_CLIP_SPEED })
    else
      robot:CrossFadeAnimation(RUNNING, FADE, { Speed = speed / RUN_CLIP_SPEED })
    end
  end

  hud.Text = string.format("WASD move, Shift run, Space jump, E wave   |   %s  %.2f s  (speed %.1f)",
    robot:GetCurrentAnimation() or "-", robot:GetAnimationTime(), speed)
end

function LateUpdate(dt)
  local pivot = self.Transform.Position + Vector3.new(0, 1.5, 0)
  local y, p = math.rad(yaw), math.rad(pitch)
  local look = Vector3.new(math.cos(p) * math.cos(y), -math.sin(p), math.cos(p) * math.sin(y))
  local desired = pivot - look * distance

  -- Keep the camera out of walls.
  local hit = Physics.Raycast(pivot, desired - pivot, Vector3.distance(pivot, desired),
    { Ignore = { self, robot }, IgnoreTriggers = true })
  if hit then
    desired = pivot + Vector3.normalize(desired - pivot) * math.max(1, hit.Distance - 0.5)
  end

  cameraPosition = cameraPosition and Vector3.lerp(cameraPosition, desired, 1 - math.exp(-12 * dt)) or desired
  Camera.Position = cameraPosition
  Camera.LookAt(pivot)
end
