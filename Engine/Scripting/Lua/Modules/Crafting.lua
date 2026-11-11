-- Wisko.Crafting: optional recipes, crafting stations and timed crafting jobs on plain Lua tables.
--
--   local Crafting = require("Wisko.Crafting")
--   local recipes = Crafting.Recipes({
--     { Id = "Plank", Inputs = { Wood = 1 }, Outputs = { Plank = 4 }, Time = 1.5, Station = "Workbench" },
--     { Id = "Pickaxe", Inputs = { Plank = 2, Stone = 3 }, Outputs = { Pickaxe = 1 }, Time = 3, Station = "Workbench" },
--     { Id = "SharpenAxe", Inputs = { Stone = 1 }, Outputs = function(ctx) ctx.item:SetAttribute("Sharp", true) end },
--   })
--   local bench = Crafting.Station({ Kind = "Workbench", Recipes = recipes, Object = Scene.Find("Workbench") })
--   local job, reason = bench:Start("Plank", inventory)     -- inputs are taken now; outputs arrive after Time seconds
--   job:OnComplete(function(job) print("made", job.Recipe.Id) end)
--
-- An inventory is anything with Count(item) -> number, Remove(item, n) -> bool and Add(item, n), called with a dot (a
-- script's exports, such as an inventory script's, work as they are): the developer's own
-- (Crafting.TableInventory(t) wraps a plain { item = count } table). Recipes may require a station kind, and may have
-- a Requires(ctx) -> ok, reason rule (skills, unlocks, tools...). Outputs are item counts or a function (upgrades and
-- any other effect). Nothing about items, stations or rules is built in. Ordinary Lua on Wisko's public API.

local Signal = require("Wisko.Signal")

local Crafting = {}

-- A plain table { item = count } as an inventory.
function Crafting.TableInventory(items)
  items = items or {}
  local inventory = { Items = items }
  function inventory.Count(item) return items[item] or 0 end
  function inventory.Remove(item, count)
    count = count or 1
    if (items[item] or 0) < count then
      return false
    end
    items[item] = items[item] - count
    if items[item] == 0 then
      items[item] = nil
    end
    return true
  end
  function inventory.Add(item, count)
    items[item] = (items[item] or 0) + (count or 1)
  end
  return inventory
end

-- An inventory's functions are called with a dot, as a script's exports are (inventory.Count(item)).
local function call(inventory, name, ...)
  local f = type(inventory) == "table" and inventory[name] or nil
  if type(f) ~= "function" then
    error("the inventory has no " .. name .. " function (an inventory needs Count, Remove and Add)", 4)
  end
  return f(...)
end

local RecipeBook = {}
RecipeBook.__index = RecipeBook

-- A recipe book from a list of recipes: { Id, Name, Inputs = { item = count }, Outputs = { item = count } or
-- function(ctx), Time (seconds, 0), Station (a kind, or nil for anywhere), Requires(ctx) -> ok, reason, Category }.
function Crafting.Recipes(list)
  if type(list) ~= "table" then
    error("Crafting.Recipes expects a list of recipes", 2)
  end
  local book = setmetatable({ _byId = {}, _list = {} }, RecipeBook)
  for i, recipe in ipairs(list) do
    book:Add(recipe, i)
  end
  return book
end

function RecipeBook:Add(recipe, index)
  if type(recipe) ~= "table" or type(recipe.Id) ~= "string" then
    error("recipe " .. tostring(index or "?") .. " needs an Id (a string)", 3)
  end
  if self._byId[recipe.Id] ~= nil then
    error("there are two recipes with the Id \"" .. recipe.Id .. "\"", 3)
  end
  recipe.Inputs = recipe.Inputs or {}
  recipe.Time = recipe.Time or 0
  self._byId[recipe.Id] = recipe
  self._list[#self._list + 1] = recipe
end

function RecipeBook:Get(id)
  return self._byId[id]
end

-- Recipes, optionally only those filter(recipe) accepts.
function RecipeBook:List(filter)
  local result = {}
  for _, recipe in ipairs(self._list) do
    if filter == nil or filter(recipe) then
      result[#result + 1] = recipe
    end
  end
  return result
end

-- Whether a recipe can be made now: ok, missing ({ item = how many more }), reason. ctx is handed to Requires.
function RecipeBook:CanCraft(id, inventory, ctx, count)
  local recipe = self._byId[id]
  if recipe == nil then
    return false, {}, "there is no recipe \"" .. tostring(id) .. "\""
  end
  count = count or 1
  ctx = ctx or {}
  if recipe.Station ~= nil and ctx.station ~= nil and ctx.station.Kind ~= recipe.Station then
    return false, {}, recipe.Id .. " is made at a " .. recipe.Station
  end
  if recipe.Station ~= nil and ctx.station == nil and not ctx.anywhere then
    return false, {}, recipe.Id .. " is made at a " .. recipe.Station
  end
  local missing, short = {}, false
  local names = {}
  for item in pairs(recipe.Inputs) do
    names[#names + 1] = item
  end
  table.sort(names)
  for _, item in ipairs(names) do
    local need = recipe.Inputs[item] * count
    local have = call(inventory, "Count", item) or 0
    if have < need then
      missing[item] = need - have
      short = true
    end
  end
  if short then
    local parts = {}
    for _, item in ipairs(names) do
      if missing[item] then
        parts[#parts + 1] = missing[item] .. " " .. item
      end
    end
    return false, missing, "needs " .. table.concat(parts, ", ") .. " more"
  end
  if recipe.Requires ~= nil then
    local ok, reason = recipe.Requires(ctx, recipe)
    if not ok then
      return false, {}, reason or "not possible now"
    end
  end
  return true, {}, nil
end

local Station = {}
Station.__index = Station

-- A crafting station. Options: Kind (what recipes' Station names), Recipes (a recipe book), Object (the station's scene
-- object, optional), Queue (jobs at once, 1), Speed (time multiplier, 1: a Speed of 2 crafts twice as fast).
function Crafting.Station(options)
  options = options or {}
  if options.Recipes == nil then
    error("Crafting.Station expects Recipes (Crafting.Recipes)", 2)
  end
  return setmetatable({
    Kind = options.Kind or "Station",
    Recipes = options.Recipes,
    Object = options.Object,
    Queue = options.Queue or 1,
    Speed = options.Speed or 1,
    _jobs = {},
    _started = Signal.new(),
    _crafted = Signal.new(),
    _cancelled = Signal.new(),
  }, Station)
end

local Job = {}
Job.__index = Job

-- Starts crafting: inputs are taken from the inventory now, outputs go into it when the time is up. Options: Count (how
-- many times, 1), Context (handed to Requires and output functions; inventory, station and count are added), Owner.
-- Returns the job, or nil and why.
function Station:Start(id, inventory, options)
  options = options or {}
  local count = options.Count or 1
  if #self._jobs >= self.Queue then
    return nil, "the " .. self.Kind .. " is busy"
  end
  local ctx = options.Context or {}
  ctx.station, ctx.inventory, ctx.count, ctx.owner = self, inventory, count, options.Owner
  local ok, _, reason = self.Recipes:CanCraft(id, inventory, ctx, count)
  if not ok then
    return nil, reason
  end
  local recipe = self.Recipes:Get(id)
  -- Take everything first; if any removal fails, give back what was taken.
  local taken = {}
  for item, amount in pairs(recipe.Inputs) do
    if call(inventory, "Remove", item, amount * count) then
      taken[item] = amount * count
    else
      for back, n in pairs(taken) do
        call(inventory, "Add", back, n)
      end
      return nil, "could not take " .. item .. " from the inventory"
    end
  end
  local duration = recipe.Time * count / math.max(self.Speed, 1e-6)
  local job = setmetatable({
    Recipe = recipe, Station = self, Inventory = inventory, Count = count, Context = ctx, Duration = duration,
    State = "Crafting", StartedAt = Time.elapsedTime, _taken = taken, _complete = Signal.new(),
  }, Job)
  self._jobs[#self._jobs + 1] = job
  if duration <= 0 then
    job:_finish()
  else
    job._timer = Timer.After(duration, function() job:_finish() end)
  end
  self._started:Fire(job)
  return job
end

-- 0 at the start, 1 when done.
function Job:GetProgress()
  if self.State ~= "Crafting" or self.Duration <= 0 then
    return self.State == "Done" and 1 or 0
  end
  return math.min(1, (Time.elapsedTime - self.StartedAt) / self.Duration)
end

function Job:GetRemaining()
  if self.State ~= "Crafting" then
    return 0
  end
  return math.max(0, self.Duration - (Time.elapsedTime - self.StartedAt))
end

function Job:OnComplete(f)
  return self._complete:Connect(f)
end

function Job:_remove()
  local jobs = self.Station._jobs
  for i = #jobs, 1, -1 do
    if jobs[i] == self then
      table.remove(jobs, i)
    end
  end
end

function Job:_finish()
  if self.State ~= "Crafting" then
    return
  end
  self.State = "Done"
  self:_remove()
  local outputs = self.Recipe.Outputs
  local made = {}
  if type(outputs) == "function" then
    made = outputs(self.Context, self) or {}
  elseif type(outputs) == "table" then
    for item, amount in pairs(outputs) do
      call(self.Inventory, "Add", item, amount * self.Count)
      made[item] = amount * self.Count
    end
  end
  self.Made = made
  self._complete:Fire(self, made)
  self.Station._crafted:Fire(self, made)
  Events.Emit("Crafted", { recipe = self.Recipe.Id, station = self.Station.Kind, count = self.Count, made = made })
end

-- Stops the job; with refund (default true) its inputs go back into the inventory. Returns true when it was running.
function Job:Cancel(refund)
  if self.State ~= "Crafting" then
    return false
  end
  self.State = "Cancelled"
  if self._timer ~= nil then
    self._timer:Cancel()
  end
  self:_remove()
  if refund ~= false then
    for item, amount in pairs(self._taken) do
      call(self.Inventory, "Add", item, amount)
    end
  end
  self.Station._cancelled:Fire(self)
  return true
end

function Station:GetJobs()
  return { table.unpack(self._jobs) }
end

function Station:IsBusy()
  return #self._jobs >= self.Queue
end

function Station:OnStarted(f) return self._started:Connect(f) end
function Station:OnCrafted(f) return self._crafted:Connect(f) end
function Station:OnCancelled(f) return self._cancelled:Connect(f) end

-- The recipes this station can make now with this inventory (and why the others cannot).
function Station:GetAvailable(inventory, ctx)
  local available, blocked = {}, {}
  ctx = ctx or {}
  ctx.station = self
  for _, recipe in ipairs(self.Recipes:List(function(r) return r.Station == nil or r.Station == self.Kind end)) do
    local ok, _, reason = self.Recipes:CanCraft(recipe.Id, inventory, ctx)
    if ok then
      available[#available + 1] = recipe
    else
      blocked[#blocked + 1] = { Recipe = recipe, Reason = reason }
    end
  end
  return available, blocked
end

return Crafting
