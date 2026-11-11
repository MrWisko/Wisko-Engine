-- Guard.lua (on the invisible "GuardBody"): an animated guard written from Wisko's primitives, no AI framework.
--   * Patrols a route around the warehouse (a Navigation agent; the route is the developer's list of points).
--   * Sees: a Perception sensor (range, view cone, line of sight). Seeing the player, it follows them.
--   * Hears: running footsteps (Perception.OnNoise): it walks to where the noise was.
--   * Loses the player behind walls (after a short memory) and goes back to its patrol.
--   * Catching the player (within 2.5 studs) sends them back to their last checkpoint.
-- The "GuardRobot" model only shows it: walking, running and idle follow its real speed. N draws its path and view.

local ROUTE = {
  Vector3.new(-26, 0, -6), Vector3.new(26, 0, -6), Vector3.new(26, 0, -44), Vector3.new(-26, 0, -44),
}
local PATROL_SPEED, CHASE_SPEED = 6, 11

local robot, agent, sensor
local state = "patrol"          -- "patrol", "chase", "investigate", "return"
local stateLabel
local lostAt
local caughtCooldown = 0
local debugOn = false

local function setState(new)
  if state == new then
    return
  end
  state = new
  stateLabel.Text = "Guard: " .. new
  Events.Emit("GuardState", new)
end

local function patrol()
  agent.Speed = PATROL_SPEED
  agent:Patrol(ROUTE, { Loop = true, Wait = 1 })
  setState("patrol")
end

function Start()
  self.RotationLocked = true
  self.Visible = false
  robot = Scene.Find("GuardRobot")
  robot:SetParent(self, Vector3.new(0, -2.25, 0), Vector3.new(0, 180, 0))
  robot:PlayAnimation("Idle")
  stateLabel = UI.Create("Text", { Name = "GuardState", Position = { 0, 16, 0, 48 }, Size = { 0, 360, 0, 22 },
    FontSize = 15, TextColor = "#FFB4A8", Text = "Guard: patrol", HorizontalAlignment = "Left" })


  agent = Navigation.CreateAgent(self, { Speed = PATROL_SPEED, Acceleration = 30, StoppingDistance = 0.8, Radius = 1.2 })
  agent:OnFailed(function(reason)
    if state ~= "patrol" then
      patrol()
    end
  end)
  patrol()

  sensor = Perception.CreateSensor(self, { Range = 32, FieldOfView = 110, OnlyTags = "Player", EyeOffset = Vector3.new(0, 1.2, 0),
    Memory = 2, Interval = 0.1 })
  sensor:OnDetected(function(player)
    setState("chase")
    agent.Speed = CHASE_SPEED
    agent:Follow(player, 1.5)
    Shared.game.Hint("The guard has seen you!", 2)
  end)
  sensor:OnLost(function()
    lostAt = Time.elapsedTime
    setState("return")
    patrol()
  end)
  Perception.OnNoise(self, function(noise)
    if state == "patrol" or state == "investigate" then
      setState("investigate")
      agent.Speed = PATROL_SPEED + 2
      agent:SetDestination(noise.Position)
    end
  end)
  agent:OnArrived(function()
    if state == "investigate" then
      -- Nothing here: look around a little, then back to the route.
      Timer.After(1.5, function()
        if state == "investigate" then
          patrol()
        end
      end)
    end
  end)
end

function Update(dt)
  if Input.IsKeyPressed("N") then
    debugOn = not debugOn
    sensor.Debug = debugOn
  end
  local status, velocity = agent:Update(dt)
  if debugOn then
    agent:DrawPath(state == "chase" and "#FF453A" or "#33FF66")
    Navigation.DrawGrid(self.Transform.Position, 6)
  end

  -- The model shows what the body really does.
  local speed = math.sqrt(velocity.x * velocity.x + velocity.z * velocity.z)
  if speed < 0.5 then
    robot:CrossFadeAnimation("Idle", 0.2)
  elseif speed < 8.5 then
    robot:CrossFadeAnimation("Walking", 0.2, { Speed = speed / 5.5 })
  else
    robot:CrossFadeAnimation("Running", 0.2, { Speed = speed / 12 })
  end

  -- Caught.
  local player = Shared.player
  caughtCooldown = caughtCooldown - dt
  if player ~= nil and state == "chase" and caughtCooldown <= 0 and Vector3.distance(player.Transform.Position, self.Transform.Position) < 2.6 then
    caughtCooldown = 3
    Events.Emit("Caught", { by = self })
    Shared.game.Hint("Caught! Back to your last checkpoint", 2.5)
    if Shared.checkpoints then
      Shared.checkpoints:Respawn(player)
    end
    patrol()
  end
end
