-- Wisko.Quests: an optional quest log: quests with objectives, dependencies, progress, rewards and a HUD tracker.
--
--   local Quests = require("Wisko.Quests")
--   local log = Quests.new()
--   log:Define({ Id = "wood", Title = "Firewood", Requires = { "intro" },
--     Objectives = { { Id = "gather", Text = "Gather wood", Target = 3 } },
--     Reward = function(quest, log) inventory.Add("Coin", 10) end })
--   log:Start("wood")
--   log:Progress("wood", "gather")           -- +1; at 3 the objective is done, and the quest completes (AutoComplete)
--   log:OnCompleted(function(quest) print(quest.Title .. " done") end)
--   local tracker = Quests.Tracker(log)      -- optional HUD: active quests and their objectives
--
-- Quest states: "Locked" (a required quest is not completed), "Available", "Active", "Completed", "Failed". Rewards are
-- functions: what a reward is (items, coins, stats, unlocks) is the game's. log:Save() gives plain data for GameSave;
-- log:Load(data) puts it back. Ordinary Lua on Wisko's public API: copy it to Assets/Scripts/Wisko/Quests.lua.

local Signal = require("Wisko.Signal")

local Quests = {}

local Log = {}
Log.__index = Log

function Quests.new(options)
  options = options or {}
  local log = setmetatable({
    _quests = {},
    _order = {},
    _changed = Signal.new(),
    _completed = Signal.new(),
    _objective = Signal.new(),
    _started = Signal.new(),
  }, Log)
  if options.OnChanged then log._changed:Connect(options.OnChanged) end
  return log
end

-- A quest: { Id, Title, Description, Requires = { quest ids }, Objectives = { { Id, Text, Target (1) } },
-- Reward = function(quest, log), AutoComplete (true: completes when every objective is done), Data (anything) }.
function Log:Define(definition)
  if type(definition) ~= "table" or type(definition.Id) ~= "string" then
    error("log:Define expects a quest with an Id (a string)", 2)
  end
  if self._quests[definition.Id] ~= nil then
    error("there is already a quest \"" .. definition.Id .. "\"", 2)
  end
  local quest = {
    Id = definition.Id, Title = definition.Title or definition.Id, Description = definition.Description,
    Requires = definition.Requires or {}, Reward = definition.Reward, AutoComplete = definition.AutoComplete ~= false,
    Data = definition.Data, State = "Available", Objectives = {}, Rewarded = false,
  }
  for i, objective in ipairs(definition.Objectives or {}) do
    if type(objective.Id) ~= "string" then
      error("quest " .. definition.Id .. ": objective " .. i .. " needs an Id (a string)", 2)
    end
    quest.Objectives[#quest.Objectives + 1] = { Id = objective.Id, Text = objective.Text or objective.Id, Target = objective.Target or 1,
      Value = 0, Done = false, Optional = objective.Optional == true }
  end
  self._quests[quest.Id] = quest
  self._order[#self._order + 1] = quest.Id
  self:_refreshLocks()
  return quest
end

function Log:_refreshLocks()
  for _, id in ipairs(self._order) do
    local quest = self._quests[id]
    if quest.State == "Available" or quest.State == "Locked" then
      local locked = false
      for _, required in ipairs(quest.Requires) do
        local other = self._quests[required]
        if other == nil or other.State ~= "Completed" then
          locked = true
        end
      end
      quest.State = locked and "Locked" or "Available"
    end
  end
end

function Log:Get(id)
  return self._quests[id]
end

function Log:GetState(id)
  local quest = self._quests[id]
  return quest and quest.State or nil
end

function Log:IsActive(id) return self:GetState(id) == "Active" end
function Log:IsComplete(id) return self:GetState(id) == "Completed" end

-- Whether a quest can start now: ok, reason.
function Log:CanStart(id)
  local quest = self._quests[id]
  if quest == nil then
    return false, "there is no quest \"" .. tostring(id) .. "\""
  end
  if quest.State == "Locked" then
    local missing = {}
    for _, required in ipairs(quest.Requires) do
      if self:GetState(required) ~= "Completed" then
        local other = self._quests[required]
        missing[#missing + 1] = other and other.Title or required
      end
    end
    return false, "first complete: " .. table.concat(missing, ", ")
  end
  if quest.State ~= "Available" then
    return false, quest.Title .. " is " .. string.lower(quest.State)
  end
  return true
end

function Log:Start(id)
  local ok, reason = self:CanStart(id)
  if not ok then
    return false, reason
  end
  local quest = self._quests[id]
  quest.State = "Active"
  self._started:Fire(quest)
  self._changed:Fire(quest, "started")
  Events.Emit("QuestStarted", { quest = id })
  return true
end

local function objectiveOf(quest, objectiveId)
  for _, objective in ipairs(quest.Objectives) do
    if objective.Id == objectiveId then
      return objective
    end
  end
  return nil
end

-- Adds to an objective (amount 1 by default; never past its Target). Returns the new value, or nil and why.
function Log:Progress(questId, objectiveId, amount)
  local quest = self._quests[questId]
  if quest == nil then
    return nil, "there is no quest \"" .. tostring(questId) .. "\""
  end
  local objective = objectiveOf(quest, objectiveId)
  if objective == nil then
    return nil, quest.Title .. " has no objective \"" .. tostring(objectiveId) .. "\""
  end
  return self:SetProgress(questId, objectiveId, objective.Value + (amount or 1))
end

function Log:SetProgress(questId, objectiveId, value)
  local quest = self._quests[questId]
  if quest == nil then
    return nil, "there is no quest \"" .. tostring(questId) .. "\""
  end
  if quest.State ~= "Active" then
    return nil, quest.Title .. " is not active"
  end
  local objective = objectiveOf(quest, objectiveId)
  if objective == nil then
    return nil, quest.Title .. " has no objective \"" .. tostring(objectiveId) .. "\""
  end
  value = math.max(0, math.min(objective.Target, value))
  if value == objective.Value then
    return value
  end
  objective.Value = value
  objective.Done = value >= objective.Target
  self._objective:Fire(quest, objective)
  self._changed:Fire(quest, "progress")
  if quest.AutoComplete and self:_allDone(quest) then
    self:Complete(questId)
  end
  return value
end

function Log:_allDone(quest)
  for _, objective in ipairs(quest.Objectives) do
    if not objective.Done and not objective.Optional then
      return false
    end
  end
  return true
end

-- Completes a quest (its Reward runs once) and unlocks the quests that required it.
function Log:Complete(id)
  local quest = self._quests[id]
  if quest == nil or quest.State == "Completed" then
    return false
  end
  quest.State = "Completed"
  if quest.Reward ~= nil and not quest.Rewarded then
    quest.Rewarded = true
    quest.Reward(quest, self)
  end
  self:_refreshLocks()
  self._completed:Fire(quest)
  self._changed:Fire(quest, "completed")
  Events.Emit("QuestCompleted", { quest = id })
  return true
end

function Log:Fail(id)
  local quest = self._quests[id]
  if quest == nil or quest.State ~= "Active" then
    return false
  end
  quest.State = "Failed"
  self._changed:Fire(quest, "failed")
  return true
end

-- Quests in a state ("Active" by default), in definition order.
function Log:GetQuests(state)
  local list = {}
  for _, id in ipairs(self._order) do
    local quest = self._quests[id]
    if state == nil or quest.State == state then
      list[#list + 1] = quest
    end
  end
  return list
end

function Log:GetActive() return self:GetQuests("Active") end

function Log:OnChanged(f) return self._changed:Connect(f) end
function Log:OnStarted(f) return self._started:Connect(f) end
function Log:OnCompleted(f) return self._completed:Connect(f) end
function Log:OnObjectiveChanged(f) return self._objective:Connect(f) end

-- Plain data (for GameSave): { [quest id] = { State, Rewarded, Objectives = { [objective id] = value } } }.
function Log:Save()
  local data = {}
  for _, id in ipairs(self._order) do
    local quest = self._quests[id]
    local objectives = {}
    for _, objective in ipairs(quest.Objectives) do
      objectives[objective.Id] = objective.Value
    end
    data[id] = { State = quest.State, Rewarded = quest.Rewarded, Objectives = objectives }
  end
  return data
end

-- Puts saved progress back (quests are defined first; rewards already given are not given again).
function Log:Load(data)
  if type(data) ~= "table" then
    return
  end
  for id, saved in pairs(data) do
    local quest = self._quests[id]
    if quest ~= nil and type(saved) == "table" then
      quest.State = saved.State or quest.State
      quest.Rewarded = saved.Rewarded == true
      for _, objective in ipairs(quest.Objectives) do
        local value = saved.Objectives and saved.Objectives[objective.Id]
        if type(value) == "number" then
          objective.Value = math.max(0, math.min(objective.Target, value))
          objective.Done = objective.Value >= objective.Target
        end
      end
      self._changed:Fire(quest, "loaded")
    end
  end
  self:_refreshLocks()
end

-- An optional HUD: a panel listing the active quests and their objectives ("Gather wood 2/3"), kept up to date.
-- Options: Position ({ xScale, xOffset, yScale, yOffset }, default top right), Width (300), FontSize (16).
function Quests.Tracker(log, options)
  options = options or {}
  local width = options.Width or 300
  local panel = UI.Create("Frame", { Name = "QuestTracker", Anchor = { 1, 0 }, Position = options.Position or { 1, -16, 0, 80 },
    Size = { 0, width, 0, 40 }, Color = options.Color or "#1C2030", Opacity = 0.8, ZOrder = 40 })
  local lines = {}
  local tracker = { Panel = panel }

  local function refresh()
    for _, line in ipairs(lines) do
      line:Destroy()
    end
    lines = {}
    local y = 8
    local active = log:GetActive()
    if #active == 0 then
      lines[1] = UI.Create("Text", { Name = "QuestTrackerEmpty", Parent = panel, Position = { 0, 10, 0, y }, Size = { 1, -20, 0, 22 },
        Text = "No active quest", FontSize = options.FontSize or 16, TextColor = "#9AA3B5", HorizontalAlignment = "Left" })
      y = y + 24
    end
    for _, quest in ipairs(active) do
      lines[#lines + 1] = UI.Create("Text", { Name = "QuestTitle_" .. quest.Id, Parent = panel, Position = { 0, 10, 0, y }, Size = { 1, -20, 0, 22 },
        Text = quest.Title, FontSize = (options.FontSize or 16) + 1, TextColor = "#FFD479", HorizontalAlignment = "Left" })
      y = y + 24
      for _, objective in ipairs(quest.Objectives) do
        local mark = objective.Done and "[x] " or "[ ] "
        lines[#lines + 1] = UI.Create("Text", { Name = "QuestObjective_" .. quest.Id .. "_" .. objective.Id, Parent = panel,
          Position = { 0, 18, 0, y }, Size = { 1, -28, 0, 20 }, Text = mark .. objective.Text .. " " .. objective.Value .. "/" .. objective.Target,
          FontSize = options.FontSize or 16, TextColor = objective.Done and "#7EE787" or "#F0F2F8", HorizontalAlignment = "Left" })
        y = y + 22
      end
    end
    panel:SetSize(0, width, 0, y + 8)
  end

  tracker._connection = log:OnChanged(refresh)
  refresh()

  function tracker:Refresh() refresh() end

  function tracker:Destroy()
    self._connection:Disconnect()
    panel:Destroy()
  end

  return tracker
end

return Quests
