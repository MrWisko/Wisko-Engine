-- ResourceNode.lua: a tree or rock to harvest with E (through Interact.lua). Each hit takes 1 from its "Health"
-- attribute; at 0 it drops items (real Parts that fly out; Collector.lua picks them up), disappears and grows back
-- REGROW seconds later. What it gives comes from its name (edit KINDS to add one).
local KINDS = {
  Tree = { item = "Wood", hits = 3, drops = 3, regrow = 8, color = "#9A6A3A", verb = "chop" },
  Rock = { item = "Stone", hits = 4, drops = 2, regrow = 10, color = "#8E9196", verb = "mine" },
}
local kind
local depleted = false

function Start()
  for prefix, def in pairs(KINDS) do
    if string.sub(self.Name, 1, #prefix) == prefix then
      kind = def
    end
  end
  kind = kind or KINDS.Tree
  self:SetAttribute("Health", kind.hits)
  self:SetAttribute("Interact", "ResourceNode")
  self:AddTag("Interactable")
end

local function spawnDrops()
  local base = self.Transform.Position
  for i = 1, kind.drops do
    local angle = (i / kind.drops) * math.pi * 2
    local out = Vector3.new(math.cos(angle), 0, math.sin(angle))
    Scene.Create("Block", {
      Name = kind.item .. "Drop", Anchored = false, Size = Vector3.new(0.8, 0.8, 0.8), Color = kind.color,
      Position = Vector3.new(base.x, base.y, base.z) + out * 2 + Vector3.new(0, 1, 0),
      LinearVelocity = out * 4 + Vector3.new(0, 8, 0),
      Tags = { "Drop" }, Attributes = { Item = kind.item, Count = 1 },
    })
  end
  Events.Emit("ResourceDropped", { node = self, item = kind.item, count = kind.drops })
end

local function regrow()
  depleted = false
  self:SetAttribute("Health", kind.hits)
  self.Visible = true
  self.CanCollide = true
  self:AddTag("Interactable")
  print("[Resource] " .. self.Name .. " grew back")
  Events.Emit("ResourceRegrown", { node = self, item = kind.item })
end

local function hit(by)
  if depleted then
    return false
  end
  local health = self:GetAttribute("Health") - 1
  self:SetAttribute("Health", health)
  Events.Emit("ResourceHit", { node = self, health = health, item = kind.item })
  if health <= 0 then
    depleted = true
    print("[Resource] " .. self.Name .. " harvested: " .. kind.drops .. " " .. kind.item .. " dropped")
    spawnDrops()
    self.Visible = false
    self.CanCollide = false
    self:RemoveTag("Interactable") -- no prompt while it is gone
    Timer.After(kind.regrow, regrow)
  end
  return true
end

return {
  Prompt = function() return kind.verb .. " the " .. self.Name .. " (" .. self:GetAttribute("Health") .. " hits left)" end,
  Interact = function(player) hit(player) end,
  Hit = hit,
  IsDepleted = function() return depleted end,
}
