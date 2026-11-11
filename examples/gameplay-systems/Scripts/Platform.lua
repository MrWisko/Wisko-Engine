-- Platform.lua (on the "SkyLift" platform): it travels back and forth along a curved path (Path, Catmull-Rom) from the
-- sky island down to the ground. Path.Follow drives the anchored platform by velocity, so whoever stands on it rides
-- along (carried by friction, like the moving platforms of the Gameplay Basics Demo). N draws the path.

local follower, path
local debugOn = false

function Start()
  local points = {}
  for i = 1, 6 do
    local marker = Scene.Find("LiftPoint" .. i)
    points[i] = marker.Transform.Position + Vector3.new(0, 0, 0)
  end
  path = Path.new(points, { Type = "CatmullRom" })
  follower = Path.Follow(self, path, { Speed = 5, PingPong = true })
  follower:OnLooped(function()
    -- A short stop at each end, so getting on and off is easy.
    follower:Pause()
    Timer.After(2, function() follower:Resume() end)
  end)
  print(string.format("SkyLift: a %.1f stud path through %d points", path.Length, path.Count))
end

function Update()
  if Input.IsKeyPressed("N") then
    debugOn = not debugOn
  end
  if debugOn then
    path:Draw("#33D6FF")
  end
end
