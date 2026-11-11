-- HUD.lua (on the player): the game's HUD, built by Lua at Start with UI.Create (nothing is drawn in the editor's UI
-- panel). It only listens: attribute signals for the stats and the coins, events for the inventory, the interaction
-- prompt, status effects, objectives and hints. Like every Play-time change, all of it is gone at Stop.
--   top left    Health / Hunger / Thirst bars, and active status effects below them
--   top centre  the current objective
--   top right   the Fly button (Flight.lua) and the coin counter
--   right       the inventory panel (I shows / hides it)
--   bottom      the interaction prompt ("E: ...") and short hints
local player = self
local bars = {}
local rows = {}
local effects = {}
local panel, coins, objective, prompt, hint, effectsText, inventoryPanel
local hintTimer

local COLORS = { Health = "#E0483E", Hunger = "#E0A23E", Thirst = "#3E9BE0" }
local TEXT = "#E8EAF0"

local function bar(name, y)
  UI.Create("Text", { Name = name .. "Label", Parent = panel, Position = { 0, 12, 0, y }, Size = { 0, 70, 0, 20 },
    Text = name, FontSize = 16, TextColor = TEXT })
  local back = UI.Create("Frame", { Name = name .. "Bar", Parent = panel, Position = { 0, 84, 0, y + 2 },
    Size = { 0, 180, 0, 16 }, Color = "#2E323C" })
  local fill = UI.Create("Frame", { Name = name .. "Fill", Parent = back, Position = { 0, 0, 0, 0 },
    Size = { 1, 0, 1, 0 }, Color = COLORS[name] })
  local value = UI.Create("Text", { Name = name .. "Value", Parent = panel, Position = { 0, 272, 0, y },
    Size = { 0, 40, 0, 20 }, Text = "100", FontSize = 16, TextColor = TEXT })
  bars[name] = { fill = fill, value = value }
end

local function showStat(name, value)
  local b = bars[name]
  if b == nil or type(value) ~= "number" then
    return
  end
  b.fill:SetSize(math.clamp(value / 100, 0, 1), 0, 1, 0)
  b.value.Text = tostring(math.floor(value + 0.5))
end

local function showCoins(value)
  coins.Text = "Coins: " .. tostring(math.floor(value or 0))
end

local function showEffects()
  local names = {}
  for name in pairs(effects) do
    names[#names + 1] = name
  end
  table.sort(names)
  effectsText.Text = #names > 0 and ("Effects: " .. table.concat(names, ", ")) or ""
end

local function showInventory(items)
  for _, row in ipairs(rows) do
    row:Destroy()
  end
  rows = {}
  local names = {}
  for name in pairs(items) do
    names[#names + 1] = name
  end
  table.sort(names)
  if #names == 0 then
    names = { false }
  end
  for i, name in ipairs(names) do
    rows[i] = UI.Create("Text", { Name = name and ("Item" .. name) or "ItemNone", Parent = inventoryPanel,
      Position = { 0, 14, 0, 14 + i * 24 }, Size = { 0, 172, 0, 22 }, FontSize = 16, TextColor = TEXT,
      Text = name and (name .. "  x" .. items[name]) or "(empty)" })
  end
end

local function showHint(text)
  hint.Text = text
  hint.Visible = true
  if hintTimer then
    hintTimer:Cancel()
  end
  hintTimer = Timer.After(2.5, function() hint.Visible = false end)
end

function Start()
  panel = UI.Create("Frame", { Name = "StatsPanel", Position = { 0, 16, 0, 16 }, Size = { 0, 320, 0, 104 },
    Color = "#1E2128", Opacity = 0.85 })
  bar("Health", 12)
  bar("Hunger", 42)
  bar("Thirst", 72)
  effectsText = UI.Create("Text", { Name = "Effects", Position = { 0, 18, 0, 126 }, Size = { 0, 320, 0, 22 },
    Text = "", FontSize = 16, TextColor = "#FFB070" })

  objective = UI.Create("Text", { Name = "Objective", Anchor = { 0.5, 0 }, Position = { 0.5, 0, 0, 18 },
    Size = { 0, 560, 0, 30 }, Text = "", FontSize = 20, TextColor = "#FFFFFF", HorizontalAlignment = "Center" })
  coins = UI.Create("Text", { Name = "Coins", Anchor = { 1, 0 }, Position = { 1, -16, 0, 64 }, Size = { 0, 160, 0, 28 },
    Text = "Coins: 0", FontSize = 20, TextColor = "#FFD84A", HorizontalAlignment = "Right" })

  inventoryPanel = UI.Create("Frame", { Name = "InventoryPanel", Anchor = { 1, 0.5 }, Position = { 1, -16, 0.5, 0 },
    Size = { 0, 200, 0, 190 }, Color = "#1E2128", Opacity = 0.85 })
  UI.Create("Text", { Name = "InventoryTitle", Parent = inventoryPanel, Position = { 0, 14, 0, 8 }, Size = { 0, 172, 0, 24 },
    Text = "Inventory (I)", FontSize = 18, TextColor = "#FFFFFF" })

  prompt = UI.Create("Text", { Name = "Prompt", Anchor = { 0.5, 1 }, Position = { 0.5, 0, 1, -96 }, Size = { 0, 640, 0, 30 },
    Text = "", FontSize = 22, TextColor = "#FFFFFF", HorizontalAlignment = "Center", Visible = false })
  hint = UI.Create("Text", { Name = "Hint", Anchor = { 0.5, 1 }, Position = { 0.5, 0, 1, -136 }, Size = { 0, 640, 0, 28 },
    Text = "", FontSize = 18, TextColor = "#B8E0FF", HorizontalAlignment = "Center", Visible = false })

  -- Stats and coins: attributes on the player (whichever script changes them).
  for name in pairs(COLORS) do
    showStat(name, player:GetAttribute(name))
    player:OnAttributeChanged(name, function(value) showStat(name, value) end)
  end
  showCoins(player:GetAttribute("Coins"))
  player:OnAttributeChanged("Coins", showCoins)
  showInventory({})

  Events.On("InventoryChanged", function(info)
    if info.owner == player then
      showInventory(info.items)
    end
  end)
  Events.On("PromptChanged", function(info)
    prompt.Visible = info.text ~= nil
    prompt.Text = info.text and ("E: " .. info.text) or ""
  end)
  Events.On("StatusChanged", function(info)
    if info.object == player then
      effects[info.effect] = info.active or nil
      showEffects()
    end
  end)
  Events.On("ObjectiveChanged", function(info)
    objective.Text = info.done and info.text or string.format("Objective %d/%d: %s", info.index, info.count, info.text)
  end)
  Events.On("Hint", showHint)
end

function Update(dt)
  if Input.IsKeyPressed("I") then
    inventoryPanel.Visible = not inventoryPanel.Visible
  end
end
