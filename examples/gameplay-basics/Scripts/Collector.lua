-- Collector.lua (on the player): picks up dropped items: Parts tagged "Drop" with an "Item" attribute (and an
-- optional "Count"). Drops within MAGNET studs fly to the player; within PICKUP studs they go into the inventory and
-- are destroyed.
local MAGNET, PICKUP, PULL = 8, 2.5, 18

function Update(dt)
  local here = self.Transform.Position
  for _, drop in ipairs(Physics.OverlapSphere(here, MAGNET, { OnlyTags = "Drop", IgnoreEnvironment = true })) do
    local to = here - drop.Transform.Position
    if Vector3.magnitude(to) <= PICKUP then
      local item, count = drop:GetAttribute("Item"), drop:GetAttribute("Count") or 1
      drop:Destroy()
      self:GetScript("Inventory").Add(item, count)
      print("[Collector] +" .. count .. " " .. item)
      Events.Emit("Hint", "+" .. count .. " " .. item)
    else
      drop.LinearVelocity = Vector3.normalize(to) * PULL
    end
  end
end
