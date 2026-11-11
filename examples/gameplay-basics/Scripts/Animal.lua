-- Animal.lua: a goat to feed. It has stats of its own (Survival.lua on it too) and starts hungry. E with an Apple in
-- your inventory feeds it: the Apple leaves your inventory and restores the goat's Hunger, not yours.
function Start()
  self:SetAttribute("Hunger", 40)
  self:AddTag("Interactable")
  self:SetAttribute("Interact", "Animal")
end

return {
  Prompt = function(player)
    local hunger = math.floor(self:GetAttribute("Hunger") or 0)
    if player:GetScript("Inventory").Count("Apple") > 0 then
      return "feed the " .. self.Name .. " an Apple (Hunger " .. hunger .. ")"
    end
    return "find an Apple to feed the " .. self.Name .. " (Hunger " .. hunger .. ")"
  end,
  Interact = function(player)
    if player:GetScript("Inventory").Remove("Apple", 1) then
      self:GetScript("Survival").Eat("Apple")
      print("[Animal] " .. self.Name .. " fed (Hunger " .. math.floor(self:GetAttribute("Hunger")) .. ")")
      Events.Emit("Fed", { animal = self, by = player })
      Events.Emit("Hint", "The " .. self.Name .. " liked that")
    else
      Events.Emit("Hint", "You have no Apple")
    end
  end,
}
