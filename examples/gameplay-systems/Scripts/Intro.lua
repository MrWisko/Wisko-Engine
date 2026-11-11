-- Intro.lua (on the "Intro" marker): a cinematic flyover when Play starts (C replays it, Space skips it). A named
-- camera ("Flyover") follows a curved path (camera:PlayPath, a Catmull-Rom Path) looking at the village; Cameras.SetActive
-- blends to it and back to the gameplay camera. While it plays, Shared.cinematic tells the player script to wait.

local flyover, playback

local function finish()
  if playback ~= nil then
    playback:Stop()
    playback = nil
  end
  Cameras.SetActive(nil, { Blend = 1.2 })
  Shared.cinematic = false
end

local function play()
  if playback ~= nil then
    return
  end
  local points = {}
  for i = 1, 5 do
    points[i] = Scene.Find("FlyPoint" .. i).Transform.Position + Vector3.new(0, 0, 0)
  end
  local path = Path.new(points, { Type = "CatmullRom" })
  Shared.cinematic = true
  flyover.Position = points[1]
  playback = flyover:PlayPath(path, { Duration = 9, LookAt = Scene.Find("Spawn") })
  playback:OnFinished(finish)
  Cameras.SetActive(flyover, { Blend = 0.8 })
  Shared.game.Hint("Space skips the flyover", 3)
end

function Start()
  flyover = Cameras.Create("Flyover", { FieldOfView = 60 })
  Cameras.OnActivated(function(camera, previous)
    print("Camera: " .. (camera and camera.Name or "gameplay") .. " (was " .. (previous and previous.Name or "gameplay") .. ")")
  end)
  play()
end

function Update()
  if Input.IsKeyPressed("C") then
    play()
  elseif playback ~= nil and Input.IsKeyPressed("Space") then
    finish()
  end
end
