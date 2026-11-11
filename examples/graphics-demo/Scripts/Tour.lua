-- Tour.lua (on the "Tour" marker): a camera that visits the showcase's 12 areas, with what each one shows.
--   PageDown / PageUp   next / previous area        Home   back to the first area
--   End                 free camera (the normal Play view; PageDown resumes the tour)
--   H                   show or hide the help panel
-- Every area is also a group in the World panel: select it in the editor to find it.

local Tour = {}

local AREAS = {
  { "PBR materials", "Metallic rises front to back (0 to 1), roughness left to right (0 to 1).",
    Vector3.new(-105, 14, 84), Vector3.new(-105, 2, 50) },
  { "Textured models", "Khronos sample models: water bottle (PBR maps), normal-map mirror test, texture and alpha tests.",
    Vector3.new(-35, 9, 82), Vector3.new(-35, 3, 50) },
  { "Coloured lights", "Red, green and blue point lights, a magenta spot and a colour-cycling orb in a closed room.",
    Vector3.new(35, 6, 60), Vector3.new(35, 2, 42) },
  { "Real-time shadows", "Sun shadows through a pergola, a turning windmill and a spot light with its own shadows.",
    Vector3.new(105, 18, 86), Vector3.new(105, 0, 48) },
  { "Glow and bloom", "Emissive signs at strengths 1, 2, 4, 8 and 16 (bloom is on in this project) and pulsing neon.",
    Vector3.new(-105, 6, 2), Vector3.new(-105, 4, -24) },
  { "Ambient occlusion", "Soft contact shadows in corners, under stairs and between slats (O toggles them).",
    Vector3.new(-35, 9, 6), Vector3.new(-35, 2, -24) },
  { "Day and night", "N: night, M: morning, P: pause the clock. Street lamps and windows light up after dusk.",
    Vector3.new(35, 8, 6), Vector3.new(35, 3, -25) },
  { "Fog and atmosphere", "Pillars fading into the fog along the avenue (the tour turns fog on here; F toggles it).",
    Vector3.new(74, 6, -20), Vector3.new(200, 7, -20) },
  { "Toon style", "Toon shading in bands with drawn outlines (separate from the editor's selection outline).",
    Vector3.new(-105, 12, -60), Vector3.new(-105, 2, -92) },
  { "Animated character", "The textured Fox running a loop (skinned animation with its texture and shadows).",
    Vector3.new(-35, 8, -64), Vector3.new(-35, 1, -90) },
  { "Reflections", "Chrome, gold and copper reflect the sky; the polished floor and glass catch it at grazing angles.",
    Vector3.new(35, 6, -64), Vector3.new(35, 3, -90) },
  { "Quality presets", "1 Low, 2 Medium, 3 High, 4 Ultra: compare shadows, ambient occlusion, bloom and point-light shadows.",
    Vector3.new(105, 10, -62), Vector3.new(105, 2, -92) },
}

local camera
local index = 1
local touring = false
local fogByTour = false
local hud = {}

local function text(name, options)
  options.Name = name
  options.FontSize = options.FontSize or 16
  options.TextColor = options.TextColor or "#E8ECF4"
  return UI.Create("Text", options)
end

local function buildHud()
  hud.bar = UI.Create("Frame", { Name = "TourBar", Position = { 0, 0, 0, 0 }, Size = { 1, 0, 0, 58 }, Color = "#141824", Opacity = 0.75, ZOrder = 5 })
  hud.title = text("TourTitle", { Parent = hud.bar, Position = { 0, 16, 0, 6 }, Size = { 1, -32, 0, 24 }, FontSize = 20,
    HorizontalAlignment = "Left", TextColor = "#FFD479" })
  hud.info = text("TourInfo", { Parent = hud.bar, Position = { 0, 16, 0, 32 }, Size = { 1, -32, 0, 20 }, FontSize = 15, HorizontalAlignment = "Left" })
  hud.help = UI.Create("Frame", { Name = "TourHelp", Anchor = { 0, 1 }, Position = { 0, 16, 1, -16 }, Size = { 0, 430, 0, 214 }, Color = "#141824",
    Opacity = 0.88, Visible = false, ZOrder = 60 })
  local lines = {
    "PageDown / PageUp: next / previous area   Home: first   End: free camera",
    "1 Low   2 Medium   3 High   4 Ultra   (quality presets)",
    "B bloom   O ambient occlusion   F fog   V vignette",
    "N night   M morning   P pause or resume the clock",
    "H hides this panel. Stop brings the project's settings back.",
  }
  for i, line in ipairs(lines) do
    text("HelpLine" .. i, { Parent = hud.help, Position = { 0, 14, 0, 12 + (i - 1) * 38 }, Size = { 1, -28, 0, 30 }, FontSize = 15,
      HorizontalAlignment = "Left", Text = line })
  end
end

local function show(i)
  index = (i - 1) % #AREAS + 1
  local area = AREAS[index]
  camera.Position = area[3]
  camera:LookAt(area[4])
  if not touring then
    Cameras.SetActive(camera, { Blend = 0.6 })
    touring = true
  end
  hud.title.Text = string.format("%d / %d  %s", index, #AREAS, area[1])
  hud.info.Text = area[2]

  -- The fog area turns fog on while it is shown (unless F already turned it on).
  if index == 8 and not Atmosphere.FogEnabled then
    Atmosphere.FogEnabled = true
    fogByTour = true
  elseif index ~= 8 and fogByTour then
    Atmosphere.FogEnabled = false
    fogByTour = false
  end
  print("Tour: " .. area[1])
end

function Tour.Index()
  return index
end

function Start()
  camera = Cameras.Create("Tour", { FieldOfView = 60 })
  buildHud()
  Shared.tour = Tour
  show(1)
end

function Update()
  if Input.IsKeyPressed("PageDown") then
    show(index + 1)
  elseif Input.IsKeyPressed("PageUp") then
    show(index - 1)
  elseif Input.IsKeyPressed("Home") then
    show(1)
  elseif Input.IsKeyPressed("End") and touring then
    touring = false
    Cameras.SetActive(nil, { Blend = 0.6 })
    hud.title.Text = "Free camera"
    hud.info.Text = "PageDown resumes the tour. H: help."
  end
  if Input.IsKeyPressed("H") then
    hud.help.Visible = not hud.help.Visible
  end
end
