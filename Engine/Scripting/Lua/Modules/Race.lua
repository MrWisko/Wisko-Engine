-- Wisko.Race: optional timing for courses: ordered checkpoints, laps, split times, best times and rankings.
--
--   local Race = require("Wisko.Race")
--   local race = Race.new({ Checkpoints = { Scene.Find("CP1"), Scene.Find("CP2"), Scene.Find("CP3") }, Laps = 2 })
--   race:AddRacer(player)
--   race:OnLap(function(racer, lap, lapTime) print(racer.Name, lap, lapTime) end)
--   race:OnFinished(function(racer, total, place) print("finished in " .. Race.FormatTime(total)) end)
--   race:Start()            -- or race:Start(3): a 3 second countdown first
--
-- Checkpoints are trigger Parts, passed in order; the last one of a lap is the finish line (with Laps = 1 and one
-- checkpoint, a sprint to it). A checkpoint touched out of order is reported (OnWrongCheckpoint) and does not count.
-- Times are game time (Time.elapsedTime: frame-accurate, paused with the game); Validate(racer, index) can add any
-- rule (e.g. "must be in a car"). Ordinary Lua on Wisko's public API: copy it to Assets/Scripts/Wisko/Race.lua.

local Signal = require("Wisko.Signal")

local Race = {}
Race.__index = Race

-- "1:05.32" (minutes, seconds, hundredths); under a minute "5.32".
function Race.FormatTime(seconds)
  if type(seconds) ~= "number" then
    return "-"
  end
  local minutes = math.floor(seconds / 60)
  local rest = seconds - minutes * 60
  if minutes > 0 then
    return string.format("%d:%05.2f", minutes, rest)
  end
  return string.format("%.2f", rest)
end

-- Options: Checkpoints (a list of trigger Parts, in order), Laps (1), Racers (a list of objects), Validate(racer,
-- checkpointIndex) -> bool, Clock (a function giving the time in seconds; default Time.elapsedTime).
function Race.new(options)
  options = options or {}
  local checkpoints = options.Checkpoints
  if type(checkpoints) ~= "table" or #checkpoints == 0 then
    error("Race.new expects Checkpoints: a list of trigger Parts in order", 2)
  end
  local race = setmetatable({
    Checkpoints = checkpoints,
    Laps = options.Laps or 1,
    Validate = options.Validate,
    Clock = options.Clock or function() return Time.elapsedTime end,
    State = "Waiting", -- "Waiting", "Countdown", "Running", "Finished"
    StartTime = nil,
    _racers = {},       -- id -> record
    _order = {},        -- ids in the order added
    _finishers = 0,
    _connections = {},
    _started = Signal.new(),
    _checkpoint = Signal.new(),
    _lap = Signal.new(),
    _finished = Signal.new(),
    _wrong = Signal.new(),
    _allFinished = Signal.new(),
  }, Race)
  for index, checkpoint in ipairs(checkpoints) do
    if type(checkpoint) ~= "table" or checkpoint.Id == nil then
      error("Race.new: checkpoint " .. index .. " is not a scene object", 2)
    end
    race._connections[#race._connections + 1] = checkpoint:OnTriggerEntered(function(other)
      if other ~= nil then
        race:_touched(other, index)
      end
    end)
  end
  for _, racer in ipairs(options.Racers or {}) do
    race:AddRacer(racer)
  end
  return race
end

function Race:AddRacer(racer)
  if type(racer) ~= "table" or racer.Id == nil then
    error("race:AddRacer expects a scene object", 2)
  end
  if self._racers[racer.Id] == nil then
    self._racers[racer.Id] = { Racer = racer, Lap = 1, Next = 1, Splits = {}, LapTimes = {}, LapStart = nil, Finished = false,
      Time = nil, Place = nil, LastCheckpointTime = nil }
    self._order[#self._order + 1] = racer.Id
  end
end

-- Starts the clock (after countdown seconds, if given: OnStarted fires then).
function Race:Start(countdown)
  if countdown ~= nil and countdown > 0 then
    self.State = "Countdown"
    self.CountdownEnds = self.Clock() + countdown
    Timer.After(countdown, function()
      self:Start()
    end)
    return
  end
  self.State = "Running"
  self.StartTime = self.Clock()
  for _, id in ipairs(self._order) do
    local record = self._racers[id]
    record.LapStart = self.StartTime
    record.Lap, record.Next, record.Finished, record.Time, record.Place = 1, 1, false, nil, nil
    record.Splits, record.LapTimes = {}, {}
  end
  self._finishers = 0
  self._started:Fire(self)
  Events.Emit("RaceStarted", { checkpoints = #self.Checkpoints, laps = self.Laps })
end

-- Seconds since the start (nil before it).
function Race:GetElapsed()
  if self.StartTime == nil then
    return nil
  end
  return self.Clock() - self.StartTime
end

function Race:_touched(racer, index)
  local record = self._racers[racer.Id]
  if record == nil or self.State ~= "Running" or record.Finished then
    return
  end
  if index ~= record.Next then
    -- Touching the one just passed again is not "wrong" (a racer may linger in a checkpoint).
    local previous = record.Next == 1 and #self.Checkpoints or record.Next - 1
    if index ~= previous then
      self._wrong:Fire(racer, index, record.Next)
    end
    return
  end
  if self.Validate ~= nil and not self.Validate(racer, index) then
    self._wrong:Fire(racer, index, record.Next)
    return
  end
  local now = self.Clock()
  local split = now - self.StartTime
  record.Splits[#record.Splits + 1] = { Lap = record.Lap, Checkpoint = index, Time = split }
  record.LastCheckpointTime = split
  self._checkpoint:Fire(racer, index, record.Lap, split)
  if index < #self.Checkpoints then
    record.Next = index + 1
    return
  end
  -- The lap is done.
  local lapTime = now - record.LapStart
  record.LapTimes[#record.LapTimes + 1] = lapTime
  if record.BestLap == nil or lapTime < record.BestLap then
    record.BestLap = lapTime
  end
  self._lap:Fire(racer, record.Lap, lapTime)
  if record.Lap >= self.Laps then
    record.Finished = true
    record.Time = split
    self._finishers = self._finishers + 1
    record.Place = self._finishers
    self._finished:Fire(racer, split, record.Place)
    Events.Emit("RaceFinished", { racer = racer, time = split, place = record.Place })
    local all = true
    for _, id in ipairs(self._order) do
      if not self._racers[id].Finished then
        all = false
      end
    end
    if all then
      self.State = "Finished"
      self._allFinished:Fire(self)
    end
    return
  end
  record.Lap = record.Lap + 1
  record.Next = 1
  record.LapStart = now
end

-- One racer's progress: { Lap, NextCheckpoint, Splits = {{Lap, Checkpoint, Time}}, LapTimes, BestLap, Finished, Time, Place }.
function Race:GetProgress(racer)
  local record = self._racers[racer.Id]
  if record == nil then
    return nil
  end
  return { Lap = record.Lap, NextCheckpoint = record.Next, Splits = record.Splits, LapTimes = record.LapTimes, BestLap = record.BestLap,
    Finished = record.Finished, Time = record.Time, Place = record.Place }
end

function Race:GetSplits(racer)
  local record = self._racers[racer.Id]
  return record and record.Splits or {}
end

-- Racers in order: by default finished ones by time, then the others by laps and checkpoints done (and who got there
-- first). compare(a, b) over the progress tables (with Racer) gives any other order.
function Race:GetRankings(compare)
  local list = {}
  for _, id in ipairs(self._order) do
    local record = self._racers[id]
    list[#list + 1] = { Racer = record.Racer, Lap = record.Lap, NextCheckpoint = record.Next, Finished = record.Finished, Time = record.Time,
      Place = record.Place, Done = #record.Splits, LastCheckpointTime = record.LastCheckpointTime }
  end
  table.sort(list, compare or function(a, b)
    if a.Finished ~= b.Finished then
      return a.Finished
    end
    if a.Finished then
      return a.Time < b.Time
    end
    if a.Done ~= b.Done then
      return a.Done > b.Done
    end
    return (a.LastCheckpointTime or math.huge) < (b.LastCheckpointTime or math.huge)
  end)
  return list
end

function Race:OnStarted(f) return self._started:Connect(f) end
function Race:OnCheckpoint(f) return self._checkpoint:Connect(f) end
function Race:OnLap(f) return self._lap:Connect(f) end
function Race:OnFinished(f) return self._finished:Connect(f) end
function Race:OnWrongCheckpoint(f) return self._wrong:Connect(f) end
function Race:OnAllFinished(f) return self._allFinished:Connect(f) end

function Race:Destroy()
  for _, connection in ipairs(self._connections) do
    connection:Disconnect()
  end
end

-- Measures an object's speed (studs per second) from how far it really moved, smoothed over about smoothing seconds.
-- speedometer:Update(dt) every frame; .Speed, .TopSpeed.
function Race.Speedometer(object, smoothing)
  local meter = { Speed = 0, TopSpeed = 0 }
  local last
  smoothing = smoothing or 0.25
  function meter:Update(dt)
    if not object.Exists or dt <= 0 then
      return self.Speed
    end
    local position = object.Transform.Position
    if last ~= nil then
      local now = Vector3.distance(position, last) / dt
      local k = 1 - math.exp(-dt / smoothing)
      self.Speed = self.Speed + (now - self.Speed) * k
      self.TopSpeed = math.max(self.TopSpeed, self.Speed)
    end
    last = Vector3.new(position.x, position.y, position.z)
    return self.Speed
  end
  return meter
end

-- Best times kept in a GameSave slot (one table per course name): best:Get(), best:Submit(time) -> improved.
function Race.BestTimes(slot, course)
  local store = {}
  local data = GameSave.Exists(slot) and GameSave.Load(slot) or {}
  if type(data) ~= "table" then
    data = {}
  end
  function store:Get()
    return data[course]
  end
  function store:Submit(time)
    if data[course] == nil or time < data[course] then
      data[course] = time
      local ok, problem = GameSave.Save(slot, data, { Metadata = { Kind = "BestTimes" } })
      return true, ok, problem
    end
    return false
  end
  return store
end

return Race
