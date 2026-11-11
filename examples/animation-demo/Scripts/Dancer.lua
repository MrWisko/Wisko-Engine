-- Dancer.lua: an animated model that cycles through some of its clips with smooth crossfades, at its own tempo.
-- Several Dancers on copies of one model ("Dancer 1", "Dancer 2", ...) play different clips at different speeds at
-- the same time: every instance has its own playback (the model data is shared).
local CLIPS = { "Dance", "Wave", "Yes", "ThumbsUp", "No", "Punch" }
local HOLD = 3.5  -- seconds on each clip
local FADE = 0.4  -- crossfade between clips

local index, timer, speed = 1, 0, 1

function Start()
  -- Each Dancer starts elsewhere in the list, at its own tempo (from the number in its name).
  local number = tonumber(string.match(self.Name, "%d+")) or 1
  index = (number - 1) % #CLIPS + 1
  speed = 0.7 + 0.2 * number
  local ok, reason = self:PlayAnimation(CLIPS[index], { Speed = speed })
  if not ok then
    print("[Dancer] " .. self.Name .. ": " .. reason)
  end
end

function Update(dt)
  timer = timer + dt
  if timer >= HOLD then
    timer = 0
    index = index % #CLIPS + 1
    self:CrossFadeAnimation(CLIPS[index], FADE, { Speed = speed })
  end
end
