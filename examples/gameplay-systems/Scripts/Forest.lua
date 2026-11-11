-- Forest.lua (on the "Forest" marker): a procedurally generated forest. The same Seed always grows the same forest
-- (Random + Wisko.Generate's Poisson-disc sampling); it is built a little each frame (a generation job with a progress
-- bar), so even a large forest never freezes the game. Trees give wood, rocks give stone: press E (with the axe a tree
-- falls in one hit). Harvested ones grow back after a while. What was harvested is part of the saved game.

local Generate = require("Wisko.Generate")

local SEED = self:GetAttribute("Seed") or 20251018
local AREA_MIN, AREA_MAX = Vector3.new(36, 0, -70), Vector3.new(104, 0, 10)
local REGROW = 60

local forest = { Objects = {} }
local health = {}       -- object Id -> hits left
local crowns = {}       -- tree Id -> its crown (a separate Part: trees never move)
local bar, barFill, barLabel

local function interactable(object, kind)
  object:AddTag("Interactable")
  object:AddTag(kind)
  health[object.Id] = kind == "Tree" and 3 or 4
  Shared.interactions[object.Id] = {
    Prompt = function()
      return (kind == "Tree" and "Chop the tree" or "Mine the rock") .. " (" .. health[object.Id] .. ")"
    end,
    Use = function(player)
      local swing = player:GetScript("Player")
      if swing then swing.Swing() end
      local damage = (kind == "Tree" and Shared.equipped) and 3 + (Shared.axeSharpness or 0) or 1
      health[object.Id] = health[object.Id] - damage
      if health[object.Id] > 0 then
        return
      end
      -- Harvested: it gives its resource, disappears and grows back later.
      local item = kind == "Tree" and "Wood" or "Stone"
      local amount = kind == "Tree" and 2 or 1
      Shared.game.inventory.Add(item, amount)
      Shared.game.Hint("+" .. amount .. " " .. item, 1.5)
      if item == "Wood" and Shared.game.quests:IsActive("firewood") then
        Shared.game.quests:SetProgress("firewood", "wood", Shared.game.inventory.Count("Wood"))
      end
      forest.Harvest(object, kind)
    end,
  }
end

function forest.Harvest(object, kind)
  object.Visible, object.CanCollide = false, false
  if crowns[object.Id] then crowns[object.Id].Visible = false end
  object:RemoveTag("Interactable")
  object:SetAttribute("Harvested", true)
  Timer.After(REGROW, function()
    if object.Exists then
      object.Visible, object.CanCollide = true, true
      if crowns[object.Id] then crowns[object.Id].Visible = true end
      object:AddTag("Interactable")
      object:SetAttribute("Harvested", false)
      health[object.Id] = kind == "Tree" and 3 or 4
    end
  end)
end

-- Saved: the names of what is harvested now (names are stable: the same seed makes the same names).
function forest.Save()
  local names = {}
  for _, object in ipairs(forest.Objects) do
    if object.Exists and object:GetAttribute("Harvested") then
      names[#names + 1] = object.Name
    end
  end
  return names
end

function forest.Load(names)
  local harvested = {}
  for _, name in ipairs(names) do
    harvested[name] = true
  end
  for _, object in ipairs(forest.Objects) do
    if object.Exists and harvested[object.Name] and not object:GetAttribute("Harvested") then
      forest.Harvest(object, object:HasTag("Tree") and "Tree" or "Rock")
    end
  end
end

Shared.forest = forest

function Start()
  Shared.interactions = Shared.interactions or {}
  local rng = Random.new(SEED)
  local points = Generate.PoissonDisk(rng, { Min = AREA_MIN, Max = AREA_MAX, Radius = 6.5 })
  bar = UI.Create("Frame", { Name = "ForestProgress", Anchor = { 0.5, 0 }, Position = { 0.5, 0, 0, 70 }, Size = { 0, 320, 0, 26 },
    Color = "#1C2030", Opacity = 0.85, ZOrder = 20 })
  barFill = UI.Create("Frame", { Name = "ForestProgressFill", Parent = bar, Size = { 0, 0, 1, 0 }, Color = "#4CD964" })
  barLabel = UI.Create("Text", { Name = "ForestProgressLabel", Parent = bar, Size = { 1, 0, 1, 0 }, FontSize = 15, Text = "Growing the forest..." })
  local job = Generate.Spawn(points, function(p, i)
    -- Noise thins the forest into clearings; the generator decides tree or rock, and its size.
    local density = Noise.Fractal(p.x * 0.04, p.z * 0.04, 0, { Seed = SEED, Octaves = 3 })
    if density < -0.25 then
      return nil
    end
    local kind = rng:Weighted({ { "Tree", 4 }, { "Rock", 1 } })
    local object
    if kind == "Tree" then
      local height = rng:Number(5, 9)
      object = Scene.Create("Cylinder", { Name = "Tree" .. i, Position = p + Vector3.new(0, height / 2, 0), Size = Vector3.new(1.4, height, 1.4),
        Color = rng:Choice({ "#6B4A2B", "#7A5533", "#5E4027" }) })
      local crown = Scene.Create("Sphere", { Name = "Crown" .. i, Position = p + Vector3.new(0, height + 1.2, 0),
        Size = Vector3.new(4.5, 4, 4.5), Color = rng:Choice({ "#2E7D32", "#388E3C", "#43A047", "#2F6B2F" }), CanCollide = false })
      crowns[object.Id] = crown
    else
      local size = rng:Number(1.5, 3)
      object = Scene.Create("Block", { Name = "Rock" .. i, Position = p + Vector3.new(0, size / 2, 0), Size = Vector3.new(size * 1.3, size, size),
        Rotation = Vector3.new(0, rng:Number(0, 90), 0), Color = "#8A8F98" })
    end
    interactable(object, kind)
    forest.Objects[#forest.Objects + 1] = object
    return object
  end, { Budget = 1.5 })
  job:OnProgress(function(progress)
    barFill:SetSize(progress, 0, 1, 0)
    barLabel.Text = string.format("Growing the forest... %d%%", math.floor(progress * 100))
  end)
  job:OnDone(function(made)
    bar:Destroy()
    -- The trees are obstacles now: the navigation map learns about them.
    Navigation.Update((AREA_MIN + AREA_MAX) / 2 + Vector3.new(0, 5, 0), (AREA_MAX - AREA_MIN) + Vector3.new(4, 12, 4))
    print("Forest: " .. #made .. " trees and rocks from seed " .. SEED .. " in " .. job.Frames .. " frames")
  end)
end

