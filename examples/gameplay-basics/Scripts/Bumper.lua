-- Bumper.lua: whatever runs into it bounces off. Objects with Status.lua get a knockback (the player loses the
-- controls for a moment); anything else loose gets a plain impulse.
local SPEED = 30

function OnCollisionEnter(other)
  if other == nil or other.Anchored then
    return
  end
  local status = other:GetScript("Status")
  if status then
    status.Knockback(self.Transform.Position, SPEED)
  else
    local away = other.Transform.Position - self.Transform.Position
    other:ApplyImpulse(Vector3.normalize(Vector3.new(away.x, 0.3, away.z)) * SPEED * other.Mass)
  end
  Events.Emit("Bumped", { object = other })
end
