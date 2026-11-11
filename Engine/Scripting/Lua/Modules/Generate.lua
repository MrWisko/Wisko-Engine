-- Wisko.Generate: optional helpers for procedural generation: jobs spread over frames (with progress and
-- cancellation), grids, Poisson-disc and scattered sampling, and batch creation.
--
--   local Generate = require("Wisko.Generate")
--   local rng = Random.new(1234)                          -- the same seed gives the same world
--   local points = Generate.PoissonDisk(rng, { Min = Vector3.new(-50, 0, -50), Max = Vector3.new(50, 0, 50), Radius = 6 })
--   local job = Generate.Spawn(points, function(p, i)
--     return Scene.Create("Cylinder", { Name = "Tree" .. i, Position = p + Vector3.new(0, 3, 0), Size = Vector3.new(1, 6, 1) })
--   end, { Budget = 2, OnProgress = function(p) bar:SetSize(p, 0, 1, 0) end })
--   job:OnDone(function(made) print(#made .. " trees") end)    -- job:Cancel() stops it (what was made stays)
--
-- A job runs a function as a coroutine a few milliseconds per frame (Budget), so a large generation never freezes the
-- game or the editor, and never runs into a script's per-callback instruction budget. Inside it, job:Step(done,
-- total) reports progress and yields when the frame's budget is spent. Nothing here knows about genres: what is placed
-- and where is the caller's. Ordinary Lua on Wisko's public API: copy it to Assets/Scripts/Wisko/Generate.lua.

local Signal = require("Wisko.Signal")

local Generate = {}

local Job = {}
Job.__index = Job

-- Options: Budget (milliseconds of work per frame, 2), OnProgress(f(progress 0..1, done, total)), OnDone(f(result)).
-- work(job) runs inside the job; what it returns is the result.
function Generate.Job(work, options)
  if type(work) ~= "function" then
    error("Generate.Job expects a function(job)", 2)
  end
  options = options or {}
  local job = setmetatable({
    Budget = options.Budget or 2,
    Progress = 0,
    Done = false,
    Cancelled = false,
    Running = true,
    Result = nil,
    Error = nil,
    Frames = 0,
    _progress = Signal.new(),
    _done = Signal.new(),
    _deadline = 0,
  }, Job)
  if options.OnProgress then job._progress:Connect(options.OnProgress) end
  if options.OnDone then job._done:Connect(options.OnDone) end
  job._thread = coroutine.create(function()
    return work(job)
  end)
  job._tick = Time.OnUpdate(function()
    job:_resume()
  end)
  return job
end

function Job:_resume()
  if not self.Running then
    return
  end
  self.Frames = self.Frames + 1
  self._deadline = os.clock() + self.Budget / 1000
  local ok, result = coroutine.resume(self._thread)
  if not ok then
    self.Running = false
    self.Error = result
    self._tick:Disconnect()
    error("Generate.Job failed: " .. tostring(result), 0)
  end
  if coroutine.status(self._thread) == "dead" then
    self.Running = false
    self.Done = true
    self.Progress = 1
    self.Result = result
    self._tick:Disconnect()
    self._progress:Fire(1, nil, nil, self)
    self._done:Fire(result, self)
  end
end

-- Inside the work: reports progress (done of total) and yields when this frame's time is used up.
function Job:Step(done, total)
  if total ~= nil and total > 0 then
    local progress = math.min(1, done / total)
    if progress ~= self.Progress then
      self.Progress = progress
      self._progress:Fire(progress, done, total, self)
    end
  end
  if os.clock() >= self._deadline then
    coroutine.yield()
  end
  if self.Cancelled then
    -- Unwinds the work quietly (Cancel already ended the job).
    coroutine.yield()
  end
end

-- Stops the job (what it already made stays). True when it was running.
function Job:Cancel()
  if not self.Running then
    return false
  end
  self.Running = false
  self.Cancelled = true
  self._tick:Disconnect()
  return true
end

function Job:OnProgress(f) return self._progress:Connect(f) end
function Job:OnDone(f) return self._done:Connect(f) end

-- Calls make(point, i) for every point, spread over frames; the result is the list of what make returned.
function Generate.Spawn(points, make, options)
  if type(points) ~= "table" or type(make) ~= "function" then
    error("Generate.Spawn expects a list of points and a function(point, i)", 2)
  end
  return Generate.Job(function(job)
    local made = {}
    for i = 1, #points do
      local value = make(points[i], i)
      if value ~= nil then
        made[#made + 1] = value
      end
      job:Step(i, #points)
    end
    return made
  end, options)
end

-- Cell centres of a width x depth grid of cellSize studs starting at origin (a vector; the grid lies in X and Z), with
-- f(x, z, position) called for each (x, z from 1). Returns the list of what f returned (or of the positions).
function Generate.Grid(width, depth, cellSize, origin, f)
  origin = origin or Vector3.new(0, 0, 0)
  local results = {}
  for z = 1, depth do
    for x = 1, width do
      local position = Vector3.new(origin.x + (x - 0.5) * cellSize, origin.y, origin.z + (z - 0.5) * cellSize)
      local value = position
      if f ~= nil then
        value = f(x, z, position)
      end
      if value ~= nil then
        results[#results + 1] = value
      end
    end
  end
  return results
end

-- Points at least Radius apart inside the box Min..Max (on the ground plane at Min.y), evenly spread (Bridson's
-- Poisson-disc sampling). Options: Min, Max, Radius, Limit (most points), Tries (30). The same generator state gives
-- the same points.
function Generate.PoissonDisk(rng, options)
  options = options or {}
  local low, high, radius = options.Min, options.Max, options.Radius
  if low == nil or high == nil or type(radius) ~= "number" or radius <= 0 then
    error("Generate.PoissonDisk expects Min, Max (vectors) and Radius (a number)", 2)
  end
  local tries, limit = options.Tries or 30, options.Limit or math.huge
  local cell = radius / math.sqrt(2)
  local width = math.max(1, math.ceil((high.x - low.x) / cell))
  local depth = math.max(1, math.ceil((high.z - low.z) / cell))
  local grid, points, active = {}, {}, {}

  local function key(gx, gz) return gz * (width + 1) + gx end

  local function fits(x, z)
    if x < low.x or x > high.x or z < low.z or z > high.z then
      return false
    end
    local gx, gz = math.floor((x - low.x) / cell), math.floor((z - low.z) / cell)
    for dz = -2, 2 do
      for dx = -2, 2 do
        local other = grid[key(gx + dx, gz + dz)]
        if other ~= nil then
          local ox, oz = other.x - x, other.z - z
          if ox * ox + oz * oz < radius * radius then
            return false
          end
        end
      end
    end
    return true
  end

  local function add(x, z)
    local point = Vector3.new(x, low.y, z)
    points[#points + 1] = point
    active[#active + 1] = point
    grid[key(math.floor((x - low.x) / cell), math.floor((z - low.z) / cell))] = point
  end

  add(rng:Number(low.x, high.x), rng:Number(low.z, high.z))
  while #active > 0 and #points < limit do
    local index = rng:Integer(1, #active)
    local origin = active[index]
    local found = false
    for _ = 1, tries do
      local angle = rng:Number(0, 2 * math.pi)
      local distance = rng:Number(radius, 2 * radius)
      local x, z = origin.x + math.cos(angle) * distance, origin.z + math.sin(angle) * distance
      if fits(x, z) then
        add(x, z)
        found = true
        break
      end
    end
    if not found then
      active[index] = active[#active]
      active[#active] = nil
    end
  end
  return points
end

-- count points anywhere inside the box Min..Max (uniform, may be close together).
function Generate.Scatter(rng, count, options)
  options = options or {}
  local low, high = options.Min, options.Max
  if low == nil or high == nil then
    error("Generate.Scatter expects Min and Max (vectors)", 2)
  end
  local points = {}
  for i = 1, count do
    points[i] = rng:PointInBox(low, high)
  end
  return points
end

return Generate
