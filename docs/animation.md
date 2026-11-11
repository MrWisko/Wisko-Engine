# Animation

Wisko imports rigged, animated 3D models and plays their animations: in the editor's viewport (a preview, without
entering Play), and in Play under the control of your Lua scripts. Animated meshes deform on the GPU (OpenGL), clips
blend smoothly into each other, and every instance of a model plays on its own.

This page covers the whole system: formats, import, how skeletons and clips are handled, playback and blending, the
Lua API with complete examples, the editor's Animation panel, what is saved, performance, limits and platform
differences. The Lua API is also listed with Wisko's other scripting features in [lua-api.md](lua-api.md#animation).

- [Supported formats](#supported-formats)
- [Importing an animated model](#importing-an-animated-model)
- [Skeletons, skins and clips](#skeletons-skins-and-clips)
- [Bone influences and limits](#bone-influences-and-limits)
- [Clip names and numbers](#clip-names-and-numbers)
- [Playback](#playback)
- [Blending (crossfades)](#blending-crossfades)
- [Lua API](#lua-api)
- [Examples](#examples)
- [The Animation panel (editor preview)](#the-animation-panel-editor-preview)
- [What is saved](#what-is-saved)
- [Root motion, physics and picking](#root-motion-physics-and-picking)
- [Performance](#performance)
- [Known limitations](#known-limitations)
- [Platforms](#platforms)
- [The demo and the models it uses](#the-demo-and-the-models-it-uses)

## Supported formats

| Format | Animation | Notes |
|---|---|---|
| **GLB / glTF 2.0** | Skinning, skeletons, node animation, any number of clips | The main format. Tested with the Khronos samples (RiggedSimple, CesiumMan, Fox, RiggedFigure, BoxAnimated) and RobotExpressive. |
| **FBX** | Skinning, skeletons, node animation, clips | Read through Assimp. Tested with an FBX rig from Assimp's test models (14 bones, up to 6 influences per vertex). FBX files often store a rest pose that differs from their bind pose; Wisko shows the rest pose until something plays. |
| **OBJ** | None (static) | Unchanged. |

Not supported (reported, never silently wrong):

- **Morph targets (blend shapes)** and morph or vertex-animation channels: the mesh shows its base shape; the import
  log and the Animation panel say so.
- **Cubic-spline keyframes** (glTF `CUBICSPLINE`) are played with straight interpolation between their keyframe
  values (the tangents are not used).
- **Textures** are drawn by the Universal renderer (Windows), animated models included; the Classic renderer (and so
  the macOS app) shows the models' material colours.

## Importing an animated model

1. Open a project (an unnamed world asks for a name first: assets live in the project folder).
2. **Add Meshes > Import Model…** and choose a `.glb`, `.gltf`, `.fbx` or `.obj` file.
3. Wisko copies the file (and the files it needs) into `Assets/Models/<name>/`, decodes the copy and places one
   instance in the scene. Importing the same file again makes a second asset (`<name>-2`); existing instances keep
   theirs.

During import Wisko reads the meshes, the full node hierarchy, every skin (bone names, inverse bind matrices, per-vertex
bone indices and weights) and every clip (name, duration, translation / rotation / scale keyframes per node).
Problems in the animation data are reported with the import (in the log, and in the Output panel the first time the
Animation panel shows that model). A file that cannot be read at all is refused with a message (for example
"Walker.gltf has invalid animation data (keyframes or bones; see the log)") and nothing is copied.

## Skeletons, skins and clips

- **Skeleton**: every node of the model (not only bones), in depth-first order so a parent always comes before its
  children. A node's identifier is its index in that order (stable for the same file) and its name. Bones are the
  nodes a skin follows; any node can be animated, so rigid parts of a model (a mesh hanging under a bone, a spinning
  propeller) move too.
- **Skin**: per skinned mesh, its bones (resolved by name to nodes), their inverse bind matrices and up to 4 weighted
  bones per vertex.
- **Clip**: a name, a length in seconds and keyframe tracks, each resolved to a node. A node a clip has no track for
  stays in its rest pose while that clip plays; a channel a track lacks (for example scale) stays at its rest value.
- All of this is immutable and shared by every instance of the model; each instance only has its own playback state
  (an *animator*: clips, times, speeds, fades, pose buffers).

How a skinned vertex is placed (glTF's rule, which Assimp follows for every format):
`model-space position = Σ weight × (bone's model-space transform × inverse bind matrix) × vertex`. The skinned mesh's
own node transform is not applied (its bones place it), so nothing is transformed twice. The instance's own position,
rotation and scale (the Part's) are applied after that, like for any model.

Coordinate system: right-handed, Y up, as Assimp delivers it (glTF is already Y-up; FBX and Z-up files carry the
conversion in their node hierarchy). Rotations are unit quaternions; local transforms are translation × rotation ×
scale.

When names are duplicated (exporters often name a joint and the mesh node under it the same), a bone or track follows
the first node with that name that draws no mesh, else the first one.

## Bone influences and limits

| Limit | Value | What happens beyond it |
|---|---|---|
| Bone influences per vertex | **4** | The 4 strongest are kept (ties: the lower bone slot), renormalized to sum to 1; the import reports how many vertices were affected. |
| Weights that do not sum to 1 | — | Renormalized; reported. |
| Vertices with no weight | — | They follow the mesh's own node rigidly; reported. |
| Bone matrices per skinned mesh | **256** | 256 × 64 bytes is the 16 KB uniform block every OpenGL 3.3 implementation guarantees. A mesh needing more is drawn in its rest pose (reported). |
| Layers blending at once | 8 | Starting a ninth crossfade drops the oldest layer (by then almost invisible). |

Other problems and what Wisko does:

| Problem | Behaviour |
|---|---|
| A bone names a node the model does not have | That mesh is drawn rigidly in its rest pose; reported. |
| An inverse bind matrix that is not finite or cannot be inverted | That mesh is drawn in its rest pose; reported. |
| A track for a node the model does not have | That track is ignored; reported. |
| A clip without any usable keyframes | Listed (as in the file), plays the rest pose; reported. |
| Keyframes out of time order, or past the clip's end | Assimp refuses the file: the import fails with a message. (Wisko also sorts keys if a format ever lets them through.) |
| Keyframes whose time or value is not a number | Ignored; reported. |
| A damaged file | The import is refused with a message; an existing instance shows a placeholder box. |

## Clip names and numbers

- A clip is named as in the file. A clip without a name is called **"Animation N"** (N = its number), e.g.
  CesiumMan's single clip is "Animation 1".
- Clips keep the file's order; their **number** is their position from 1 (`GetAnimations()` lists them).
- By name: an exact match first (the first clip with that name), then a match ignoring case. Two clips with the same
  name are reported; play the others by number.
- Wisko never creates, renames or invents animations: a model plays only what its file contains.

## Playback

Each instance's animator has, at any moment, a **current clip** (or none: the rest pose), its time, speed and loop
mode, and whether it is paused.

- **Play**: at once, or fading in from what shows now (see Blending). Playing the clip that is already playing keeps
  it where it is (only loop and speed change) unless you ask for a restart; this makes it safe to call every frame.
- **Loop**: a looping clip wraps around; a **one-shot** clip stops at its end and holds its last pose. When the current
  clip reaches its end playing once, `OnAnimationFinished` is told (once). Clips that are interrupted (another clip
  started) and seeks never report finishing.
- **Speed**: 1 is as authored; 2 twice as fast; 0 holds; negative plays backwards (a backwards one-shot ends at 0).
- **Pause / Resume** freeze and continue everything (clip times and fades).
- **Seek** sets the current clip's time: wrapped for a looping clip, clamped for a one-shot.
- **Restart** plays the current clip from its start (its end when playing backwards).
- **Stop** returns to the rest pose, at once or fading to it.
- **Frame-rate independent**: clips advance by real time × speed, fades by real time. What shows depends only on the
  total time played, not on the frame rate (tested at 30, 60 and 144 fps: same time, same pose).
- Outside the keyframes: before the first key the first value holds, after the last key the last value holds.
- Keyframes are interpolated linearly (translation, scale) and spherically along the shorter arc (rotation); step keys
  hold until the next key.
- In Play, animations advance once per frame after physics and before `LateUpdate`, so a `LateUpdate` (e.g. a camera)
  sees this frame's pose.

## Blending (crossfades)

`CrossFadeAnimation(name, fadeTime)` (or `PlayAnimation(name, { FadeTime = t })`) blends from whatever shows now to
the new clip over `fadeTime` seconds:

- translation and scale blend linearly, rotation spherically;
- at the start of a crossfade the pose is exactly what showed before (no snap), at its end only the new clip remains;
- a crossfade started while another is still running simply blends over the current mix (interrupted transitions
  stay continuous: walk → run interrupted by idle never jumps);
- a node the new clip does not animate blends towards its rest pose;
- looping and one-shot clips mix freely (e.g. Walk → Jump → back to Idle);
- `StopAnimation(fadeTime)` fades back to the rest pose.

Wisko gives you these primitives; there is no state-machine editor. Your script decides what plays (see the examples).

## Lua API

Animation methods exist on every scene object a script can reach (`self`, `Scene.Find(...)`, ...). They work on
imported model instances that have clips; on anything else they answer that there are no animations (stopping,
pausing and listing are harmless; setting a time or speed is an error). They work in Play (scripts only run in Play).
A clip is given by name (a string) or number (1 = the first).

| Method | Returns | What it does |
|---|---|---|
| `object:HasAnimations()` | `boolean` | Whether it is an imported model with clips. |
| `object:GetAnimations()` | `{ {Name, Duration, Index}, ... }` | Its clips in file order (Duration in seconds, Index from 1). |
| `object:PlayAnimation(clip[, options])` | `true` or `false, reason` | Plays a clip. Options: `Loop` (boolean, default true), `Speed` (number, default 1), `FadeTime` (seconds, default 0), `StartTime` (seconds), `Restart` (boolean: restart even if it is already playing). An unknown clip answers `false` and why (not an error). |
| `object:CrossFadeAnimation(clip, fadeTime[, options])` | `true` or `false, reason` | Plays a clip, blending from what shows now over `fadeTime` seconds. Options: `Loop`, `Speed`, `StartTime`, `Restart`. |
| `object:StopAnimation([fadeTime])` | — | Back to the rest pose (fading over `fadeTime` seconds when given). |
| `object:PauseAnimation()` / `object:ResumeAnimation()` | — | Freezes / continues (clips and fades). |
| `object:RestartAnimation()` | — | The current clip from its start, playing. |
| `object:SetAnimationSpeed(speed)` | — | Speed of the current clip (1 as authored, 0 holds, negative backwards). Error when nothing plays. |
| `object:SetAnimationLooping(loop)` | — | Whether the current clip loops. Error when nothing plays. |
| `object:GetAnimationTime()` | `number` | The current clip's time in seconds (0 when nothing plays). |
| `object:SetAnimationTime(seconds)` | — | Moves the current clip (wrapped when looping, clamped otherwise). Error when nothing plays. |
| `object:GetAnimationLength([clip])` | `number` or `nil` | A clip's length in seconds (the current clip's without an argument); nil when there is no such clip. |
| `object:GetCurrentAnimation()` | `string` or `nil` | The clip playing or fading in. |
| `object:IsAnimationPlaying([clip])` | `boolean` | Whether a clip (or that clip) is playing: not paused, not finished. |
| `object:OnAnimationFinished(function(name, object) ... end)` | `Connection` | Runs at the start of the next frame when the current clip, playing once, reaches its end. |

Errors (raised, like other Wisko API misuse): a clip that is neither a string nor a whole number, an options table
with an unknown key or a wrong type (`PlayAnimation: unknown option Lop (use Loop, Speed, FadeTime, StartTime or
Restart)`), a negative or non-finite fade time or speed, calling a method with `.` instead of `:`, and using an object
that no longer exists. Playback is Play-only state: nothing a script does to an animation is saved, and Stop returns
every model to its rest pose.

## Examples

All five are complete scripts from the demo project ([`examples/animation-demo/Scripts`](../examples/animation-demo/Scripts));
the automated tests play them on the real models.

### 1. Idle / walk / run transitions from real movement

From [`Character.lua`](../examples/animation-demo/Scripts/Character.lua) (an invisible physics body moves; the
imported model follows it and only shows the animation). The clips follow what the body actually does, not the keys:

```lua
local IDLE, WALKING, RUNNING = "Idle", "Walking", "Running"
local WALK, RUN, FADE = 7, 16, 0.2
local WALK_CLIP_SPEED, RUN_CLIP_SPEED = 5.5, 12 -- studs per second each cycle covers at speed 1

function Update(dt)
  -- ... movement sets self.LinearVelocity ...
  local v = self.LinearVelocity
  local speed = math.sqrt(v.x * v.x + v.z * v.z)
  -- Calling CrossFadeAnimation every frame is fine: a clip already playing keeps playing (its speed follows).
  if speed < 0.6 then
    robot:CrossFadeAnimation(IDLE, FADE)
  elseif speed < (WALK + RUN) / 2 then
    robot:CrossFadeAnimation(WALKING, FADE, { Speed = speed / WALK_CLIP_SPEED }) -- no foot sliding
  else
    robot:CrossFadeAnimation(RUNNING, FADE, { Speed = speed / RUN_CLIP_SPEED })
  end
end
```

### 2. Playback speed

[`SpeedPulse.lua`](../examples/animation-demo/Scripts/SpeedPulse.lua): one clip whose speed rises and falls.

```lua
local CLIP = "Run"

function Start()
  if not self:PlayAnimation(CLIP) then
    local clips = self:GetAnimations() -- another model: its first clip, whatever it is called
    if #clips > 0 then
      CLIP = clips[1].Name
      self:PlayAnimation(1)
    end
  end
  print(self.Name .. " plays " .. CLIP .. " (" .. string.format("%.2f", self:GetAnimationLength() or 0) .. " s at speed 1)")
end

function Update(dt)
  self:SetAnimationSpeed(1 + 0.75 * math.sin(Time.elapsedTime * 0.8))
end
```

### 3. Jump

From `Character.lua`: Space starts the jump and its clip once; landing gives the movement clips back.

```lua
local jumpedAt

function Update(dt)
  local grounded = self:IsGrounded()
  if jumpedAt ~= nil and grounded and Time.elapsedTime - jumpedAt > 0.25 then
    jumpedAt = nil -- landed: the movement clips take over again (example 1, only while jumpedAt is nil)
  end
  if grounded and jumpedAt == nil and Input.IsKeyPressed("Space") then
    local v = self.LinearVelocity
    self.LinearVelocity = Vector3.new(v.x, 18, v.z)
    jumpedAt = Time.elapsedTime
    robot:CrossFadeAnimation("Jump", 0.1, { Loop = false })
  end
end
```

### 4. One-shot interaction

From `Character.lua`: E plays Wave once; `OnAnimationFinished` hands control back.

```lua
local waving = false

function Start()
  robot:OnAnimationFinished(function(name)
    if name == "Wave" then
      waving = false
    end
  end)
end

function Update(dt)
  if Input.IsKeyPressed("E") and not waving then
    waving = true
    robot:CrossFadeAnimation("Wave", 0.15, { Loop = false })
  end
  -- while waving: stand still, and do not run the movement clips of example 1
end
```

### 5. Multiple independent characters

[`Dancer.lua`](../examples/animation-demo/Scripts/Dancer.lua), on three copies of one model: each cycles through clips
with crossfades at its own tempo, at the same time.

```lua
local CLIPS = { "Dance", "Wave", "Yes", "ThumbsUp", "No", "Punch" }
local HOLD, FADE = 3.5, 0.4
local index, timer, speed = 1, 0, 1

function Start()
  local number = tonumber(string.match(self.Name, "%d+")) or 1 -- "Dancer 2" -> 2
  index = (number - 1) % #CLIPS + 1
  speed = 0.7 + 0.2 * number
  self:PlayAnimation(CLIPS[index], { Speed = speed })
end

function Update(dt)
  timer = timer + dt
  if timer >= HOLD then
    timer = 0
    index = index % #CLIPS + 1
    self:CrossFadeAnimation(CLIPS[index], FADE, { Speed = speed })
  end
end
```

## The Animation panel (editor preview)

**ToolBench (top left) > Animation** opens the panel on the left (beside Physics when both are open). Select one
imported model (in the viewport or the World panel). The panel shows:

- the model's name, its number of clips and bones (and of notes about its animation data, written to Output);
- **Clips**: name and length; click one to choose it (a green dot marks the saved Play on start clip; the wheel
  scrolls a long list);
- **Preview**: **Play**, **Pause** and **Stop**, and a **timeline** with the time (click or drag to scrub; scrubbing
  pauses);
- **Saved settings**: **Loop**, **Speed** (− / + steps from 0.1x to 4x; click the value for 1x) and **Play on start**
  (the chosen clip plays by itself when Play starts).

The preview runs in Edit mode only, on that one model. It is never saved and is never an undo step: previewing does
not make the project "changed". Stop, closing the panel, selecting something else, leaving the project or pressing
Play end it, and the model is drawn in its rest pose again (the tests check the image is pixel-identical to before).
The saved settings are ordinary scene edits: each change is one undo step and marks the project as changed.

In Play the panel shows the selected model's running animation (clip, time, playing / paused / finished), read-only.

## What is saved

`Scene.json` stores, per model instance, only the settings that differ from the defaults:

```json
"Animation": { "Clip": "Run", "PlayOnStart": true, "Loop": false, "Speed": 1.25 }
```

- No settings (nothing on start, loop on, speed 1): no `"Animation"` entry at all, so older scenes keep their exact
  JSON, and older builds simply ignore the entry.
- Playback is never saved: no times, poses or current clips. Play's changes are discarded at Stop.
- Duplicates (Ctrl+D) copy the settings; `object:Clone()` in Play copies them too (and starts its own playback).
- Model references are project-relative (`Assets/Models/...`), so moving the project folder keeps everything working.
- A missing or unreadable model file: the instance loads as a placeholder box that keeps its transform and its
  animation settings (saving writes them back unchanged); Play reports that its Play on start clip cannot play.
- A Play on start clip the model does not have (e.g. after replacing the file): Play shows a warning in Output naming
  the clips it does have; nothing crashes.

## Root motion, physics and picking

- **Root motion**: clips play exactly as authored. If a clip moves its root or hips, the model moves relative to its
  own origin, but Wisko never moves the Part or its physics body from an animation. Move characters from Lua (as in
  example 1) and use in-place clips; the demo's clips are in place.
- **Physics**: imported models are not simulated by physics in Play (Wisko has no model colliders yet; the log says
  "imported model colliders are not supported yet"). A character that should collide uses a
  primitive Part as its body and the model follows it (`SetParent`), as the demo's character does.
- **Picking and the selection outline** follow the pose that is drawn (a skinned mesh is skinned on the CPU for the
  click); the first, coarse test is still the Part's rest-pose box.
- **Bounds** (the editor's box, `GetBounds`) are the rest pose's.

## Performance

Measured on Windows 11, RTX 3060, Release build, RobotExpressive (74 nodes, 4 skinned meshes of 43 bones, 15 rigid
parts):

| | 1 character | 10 | 50 | 200 |
|---|---|---|---|---|
| Advance + evaluate every pose (CPU, headless) | 0.043 ms | 0.20 ms | 0.39 ms | 1.56 ms |
| Memory allocated per frame by animation | 0 bytes | 0 | 0 | 0 |
| Drawing the parts, CPU, animated (rest pose) | 0.069 (0.044) ms | 0.25 (0.15) ms | 1.00 (0.54) ms | — |
| Drawing the parts, GPU, animated (rest pose) | 0.012 (0.004) ms | 0.18 (0.18) ms | 1.09 (1.02) ms | — |

Loading (decode + skeleton, skins, clips, rest pose): RobotExpressive 13 ms, Fox 3 ms, CesiumMan 5 ms, the FBX rig
7 ms. The frame rate stayed at 60 fps with 50 animated characters. Scenes without animation are as fast as before
(the other script benchmarks are unchanged within noise).

Considerations:

- Skinning runs on the GPU; the CPU evaluates each animated instance's pose once per frame, only when it is drawn.
- Instances share all model data; each animator allocates its pose buffers once, so playing allocates nothing per
  frame.
- Each skinned mesh is one draw with its bone matrices uploaded before it; rigid parts are one draw each. Many
  instances of a model with many rigid parts cost more than one skinned mesh would.

## Known limitations

- No morph targets, no inverse kinematics, no state-machine editor, no animation authoring (by design for the panel
  previews; it does not edit keyframes).
- Root motion is not extracted (see above).
- Cubic-spline keys play linearly between their values.
- At most 4 influences per vertex and 256 bone matrices per skinned mesh.
- Mirrored (negative-scale) bones and nodes are not specially handled during animation: faces they turn inside out can
  be lit as if seen from behind (rest-pose mirroring in the file is handled as before).
- The coarse picking box (the Part's box) is the rest pose's; imported models have no physics collider.
- macOS (bgfx) skinning is implemented but has not been run (see Platforms).

## Platforms

- **Windows (OpenGL)**: GPU skinning (the skinned shaders described above), tested with the automated suites, the
  editor GUI tests and the installed desktop app.
- **macOS (bgfx / Metal)**: Wisko's bgfx shaders are compiled offline and this build has no skinning shader, so on
  bgfx a skinned mesh is skinned on the CPU (the same formula, `SkinningCpu`) into a dynamic vertex buffer per
  instance, refilled only when its pose changed, and drawn with the ordinary lit program (the selection rim likewise).
  Rigid node animation needs nothing special. This path compiles and its CPU skinning is tested on Windows, but it has
  **not been run on a Mac**: treat macOS animation as unverified until it is.

## The demo and the models it uses

`dotnet run --project Tests/Wisko.Tests -- --make-anim-demo "<projects folder>"` creates the **Animation Demo**
project (it refuses when one already exists): the player character (RobotExpressive driven by `Character.lua`),
four foxes on one model playing different clips and speeds, three dancers, CesiumMan, RiggedSimple, BoxAnimated, a
static box and the FBX rig. See [`examples/animation-demo/README.md`](../examples/animation-demo/README.md) and the
models' licences in [`examples/animation-demo/Models/CREDITS.md`](../examples/animation-demo/Models/CREDITS.md).
