-- Car.lua: a raycast car from general physics calls. Four suspension rays push the body up (springs), each tyre
-- grips sideways, the rear wheels drive and every wheel brakes. W/S throttle and reverse, A/D steer, Space
-- brakes, only while Shared.vehicle is this car (someone is driving it).
local REST, RADIUS = 0.6, 0.5        -- spring length below each mount, wheel radius
local MAX_STEER, MAX_SPEED = 30, 40
local GRIP = 10                        -- how quickly a tyre stops sliding sideways (per second)
local stiffness, damping, engine, brake, mass
local wheels = {}
local steer, throttle = 0, 0

function Start()
  mass = self:GetMass()
  local gravity = -Physics.Gravity.y
  stiffness = mass * gravity / 4 / (REST * 0.5)       -- at rest each spring is half compressed
  damping = 2 * math.sqrt(stiffness * mass / 4) * 0.7  -- a little under critical: no bouncing
  engine = mass * 14 / 2                             -- 14 studs/s^2 from the two driven wheels
  brake = mass * 25 / 4
  self.AngularDamping = 1.5
  local size = self.Transform.Scale
  local x, y, z = size.x / 2 - 0.4, -size.y / 2, size.z / 2 - 0.9
  wheels = {
    { mount = Vector3.new(-x, y, -z), front = true }, { mount = Vector3.new(x, y, -z), front = true },
    { mount = Vector3.new(-x, y, z) }, { mount = Vector3.new(x, y, z) },
  }
end

function Update(dt)
  local driving = Shared.vehicle == self
  throttle = driving and Input.GetAxis("S", "W") or 0
  local braking = driving and Input.IsKeyDown("Space") or not driving
  local steerTarget = (driving and Input.GetAxis("A", "D") or 0) * MAX_STEER
  steer = steer + (steerTarget - steer) * math.min(1, 8 * dt)
  local up, forward, right = self.Up, self.Forward, self.Right
  local speed = Vector3.dot(self.LinearVelocity, forward)
  local filter = { Ignore = self, IgnoreTriggers = true, IgnoreTags = "CarPart" }
  for _, wheel in ipairs(wheels) do
    local origin = self:PointToWorld(wheel.mount)
    local hit = Physics.Raycast(origin, -up, REST + RADIUS, filter)
    wheel.grounded = hit ~= nil
    if hit then
      local compression = REST + RADIUS - hit.Distance
      local spring = stiffness * compression - damping * Vector3.dot(self:GetVelocityAtPoint(origin), up)
      self:ApplyForceAtPoint(up * math.max(0, spring), origin)
      local wheelForward, wheelRight = forward, right
      if wheel.front then
        local a = math.rad(steer)
        wheelForward = forward * math.cos(a) + right * math.sin(a)
        wheelRight = right * math.cos(a) - forward * math.sin(a)
      end
      local velocity = self:GetVelocityAtPoint(origin)
      local load = mass / 4
      local force = wheelRight * (-Vector3.dot(velocity, wheelRight) * load * GRIP)
      if braking then
        local along = Vector3.dot(velocity, wheelForward)
        force = force + wheelForward * -math.clamp(along * load * 8, -brake, brake)
      elseif not wheel.front and throttle ~= 0 and (math.abs(speed) < MAX_SPEED or speed * throttle < 0) then
        force = force + wheelForward * (throttle * engine)
      end
      self:ApplyForceAtPoint(force, origin)   -- at the mount: grip does not roll the car over
    end
  end
end

return {
  Speed = function() return Vector3.dot(self.LinearVelocity, self.Forward) end,
  Steer = function() return steer end,
  GroundedWheels = function()
    local count = 0
    for _, wheel in ipairs(wheels) do if wheel.grounded then count = count + 1 end end
    return count
  end,
}
