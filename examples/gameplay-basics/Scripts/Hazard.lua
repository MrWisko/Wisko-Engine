-- Hazard.lua: a trigger zone that gives whoever is inside a timed status effect (through their Status.lua). Which
-- effect comes from the zone's name (edit ZONES to add one). While someone stays inside, the effect is refreshed
-- twice a second, so it lasts SECONDS after they step out.
local ZONES = { Lava = "Burn", Mud = "Slow", HealPad = "Regen" }
local SECONDS = 2
local effect
local refreshed = {} -- object Id -> time of the last refresh

function Start()
  for prefix, name in pairs(ZONES) do
    if string.sub(self.Name, 1, #prefix) == prefix then
      effect = name
    end
  end
end

local function give(other)
  if effect == nil or other == nil then
    return
  end
  local status = other:GetScript("Status")
  if status and Time.elapsedTime - (refreshed[other.Id] or -1) >= 0.5 then
    refreshed[other.Id] = Time.elapsedTime
    status.Apply(effect, SECONDS)
  end
end

function OnTriggerEnter(other)
  give(other)
end

function OnTriggerStay(other)
  give(other)
end
