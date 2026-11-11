-- Status.lua: damage, healing, knockback and timed status effects for this object (the player, the dummies).
-- Health is the "Health" attribute (Survival.lua uses the same one). Effects are timers owned by this script, so they
-- end when the object is destroyed or Play stops; applying an effect again restarts its clock (no stacking).
--   Damage(amount, source?)      Heal(amount)
--   Knockback(fromPoint, speed)  pushed away from a point (and a little up) at about that speed, whatever its mass
--   Apply(effect, seconds)       "Burn" (5 damage every 0.5 s), "Slow" (half speed), "Regen" (+5 Health every 0.5 s)
--   Has(effect), Clear(effect)
-- Events: "Damaged" { object, amount, source, health }, "StatusChanged" { object, effect, active }, "Died" { object }.
local MAX_HEALTH = 100
local active = {} -- effect name -> { timer, ticker }
local dead = false

local function health()
  return self:GetAttribute("Health") or MAX_HEALTH
end

local function setHealth(value)
  self:SetAttribute("Health", math.clamp(value, 0, MAX_HEALTH))
end

local function damage(amount, source)
  if dead or amount <= 0 then
    return
  end
  setHealth(health() - amount)
  Events.Emit("Damaged", { object = self, amount = amount, source = source, health = health() })
end

local function heal(amount)
  if not dead then
    setHealth(health() + amount)
  end
end

local EFFECTS = {
  Burn = { every = 0.5, tick = function() damage(5, "Burn") end },
  Regen = { every = 0.5, tick = function() heal(5) end },
  Slow = {
    begin = function() self:SetAttribute("SpeedMultiplier", 0.5) end,
    finish = function() self:SetAttribute("SpeedMultiplier", 1) end,
  },
}

local function clear(name)
  local effect = active[name]
  if effect == nil then
    return
  end
  active[name] = nil
  effect.timer:Cancel()
  if effect.ticker then
    effect.ticker:Cancel()
  end
  if EFFECTS[name].finish then
    EFFECTS[name].finish()
  end
  Events.Emit("StatusChanged", { object = self, effect = name, active = false })
end

local function apply(name, seconds)
  local def = EFFECTS[name]
  if def == nil then
    error("unknown status effect " .. tostring(name) .. " (Burn, Slow, Regen)", 2)
  end
  if dead then
    return
  end
  local effect = active[name]
  if effect then
    effect.timer:Cancel()
    effect.timer = Timer.After(seconds, function() clear(name) end)
    return
  end
  effect = { timer = Timer.After(seconds, function() clear(name) end) }
  if def.every then
    effect.ticker = Timer.Every(def.every, def.tick)
  end
  active[name] = effect
  if def.begin then
    def.begin()
  end
  print("[Status] " .. self.Name .. ": " .. name .. " for " .. seconds .. " s")
  Events.Emit("StatusChanged", { object = self, effect = name, active = true, seconds = seconds })
end

local function knockback(from, speed)
  local away = self.Transform.Position - from
  away = Vector3.new(away.x, 0, away.z)
  if Vector3.magnitude(away) < 0.01 then
    away = Vector3.new(0, 0, 1)
  end
  local push = Vector3.normalize(away) * speed + Vector3.new(0, speed * 0.4, 0)
  self:ApplyImpulse(push * self.Mass) -- impulse = mass x velocity change
  self:SetAttribute("KnockbackUntil", Time.elapsedTime + 0.4) -- Player.lua lets go of the controls meanwhile
end

function Start()
  if not self:HasAttribute("Health") then
    setHealth(MAX_HEALTH)
  end
  self:SetAttribute("SpeedMultiplier", 1)
  self:OnAttributeChanged("Health", function(value)
    if value <= 0 and not dead then
      dead = true
      for name in pairs(active) do
        clear(name)
      end
      Events.Emit("Died", { object = self })
    elseif value > 0 then
      dead = false
    end
  end)
end

return {
  Damage = damage,
  Heal = heal,
  Knockback = knockback,
  Apply = apply,
  Clear = clear,
  Has = function(name) return active[name] ~= nil end,
  Health = health,
}
