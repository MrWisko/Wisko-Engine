-- Coin.lua: a trigger coin. Touching it adds VALUE to the toucher's "Coins" attribute (a currency is just a number
-- on the object) and removes the coin. It spins slowly so it is easy to see.
local VALUE = 1

function Start()
  self.AngularVelocity = Vector3.new(0, 120, 0)
end

function OnTriggerEnter(other)
  if other ~= nil and other == Shared.player then
    local coins = (other:GetAttribute("Coins") or 0) + VALUE
    other:SetAttribute("Coins", coins)
    print("[Coin] " .. self.Name .. " collected (" .. coins .. " coins)")
    Events.Emit("Hint", "+" .. VALUE .. " coin")
    self:Destroy()
  end
end
