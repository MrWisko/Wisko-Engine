-- Lever.lua: E cycles the conveyors forward -> stopped -> reverse -> forward (a "ConveyorMode" event; the belts
-- listen). Its colour shows the mode.
local MODES = {
  { mode = 1, name = "forward", color = "#3CC86E" },
  { mode = 0, name = "stopped", color = "#D2C23C" },
  { mode = -1, name = "reverse", color = "#D2463C" },
}
local index = 1

function Start()
  self:AddTag("Interactable")
  self:SetAttribute("Interact", "Lever")
  self.Color = MODES[index].color
end

return {
  Prompt = function() return "switch the conveyors (now " .. MODES[index].name .. ")" end,
  Interact = function()
    index = index % #MODES + 1
    self.Color = MODES[index].color
    Events.Emit("ConveyorMode", MODES[index].mode)
    print("[Lever] conveyors " .. MODES[index].name)
    Events.Emit("Hint", "Conveyors " .. MODES[index].name)
  end,
}
