-- Game.lua (on the invisible "Game" object): the showcase's shared state and HUD. Everything here is ordinary Lua on
-- Wisko's public API: the inventory is a table, quests come from Wisko.Quests, the clock from GameTime + Wisko.DayNight,
-- the weather from Wisko.Weather, saving from GameSave. Other scripts reach it through Shared.game.
--
-- Keys: F5 saves, F9 loads, T changes the weather, M shows the vault's security camera, H shows the controls.

local Quests = require("Wisko.Quests")
local DayNight = require("Wisko.DayNight")
local Weather = require("Wisko.Weather")
local Building = require("Wisko.Building")

local SAVE_SLOT = "Showcase"
local SAVE_VERSION = 1
local WEATHER_CYCLE = { "Clear", "Cloudy", "Rain", "Clear", "Fog", "Clear", "Storm", "Clear", "Snow" }

local game = {}
local items = {}
local hud = {}
local weather, cycle, tracker
local weatherIndex = 1
local monitor
local helpVisible = false
local hintUntil = 0

-- ---- Inventory: a plain table (and the three functions Wisko.Crafting asks of an inventory) ----

local function refreshInventory()
  local order = { "Coin", "Wood", "Stone", "Plank", "Gem", "Axe" }
  local parts = {}
  for _, item in ipairs(order) do
    if (items[item] or 0) > 0 or item == "Coin" or item == "Wood" then
      parts[#parts + 1] = item .. " " .. (items[item] or 0)
    end
  end
  hud.items.Text = table.concat(parts, "    ")
end

game.inventory = {
  Count = function(item) return items[item] or 0 end,
  Add = function(item, count)
    items[item] = (items[item] or 0) + (count or 1)
    refreshInventory()
    Events.Emit("InventoryChanged", { item = item, count = items[item] })
  end,
  Remove = function(item, count)
    count = count or 1
    if (items[item] or 0) < count then
      return false
    end
    items[item] = items[item] - count
    refreshInventory()
    Events.Emit("InventoryChanged", { item = item, count = items[item] })
    return true
  end,
}

-- A message at the bottom of the screen for a few seconds.
function game.Hint(text, seconds)
  hud.hint.Text = text
  hud.hint.Visible = true
  hintUntil = Time.elapsedTime + (seconds or 3)
end

-- The prompt for what E does now ("" hides it).
function game.Prompt(text)
  hud.prompt.Text = text ~= "" and ("[E] " .. text) or ""
  hud.prompt.Visible = text ~= ""
end

-- ---- Quests (definitions here; Mira's dialogue starts them) ----

local log = Quests.new()
game.quests = log
log:Define({ Id = "firewood", Title = "Firewood for Mira", Objectives = { { Id = "wood", Text = "Gather wood", Target = 5 } },
  AutoComplete = false })
log:Define({ Id = "vault", Title = "The Vault", Requires = { "firewood" },
  Objectives = { { Id = "open", Text = "Open the vault (two switches and the plate)" }, { Id = "gem", Text = "Bring the gem to Mira" } },
  AutoComplete = false })
log:Define({ Id = "builder", Title = "Builder", Objectives = { { Id = "place", Text = "Build 3 pieces (B)", Target = 3 } },
  Reward = function() game.inventory.Add("Coin", 15) game.Hint("Builder done: +15 coins") end })
log:Define({ Id = "racer", Title = "Racer", Objectives = { { Id = "finish", Text = "Finish the 2-lap race" } },
  Reward = function() game.inventory.Add("Coin", 25) game.Hint("Race finished: +25 coins") end })

-- ---- Weather and the day ----

local function setWeather(name, seconds)
  weather:Set(name, seconds or 6)
  hud.clock.Text = GameTime.Clock() .. "   Day " .. GameTime.Day .. "   " .. name
end

function game.NextWeather()
  weatherIndex = weatherIndex % #WEATHER_CYCLE + 1
  setWeather(WEATHER_CYCLE[weatherIndex])
  game.Hint("Weather: " .. WEATHER_CYCLE[weatherIndex], 2)
end

-- ---- Saving ----

-- Everything the game keeps: plain data only (positions as vectors, quests as Quests:Save(), the clock's state...).
function game.Save()
  local player = Shared.player
  local data = {
    Player = { Position = player.Transform.Position + Vector3.new(0, 0, 0), Yaw = player.Transform.Rotation.y },
    Items = items,
    Quests = log:Save(),
    Clock = GameTime.GetState(),
    Weather = weatherIndex,
    Built = Building.Describe(Scene.FindAllByTag("Built")),
    Vault = Shared.vaultOpen == true,
    Chopped = Shared.forest and Shared.forest.Save() or {},
    Equipped = Shared.equipped == true,
  }
  local ok, problem = GameSave.Save(SAVE_SLOT, data, { Version = SAVE_VERSION,
    Metadata = { Day = GameTime.Day, Time = GameTime.Clock(), Coins = items.Coin or 0 } })
  game.Hint(ok and ("Saved (day " .. GameTime.Day .. ", " .. GameTime.Clock() .. ")") or ("Could not save: " .. tostring(problem)), 3)
  return ok
end

function game.Load()
  local data, info = GameSave.Load(SAVE_SLOT)
  if data == nil then
    game.Hint("Nothing to load: " .. tostring(info), 3)
    return false
  end
  if info.Version > SAVE_VERSION then
    game.Hint("That save is from a newer version of this game", 3)
    return false
  end
  for key in pairs(items) do
    items[key] = nil
  end
  for item, count in pairs(data.Items or {}) do
    items[item] = count
  end
  refreshInventory()
  log:Load(data.Quests)
  GameTime.SetState(data.Clock)
  weatherIndex = data.Weather or 1
  setWeather(WEATHER_CYCLE[weatherIndex], 0)
  for _, built in ipairs(Scene.FindAllByTag("Built")) do
    built:Destroy()
  end
  Building.Rebuild(data.Built)
  if Shared.forest then Shared.forest.Load(data.Chopped or {}) end
  if data.Vault and Shared.openVault then Shared.openVault() end
  Shared.player:Teleport(data.Player.Position, Vector3.new(0, data.Player.Yaw or 0, 0))
  Events.Emit("GameLoaded", { equipped = data.Equipped == true })
  game.Hint("Loaded (day " .. GameTime.Day .. ", " .. GameTime.Clock() .. ")", 3)
  return true
end

-- ---- HUD ----

local function text(name, options)
  options.Name = name
  options.FontSize = options.FontSize or 18
  options.TextColor = options.TextColor or "#F0F2F8"
  return UI.Create("Text", options)
end

local function buildHud()
  hud.bar = UI.Create("Frame", { Name = "TopBar", Position = { 0, 0, 0, 0 }, Size = { 1, 0, 0, 40 }, Color = "#141824", Opacity = 0.7, ZOrder = 5 })
  hud.items = text("Items", { Parent = hud.bar, Position = { 0, 16, 0, 8 }, Size = { 0.5, 0, 0, 24 }, HorizontalAlignment = "Left" })
  hud.clock = text("Clock", { Parent = hud.bar, Anchor = { 0.5, 0 }, Position = { 0.5, 0, 0, 8 }, Size = { 0, 360, 0, 24 } })
  hud.prompt = text("Prompt", { Anchor = { 0.5, 1 }, Position = { 0.5, 0, 1, -270 }, Size = { 0, 600, 0, 30 }, FontSize = 20, TextColor = "#FFD479" })
  hud.hint = text("Hint", { Anchor = { 0.5, 1 }, Position = { 0.5, 0, 1, -300 }, Size = { 0, 800, 0, 28 }, TextColor = "#9FE3FF" })
  hud.help = UI.Create("Frame", { Name = "Help", Anchor = { 0, 1 }, Position = { 0, 16, 1, -16 }, Size = { 0, 470, 0, 270 }, Color = "#141824",
    Opacity = 0.85, Visible = false, ZOrder = 60 })
  local lines = {
    "WASD move, Shift run, Space jump, mouse look (Tab frees the mouse)",
    "E talk / chop / mine / use        1-9 choose in menus",
    "Q equip or put away the axe        B build (again: next piece, off)",
    "R turn the piece, left click place",
    "C replay the flyover        M vault camera on the HUD",
    "T next weather        N show the guard's path and view",
    "F5 save        F9 load        H this help",
    "",
    "Mira (by the well) has work for you. The guard does not like",
    "visitors: stay out of its sight, or run and it hears you.",
  }
  for i, line in ipairs(lines) do
    text("HelpLine" .. i, { Parent = hud.help, Position = { 0, 12, 0, 8 + (i - 1) * 25 }, Size = { 1, -24, 0, 24 }, FontSize = 16,
      HorizontalAlignment = "Left", Text = line })
  end
  hud.prompt.Visible = false
  refreshInventory()
end

-- The vault's security camera, shown in a corner of the HUD (a camera feed: rendered to a texture every frame).
local function toggleMonitor()
  if monitor == nil then
    local cam = Cameras.Find("VaultCam")
    if cam == nil then
      return
    end
    local ok, why = cam:CreateFeed({ Width = 384, Height = 216 })
    if not ok then
      game.Hint("The camera feed is not available: " .. tostring(why), 3)
      return
    end
    monitor = UI.Create("Frame", { Name = "Monitor", Anchor = { 1, 1 }, Position = { 1, -16, 1, -16 }, Size = { 0, 400, 0, 250 }, Color = "#0B0D12", ZOrder = 30 })
    UI.Create("Image", { Name = "MonitorFeed", Parent = monitor, Position = { 0, 8, 0, 26 }, Size = { 0, 384, 0, 216 }, Image = "camera:VaultCam" })
    text("MonitorLabel", { Parent = monitor, Position = { 0, 10, 0, 3 }, Size = { 1, -20, 0, 20 }, FontSize = 14, TextColor = "#FF6B6B",
      HorizontalAlignment = "Left", Text = "REC  VAULT CAM" })
  else
    monitor.Visible = not monitor.Visible
  end
end

-- Made when the file loads: every script's Start may use Shared.game (and its HUD).
Shared.game = game
-- The walkable map for the guard and the fox, made once before anyone needs it (the forest adds its trees later).
local navigation = Navigation.Build({ Min = Vector3.new(-80, -2, -80), Max = Vector3.new(110, 30, 130), CellSize = 1, AgentRadius = 1 })
print(string.format("Navigation: %d x %d cells, %d walkable, %d areas, baked in %.0f ms", navigation.Width, navigation.Depth,
  navigation.Walkable, navigation.Regions, navigation.Milliseconds))
buildHud()

function Start()
  GameTime.DayLength = 240     -- a whole day in 4 minutes
  GameTime.Set(1, 9)
  weather = Weather.new({ Initial = "Clear", WindTag = "Wind" })
  cycle = DayNight.Start({ Weather = weather })
  tracker = Quests.Tracker(log, { Position = { 1, -16, 0, 56 } })
  -- A new weather every 3 game hours.
  GameTime.Every(3, function() game.NextWeather() end)
  GameTime.OnDayChanged(function(day) game.Hint("Day " .. day .. " begins", 3) end)
  log:Start("builder")
  log:Start("racer")
  if GameSave.Exists(SAVE_SLOT) then
    local info = GameSave.GetInfo(SAVE_SLOT)
    game.Hint("A saved game exists (day " .. tostring(info and info.Metadata and info.Metadata.Day) .. "): F9 to continue", 6)
  else
    game.Hint("H shows the controls", 6)
  end
end

function Update(dt)
  if Input.IsKeyPressed("F5") then game.Save() end
  if Input.IsKeyPressed("F9") then game.Load() end
  if Input.IsKeyPressed("T") then game.NextWeather() end
  if Input.IsKeyPressed("M") then toggleMonitor() end
  if Input.IsKeyPressed("H") then
    helpVisible = not helpVisible
    hud.help.Visible = helpVisible
  end
  if hud.hint.Visible and Time.elapsedTime > hintUntil then
    hud.hint.Visible = false
  end
  hud.clock.Text = GameTime.Clock() .. "   Day " .. GameTime.Day .. "   " .. (weather.State or "")
end

return game
