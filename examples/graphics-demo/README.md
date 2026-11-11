# Graphics Showcase

One project with twelve areas, one per graphics feature, drawn by the Universal renderer (Windows / OpenGL). Its
models are in [`Models`](Models) (see [`Models/CREDITS.md`](Models/CREDITS.md) for authors and licences), its scripts in
[`Scripts`](Scripts). [`docs/graphics.md`](../../docs/graphics.md) explains the features and the Lua API they use.

## Create the project

```
dotnet run --project Tests/Wisko.Tests -- --make-graphics-demo "%USERPROFILE%\Documents\Wisko\Projects"
```

This creates a project called **Graphics Showcase** (it refuses when one already exists, so it never overwrites
anything). The models go through the editor's own import (copied into `Assets/Models`), the brick, plank and grass
textures are generated into `Assets/Textures`, the scripts are copied into `Assets/Scripts`. Open it from the Hub.

## Look around

In the editor every area is a group in the World panel (**01 PBR Materials** ... **12 Quality Presets**). Press **Play**
for the guided tour:

| Key | Does |
|---|---|
| PageDown / PageUp | Next / previous area (the camera moves there; the top bar says what it shows) |
| Home / End | First area / free camera (PageDown resumes the tour) |
| 1 / 2 / 3 / 4 | Quality preset Low / Medium / High / Ultra |
| B / O / F / V | Bloom / ambient occlusion / fog / vignette on or off |
| N / M / P | Night (22:00) / morning (7:00) / pause or resume the clock |
| H | Help panel |

The status line (bottom right) shows the quality, the effects, the game clock and the frame rate. Like every Play
change, Stop brings the project's settings back.

## The areas

| # | Area | What to look at |
|---|---|---|
| 1 | PBR materials | 30 spheres: metallic rises front to back (0, 0.25, 0.5, 0.75, 1), roughness left to right (0 to 1). Smooth ones show the sky in their reflection and a sharp sun highlight; rough ones a broad one; metals take their colour into the reflection. |
| 2 | Textured models | The water bottle (base colour, metal/roughness, normal, occlusion and glow maps), the normal-map mirror test (all four columns should shade alike), the texture settings and alpha tests (every row should show a green check), a brick wall (colour and normal map, tiled in studs) and a plank crate. |
| 3 | Coloured lights | A closed room: red, green and blue point lights, a magenta spot light with shadows, and an orb whose light cycles through the colours (Neon.lua). |
| 4 | Real-time shadows | Sun shadows through a pergola, a windmill whose shadow sweeps the ground (Windmill.lua), steps, and a spot light with its own shadow on a pedestal. |
| 5 | Glow and bloom | A dark booth: glowing bars at strengths 1, 2, 4, 8 and 16, a pulsing neon tube (Neon.lua), neon orbs, and the Khronos emissive strength test. Bloom is on in this project (B toggles it). |
| 6 | Ambient occlusion | Light grey stairs, a crate corner, slats and a colonnade: soft contact shadows in the corners. Press O to compare. |
| 7 | Day and night | A street with lamps and houses. The clock runs a 3-minute day (Clock.lua with Wisko.DayNight); after dusk the lamps switch on and the windows glow. N jumps to night, M to morning. |
| 8 | Fog and atmosphere | An avenue of pillars fading into the fog (the tour turns fog on here; F toggles it anywhere). |
| 9 | Toon style | Trees, a house, a snowman: toon shading in bands with drawn outlines (separate from the editor's selection outline). |
| 10 | Animated character | The textured Fox running a loop (FoxRun.lua) and another one surveying: skinned animation with its texture and shadows. |
| 11 | Reflections | Chrome, gold and copper spheres reflect the sky; a polished floor, glass and smooth plastic catch it at grazing angles. Reflections are of the sky only (no ray tracing, no screen-space reflections). |
| 12 | Quality presets | Fine shadows through slats, a crate corner, a glowing bar and two point lights with shadows. Press P to stop the clock, then 1 to 4 to compare: Low has no post-processing (no bloom, ambient occlusion or vignette: the status line says "on (not at Low)") and simple shadows; point-light shadows appear from High. |

## Notes

- The scene uses only Wisko's editor features and the public Lua API; nothing here is special to the showcase.
- The Universal renderer is Windows / OpenGL only for now. On macOS (bgfx / Metal) the editor is built to draw this project with the Classic
  renderer instead (no materials, textures, lights, shadows or post-processing); that path has not been tested on a Mac.
