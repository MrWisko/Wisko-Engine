-- FoxRun.lua (on "Fox Runner"): the textured Fox runs a loop around the meadow, turned along its way, with its Run clip.

local RADIUS = 9
local SPEED = 0.55 -- radians per second
local center
local angle = 0

function Start()
  center = self.Transform.Position + Vector3.new(0, 0, -RADIUS)
  self:PlayAnimation("Run", { Speed = 0.8 })
end

function Update(dt)
  angle = angle + SPEED * dt
  local x, z = math.sin(angle) * RADIUS, math.cos(angle) * RADIUS
  self.Transform.Position = center + Vector3.new(x, 0, z)
  -- Heading along the circle; the model faces +Z, so a turn of angle + 90 degrees points it the way it runs.
  self.Transform.Rotation.y = math.deg(angle) + 90
end
