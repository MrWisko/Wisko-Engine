-- Flight.lua: flying is an ordinary state: gravity off, velocity steered toward camera-relative input,
-- accelerated smoothly and limited. G, the Fly button, or touching a FlightOrb (it calls SetFlying) toggle it.
local SPEED, ACCEL = 20, 40
local flying = false
local button

local function setFlying(on)
  flying = on
  self.GravityScale = on and 0 or 1   -- off: normal physics again (it falls)
  button.Text = on and "Land" or "Fly"
  print("[Flight] " .. (on and "flying" or "landed"))
  Events.Emit("FlightChanged", { object = self, flying = on })
end

function Start()
  self.RotationLocked = true
  button = UI.Create("Button", "FlyButton")
  button:SetAnchor(1, 0)
  button:SetPosition(1, -16, 0, 16)
  button:SetSize(0, 120, 0, 40)
  button.Text = "Fly"
  button:OnClick(function() setFlying(not flying) end)
end

function Update(dt)
  if Input.IsKeyPressed("G") then
    setFlying(not flying)
  end
  if not flying then
    return
  end
  local move = Camera.Forward * Input.GetAxis("S", "W") + Camera.Right * Input.GetAxis("A", "D")
    + Vector3.new(0, 1, 0) * Input.GetAxis("LeftControl", "Space")
  local target = Vector3.clampMagnitude(move * SPEED, SPEED)                     -- no faster than SPEED
  self.LinearVelocity = Vector3.moveTowards(self.LinearVelocity, target, ACCEL * dt) -- frame-rate independent
end

return { SetFlying = setFlying, IsFlying = function() return flying end }
