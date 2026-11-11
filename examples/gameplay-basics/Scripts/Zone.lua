-- Zone.lua: a trigger zone that reports who is inside (it lights up while someone is).
local inside = 0

function OnTriggerEnter(other)
  inside = inside + 1
  self.Color = "#46C8FF"
  print("ZONE enter " .. (other and other.Name or "?"))
  Events.Emit("Hint", (other and other.Name or "Something") .. " entered the zone")
end

function OnTriggerExit(other)
  inside = math.max(0, inside - 1)
  if inside == 0 then
    self.Color = "#2A6E8C"
  end
  print("ZONE exit " .. (other and other.Name or "?"))
end
