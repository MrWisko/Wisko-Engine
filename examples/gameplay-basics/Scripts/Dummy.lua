-- Dummy.lua: a training target. It is an enemy (the "Enemy" tag: blasts look for it), shows its Health in its colour
-- (green to red) and, 3 seconds after dropping to 0, is healed again. Status.lua on the same object does the rest.
function Start()
  self:AddTag("Enemy")
  self:OnAttributeChanged("Health", function(value)
    local t = math.clamp(value / 100, 0, 1)
    self.Color = string.format("#%02X%02X40", math.floor(255 - 120 * t), math.floor(60 + 160 * t))
  end)
  Events.On("Died", function(info)
    if info.object == self then
      print("DUMMY down " .. self.Name)
      Timer.After(3, function()
        self:SetAttribute("Health", 100)
      end)
    end
  end)
end
