-- Pulse.lua (on the player): three abilities, each with a cooldown.
--   F  shove: knocks back every loose object in front of you (within SHOVE_RANGE).
--   V  blast: damages Enemy-tagged objects within BLAST_RADIUS that you can see (a ray must reach them).
--   X  "X-ray" blast: the same targets, but through walls. It uses an overlap query, which has no line of sight,
--      and its filter (OnlyTags = "Enemy") means it never hits anything that is not an enemy.
local SHOVE_RANGE, SHOVE_SPEED = 8, 25
local BLAST_RADIUS, BLAST_DAMAGE = 20, 25
local COOLDOWN = 0.5
local ready = 0

local function flat(v)
  local f = Vector3.new(v.x, 0, v.z)
  return Vector3.magnitude(f) > 1e-4 and Vector3.normalize(f) or Vector3.new(0, 0, -1)
end

local function inSight(target)
  local from, to = self.Transform.Position, target.Transform.Position
  local hit = Physics.Raycast(from, to - from, Vector3.distance(from, to) + 1, { Ignore = self, IgnoreTriggers = true })
  return hit ~= nil and hit.Object == target
end

local function blast(throughWalls)
  local hits = 0
  local targets = Physics.OverlapSphere(self.Transform.Position, BLAST_RADIUS, { OnlyTags = "Enemy", IgnoreEnvironment = true, Ignore = self })
  for _, target in ipairs(targets) do
    local status = target:GetScript("Status")
    if status and (throughWalls or inSight(target)) then
      status.Damage(BLAST_DAMAGE, throughWalls and "X-ray" or "Blast")
      hits = hits + 1
    end
  end
  print("[Pulse] " .. (throughWalls and "X-ray" or "Blast") .. " hit " .. hits)
  Events.Emit("AbilityUsed", { ability = throughWalls and "X-ray" or "Blast", hits = hits })
  return hits
end

local function shove()
  local forward = flat(Camera.Forward)
  local here = self.Transform.Position
  local count = 0
  for _, target in ipairs(Physics.OverlapSphere(here, SHOVE_RANGE, { Ignore = self, IgnoreEnvironment = true, IgnoreTriggers = true })) do
    local to = target.Transform.Position - here
    if not target.Anchored and Vector3.dot(to, forward) > 0 then
      local status = target:GetScript("Status")
      if status then
        status.Knockback(here, SHOVE_SPEED)
      else
        target:ApplyImpulse(Vector3.normalize(Vector3.new(to.x, 0.4, to.z)) * SHOVE_SPEED * target.Mass)
      end
      count = count + 1
    end
  end
  print("[Pulse] Shove hit " .. count)
  Events.Emit("AbilityUsed", { ability = "Shove", hits = count })
  return count
end

function Update(dt)
  if Shared.vehicle ~= nil or Time.elapsedTime < ready then
    return
  end
  if Input.IsKeyPressed("F") then
    ready = Time.elapsedTime + COOLDOWN
    shove()
  elseif Input.IsKeyPressed("V") then
    ready = Time.elapsedTime + COOLDOWN
    blast(false)
  elseif Input.IsKeyPressed("X") then
    ready = Time.elapsedTime + COOLDOWN
    blast(true)
  end
end

return { Blast = blast, Shove = shove }
