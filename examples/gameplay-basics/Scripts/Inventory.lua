-- Inventory.lua (on the player): plain Lua data owned by this script (per-script state), reached by other scripts
-- with player:GetScript("Inventory"). Every change emits "InventoryChanged" with a copy of the items (the HUD lists
-- them). Nothing is saved: a new Play session starts empty.
local items = {}

local function changed(item)
  local copy = {}
  for name, count in pairs(items) do
    copy[name] = count
  end
  Events.Emit("InventoryChanged", { owner = self, items = copy, item = item })
end

local Inventory = {}

function Inventory.Add(item, count)
  count = count or 1
  items[item] = (items[item] or 0) + count
  changed(item)
end

function Inventory.Remove(item, count)
  count = count or 1
  if (items[item] or 0) < count then
    return false
  end
  items[item] = items[item] - count
  if items[item] == 0 then
    items[item] = nil
  end
  changed(item)
  return true
end

function Inventory.Count(item)
  return items[item] or 0
end

return Inventory
