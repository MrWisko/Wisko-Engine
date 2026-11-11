-- FoodDefs.lua: a module (require("FoodDefs")): what each food restores. Shared by Survival.lua, Food.lua and
-- Animal.lua, so a new food is one line here.
return {
  Apple = { Hunger = 25, Thirst = 5 },
  Berry = { Hunger = 10 },
  Water = { Thirst = 35 },
}
