-- Quality.lua (on the "Quality" marker): switches what the renderer does while playing, through the Graphics global.
--   1 / 2 / 3 / 4   Low / Medium / High / Ultra quality preset
--   B  bloom   O  ambient occlusion   F  fog (Atmosphere)   V  vignette
-- The status line shows the settings and the frame rate. Like every Play change, Stop brings the project's back.

local status
local frames, elapsed, fps = 0, 0, 0
local LEVELS = { ["1"] = "Low", ["2"] = "Medium", ["3"] = "High", ["4"] = "Ultra" }

local function onOff(value)
  return value and "on" or "off"
end

-- Low quality draws without post-processing: bloom, ambient occlusion and the vignette keep their setting (B, O and V
-- still switch it) but are not drawn until the quality goes up again.
local function effect(value)
  if Graphics.Quality == "Low" then
    return value and "on (not at Low)" or "off"
  end
  return onOff(value)
end

local function refresh()
  status.Text = string.format("%s quality   bloom %s   AO %s   fog %s   vignette %s   %s   %d fps",
    Graphics.Quality, effect(Graphics.Bloom), effect(Graphics.AmbientOcclusion), onOff(Atmosphere.FogEnabled),
    effect(Graphics.Vignette), GameTime.Clock(), fps)
end

function Start()
  status = UI.Create("Text", { Name = "QualityStatus", Anchor = { 1, 1 }, Position = { 1, -16, 1, -12 }, Size = { 0, 720, 0, 24 },
    FontSize = 15, TextColor = "#C8D2E0", HorizontalAlignment = "Right", ZOrder = 6 })
  refresh()
end

function Update(dt)
  for key, level in pairs(LEVELS) do
    if Input.IsKeyPressed(key) then
      Graphics.Quality = level
      print("Quality: " .. level)
    end
  end
  if Input.IsKeyPressed("B") then
    Graphics.Bloom = not Graphics.Bloom
  end
  if Input.IsKeyPressed("O") then
    Graphics.AmbientOcclusion = not Graphics.AmbientOcclusion
  end
  if Input.IsKeyPressed("F") then
    Atmosphere.FogEnabled = not Atmosphere.FogEnabled
  end
  if Input.IsKeyPressed("V") then
    Graphics.Vignette = not Graphics.Vignette
  end

  -- Frames per second over the last half second of real time.
  frames = frames + 1
  elapsed = elapsed + Time.unscaledDeltaTime
  if elapsed >= 0.5 then
    fps = math.floor(frames / elapsed + 0.5)
    frames, elapsed = 0, 0
  end
  refresh()
end
