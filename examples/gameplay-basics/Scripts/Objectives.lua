-- Objectives.lua (on the player): a short list of goals, checked twice a second. The current one is announced with
-- "ObjectiveChanged" { text, index, count, done } (the HUD shows it). Progress lives in this script for this Play
-- session only: nothing is saved.
local fed, drove, flew = false, false, false

local function inventory()
  return self:GetScript("Inventory")
end

local GOALS = {
  { text = "Collect 3 coins", done = function() return (self:GetAttribute("Coins") or 0) >= 3 end },
  { text = "Chop the tree for 3 Wood", done = function() return inventory().Count("Wood") >= 3 end },
  { text = "Pick up an Apple and feed the goat", done = function() return fed end },
  { text = "Fly (touch the orb or press G)", done = function() return flew end },
  { text = "Drive the car (E next to it)", done = function() return drove end },
}
local current = 0

local function announce()
  local goal = GOALS[current]
  print("[Objectives] " .. (goal and (current .. "/" .. #GOALS .. ": " .. goal.text) or "all done"))
  Events.Emit("ObjectiveChanged", {
    text = goal and goal.text or "All done - explore!", index = current, count = #GOALS, done = goal == nil,
  })
end

local function advance()
  while current <= #GOALS and (current == 0 or GOALS[current].done()) do
    current = current + 1
    if GOALS[current] == nil then
      break
    end
  end
  announce()
end

function Start()
  Events.On("Fed", function() fed = true end)
  Events.On("VehicleChanged", function(info) if info.vehicle ~= nil then drove = true end end)
  Events.On("FlightChanged", function(info) if info.flying then flew = true end end)
  advance()
  Timer.Every(0.5, function()
    local before = current
    if GOALS[current] and GOALS[current].done() then
      advance()
      if current ~= before then
        Events.Emit("Hint", "Objective complete!")
      end
    end
  end)
end

return { Current = function() return current end, Count = function() return #GOALS end }
