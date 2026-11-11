# Graphics

Wisko has two renderers. The **Universal renderer** draws physically based materials and textures,
point, spot and directional lights, real-time shadows, HDR with tone mapping, bloom, ambient occlusion, sky
reflections, toon shading with outlines, and quality presets, all editable in the editor and from Lua. The original
renderer is the **Classic renderer**: older projects (without a `Graphics.json`) keep it until you switch.

The Universal renderer is **Windows / OpenGL only** for now. The macOS build (bgfx / Metal) draws every project with the Classic renderer;
see [Backends](#backends).

- [Classic and Universal](#classic-and-universal)
- [How a frame is drawn](#how-a-frame-is-drawn)
- [Materials](#materials)
- [Textures](#textures)
- [Lights](#lights)
- [Shadows](#shadows)
- [Ambient light, reflections and the Atmosphere](#ambient-light-reflections-and-the-atmosphere)
- [Image: HDR, tone mapping, post-processing](#image-hdr-tone-mapping-post-processing)
- [Quality presets](#quality-presets)
- [Editor](#editor)
- [Lua](#lua)
- [What is saved](#what-is-saved)
- [Backends](#backends)
- [Performance](#performance)
- [Known limitations](#known-limitations)
- [Testing](#testing)

## Classic and Universal

| | Classic | Universal |
|---|---|---|
| Used by | projects without `Graphics.json` (older projects), the macOS build | new projects (Windows), and any project switched in Environment & Graphics > Rendering |
| Colour | `Part.Color`, model vertex colours | `Part.Color` x material, textures, metal/roughness, glow |
| Light | the Atmosphere's sun and ambient | the sun, ambient from sky and ground, up to 64 Part lights |
| Shadows | none | sun (cascaded), spot and point lights |
| Image | display-space colour grade | linear HDR, exposure, tone mapping, bloom, ambient occlusion, vignette, white balance, FXAA |

Switching is one choice (Environment & Graphics > Rendering > Renderer) and an undo step; both renderers read the same scene, so
switching back loses nothing. Materials and lights are saved either way; the Classic renderer simply does not draw
them.

The Universal renderer is calibrated so a project looks alike in both: the Atmosphere's colours and intensities mean
the same, a part facing the sun shows the same colour as in Classic, and a part in shade the same ambient colour (in
Classic both are added in display space; Universal works in linear light, so faces turned partly towards the sun come
out somewhat brighter). Shadows, of course, only exist in Universal: a closed room is dark in Universal unless lights
are placed in it.

If the Universal renderer cannot start (a shader the driver will not compile), Wisko logs why, shows a short message
once, and draws with the Classic renderer; nothing else changes.

## How a frame is drawn

The Universal renderer is a forward renderer working in linear light:

1. **Collect**: the scene's parts, models (with their animated poses) and the baseplate, their materials and textures;
   the lights (at most 64 per frame: with more, those that matter most to the camera are kept); for each object the
   lights that reach it (up to the quality's limit per object, strongest first).
2. **Shadow maps**: the sun's cascades every frame; spot and point light maps only when the light or a shadow caster in
   its reach changed (animated casters update every frame).
3. **Environment**: the sky is captured into a small cube map with blurred levels for rough reflections, only when
   the sky changes (at most every 10 frames).
4. **Depth prepass and ambient occlusion** (when on).
5. **Scene** into an HDR target (RGBA16F) with 4x MSAA: opaque and cut-out surfaces, drawn outlines, the sky,
   see-through surfaces back to front, Lua particles.
6. **Post**: resolve, bloom, then one pass that applies exposure, white balance, saturation and contrast, tone mapping,
   vignette and converts to the display, into the window; FXAA if chosen.
7. **Editor overlays**: the scene's depth and the selection are restored in the window, so the editor's selection
   outlines, gizmos, labels and the World panel work exactly as before.

At **Low** quality there is no post-processing: surfaces are drawn straight into the window (with the window's 4x
MSAA), tone mapped and converted in their own shader. Camera feeds and project thumbnails use this direct path
too.

## Materials

Every primitive has a surface material; a model's materials come from its file, and each can be made the instance's
own. The Materials tab (Environment & Graphics > Material) edits them; Lua edits them through `object.Material`.

| Setting | Values | Notes |
|---|---|---|
| Shading | Lit, Simple, Unlit, Toon | Lit: metallic-roughness PBR (GGX). Simple: diffuse with a soft highlight, cheaper. Unlit: the colour as is (glowing signs, light bulbs). Toon: light in bands. |
| Color | `Part.Color` | A primitive's colour is its `Part.Color`, exactly as the Color tool and `object.Color` set it. The material's Tint (white by default) multiplies it. |
| Metallic | 0-1 | 0 for plastic, stone, wood; 1 for metals (their colour tints their reflection). |
| Roughness | 0-1 | 0 mirror smooth, 1 matte. Default 0.55. |
| Transparency | Opaque, Cutout, Blend | Cutout drops pixels below the cutoff (leaves, fences); Blend is see-through by Opacity (glass), drawn after opaque surfaces, back to front. |
| Glow | colour, strength 0-100 | Light the surface gives off (not light cast on others: use a Light for that). Above 1 it blooms when bloom is on. |
| Textures | colour, normal, metal/rough, occlusion, glow | See [Textures](#textures). |
| Tiling | X, Y | Mesh mapping: repeats over the mesh's own UVs. Studs mapping (primitives): projected from the part's faces in studs, one repeat per 4 studs at tiling 1 (2: every 2 studs), so a texture keeps its size when the part is resized. |
| Toon bands, Outline | 2-8; colour, width | The outline is a drawn rim around the object (an inverted hull), separate from the editor's selection outline. |
| Flat shading, double sided, cast / receive shadows | on / off | |

Presets (a starting point; colour, textures and tiling are kept): Plastic, Smooth Plastic, Matte, Metal, Brushed Metal,
Glass, Neon (glows in the part's colour), Toon, Unlit, Simple.

**Imported models** keep their files' materials: glTF/GLB metallic-roughness values and textures, alpha modes and
cutoffs, double-sided flags, emissive colour and `KHR_materials_emissive_strength`, `KHR_materials_unlit`, and sampler
wrap modes (repeat, clamp, mirrored repeat). OBJ and FBX colours are treated as display colours and their roughness is
estimated from shininess where they have no roughness. Editing a model material makes it that instance's own (it starts
from the imported look, its textures kept); **Back to Imported** returns to the file's. The instance's colour still
tints it.

## Textures

- **Formats**: PNG, JPEG, TGA and BMP from the Browse button (the picker), plus the images inside GLB/glTF files (PNG,
  JPEG). Decoding is StbImageSharp, which also reads GIF (first frame) and PSD (composite) when they arrive inside a
  model. KTX2/Basis, WebP and DDS images inside models are not decoded: the import says so and the slot is left empty.
- **Sizes**: any; larger than 4096 pixels on a side are halved until they fit. Mipmaps and up to 8x anisotropic
  filtering. Files over 256 MB are refused.
- **Colour spaces**: colour and glow textures are sRGB (decoded to linear by the GPU); normal, metal/rough and
  occlusion textures are data.
- **Normal maps**: OpenGL convention (green up). Tangents come from the file when it has them, else are computed.
- **In the project**: a picked image outside the project is copied into `Assets/Textures` (an identical file there is
  reused; a different one with the same name is numbered; nothing is overwritten). An image already inside the project is
  used where it is.
- **Missing or broken** textures never stop a project: the slot draws without it and the log says so once.

## Lights

Lights are a property of a part: Environment & Graphics > Add Light places a small invisible, anchored, non-colliding
part that carries one (its material is fully see-through), and any part (a lamp, a torch) can carry one through the
Materials tab's Light rows. A light shines from the part's position along the part's **forward** direction (its local -Z, the
same as `object.Forward` in Lua); a new spot light points down (Rotation X 90).

| Type | Settings | Notes |
|---|---|---|
| Point | colour, intensity, range | A white surface 1 stud away receives Intensity, at distance d Intensity / d², fading smoothly to nothing at Range. |
| Spot | + cone angle, inner cone | Full brightness inside the inner cone, fading to the cone's edge. |
| Directional | colour, intensity | Lights the whole scene along its direction, like a second sun, without shadows (the Atmosphere's sun has them). |

Every light has Enabled and Cast Shadows. In Edit mode every light shows an icon (a small sun in its colour, with a
tick for a spot or directional light's direction), so invisible lights can be found and clicked; a selected light also
shows its range (point), cone (spot) or direction (directional). Icons are never drawn in Play. The World panel names
light parts "Point Light", "Spot Light" or "Directional". A part whose material is fully see-through and does not glow
is not drawn by either renderer.

## Shadows

| Light | Shadows | Limits |
|---|---|---|
| Sun (Atmosphere) | Cascaded shadow maps: 1-4 cascades over the shadow distance, each fitted to its slice of the view and snapped to whole texels (no shimmering), faded at the far end | Distance (Environment & Graphics > Lighting > Shadows, scaled by the quality), resolution 1024-4096 |
| Spot | One tile of a shared atlas each | Up to 0/2/4/8 lights (Low/Medium/High/Ultra), 512-1024 pixels |
| Point | A cube map each (six views: the costliest) | **Off at Low and Medium**; 1 at High (256 px), 2 at Ultra (512 px); a project can turn them off entirely (Point Light Shadows) |
| Directional (Part) | none | |

Spot and point shadow maps are only redrawn when their light or a shadow caster in their reach changes. Shadows are
filtered with 1/4/9/16 taps per quality. Bias and normal bias are adjustable when a scene shows acne or gaps.
Transparent (Blend) surfaces cast no shadow; cut-outs cast cut-out shadows.

## Ambient light, reflections and the Atmosphere

The Atmosphere (sky, sun, ambient light, fog, haze, exposure, saturation, contrast) drives both renderers. The Universal renderer
uses three more settings, in Environment & Graphics > Lighting and in Lua:

- **Sky Ambient** (Ambient Mode Sky / Flat): ambient light from the sky above (the Ambient colour) and the ground below
  (Ground Color), blended by which way a surface faces; Flat is one colour from everywhere.
- **Ground Color**: the light bouncing up from the ground in Sky mode.
- **Reflections** (0-2): how strongly smooth and metal surfaces reflect the sky.

Reflections are of the **sky only** (a cube map captured from the Atmosphere's sky, blurred for rough surfaces): metals
and glossy surfaces reflect the sky and horizon, not other objects (no ray tracing, no screen-space reflections, no
reflection probes). On rough non-metal surfaces the sky reflection is kept in proportion to the ambient light, so
coloured parts keep their colour in shade.

Fog and haze apply to every surface, the sky and particles, as in Classic. The day and night cycle
(`Wisko.DayNight`) and the weather of `Wisko.Weather` drive the Atmosphere, so they light the Universal renderer too.

## Image: HDR, tone mapping, post-processing

| Setting | Values | Notes |
|---|---|---|
| Exposure | Atmosphere, -3 to +3 | As in Classic: +1 doubles the displayed brightness. |
| Tone mapping | None, Neutral, Filmic | Neutral (default): colours up to 0.8 stay exactly as lit, brighter ones roll off softly to white (the highlight curve of Khronos PBR Neutral). Filmic: ACES (Hill fit), more contrast, deeper shadows. None: clipped. |
| White balance | Temperature, Tint (-1 to 1) | Warm / cool, green / magenta. |
| Bloom | threshold (1.5), strength, spread | Glow around HDR highlights (glowing materials, the sun, bright lights); the default threshold leaves sunlit white surfaces alone. Off by default. |
| Vignette | amount | Darker edges. Off by default. |
| Ambient occlusion | radius, strength | Screen-space, from depth: soft contact shadows in corners and crevices; it darkens ambient light only (never direct light). On by default from Medium. |
| Anti-aliasing | MSAA, FXAA, Both, Off | 4x MSAA by default; FXAA also smooths texture and cut-out edges, softer. |

## Quality presets

The project's quality (Environment & Graphics > Rendering > Quality) chooses detail, never the look of materials or lights:

| | Low | Medium (default) | High | Ultra |
|---|---|---|---|---|
| Post-processing (bloom, ambient occlusion, vignette, FXAA) | off | on | on | on |
| Sun shadow map / cascades / filter taps | 1024 / 1 / 1 | 2048 / 2 / 4 | 2048 / 3 / 9 | 4096 / 4 / 16 |
| Shadow distance | 50 % | 75 % | 100 % | 100 % |
| Spot shadows | 0 | 2 (512) | 4 (1024) | 8 (1024) |
| Point shadows | 0 | 0 | 1 (256) | 2 (512) |
| Ambient occlusion samples | - | 8, half resolution | 12, half | 16, full |
| Bloom levels | - | 4 | 5 | 6 |
| Reflection cube | 32 | 64 | 128 | 256 |
| Lights per object | 4 | 8 | 8 | 16 |

**Custom** starts as a copy of the current level and makes every value editable (and adds render scale, 0.5-1).

The **3D View** preference (Environment & Graphics > Rendering > 3D View) is yours, not the project's: a slow computer can preview at
Low while the game keeps the project's quality. It is saved on this computer (`%LOCALAPPDATA%\Wisko\editor-graphics.json`;
macOS: `~/Library/Application Support/Wisko`), never in a project.

## Editor

ToolBench > **Environment & Graphics** is the one place for the project's look. Its toolbar has **Environment** (the
panel), **Presets** (the Atmosphere presets and Reset to Default), **Material** (the panel's Materials tab) and **Add
Light**. The panel, over the left of the 3D View, has five tabs; each setting is in exactly one of them:

| Tab | Settings |
|---|---|
| **Atmosphere** | Atmosphere on / off; sky (Sky Color, Horizon, Sky Intensity); sun (Sun Color, Intensity, Height, Direction, Disc); ambient light (Ambient Color, Ambient Intensity); fog (on / off, colour, density, start, end); haze; Reset to Default (every Atmosphere value, in every tab). The day and night cycle and the weather are Lua (`Wisko.DayNight`, `Wisko.Weather`): they move the sun and change fog in Play. |
| **Lighting** | Environment light (Sky Ambient, Ground Color, Reflections); shadows (on / off, distance, bias, normal bias, point light shadows). |
| **Rendering** | Renderer (Classic / Universal), quality (and the Custom values), anti-aliasing, your 3D View preference, Reset to Defaults (rendering, shadows and post-processing). |
| **Post-Processing** | Exposure, tone mapping; colour (temperature, tint, saturation, contrast); bloom, ambient occlusion, vignette. |
| **Materials** | The selected object's surface (preset, shading, colour, metallic, roughness, transparency, glow, textures with Browse and Clear, tiling, toon bands, outline, flat shading, double-sided, shadows; a model's materials one at a time, with Back to Imported) and its **Light** (add a point, spot or directional light; type, colour, intensity, range, cone, shadows; remove). With several primitives selected, an edit applies to all of them. |

The Atmosphere's values (Atmosphere, environment light, exposure, saturation, contrast) are saved in `Atmosphere.json`
and drawn by both renderers; the others are `Graphics.json` and only the Universal renderer draws them (Classic greys
them out). **Add Light** places a new (invisible) point, spot or directional light in front of the camera, selected.

Every change is one undo step (a whole slider or colour drag, a switch, a choice, a texture, a button, a preset); Escape
cancels a drag, then folds an open colour, then closes the panel. In Play the panel is read-only (scripts own the Play
session's look; Stop brings the project's back); it shows what Play draws for the Atmosphere.

## Lua

Everything lasts until Stop, like every Play change. Colours are `"#RRGGBB"`, names are case-insensitive strings
(never numbers), and a value out of range or of the wrong kind is refused with a message naming it.

```lua
-- Materials: every object has one (a model reads its first material and writes all of them)
local m = self.Material
m.Shading = "Lit"                -- "Lit", "SimpleLit", "Unlit", "Toon"
m.Metallic = 1                   -- 0..1
m.Roughness = 0.2                -- 0..1
m.EmissiveColor = "#FF8800"      -- glow colour
m.EmissiveIntensity = 4          -- 0..100 (above 1 blooms)
m.AlphaMode = "Blend"            -- "Opaque", "Mask", "Blend"
m.Opacity = 0.5
m.BaseColorTexture = "Assets/Textures/brick.png"  -- a project path, or nil
m.Outline = true
m:ApplyPreset("Metal")           -- the editor's presets
m:Reset()                        -- back to the default (a model: its imported materials)
print(m.Preset)                  -- "Metal", or "Custom"

-- Lights: object.Light is nil until the object has one
local light = self:AddLight("Spot")  -- "Point" (default), "Spot", "Directional"
light.Color = "#3366FF"
light.Intensity = 250            -- 0..100000
light.Range = 30                 -- studs
light.Angle = 45                 -- spot cone, degrees
light.CastShadows = true
light.Enabled = false
self:RemoveLight()

-- The Play session's rendering settings
Graphics.Quality = "High"        -- "Low", "Medium", "High", "Ultra", "Custom"
Graphics.ToneMapping = "Filmic"  -- "None", "Neutral", "Filmic"
Graphics.Bloom = true
Graphics.Vignette = true
Graphics.AmbientOcclusion = false
Graphics.Temperature = 0.3       -- -1..1
print(Graphics.Renderer)         -- "Universal" or "Classic" (read-only)

-- The Atmosphere's environment lighting
Atmosphere.AmbientMode = "Sky"   -- "Sky" or "Flat"
Atmosphere.GroundColor = "#786860"
Atmosphere.ReflectionIntensity = 1
```

| Member | Kind |
|---|---|
| `Material.Preset` | string (setting it applies the preset) |
| `Material.Shading`, `AlphaMode`, `UvMode` | `"Lit"`/`"SimpleLit"`/`"Unlit"`/`"Toon"`; `"Opaque"`/`"Mask"`/`"Blend"`; `"Mesh"`/`"Studs"` |
| `Material.Tint`, `EmissiveColor`, `OutlineColor` | `"#RRGGBB"` |
| `Material.Metallic`, `Roughness`, `Opacity`, `AlphaCutoff`, `OcclusionStrength` | 0..1 |
| `Material.EmissiveIntensity` 0..100, `NormalStrength` 0..4, `ToonBands` 2..8, `OutlineThickness` 0.005..2, `UvScaleX`/`UvScaleY` 0.01..1000 | number |
| `Material.Outline`, `FlatShading`, `DoubleSided`, `CastShadows`, `ReceiveShadows` | boolean |
| `Material.BaseColorTexture`, `NormalTexture`, `MetallicRoughnessTexture`, `OcclusionTexture`, `EmissiveTexture` | project path or nil |
| `Material:ApplyPreset(name)`, `Material:Reset()` | methods |
| `object.Light` | Light or nil |
| `object:AddLight([type])` -> Light, `object:RemoveLight()` | methods |
| `Light.Type`, `Enabled`, `Color`, `Intensity`, `Range`, `Angle`, `InnerAngle`, `CastShadows` | as above |
| `Graphics.Renderer` (read-only), `Quality`, `ToneMapping`, `Bloom`, `BloomThreshold`, `BloomIntensity`, `BloomRadius`, `Vignette`, `VignetteIntensity`, `AmbientOcclusion`, `AmbientOcclusionIntensity`, `Temperature`, `Tint`, `Shadows`, `ShadowDistance` | |
| `Atmosphere.AmbientMode`, `GroundColor`, `ReflectionIntensity` | |

## What is saved

- **Scene.json**: each part's `Material` (only values that differ from the default), a model's `Materials` (its own
  versions, by material number) and `Light`. Projects without them load exactly as before.
- **Graphics.json** (next to Scene.json): the renderer, quality, Custom values and the post-processing and shadow
  settings. A project without it is Classic; it is only written when a project has one or its settings differ from
  Classic's.
- **Atmosphere.json**: Ambient mode and Ground color, and ReflectionIntensity.
- **`editor-graphics.json`** (in `%LOCALAPPDATA%\Wisko`, not in any project): your 3D View preference.

## Backends

| | Windows (OpenGL 3.3) | macOS (bgfx / Metal) |
|---|---|---|
| Classic renderer | yes | yes |
| Universal renderer | yes (tested: this page's tests, the showcase) | not available: every project is drawn with the Classic renderer |
| Shadows | yes (Universal) | **none** (Classic has no shadows on any platform) |
| Materials, lights, Graphics.json in the editor and Lua | edited, saved, drawn | edited and saved, not drawn |

On macOS the Universal renderer's settings stay editable and are saved unchanged, so a project made or edited on a Mac
keeps its look on Windows. The Environment & Graphics panel draws them muted, with a note that this Mac draws the
Classic renderer; opening a project set to Universal shows a short notice once per session; and the New World screen
notes that its pictures show the Universal renderer. Wisko 0.1.0 on macOS is Metal + Classic.

## Performance

Measured on the development machine (Intel Core i5-12400F, NVIDIA GeForce RTX 3060, driver 610.74, 16 GB (15.7 GB usable), Windows 11
Home 10.0.26300, .NET 8.0.28, Release build, 1280 x 720), with the benchmark described under [Testing](#testing). Frame
times are measured without presenting (this machine's sleeping display throttles presentation to ~4 frames per
second for every build, which says nothing about the engine).

**How it was measured.** Nine repeatable scenes are built as ordinary projects and opened in the real editor. Each
is drawn for 8 frames, then 200 frames back to back (unmeasured, so the GPU leaves its power-saving clocks), then
120 measured frames: Update + Render each, timed on the CPU and on the GPU (OpenGL timer queries), with the
GPU finished at the end. "Frame" below is the wall time per frame over those 120 frames, CPU and GPU overlapped:
the frame cost the engine sustains, before presentation (1000 / frame = frames per second without vsync). The same
benchmark source is compiled against this build and against the approved checkpoint `e2d3517` (Classic only), with
the same geometry, models, scripts and camera views. Classic figures are means of three runs; Universal figures are
one run each.

| Scene | Content | Checkpoint (Classic) | Now, Classic | Universal Low | Medium | High | Ultra |
|---|---|---|---|---|---|---|---|
| 1 Basic | 50 primitives | 0.66 ms | 0.56 | 0.56 | 0.78 | 0.98 | 2.06 |
| 2 Textured PBR | 6 water bottles, 2 Khronos test models, 12 PBR spheres | 0.63 | 0.71 | 0.49 | 0.83 | 0.96 | 2.26 |
| 3 Multiple lights | 40 primitives, 24 lights (8 spot) | 0.68 | 0.47 | 0.50 | 0.91 | 1.03 | 2.33 |
| 4 Shadow-heavy | 300 pillars, 6 lights with shadows | 0.97 | 0.91 | 0.96 | 1.29 | 1.53 | 2.17 |
| 5 Animated characters | 30 skinned foxes (Play) | 0.78 | 0.79 | 0.71 | 0.87 | 1.06 | 2.25 |
| 6 Weather | 32 primitives, rain and fog (Wisko.Weather, Play) | 0.75 | 0.71 | 0.72 | 1.25 | 0.94 | 2.17 |
| 7 Large object count | 2,000 primitives | 4.31 | 4.37 | 3.64 | 4.76 | 4.94 | 4.84 |
| 8 Camera feed | 32 primitives, a 384 x 216 Lua camera feed in the HUD (Play) | 0.58 | 0.53 | 0.44 | 0.77 | 0.87 | 2.12 |
| 9 Combined stress | 900 primitives, 16 lights (6 with shadows), 10 foxes, 4 water bottles, rain, a camera feed (Play) | 2.46 | 2.54 | 2.39 | 2.94 | 3.11 | 3.21 |

- **Classic is unchanged**: projects that keep the Classic renderer cost what they did, within run-to-run variation
  (the checkpoint's own three runs of a scene differ by up to 0.33 ms). Scene 9 was the one consistent difference,
  about 3 % (2.51-2.58 ms against 2.43-2.48 ms over three runs each, 0.08 ms per frame); its cause was not identified.
  A session that opens only Classic projects never starts the Universal renderer (no shaders compiled, no GPU
  memory used). Comparing the final Classic image of the five static scenes (1, 2, 3, 4 and 7) with the checkpoint's:
  scenes 1, 2 and 7 are pixel-identical (the whole window); scenes 3 and 4 differ only at the edges of the spot-light
  spheres, which the benchmark turns in this build only (a spot light points along its part's forward).
- **Universal Low** costs about what Classic does (it is faster with many objects, as it skips those out of view);
  **Medium** adds 0.2-0.5 ms (post-processing, ambient occlusion, a second shadow cascade); **Ultra** has a floor of
  about 2 ms (four 4096 shadow cascades, full-resolution ambient occlusion with 16 samples, six bloom levels). The
  2,000-part scene is limited by the CPU (one draw call per part and per shadow cascade) at every level.
- **Spot and point light shadow maps are cached**: in these static scenes only the sun's cascades are redrawn each
  frame (1-4 maps), whatever the number of shadowed lights.
- **GPU memory** of the Universal renderer at 1280 x 720 (render targets, shadow maps, environment): Low 4 MB, Medium
  90 MB, High 108 MB, Ultra 322 MB (most of it the 4096 sun shadow cascades), plus textures (219 MB for scene 2's
  2048-pixel PBR maps). The process's working set was 180-250 MB for Classic and 200-500 MB for Universal.
- **Shaders**: 24 programs, compiled once when a project first needs the Universal renderer: 742 ms the first time on
  this machine, 9-15 ms afterwards (the driver's shader cache). Never compiled again while the session runs.
- **Allocations**: 26-120 KB of managed memory per frame (Classic: 29-290 KB), mostly the editor and scripts.
- **Resources**: after all nine scenes, back in the Hub, the Universal renderer held only its 2 fallback textures, no
  meshes, and its 24 programs (released at exit).
- **Presented frame rate** could not be measured meaningfully on this machine during testing: with its display asleep,
  Windows presents every editor build, old and new, at about 4 frames per second. On a monitor that is on, Wisko presents
  at the display's refresh rate when the frame cost above allows it.

## Known limitations

- Windows / OpenGL only; the macOS build draws Classic: no shadows, materials, textures, part lights or
  post-processing beyond exposure, saturation and contrast (the settings are kept for Windows).
- Reflections are of the sky only; no reflections of other objects, no reflection probes, no ray tracing.
- No global illumination: light does not bounce between objects (the ground colour approximates bounce from below).
- One UV set: textures that use a second UV set are read with the first (the import warns).
- Morph targets, KTX2/Basis, WebP and DDS textures, clear-coat, sheen, transmission and other glTF material extensions
  beyond emissive strength and unlit are not drawn (the import names the unsupported extensions).
- Transparent surfaces are sorted per object, not per triangle; overlapping see-through objects can draw in the wrong
  order. They cast no shadows.
- Directional Part lights cast no shadows.
- Point-light shadows are limited to 2 (Ultra) for cost.
- Ambient occlusion is screen-space: it cannot see what is off screen or hidden behind other objects.

## Testing

```
dotnet run --project Tests/Wisko.Tests -- --only "Graphics"                    # headless: data, import, Lua, editor logic
dotnet run --project Tests/Wisko.Tests -- --gui-smoke --only "Graphics" --screenshots <folder>
dotnet run --project Tests/Wisko.Tests -- --make-graphics-demo "%USERPROFILE%\Documents\Wisko\Projects"
dotnet run --project Tests/Wisko.Tests -c Release -- --bench-graphics result.txt --mode medium   # classic|low|medium|high|ultra
```

The GUI sections open real projects in the editor: the Universal renderer at every preset with screenshots, bloom,
selection outlines and gizmos, Play and Stop, the Khronos sample models (normal-map, texture-settings, alpha-mode and
emissive-strength tests), the Environment & Graphics panel driven by clicks (every row of every tab reached and hit,
Atmosphere, material, light and rendering edits with undo and redo, save and reopen), Add Light, a forced shader failure falling back to Classic, the Gameplay Systems Showcase under the Universal renderer
(camera feeds, rain, fog, debug lines), resources released when leaving a project, and the Graphics Showcase played
through its tour. The [showcase](../examples/graphics-demo/README.md) is the place to look at everything by hand.
