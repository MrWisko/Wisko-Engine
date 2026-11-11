-- CarSeat.lua: getting in and out of the car with E (through Interact.lua). Inside, the player rides along hidden
-- (attached to the car), Car.lua drives because Shared.vehicle is this car, and Player.lua switches the camera to a
-- chase view. E again gets out beside the car.

function Start()
  self:AddTag("Interactable")
  self:SetAttribute("Interact", "CarSeat")
end

local function getIn(player)
  Shared.vehicle = self
  player.LinearVelocity = Vector3.new(0, 0, 0)
  player:SetParent(self, Vector3.new(0, 1, 0))
  player.Visible = false
  print("[Car] driving")
  Events.Emit("VehicleChanged", { vehicle = self })
  Events.Emit("Hint", "Driving: W/S, A/D steer, Space brakes, E gets out")
end

local function getOut(player)
  Shared.vehicle = nil
  player:SetParent(nil)
  player.Visible = true
  player.Transform.Position = self:PointToWorld(Vector3.new(-self.Transform.Scale.x / 2 - 2, 1, 0))
  player.LinearVelocity = self.LinearVelocity
  print("[Car] got out")
  Events.Emit("VehicleChanged", { vehicle = nil })
end

return {
  Prompt = function(player)
    return Shared.vehicle == self and "get out of the car" or "drive the car"
  end,
  Interact = function(player)
    if Shared.vehicle == self then
      getOut(player)
    elseif Shared.vehicle == nil then
      getIn(player)
    end
  end,
}
