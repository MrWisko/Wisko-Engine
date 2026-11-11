-- SpeedPulse.lua: playback speed. The model plays one clip whose speed rises and falls over time, between a quarter
-- and almost twice the authored speed (SetAnimationSpeed changes how fast the current clip plays; 1 = as authored).
local CLIP = "Run"

function Start()
  if not self:PlayAnimation(CLIP) then
    -- Another model: its first clip, whatever it is called.
    local clips = self:GetAnimations()
    if #clips > 0 then
      CLIP = clips[1].Name
      self:PlayAnimation(1)
    end
  end
  print("[SpeedPulse] " .. self.Name .. " plays " .. CLIP .. " (" .. string.format("%.2f", self:GetAnimationLength() or 0) .. " s at speed 1)")
end

function Update(dt)
  self:SetAnimationSpeed(1 + 0.75 * math.sin(Time.elapsedTime * 0.8))
end
