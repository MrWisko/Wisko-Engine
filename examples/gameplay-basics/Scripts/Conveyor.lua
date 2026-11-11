-- Conveyor.lua: a belt. Its surface moves along the belt's Forward (local -Z) at SPEED, so anything resting on it is
-- carried by friction (crates, the player); the belt itself stays where it is. Belts in a line hand things on.
-- "ConveyorMode" events switch every belt: 1 forward, 0 stopped, -1 reverse.
local SPEED = 6

function Start()
  self.SurfaceVelocity = self.Forward * SPEED
  Events.On("ConveyorMode", function(mode)
    self.SurfaceVelocity = self.Forward * (SPEED * mode)
  end)
end
