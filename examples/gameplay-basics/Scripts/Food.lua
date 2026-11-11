-- Food.lua: something to eat or drink, used with E (through Interact.lua). What it is comes from its name:
--   Apple...  picked up into the inventory (eat it with Q, or feed it to the goat); it is gone afterwards.
--   Well...   a drink straight away (Water in FoodDefs: +35 Thirst); it never runs out.
local FoodDefs = require("FoodDefs")
local KINDS = {
  Apple = { item = "Apple", pickup = true },
  Berry = { item = "Berry", pickup = true },
  Well = { item = "Water", pickup = false },
}
local kind

function Start()
  for prefix, def in pairs(KINDS) do
    if string.sub(self.Name, 1, #prefix) == prefix then
      kind = def
    end
  end
  if kind == nil or FoodDefs[kind.item] == nil then
    error(self.Name .. ": Food.lua does not know what this is (name it Apple, Berry or Well)")
  end
  self:AddTag("Interactable")
  self:SetAttribute("Interact", "Food")
end

return {
  Prompt = function()
    return kind.pickup and ("pick up the " .. kind.item) or "drink from the well"
  end,
  Interact = function(player)
    if kind.pickup then
      player:GetScript("Inventory").Add(kind.item, 1)
      print("[Food] picked up " .. self.Name)
      Events.Emit("Hint", "+1 " .. kind.item .. " (Q eats it)")
      self:Destroy()
    else
      player:GetScript("Survival").Eat(kind.item)
      print("[Food] drank from " .. self.Name)
      Events.Emit("Hint", "Refreshing!")
    end
  end,
}
