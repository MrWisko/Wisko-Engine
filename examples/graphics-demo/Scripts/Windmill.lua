-- Windmill.lua (on the windmill's blades): turns them slowly, so their shadow sweeps across the ground (shadows are
-- recomputed every frame for moving objects).

local SPEED = 25 -- degrees per second

function Update(dt)
  self:Rotate(0, 0, SPEED * dt)
end
