-- Spinner.lua: an anchored Part that turns on its own, in degrees per second around each axis. A spinning platform
-- carries whoever stands on it round; a fan just spins. Which speed comes from the name (edit SPEEDS).
-- SetSpeed(vector) changes it later (Spinner's exports, through GetScript).
local SPEEDS = {
  Spinner = Vector3.new(0, 40, 0),
  Fan = Vector3.new(0, 0, 240),
}

function Start()
  local speed = Vector3.new(0, 40, 0)
  for prefix, value in pairs(SPEEDS) do
    if string.sub(self.Name, 1, #prefix) == prefix then
      speed = value
    end
  end
  self.AngularVelocity = speed
end

return { SetSpeed = function(speed) self.AngularVelocity = speed end }
