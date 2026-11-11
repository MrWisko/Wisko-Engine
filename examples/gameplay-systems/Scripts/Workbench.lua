-- Workbench.lua (on the "Workbench"): a custom crafting station (Wisko.Crafting). Recipes are plain tables; crafting
-- takes the inputs at once and gives the outputs after the recipe's time, with a progress bar. E opens the menu;
-- 1-9 crafts that recipe, 0 cancels the job (inputs back), E again closes.

local Crafting = require("Wisko.Crafting")

local recipes = Crafting.Recipes({
  { Id = "Plank", Name = "2 planks from 1 wood", Inputs = { Wood = 1 }, Outputs = { Plank = 2 }, Time = 1, Station = "Workbench" },
  { Id = "Axe", Name = "Axe (2 planks, 2 stone)", Inputs = { Plank = 2, Stone = 2 }, Outputs = { Axe = 1 }, Time = 2.5, Station = "Workbench",
    Requires = function(ctx) return ctx.inventory.Count("Axe") == 0, "you already have an axe" end },
  -- An upgrade: no item comes out; the axe gets sharper (a developer-defined effect).
  { Id = "Sharpen", Name = "Sharpen the axe (1 stone)", Inputs = { Stone = 1 }, Time = 1.5, Station = "Workbench",
    Requires = function(ctx) return ctx.inventory.Count("Axe") > 0, "you need an axe first" end,
    Outputs = function(ctx) Shared.axeSharpness = (Shared.axeSharpness or 0) + 1; Shared.game.Hint("The axe is sharper (" .. Shared.axeSharpness .. ")", 2) end },
})

local bench
local menu, rows, progressFill, progressLabel
local open = false
local job

local function closeMenu()
  open = false
  if menu then
    menu.Visible = false
  end
end

local function refresh()
  if not open then
    return
  end
  local inventory = Shared.game.inventory
  for i, recipe in ipairs(recipes:List()) do
    local ok, _, reason = recipes:CanCraft(recipe.Id, inventory, { station = bench, inventory = inventory })
    rows[i].Text = i .. ". " .. recipe.Name .. (ok and "" or ("  (" .. tostring(reason) .. ")"))
    rows[i].TextColor = ok and "#F0F2F8" or "#8A92A6"
  end
end

local function openMenu()
  open = true
  if menu == nil then
    menu = UI.Create("Frame", { Name = "CraftMenu", Anchor = { 0.5, 0.5 }, Position = { 0.5, 0, 0.5, 0 }, Size = { 0, 520, 0, 210 },
      Color = "#1C2030", Opacity = 0.94, ZOrder = 50 })
    UI.Create("Text", { Name = "CraftTitle", Parent = menu, Position = { 0, 16, 0, 10 }, Size = { 1, -32, 0, 26 }, FontSize = 20,
      TextColor = "#FFD479", HorizontalAlignment = "Left", Text = "Workbench" })
    rows = {}
    for i in ipairs(recipes:List()) do
      rows[i] = UI.Create("Text", { Name = "CraftRow" .. i, Parent = menu, Position = { 0, 18, 0, 44 + (i - 1) * 28 }, Size = { 1, -36, 0, 26 },
        FontSize = 17, HorizontalAlignment = "Left", Text = "" })
    end
    local bar = UI.Create("Frame", { Name = "CraftBar", Parent = menu, Position = { 0, 18, 1, -50 }, Size = { 1, -36, 0, 16 }, Color = "#2E3550" })
    progressFill = UI.Create("Frame", { Name = "CraftBarFill", Parent = bar, Size = { 0, 0, 1, 0 }, Color = "#4CD964" })
    progressLabel = UI.Create("Text", { Name = "CraftStatus", Parent = menu, Position = { 0, 18, 1, -30 }, Size = { 1, -36, 0, 22 }, FontSize = 15,
      HorizontalAlignment = "Left", TextColor = "#9AA3B5", Text = "1-3 craft, 0 cancel, E close" })
  end
  menu.Visible = true
  refresh()
end

function Start()
  bench = Crafting.Station({ Kind = "Workbench", Recipes = recipes, Object = self })
  bench:OnCrafted(function(done, made)
    Shared.game.Hint("Crafted: " .. done.Recipe.Id, 2)
    refresh()
  end)
  self:AddTag("Interactable")
  Shared.interactions = Shared.interactions or {}
  Shared.interactions[self.Id] = {
    Prompt = function() return open and "Close the workbench" or "Use the workbench" end,
    Use = function() if open then closeMenu() else openMenu() end end,
  }
  Events.On("InventoryChanged", function() refresh() end)
end

function Update()
  if job ~= nil and job.State == "Crafting" then
    progressFill:SetSize(job:GetProgress(), 0, 1, 0)
    progressLabel.Text = string.format("Crafting %s... %.1f s (0 cancels)", job.Recipe.Id, job:GetRemaining())
  elseif progressFill ~= nil then
    progressFill:SetSize(0, 0, 1, 0)
    progressLabel.Text = "1-3 craft, 0 cancel, E close"
  end
  if not open then
    return
  end
  -- Too far away: the menu closes.
  if Vector3.distance(Shared.player.Transform.Position, self.Transform.Position) > 8 then
    closeMenu()
    return
  end
  for i, recipe in ipairs(recipes:List()) do
    if Input.IsKeyPressed(tostring(i)) then
      local started, reason = bench:Start(recipe.Id, Shared.game.inventory)
      if started then
        job = started
      else
        Shared.game.Hint("Cannot craft: " .. tostring(reason), 2)
      end
    end
  end
  if Input.IsKeyPressed("0") and job ~= nil and job:Cancel() then
    Shared.game.Hint("Cancelled: the inputs are back", 2)
    refresh()
  end
end
