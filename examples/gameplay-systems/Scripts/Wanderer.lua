-- Wanderer.lua (on the "Fox" model): wandering, written from primitives. The fox picks a random walkable spot near its
-- home (a seeded Random, so it wanders the same way each session), walks there with a Navigation agent, looks around,
-- and picks another. The model has no collider, so the agent moves it directly ("Transform") rather than by velocity.
-- It runs away from the player when they come close.

local HOME_RADIUS = 18
local rng = Random.new("fox")
local agent
local home
local resting = false

local function wander()
  for _ = 1, 10 do
    local offset = rng:PointInCircle(HOME_RADIUS)
    local spot = Navigation.GetNearestWalkable(home + offset, 4)
    if spot ~= nil and agent:SetDestination(spot) then
      self:CrossFadeAnimation("Walk", 0.3)
      agent.Speed = 4
      return
    end
  end
end

function Start()
  home = self.Transform.Position + Vector3.new(0, 0, 0)
  -- glTF models face +Z (a Wisko object faces -Z): FaceYawOffset turns its facing round.
  agent = Navigation.CreateAgent(self, { Move = "Transform", Speed = 4, StoppingDistance = 0.5, Radius = 1, FaceYawOffset = 180 })
  agent:OnArrived(function()
    if resting then
      return
    end
    resting = true
    self:CrossFadeAnimation("Survey", 0.3)
    Timer.After(rng:Number(2, 5), function()
      resting = false
      wander()
    end)
  end)
  agent:OnFailed(function() Timer.After(1, wander) end)
  wander()
end

function Update(dt)
  -- Too close: run to a spot on the other side of home.
  local player = Shared.player
  if player ~= nil and not resting and agent.Speed < 9 and Vector3.distance(player.Transform.Position, self.Transform.Position) < 7 then
    local away = self.Transform.Position - player.Transform.Position
    local spot = Navigation.GetNearestWalkable(self.Transform.Position + Vector3.normalize(Vector3.new(away.x, 0, away.z)) * 12, 6)
    if spot ~= nil and agent:SetDestination(spot) then
      agent.Speed = 10
      self:CrossFadeAnimation("Run", 0.15, { Speed = 0.6 })
    end
  end
  agent:Update(dt)
end
