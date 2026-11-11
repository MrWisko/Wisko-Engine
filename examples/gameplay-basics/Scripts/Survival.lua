-- Survival.lua: Health, Hunger and Thirst as attributes on this object (custom stats, 0 to 100). Hunger and Thirst
-- fall every second; at 0 they hurt. The HUD listens with OnAttributeChanged; food calls Eat / Restore.
-- On the player and on the goat: an animal is just another object with stats.
local FoodDefs = require("FoodDefs")
local HUNGER_PER_SECOND, THIRST_PER_SECOND = 0.5, 0.8
local STARVE_DAMAGE = 2 -- Health per second while Hunger or Thirst is empty

local function stat(name)
  return self:GetAttribute(name) or 0
end

local function set(name, value)
  self:SetAttribute(name, math.clamp(value, 0, 100))
end

function Start()
  -- Another script may have chosen a starting value already (the goat starts hungry).
  for _, name in ipairs({ "Health", "Hunger", "Thirst" }) do
    if not self:HasAttribute(name) then
      set(name, 100)
    end
  end
  Timer.Every(1, function()
    set("Hunger", stat("Hunger") - HUNGER_PER_SECOND)
    set("Thirst", stat("Thirst") - THIRST_PER_SECOND)
    if stat("Hunger") == 0 or stat("Thirst") == 0 then
      set("Health", stat("Health") - STARVE_DAMAGE)
    end
  end)
end

local function restore(name, amount)
  set(name, stat(name) + amount)
  return stat(name)
end

return {
  Get = stat,
  Restore = restore,
  -- Applies a food from FoodDefs (Apple: +25 Hunger, +5 Thirst); false for something that is not food.
  Eat = function(food)
    local def = FoodDefs[food]
    if def == nil then
      return false
    end
    for name, amount in pairs(def) do
      restore(name, amount)
    end
    Events.Emit("Ate", { object = self, food = food })
    return true
  end,
}
