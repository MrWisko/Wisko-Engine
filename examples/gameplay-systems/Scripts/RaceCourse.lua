-- RaceCourse.lua (on "RaceStart"): a 2-lap course through three checkpoint arches (Wisko.Race). E at the start post
-- starts a 3 second countdown; checkpoints must be passed in order (a wrong one is ignored and said); the HUD shows the
-- time, the lap and the splits; the best time is kept in its own GameSave slot, across sessions.

local Race = require("Wisko.Race")

local race, best
local hud, splitsText
local racing = false

local function startRace()
  if racing then
    return
  end
  racing = true
  race = Race.new({ Checkpoints = { Scene.Find("CP1"), Scene.Find("CP2"), Scene.Find("CP3") }, Laps = 2, Racers = { Shared.player } })
  race:OnStarted(function() Shared.game.Hint("GO!", 1.5) end)
  race:OnCheckpoint(function(racer, index, lap, split)
    splitsText.Text = string.format("Lap %d, checkpoint %d: %s", lap, index, Race.FormatTime(split))
  end)
  race:OnWrongCheckpoint(function(racer, index, expected)
    Shared.game.Hint("Wrong checkpoint: go to number " .. expected, 1.5)
  end)
  race:OnLap(function(racer, lap, lapTime)
    Shared.game.Hint("Lap " .. lap .. ": " .. Race.FormatTime(lapTime), 2)
  end)
  race:OnFinished(function(racer, total)
    local improved = best:Submit(total)
    Shared.game.Hint("Finished in " .. Race.FormatTime(total) .. (improved and "  NEW BEST!" or ("  (best " .. Race.FormatTime(best:Get()) .. ")")), 4)
    Shared.game.quests:SetProgress("racer", "finish", 1)
    racing = false
    race:Destroy()
  end)
  race:Start(3)
end

function Start()
  best = Race.BestTimes("Records", "SkyCourse")
  hud = UI.Create("Text", { Name = "RaceTimer", Anchor = { 0.5, 0 }, Position = { 0.5, 0, 0, 48 }, Size = { 0, 420, 0, 28 }, FontSize = 22,
    TextColor = "#FFFFFF", Text = "", Visible = false })
  splitsText = UI.Create("Text", { Name = "RaceSplits", Anchor = { 0.5, 0 }, Position = { 0.5, 0, 0, 78 }, Size = { 0, 420, 0, 22 }, FontSize = 16,
    TextColor = "#C9D1E0", Text = "" })
  self:AddTag("Interactable")
  Shared.interactions = Shared.interactions or {}
  Shared.interactions[self.Id] = {
    Prompt = function()
      if racing then return "" end
      local record = best:Get()
      return "Start the race (2 laps)" .. (record and ("  best " .. Race.FormatTime(record)) or "")
    end,
    Use = startRace,
  }
end

function Update()
  if race == nil or not racing then
    hud.Visible = false
    return
  end
  hud.Visible = true
  if race.State == "Countdown" then
    hud.Text = string.format("Get ready... %d", math.ceil(race.CountdownEnds - Time.elapsedTime))
    return
  end
  local progress = race:GetProgress(Shared.player)
  hud.Text = string.format("%s   Lap %d/2   next: %d", Race.FormatTime(race:GetElapsed() or 0), progress.Lap, progress.NextCheckpoint)
end
