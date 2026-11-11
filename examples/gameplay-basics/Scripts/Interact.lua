-- Interact.lua (on the player): E uses the nearest interactable within reach; Q eats an Apple from the inventory.
-- Any object can be interactable: its script adds the "Interactable" tag, puts its own script name in the "Interact"
-- attribute and returns Prompt(player) and Interact(player). Nothing here knows trees, food, levers or cars.
-- The HUD shows the prompt: this script emits "PromptChanged" whenever the text changes.

local REACH = 6
local currentText

local function nearest()
  local here = self.Transform.Position
  local best, bestDistance
  for _, object in ipairs(Physics.OverlapSphere(here, REACH, { OnlyTags = "Interactable", IgnoreEnvironment = true, Ignore = self })) do
    local d = Vector3.distance(here, object.Transform.Position)
    if best == nil or d < bestDistance then
      best, bestDistance = object, d
    end
  end
  return best
end

local function handlerOf(object)
  local name = object and object:GetAttribute("Interact")
  return name and object:GetScript(name) or nil
end

function Update(dt)
  -- In a vehicle, E is about the vehicle (getting out).
  local target = Shared.vehicle or nearest()
  local handler = handlerOf(target)
  local text = handler and handler.Prompt(self) or nil
  if text ~= currentText then
    currentText = text
    Events.Emit("PromptChanged", { text = text })
  end
  if handler and Input.IsKeyPressed("E") then
    handler.Interact(self)
  end

  if Input.IsKeyPressed("Q") and Shared.vehicle == nil then
    local inventory = self:GetScript("Inventory")
    if inventory.Remove("Apple", 1) then
      self:GetScript("Survival").Eat("Apple")
      print("[Interact] ate an Apple")
      Events.Emit("Hint", "Ate an Apple")
    else
      Events.Emit("Hint", "No Apple to eat")
    end
  end
end

return { Prompt = function() return currentText end }
