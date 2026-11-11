# Lua gameplay systems

These are the building blocks most games need beyond movement and physics: walking characters, conversations and
quests, a game clock, weather, crafting, building, sight and hearing, teleports, puzzles, procedural worlds, equipment
on animated characters, races, several cameras, curves, and saved games.

**Wisko supplies primitives; you decide how to combine them.** There are two kinds of API here:

- **Engine primitives**: new globals and object methods that are always there (`Navigation`, `Perception`, `GameTime`,
  `GameSave`, `Cameras`, `Particles`, `DebugDraw`, `Random`, `Noise`, `Path`, `Time.SetTimeScale`, `Time.OnUpdate`,
  `object:Teleport`, bones, trigger connections). They do work Lua cannot do well or cheaply, such as path finding,
  rendering, files and real time. They also cover what every game would otherwise rewrite.
- **Optional modules**: ordinary Lua files loaded with `require("Wisko.Name")` (`Wisko.Dialogue`, `Wisko.Quests`,
  `Wisko.Crafting`, `Wisko.Building`, `Wisko.Logic`, `Wisko.Teleport`, `Wisko.Race`, `Wisko.Equipment`,
  `Wisko.Weather`, `Wisko.DayNight`, `Wisko.Generate`, `Wisko.Signal`).
  - They use only the public API above: nothing they do is closed to your own scripts.
  - Nothing loads unless a script requires it.
  - To change one, copy it from [`Engine/Scripting/Lua/Modules`](../Engine/Scripting/Lua/Modules) to your project's
    `Assets/Scripts/Wisko/Name.lua`; the project's file then wins.

There is no mandatory controller, AI framework, quest format or game loop. The
[Gameplay Systems Showcase](#the-gameplay-systems-showcase) combines all of it in one playable project; its scripts are plain Lua in
[`examples/gameplay-systems/Scripts`](../examples/gameplay-systems/Scripts).

The core API is documented in [lua-api.md](lua-api.md) and [animation.md](animation.md); everything there works the same
alongside these systems.

**Contents**

- [Status of the 15 areas](#status-of-the-15-areas)
- [Conventions](#conventions) (frame order, ownership, errors, time scale, trigger connections, modules)
- [1. Navigation and NPC movement](#1-navigation-and-npc-movement)
- [2. Dialogue and quests](#2-dialogue-and-quests)
- [3. Game time and day/night](#3-game-time-and-daynight)
- [4. Weather and particles](#4-weather-and-particles)
- [5. Crafting](#5-crafting)
- [6. Building and placement](#6-building-and-placement)
- [7. Vision, detection and noise](#7-vision-detection-and-noise)
- [8. Teleports, portals and checkpoints](#8-teleports-portals-and-checkpoints)
- [9. Logic and puzzles](#9-logic-and-puzzles)
- [10. Procedural generation](#10-procedural-generation)
- [11. Equipment and bone attachments](#11-equipment-and-bone-attachments)
- [12. Racing and checkpoints](#12-racing-and-checkpoints)
- [13. Cameras, cinematics and camera feeds](#13-cameras-cinematics-and-camera-feeds)
- [14. Paths and splines](#14-paths-and-splines)
- [15. Saved games](#15-saved-games)
- [Debug drawing](#debug-drawing)
- [Errors and limits](#errors-and-limits)
- [Performance](#performance)
- [Platforms](#platforms)
- [The Gameplay Systems Showcase](#the-gameplay-systems-showcase)

## Status of the 15 areas

"Visible" means it is drawn and was checked on screen (OpenGL on Windows). "Value" means a script-readable value that
nothing draws by itself.

| # | Area | Kind | Status |
|---|---|---|---|
| 1 | Navigation and NPC movement | engine `Navigation` | Done. 2.5D walkable grid (one walkable surface per grid column), A* with string pulling, agents with follow, patrol, re-pathing, stuck detection and separation. No navmesh, multi-storey stacking, jump/ladder links or crowd simulation. |
| 2 | Dialogue and quests | modules | Done. Branching trees as plain tables, conditions, actions, optional GUI presenter. Quest log with objectives, dependencies, rewards, HUD tracker and save data. |
| 3 | Game time and day/night | engine `GameTime`, module `Wisko.DayNight` | Done. Day/night drives the Atmosphere's sun, sky and ambient light (visible). No stars, moon or separate night sky. |
| 4 | Weather | engine `Particles`, module `Wisko.Weather` | Done. Rain, snow and fog are visible (particles and Atmosphere fog). Lightning is an exposure flash only, with no bolt. Clouds are not drawn ("Cloudy" dims and greys the light). Particles do not collide. Wind is a vector that slants rain, drifts snow and pushes tagged unanchored Parts. |
| 5 | Crafting | module `Wisko.Crafting` | Done. Recipes, stations, timed jobs, cancel with refund, any inventory shape. |
| 6 | Building and placement | module `Wisko.Building` | Done. Grid, rotation and surface snapping, collision checks, valid/invalid preview colours, remove, duplicate, align, describe and rebuild (for saves). |
| 7 | Vision, detection, noise | engine `Perception` | Done. View cones, line of sight, sensors with memory, developer-defined noises within a radius. No acoustics or sound occlusion. |
| 8 | Teleports, portals, checkpoints | `object:Teleport`, module `Wisko.Teleport` | Done. Physics-safe teleport, linked pads with arrival suppression and cooldowns, per-object checkpoints and respawn. Portals do not render a view through them. |
| 9 | Logic and puzzles | module `Wisko.Logic` | Done. Switches, plates, volumes, counters, All/Any/Not, Hold, Window, Delay, Sequence, Mover. |
| 10 | Procedural generation | engine `Random`, `Noise`, module `Wisko.Generate` | Done. Seeded generators, the same on every platform. Perlin and fractal noise, time-budgeted jobs over frames, Poisson-disc and scatter sampling. |
| 11 | Equipment and attachments | engine bones, module `Wisko.Equipment` | Done. `GetBones`, `GetBoneTransform`, `SetParent(..., bone)` following the animated pose; equipment slots. |
| 12 | Racing | module `Wisko.Race` | Done. Ordered checkpoints, laps, splits, wrong-checkpoint reports, rankings, countdown, best times in a save slot, speedometer. |
| 13 | Cameras and cinematics | engine `Cameras` | Done on OpenGL. Named cameras, blends, follow, shake, camera paths, and **camera feeds rendered to a texture and shown by GUI Images** (picture-in-picture, security monitors), checked on screen. The bgfx backend (macOS) has no camera feeds: `Cameras.FeedsSupported` is false there and `CreateFeed` answers false with a reason. |
| 14 | Paths and splines | engine `Path` | Done. Linear, Catmull-Rom and Bezier curves with arc-length sampling, closest point, followers that carry riders. |
| 15 | Saved games | engine `GameSave` | Done. Named slots in the project's `Saves` folder, atomic writes with a backup, versions and metadata, strict plain-data rules. Loading never runs code. |

## Conventions

These hold for every API on this page and match the core API.

**Errors and answers.**
- A wrong type, a missing argument or an impossible option raises an error at your script's line, naming the call
  (`Navigation.CreateAgent expects a scene object, got a nil value`).
- A request that is fine but cannot be met right now answers `nil, reason` or `false, reason`
  (`Navigation.FindPath` → `nil, "the destination cannot be reached from the start (no walkable connection)"`).
- Options tables are checked: an unknown key is an error (`Navigation.Build: CellSise is not an option (AgentHeight, AgentRadius, ...)`).

**Handles and ownership.**
- Scripts only hold locked tables (agents, sensors, cameras, emitters, paths, generators). No C# object reaches Lua.
- Everything a script creates belongs to that script: agents, sensors, emitters, cameras, schedules, `Time.OnUpdate`
  and trigger connections. It ends when the script stops, its object is destroyed, or Play stops.
- Connections are the usual `Connection` objects (`Disconnect()`, `Connected`).

**Play-only state.** Every service on this page lives in the Play session and is thrown away at Stop, so the scene and project are
unchanged. The one exception is `GameSave` files, which are the only thing these systems write to disk (the project's `Saves`
folder).

**Frame order while playing** (the core order, with this page's steps marked ★):

1. ★ The game clock advances, expired debug lines go, and the view returns to the gameplay camera's pose.
2. Script events queued last frame, contacts and ★ trigger connections, then timers.
3. Every script's `Update(dt)`, then ★ `Time.OnUpdate` callbacks (`GameTime` schedules, sensors, path followers, camera paths and modules tick here).
4. Physics, with the scaled step.
5. Animation, with the scaled time.
6. `SetParent` followers, including ★ bones, then every script's `LateUpdate`.
7. ★ Particles step, and the camera director applies the shown camera (active camera, blend, shake).

**Time scale.**
- `Time.SetTimeScale(scale)` slows, speeds up (up to 100) or pauses (0) the whole simulation from the next frame:
  `Update`'s `dt`, `Time.deltaTime`, timers, physics, animation and the game clock.
- `Time.unscaledDeltaTime` and `Time.realTime` (seconds since Play, high resolution) ignore it. Use them for pause
  menus and real-time HUDs.
- `Time.timeScale` reads the current value.

**`Time.OnUpdate(function(dt) ... end)`** → `Connection`. This runs every frame after every script's `Update`, so a
module or a helper can tick without your `Update` calling it. It belongs to the calling script.

**Trigger connections.**
- `object:OnTriggerEntered(function(other, trigger) ... end)` and `object:OnTriggerExited(...)` → `Connection`.
- Any script can watch any trigger Part (`IsTrigger = true`): plates, checkpoints, portals and sensors are built on
  this.
- They run in the contacts step, in the order the contacts happened.

**Built-in modules.** `local Dialogue = require("Wisko.Dialogue")`.
- A project file `Assets/Scripts/Wisko/Dialogue.lua` replaces the built-in one.
- Modules are cached per script runtime like any `require`.

## 1. Navigation and NPC movement

`Navigation` finds walks over the scene's walkable ground and moves objects along them.

**The grid.**
- `Navigation.Build(options)` bakes a grid of cells. Each column is probed with physics raycasts (down) and a
  clearance box (`AgentHeight`).
- A cell is walkable when its surface is no steeper than `MaxSlope` and an agent of `AgentRadius` fits there.
- Agents keep `AgentRadius` away from walls: places with no walkable surface, and any step higher than `MaxClimb`.
  The second case covers the sides of a solid block whose top is walkable, and the brink of a drop.
- Neighbouring cells connect when the step between them is at most `MaxClimb`.
- Without `Build`, the first navigation call bakes with the defaults, around every anchored Part plus 16 studs.
- Bake time is measured and printed (`[Navigation] Baked 190 x 210 cells of 1 studs: 38944 walkable, 6 areas, 162,6 ms` for the showcase in the installed app).
  Bake once at load time, not every frame.

| API | Returns | Notes |
|---|---|---|
| `Navigation.Build(options?)` | info table | `Min`, `Max` (vectors, both or neither), `CellSize` (1; 0.25–8), `AgentRadius` (0.8), `AgentHeight` (4), `MaxClimb` (1.1), `MaxSlope` (46°), `IgnoreTags` (Parts that are not obstacles), `UseHighestSurface` (false: the lowest surface with headroom), `IncludeUnanchored` (false). At most 250,000 cells; a larger area gets coarser cells and says so. |
| `Navigation.GetInfo()` | `{ Width, Depth, Cells, Walkable, Regions, CellSize, Milliseconds }` | |
| `Navigation.Update(center, size)` | info | Re-bakes the cells in a box: a door opened, something was built. |
| `Navigation.SetBlocked(center, size, blocked)` | | Marks a box not walkable (`true`) or walkable again (`false`). |
| `Navigation.FindPath(from, to, options?)` | `{ Points, Length, Complete }` or `nil, reason` | Points or objects. `AllowPartial = true` returns the path to the closest reachable place (`Complete = false`). |
| `Navigation.IsReachable(from, to)` | `true` or `false, reason` | Connected areas are precomputed, so this is cheap. |
| `Navigation.IsWalkable(point)` | `walkable, height` | |
| `Navigation.GetNearestWalkable(point, maxDistance?)` | point or `nil` | Default distance 10. |
| `Navigation.Raycast(from, to)` | boolean | Whether a straight walk stays on walkable ground. |
| `Navigation.DrawPath(path, color?, seconds?)`, `DrawGrid(center, radius?, seconds?)` | | Debug lines. |
| `Navigation.CreateAgent(object, options?)` | agent | See below. |

**Agents** walk one object along paths.

- **Options:** `Speed` (8), `Acceleration` (40), `StoppingDistance` (0.5), `Radius` (1, kept from other agents),
  `RepathInterval` (0.5 s while following), `Move`, `FaceMovement` (true), `FaceYawOffset` (degrees; 180 for models
  that face +Z, as glTF models do), `Avoidance` (true).
- **`Move`** decides what the agent touches:
  - `"Velocity"` (default) sets the body's horizontal velocity; gravity and collisions stay physical.
  - `"Transform"` places an anchored or collider-less object (a model) on the path.
  - `"None"` only reports the velocity it wants, for your own movement code.
- **You call `agent:Update(dt)` every frame** (usually in `Update`): the script that owns the character decides when
  it moves. It returns `status, velocity`.
- **Methods:**
  - `SetDestination(point)` → `true` or `false, reason`.
  - `Follow(object, distance?)` re-paths as the object moves (default distance 3).
  - `Patrol(points, { Loop, PingPong, Wait, StartIndex })`.
  - `Stop()`, `GetPath()`, `DrawPath(color?, seconds?)`, `Destroy()`.
- **Signals:** `OnArrived(f(agent))`, `OnFailed(f(reason, agent))`, `OnWaypoint(f(index, agent))`. They run from
  `Update`.
- **Properties:** `Status` (`"Idle"`, `"Moving"`, `"Arrived"`, `"Failed"`), `RemainingDistance`, `Velocity`,
  `Destination`, `Target`, `FailureReason`, `Patrolling`, `Exists`, `Object`; `Speed`, `Acceleration`,
  `StoppingDistance` and `Radius` are writable.
- **Stuck detection:** no progress along the path for 3 seconds re-paths. The third time, the agent fails with
  `"stuck: no progress along the path for 3 seconds"`.
- **Avoidance** is local: agents push apart by `Radius`, and an agent steers around an unanchored body in front of
  it. It is not crowd simulation.

```lua
-- A guard: patrol, chase what it sees (see Perception), go back.
local agent
function Start()
  agent = Navigation.CreateAgent(self, { Speed = 6, StoppingDistance = 0.8 })
  agent:Patrol({ Vector3.new(-26, 0, -6), Vector3.new(26, 0, -6), Vector3.new(26, 0, -44) }, { Loop = true, Wait = 1 })
  agent:OnFailed(function(reason) print("guard: " .. reason) end)
end
function Update(dt)
  agent:Update(dt)
end
```

**Limits.**
- It is a 2.5D grid with one surface per column, so a bridge over a walkable floor gives one of the two (lowest with
  headroom, or `UseHighestSurface`).
- Moving platforms are not followed while baked.
- Paths cannot use jumps, ladders or links between separate areas.
- A path search gives up after 200,000 cells (use a larger `CellSize`).

## 2. Dialogue and quests

### `Wisko.Dialogue`

A conversation is a plain table of nodes. You add the content; the module only reads these names:

- `Start`: the first node's id.
- `Nodes = { id = node }`, where a node has `Speaker`, `Text`, `Options`, `Next` and `Action(ctx)`.
- An option has `Text`, `Next`, `Condition(ctx)` and `Action(ctx)`.

`Text`, `Speaker` and option `Text` may be functions of the context table, for answers that depend on the game state.
Options whose `Condition` is false are not shown.

- `Dialogue.Start(tree, { Context, Presenter, Start, OnNode, OnChoice, OnEnd })` → conversation.
- `conversation:GetOptions()` → `{ Index, Text, Option }` for the shown options; `Choose(i)`, `Advance()` (for a node
  without options), `End(reason)`, `Goto(id)`, `GetNode()`, `GetText()`, `GetSpeaker()`.
- Signals: `OnNode`, `OnChoice`, `OnEnd`.
- `Dialogue.Presenter({ Width, Color, TextColor, OptionColor, Keys })` is an optional GUI:
  - a bottom panel with the speaker, the text and one button per option;
  - keys 1–9 choose, and Enter, Space or "Continue" advance;
  - it is built with `UI.Create`. Any table with `Show(conversation)` and `Hide()` is a presenter, so you can use your
    own.

### `Wisko.Quests`

- `Quests.new()` → log.
- `log:Define({ Id, Title, Requires = { ids }, Objectives = { { Id, Text, Target } }, Reward = function(quest, log) end, AutoComplete })`.
- **States:** `"Locked"` (a required quest is not completed), `"Available"`, `"Active"`, `"Completed"`, `"Failed"`.
- **Methods:** `Start(id)`, `CanStart(id)`, `Progress(quest, objective, amount?)`, `SetProgress`, `Complete(id)`,
  `Fail(id)`, `Get(id)`, `GetState(id)`, `IsActive(id)`, `IsComplete(id)`, `GetQuests(state?)`, `GetActive()`.
- **Signals:** `OnChanged`, `OnStarted`, `OnCompleted`, `OnObjectiveChanged`.
- `log:Save()` gives plain data for `GameSave`; `log:Load(data)` restores it.
- `Quests.Tracker(log, { Position, Width, FontSize })` is an optional HUD of active quests and objectives
  ("Gather wood 2/5").

The showcase's Mira (`Mira.lua`) gives and completes quests through dialogue options whose conditions read the
inventory and quest log.

## 3. Game time and day/night

`GameTime` is one clock per Play session, advanced by scaled time (it pauses with `Time.SetTimeScale(0)`). It starts on
day 1 at 08:00, and a day lasts 600 real seconds at scale 1.

| API | Notes |
|---|---|
| `Day`, `Hour` (with fraction), `Minute`, `TimeOfDay` (0–1), `TotalHours` | Read-only. |
| `DayLength` (1 to 10,000,000 s), `Scale` (0–10,000), `Paused` | Writable. |
| `Pause()`, `Resume()`, `Set(day, hour)` | |
| `At(hour, f(day))`, `After(hours, f)`, `Every(hours, f)`, `OnHourChanged(f(hour, day))`, `OnDayChanged(f(day))` | → `Connection`, owned by the calling script. They run in the `Time.OnUpdate` step. When hours pass in one frame (a large `Scale`, a `Set` forward), each schedule catches up in order, at most 48 times per frame; a `Set` backward fires nothing. |
| `IsBetween(fromHour, toHour)` | Wraps past midnight (`IsBetween(20, 6)`). |
| `Clock()` | `"HH:MM"`. |
| `GetState()`, `SetState(state)` | Plain data for `GameSave`. |

**`Wisko.DayNight`.**
- `DayNight.Start({ Keyframes, Sunrise = 6, Sunset = 18, MaxElevation = 70, Weather, Sun, Colors })` → cycle
  (`Apply(hour)`, `Stop()`).
- Every frame it sets only existing Atmosphere settings: sun elevation, heading, intensity and colour; sky, horizon and
  ambient colours and intensities.
- With `Weather`, rain and clouds dim the sun.
- Keyframes are plain tables (`DayNight.Keyframes` are the defaults).
- Night is a dark blue sky and dim light with the sun below the horizon. There are no stars and no moon.

## 4. Weather and particles

### `Particles` (engine)

`Particles.CreateEmitter(options)` → emitter. Options:

| Option | Meaning |
|---|---|
| `Position`, `Area` | Where particles spawn: a box. With `FollowCamera` the position is an offset from the camera. |
| `Rate`, `Lifetime` | How many spawn per second, and how many seconds each lives. |
| `Velocity`, `Spread`, `Gravity` | Starting velocity, random spread (a fraction of the velocity), and how much the world's gravity pulls them. |
| `Color` (`"#RRGGBB"`), `Size`, `Length` | Colour, width in studs, and streak length (0: square particles). |
| `FollowCamera` | Spawn around the camera, as rain and snow do. |
| `MaxParticles`, `Enabled` | Most alive at once; whether it spawns. |

- **Methods:** `Burst(n)`, `Clear()`, `Destroy()`. `Count` and `Exists` are read-only; `Particles.Count` is the
  session total.
- **Limits:** 64 emitters and 60,000 particles per session.
- **Drawing:**
  - The simulation is on the CPU, with no collisions.
  - Particles are camera-facing quads or streaks drawn with the scene's fog.
  - On OpenGL they were checked on screen: 1,265–1,460 rain streaks with fog in the showcase's GUI test.

### `Wisko.Weather`

- `Weather.new({ States, Initial, WindTag, WindForce, Shake, Seed })` → weather.
- `Set(name, seconds)` blends to a state; `GetState()`, `Lightning()`, `Save()`, `Load(data)`, `Destroy()`.
- Signals: `OnChanged(f(state, previous))`, `OnLightning`.
- Default states are `Clear`, `Cloudy`, `Fog`, `Rain`, `Storm` and `Snow`. They are plain tables in
  `Weather.States`; give your own.

**What is drawn and what is only a value:**

| Effect | How it shows |
|---|---|
| Fog (density, colour, start, end), haze, saturation, exposure | The Atmosphere: drawn. The weather changes the Play session's Atmosphere only while blending; the project's settings are untouched. |
| Rain, snow | Particle streaks or flakes around the camera: drawn. They do not collide, splash or wet anything. |
| Lightning | A short exposure flash and an optional small camera shake: drawn. There is no bolt geometry. |
| Wind | A vector (`weather.Wind`). It slants rain and drifts snow (visible) and pushes unanchored Parts tagged `WindTag`. Nothing else feels it unless a script reads it. |
| Clouds | Not drawn: there is no cloud renderer. "Cloudy" greys and dims the light. |

## 5. Crafting

**`Wisko.Crafting`.**
- `Crafting.Recipes({ { Id, Inputs = { item = n }, Outputs = { item = n } or function(ctx), Time, Station, Requires = function(ctx) -> ok, reason } })`
  → recipe book.
- Recipe book: `Add`, `Get`, `List(filter)`, and `CanCraft(id, inventory, ctx?, count?)` → `ok, missing, reason`.
- `Crafting.Station({ Kind, Recipes, Object, Queue = 1, Speed = 1 })` → station.
- `station:Start(id, inventory, { Count, Context, Owner })` → job, or `nil, reason`. Inputs are taken at once;
  outputs arrive after `Time` seconds (scaled time).
- Job: `GetProgress()`, `GetRemaining()`, `Cancel(refund)`, `OnComplete(f)`, and `State`.
- Station: `GetJobs()`, `IsBusy()`, `GetAvailable(inventory, ctx)`, and the signals `OnStarted`, `OnCrafted`,
  `OnCancelled`.
- An inventory is anything with `Count(item)`, `Remove(item, n) → bool` and `Add(item, n)`, called with a dot.
  `Crafting.TableInventory(t)` wraps a `{ item = count }` table.
- Function outputs make upgrades: the showcase's "Sharpen axe" raises an attribute instead of adding an item.

## 6. Building and placement

**`Wisko.Building`.**
- `Building.Placer({ Grid = 1, RotationStep = 90, MaxDistance = 40, SurfaceSnap = true, Ignore, ValidColor, InvalidColor, CanPlace = function(info) -> ok, reason, Tag = "Built" })`
  → placer.
- `placer:Begin(blueprint)` takes `{ Type, Size, Color, Name, ... }` or a list of pieces with `Offset`.
- `placer:Update()` aims at the middle of the screen; `placer:SetTarget(point)` aims at a point instead.
- `placer:Rotate(steps)`, `placer:Validate()`, `placer:Cancel()`.
- `placer:Confirm()` → the created objects, or `nil, reason`.
- `placer.Valid` and `placer.Reason`; signals `OnPlaced` and `OnCancelled`.
- **Previews** are Play-only Parts tagged `BuildPreview`. They have no collisions and turn green or red. Confirm checks
  the place again with `Physics.OverlapBox` and your `CanPlace`. The scene cannot be saved during Play, so a preview
  never becomes a saved object.
- **Helpers:** `Building.Remove(object, force)`, `Duplicate(object, offset, name)`,
  `Align(objects, axis, mode)`, `Describe(objects)` → plain data, and `Rebuild(list)`. `Describe` and `Rebuild` are
  how built pieces go into a `GameSave`.

## 7. Vision, detection and noise

| API | Returns | Notes |
|---|---|---|
| `Perception.InFieldOfView(observer, target, degrees, range?)` | `inView, angle, distance` | Pure maths on the observer's `Forward`. |
| `Perception.HasLineOfSight(from, to, filter?)` | `clear, hit` | A physics raycast; objects at both ends are ignored. |
| `Perception.CanSee(observer, target, options?)` | `seen, info` | `FieldOfView` (120), `Range` (50), `EyeOffset`, `Filter`. |
| `Perception.FindVisible(observer, options?)` | list | `OverlapSphere` candidates, then `CanSee`; `OnlyTags`, `IgnoreTags`. |
| `Perception.CreateSensor(observer, options?)` | sensor | See below. |
| `Perception.EmitNoise(position, options?)` | | `Radius` (20), `Kind` ("Noise"), `Source`, `Loudness` (1), `Data` (plain table). Heard next frame. |
| `Perception.OnNoise(listener, f(noise, listener), { Hearing })` | `Connection` | Every noise within `Radius × Hearing` of the listener. |

**Sensors.**
- Options: `Range` (30), `FieldOfView` (120), `EyeOffset`, `OnlyTags`, `IgnoreTags`, `LineOfSight` (true),
  `Interval` (0.1 s between looks), `Memory` (seconds an object stays detected after it leaves view), `Debug` (draws
  the cone), `Filter`.
- A sensor looks by itself every `Interval` and belongs to the script.
- Methods: `OnDetected(f(object))`, `OnLost(f(object))`, `GetDetected()`, `IsDetected(object)`, `Update()` (look
  now), `Destroy()`.
- Properties: `Enabled`, `Debug`, `Range` and `FieldOfView` are writable; `Count`, `Exists` and `Observer` are
  read-only.

**Noise** is a developer-defined event, not sound. Walls do not block it and nothing is heard from audio. The
showcase's player emits footsteps while running, and the guard investigates them.

## 8. Teleports, portals and checkpoints

**`object:Teleport(position, rotation?, options?)`.**
- Moves the object and its physics body together in one step (it does not travel through what is in between).
- It clears the velocity, or keeps it with `KeepVelocity = true`. `RotateVelocity` (default true) turns a kept
  velocity with the rotation.
- It wakes the body. Objects attached with `SetParent` come along.
- A child of another object cannot be teleported on its own (an error says so). Teleport its parent.

**`Wisko.Teleport`.**
- `Teleport.Pad(trigger, { Destination, Offset, Cooldown = 1, KeepVelocity, FaceDestination, Filter, Enabled })` →
  pad.
- `Teleport.Link(a, b)` makes two pads lead to each other.
- Pads: `SetDestination`, `Send(object)`, `OnTeleported(f(object, from, to))`, `Destroy()`.
- **Arrival suppression:** a traveller arriving on a pad is not sent back until it has left that pad's trigger. Each
  pad also has a cooldown per traveller, so there is no ping-pong.
- `Teleport.Checkpoints({ Spawn, Offset, Filter, KeepRotation })` keeps respawn points per object.
  - `Watch(trigger, order)`, `Activate`, `GetRespawn(who)`, `Respawn(who)`, `Save(objects)` and `Load(data, objects)` (keyed by object name).
  - Signals `OnActivated` and `OnRespawned`.

Portals move objects; they do not render a view of the other side. A camera feed can show one on a GUI Image.

## 9. Logic and puzzles

**`Wisko.Logic`.**
- Every block is a **signal**: a table with `Value` and `OnChanged(f(value, old))`. `Bind(f)` calls `f` now and on
  every change.
- **Inputs:**
  - `Logic.Switch(object, { On, Attribute = "On", CanUse, Toggle })`: `Set`, `Toggle`, `Use(user)`.
  - `Logic.PressurePlate(trigger, { Filter, MinCount, Attribute = "Pressed" })`.
  - `Logic.Volume(trigger, { Filter })`: `Value` is the count; `OnEntered`, `OnExited`, `GetOccupants`.
  - `Logic.Counter({ Start, Target })`: `Increment`, `Decrement`, `Set`, `Reset`; `Reached` is also a signal.
  - `Logic.Sequence(expected, { Timeout, OnComplete, OnWrong })`: `Input(value)` → `"progress"`, `"complete"` or
    `"wrong"`.
- **Combinations:** `Logic.All(...)`, `Any(...)`, `Not(signal)`, `Hold(signal, seconds)` (stays true that long after
  the input goes false), `Window(signal, seconds)`, and `Delay(seconds, f)`.
- **Outputs:** `Logic.Mover(object, { Offset, Duration })` moves a door, gate or bridge between its place and
  place + `Offset`: `Set(open)`, `Open`, `Close`, `GetProgress`. An anchored Part is driven by velocity, so riders
  are carried.
- Switches and plates mirror their state in an attribute, so other scripts, `OnAttributeChanged` and the HUD can
  follow it.

```lua
local Logic = require("Wisko.Logic")
local open = Logic.All(Logic.Switch(Scene.Find("SwitchA")), Logic.Switch(Scene.Find("SwitchB")),
  Logic.Hold(Logic.PressurePlate(Scene.Find("Plate")), 4))
local door = Logic.Mover(Scene.Find("VaultDoor"), { Offset = Vector3.new(0, 6, 0), Duration = 1.2 })
open:OnChanged(function(isOpen) door:Set(isOpen) end)
```

## 10. Procedural generation

**`Random.new(seed?)`** → generator. A whole-number or string seed gives the same sequence on every platform:
xoshiro256** on Lua integers, seeded by splitmix64. `Random.new(1):Integer(1, 1000000)` is always 702922, which an
independent C# implementation confirms in the tests.

- Methods: `Number(min?, max?)`, `Integer(min, max)`, `Bool(p?)`, `Choice(list)`, `Weighted(entries)`,
  `Shuffle(list)`, `UnitVector()`, `PointInBox(min, max)`, `PointInCircle(radius)`, `Clone()`.
- `GetState()` and `SetState(state)` are for saves; `Seed` is the seed it started from.
- Lua's own `math.random` is unchanged. Use `Random` when results must repeat.

**`Noise.Perlin(x, y?, z?, seed?)`** gives smooth noise in about −1..1, and the same seed gives the same field.
`Noise.Fractal(x, y?, z?, { Octaves = 4, Persistence = 0.5, Lacunarity = 2, Scale, Seed })` adds octaves together.

**`Wisko.Generate`.**
- `Generate.Job(work, { Budget = 2, OnProgress, OnDone })` runs `work(job)` as a coroutine a few milliseconds per frame.
  Inside it, `job:Step(done, total)` reports progress and yields when the budget is spent. This keeps large
  generations from freezing the game or running into a script's per-callback instruction budget.
- Job: `Cancel()` (what was made stays), `OnProgress`, `OnDone`, `Progress`.
- `Generate.Spawn(points, make(point, i), options)` makes one object per point, as a job.
- `Generate.Grid(width, depth, cellSize, origin, f)`.
- `Generate.PoissonDisk(rng, { Min, Max, Radius, Limit, Tries = 30 })` gives evenly spread points at least `Radius`
  apart.
- `Generate.Scatter(rng, count, { Min, Max })` gives uniform points.

The showcase's forest (`Forest.lua`) grows the same 100-odd trees and rocks from its seed every session, with a
progress bar, and saves which ones were harvested.

## 11. Equipment and bone attachments

**Bones.**
- `object:GetBones()` lists a model's bone names (empty for a Part); `object:HasBone(name)`.
- `object:GetBoneTransform(name)` → `position, rotation` (world space, in the animated pose shown now), or
  `nil, reason`.
- `child:SetParent(model, offsetPosition?, offsetRotation?, boneName?)` makes the child follow that bone every frame,
  after animation and before `LateUpdate`. Without a bone it follows the model itself.

**`Wisko.Equipment`.**
- `Equipment.new(character, { Owner, Slots = { Name = { Bone, Offset, Rotation, Accepts(item) } } })` → gear.
- `Equip(slot, item)` → `true` or `false, reason`. `Unequip(slot)` drops the item where it is, and physics takes it
  again.
- `Get(slot)`, `SlotOf(item)`, `DefineSlot`, `GetSlotTransform(slot)`, `Equipment.GetOwner(item)`.
- Signals: `OnEquipped(f(slot, item))` and `OnUnequipped`.
- An equipped item does not collide with its character. It gets the attributes `EquippedBy`, `EquippedSlot` and
  `Owner`. Destroying the item empties its slot; destroying the character drops everything.

In the showcase, Q puts the crafted axe in the robot's right hand (`Palm2.R`), and it swings with the walk and run
animations.

## 12. Racing and checkpoints

**`Wisko.Race`.**
- `Race.new({ Checkpoints = { triggers in order }, Laps = 1, Racers, Validate(racer, index), Clock })` → race.
- `AddRacer(object)`, `Start(countdown?)`, `GetElapsed()`, `GetProgress(racer)`, `GetSplits(racer)`,
  `GetRankings(compare?)`, `Destroy()`.
- **Signals:**
  - `OnStarted`;
  - `OnCheckpoint(f(racer, index, lap, split))`;
  - `OnLap(f(racer, lap, lapTime))`;
  - `OnFinished(f(racer, total, place))`;
  - `OnWrongCheckpoint`, for a checkpoint out of order, which does not count;
  - `OnAllFinished`.
- **Timing:** times are game time (`Time.elapsedTime` by default: frame-accurate and paused with the game). Pass
  `Clock = function() return Time.realTime end` for real time.
- `Race.FormatTime(seconds)` formats as `"1:05.32"`.
- `Race.Speedometer(object, smoothing)` → meter: `Update(dt)` returns the smoothed speed; `Speed` and `TopSpeed` fields.
- `Race.BestTimes(slot, course)` keeps best times in a `GameSave` slot: `Get()`, and `Submit(time)` → improved.

## 13. Cameras, cinematics and camera feeds

`Camera` stays the gameplay camera and stays fully scriptable. Named cameras are extra viewpoints. The **active**
camera only decides what is shown. When none is active, the gameplay camera is shown.

| API | Notes |
|---|---|
| `Cameras.Create(name, { Position, Rotation, LookAt, FieldOfView = 70 })` | A named camera (unique name), owned by the script. |
| `Cameras.Find(name)`, `GetAll()`, `GetActive()` | |
| `Cameras.SetActive(camera or nil, { Blend = seconds, Easing = "Smooth" or "Linear" })` | Blends in real time, from whatever is shown now. |
| `Cameras.Shake(intensity, duration = 0.5, frequency = 12)` | Shakes what is shown, fading out (0.3 is a bump, 1.5 an explosion). |
| `Cameras.OnActivated(f(camera, previous))` | Runs the next frame. |
| `Cameras.CreateFeed(camera, options)`, `camera:CreateFeed({ Width = 480, Height = 270 })` | 16–1,024 px each, at most 4 feeds. `true`, or `false, reason`. |
| `Cameras.FeedsSupported` | `true` on OpenGL, `false` on bgfx. |
| camera: `Name`, `Position`, `Rotation` (pitch, yaw), `Forward`, `FieldOfView` (10–120), `Active`, `Exists`, `Following`, `HasFeed` | |
| camera: `LookAt(point or object)`, `Follow(object, { Offset, LookAt, LookOffset, Smoothing })`, `StopFollowing()`, `Destroy()` | |
| camera: `PlayPath(path, { Duration or Speed, LookAt, Loop, Easing })` → playback (`Stop`, `OnFinished`, `Playing`, `Progress`), `StopPath()` | Cinematics on a `Path`. |

**Camera feeds (render to texture).**
- A camera with a feed is rendered every frame into its own texture: the world, particles and debug lines, but no
  editor overlays or game GUI.
- A GUI Image whose `Image` is `"camera:<Name>"` shows it, for security monitors, picture-in-picture or rear-view
  mirrors.
- The showcase's GUI test confirms that the HUD Image shows the feed on screen, the right way up: a 384×216 feed, a
  mean colour difference of 0.1 between the screen and the texture, and a non-flat image.
- On bgfx (macOS) there are no feeds; the Image stays empty and `CreateFeed` says why.

```lua
local cam = Cameras.Create("Flyover", { FieldOfView = 60 })
local path = Path.new({ Vector3.new(-60, 30, 60), Vector3.new(0, 25, 70), Vector3.new(60, 30, 40) })
Cameras.SetActive(cam, { Blend = 1 })
cam:PlayPath(path, { Duration = 10, LookAt = Vector3.new(0, 0, 0) }):OnFinished(function()
  Cameras.SetActive(nil, { Blend = 1.5 })     -- back to the gameplay camera
end)
```

## 14. Paths and splines

**`Path.new(points, { Type = "CatmullRom" | "Linear" | "Bezier", Closed = false })`** → path.
- Catmull-Rom passes through every point.
- Bezier takes 3n+1 control points when open (3n when closed).
- Sampling is by arc length, so equal distances along the path are equal distances in space.

| Path | Notes |
|---|---|
| `Length`, `Closed`, `Type`, `Count` | Read-only. |
| `GetPointAtDistance(d)`, `GetTangentAtDistance(d)` | Studs along the curve. |
| `GetPointAt(t)`, `GetTangentAt(t)` | t from 0 to 1, by distance. |
| `GetClosestDistance(point)` | `distance, point`. |
| `GetControlPoints()`, `Reverse()`, `Draw(color?, seconds?)` | |

**`Path.Follow(object, path, options)`** → follower.
- Options: `Speed` (5), `Loop`, `PingPong`, `Orient` (face along the path), `StartDistance`, `Reverse`, `Offset`,
  `Mode`.
- `Mode = "Auto"` (the default) drives an anchored, colliding Part by velocity, so what stands on it rides along.
  Anything else is placed. `"Velocity"` and `"Transform"` force one way.
- Methods: `Pause`, `Resume`, `Stop`, `Reverse`; signals `OnFinished` and `OnLooped`.
- `Speed` and `Distance` are writable; `Direction`, `Paused`, `Finished`, `Object` and `Path` are read-only.

## 15. Saved games

**`GameSave.Save(slot, data, { Version = 1, Metadata })`** → `true`, or `false, reason` (for example, the disk is
full). `Metadata` is a small plain table that `List` and `GetInfo` show without loading the data.
- **Load:** `GameSave.Load(slot)` → `data, info`, or `nil, reason`. `info` is
  `{ Slot, Version, SavedAt, Metadata, Recovered }`.
- **Other calls:** `Exists(slot)`, `Delete(slot)`, `List()`, `GetInfo(slot)`. `Available` is false when the game has
  no project folder.

**Where.**
- `<project>/Saves/<slot>.json`, and nowhere else.
- A slot is 1–64 letters, digits, spaces, `_` or `-`, starting with a letter or digit. `..`, slashes, drive letters and
  Windows device names cannot be slots.
- A `Saves` folder that is a link (junction or symlink) is refused.

**Safe writes.**
- The data is written to a temporary file in the same folder, flushed to disk, then swapped in with an atomic
  replace.
- The previous save is kept as `<slot>.json.bak`.
- If a save file is damaged, `Load` falls back to the backup and says so (`info.Recovered = true`). If both are
  damaged, it answers `nil, reason`.

**What can be saved.**
- Plain data: `nil`-free tables, booleans, finite numbers (integers and floats stay what they were), strings
  (including binary ones) and `Vector3`.
- Tables may mix list and named keys.
- Anything else raises an error naming the path to the value (`GameSave.Save: data.Player.Target is a scene object: save what you need to recreate it
  (its Name, Id, position...)`). That covers functions, scene objects, GUI elements, handles, other metatables, cycles,
  NaN or infinity, and more than 64 levels, 200,000 values or 16 MB.
- To save an object, save what you need to rebuild it: its name, position, or `Building.Describe`.

**Loading never runs code.** The file is JSON read by .NET's parser, and only plain values come back. A `Version`
higher than the game knows is the game's to refuse; the showcase does this.

```lua
local ok, why = GameSave.Save("Slot1", {
  Position = player.Transform.Position + Vector3.new(0, 0, 0),   -- a copy (Position is a live view)
  Items = items, Quests = log:Save(), Clock = GameTime.GetState(),
}, { Version = 2, Metadata = { Day = GameTime.Day } })

local data, info = GameSave.Load("Slot1")
if data then
  if info.Version < 2 then data.Items = migrate(data.Items) end
  GameTime.SetState(data.Clock)
end
```

## Debug drawing

`DebugDraw` draws lines while playing, for debugging. They are never saved and are gone at Stop.

- `Line(a, b, color?, seconds?)`, `Ray(origin, direction, ...)`, `Sphere(center, radius, ...)`,
  `Box(center, size, color?, seconds?, rotation?)`, `Point(point, size?, ...)`, and `Path(points or Path or navigation path, ...)`.
- `Clear()` removes every line; `Count` is how many are shown now.
- `seconds = 0` (the default) draws for this frame only. Colours are `"#RRGGBB"` (default yellow). At most 20,000
  lines.
- Navigation, agents, sensors and paths have their own `Draw` helpers on top of it.

## Errors and limits

| Situation | What happens |
|---|---|
| Wrong argument type, unknown option, out-of-range number | Error at your line naming the call and the option. |
| `FindPath` / `SetDestination` to an unreachable or unwalkable place | `nil` / `false` and a reason. The agent's `OnFailed` runs with the reason. |
| An agent's object destroyed | The agent ends. A `Follow` target destroyed makes it fail with `"the followed object no longer exists"`. |
| Teleporting a child (`SetParent`) | Error: teleport its parent. |
| `GetBoneTransform` on a Part or an unknown bone | `nil, reason`. |
| `GameSave.Save` with unsaveable data | Error naming the path to the value. |
| `GameSave.Save` I/O failure | `false, reason` (the previous save stays intact). |
| `GameSave.Load` on a missing or damaged slot | `nil, reason`, or the backup with `Recovered = true`. |
| `CreateFeed` on bgfx, or a fifth feed | `false, reason`. |
| More than 64 emitters or 60,000 particles | `CreateEmitter` errors with the limit; spawning stops at the limit. |
| A long generation in one callback | Use `Generate.Job`: the per-callback instruction budget still applies. |

**Behaviour notes.**
- `Transform.Position` is a live view of the object. To keep a position, copy it:
  `p + Vector3.new(0, 0, 0)` or `Vector3.new(p.x, p.y, p.z)`.
- Imported models have no physics colliders (unchanged from earlier milestones). Give a character a collider Part and
  attach the model to it, as the showcase's player and guard do. Agents can also move a model with `Move = "Transform"`.
- The scene cannot be saved during Play, so nothing these systems create (previews, built pieces, generated objects, agents)
  ever ends up in the scene file. Persist it with `GameSave`.

## Performance

Measured on the development machine:

- Intel Core i5-12400F (12 logical CPUs), NVIDIA GeForce RTX 3060 (driver 610.74), 15.7 GB RAM.
- Windows 11 Home (10.0.26300), .NET 8.0.28, Release build, no debugger.

The headless benchmark is a separate program that plays real scenes through the engine's Play loop, and its figures are
script and simulation time per frame. The GUI figures come from the editor flow test.

| Workload | Result |
|---|---|
| 10 / 50 / 100 NPCs: navigation agents patrolling, each with a sensor | 0.22 / 0.97 / 1.98 ms per frame (p95 0.39 / 1.78 / 3.87) |
| 20 `FindPath` calls per frame from Lua, 140×140 grid with walls | 2.93 ms per frame (p95 6.41) |
| 1,000 `FindPath` on a 200×200 grid (C#) | bake 15 ms; 0.16 ms per path (p95 0.49) |
| Navigation bake of the showcase, 190×210 cells, once at load time | 163 ms in the installed (Release) app; 292–316 ms in Debug test runs |
| 1,000 objects by a 2 ms/frame `Generate` job | made in 15 frames, longest frame 3.54 ms; then 0.010 ms per frame standing |
| 1,000 `Scene.Create` in one chunk | 23 ms |
| 100 trigger volumes, 100 balls through them (connections) | 0.10 ms per frame (p95 0.15) |
| 10,000 `Timer.Every` + 1,000 `GameTime.Every` | 0.52 ms per frame (p95 1.32) |
| 100 `Path.Follow` platforms, 20 riders | 1.39 ms per frame (p95 2.13) |
| Save + load, about 11,000 values (362 KB file), 50 cycles | save 19.7 ms (p95 26), load 10.7 ms (p95 21) |
| 1 / 10 / 20 / 50 walking robots with a tool on a hand bone | 0.02 / 0.17 / 0.34 / 0.53 ms per frame |
| Particle simulation: 2,000 / 10,000 / 20,000 alive | 0.02 / 0.05 / 0.09 ms per frame |
| 20,000 debug lines added, then expired | add 3.5 ms, expire 0.14 ms |
| GUI (Debug build), showcase in Play with one 384×216 feed, ~1,280 rain particles and 525 debug lines | feed pass 1.22 ms CPU / 0.48 ms GPU; particles and lines 0.36 ms CPU / 0.02 ms GPU; 60.1 fps (the display's refresh) |
| Installed app (Release), the same scene in Play | 60.0 fps, 11.9% of a CPU core, GPU 1.85 ms per frame (p95 1.94) |

What the numbers mean:
- **Saving is synchronous.** About 11,000 values cost one ~20 ms frame. Save at checkpoints or on a key, not every
  frame.
- **Navigation bake** costs hundreds of milliseconds for a large fine grid. Bake once, then `Update` small boxes.
- **Cost per script.** Each script's environment holds these globals; libraries that keep per-script state
  (`Navigation`, `Perception`, `GameTime`) are built on first use. Compared with a build without them, a script costs about
  2 KB of Lua heap and 12 µs more to start.

## Platforms

| | Windows (OpenGL) | macOS (bgfx/Metal) |
|---|---|---|
| Navigation, perception, time, saves, paths, random, modules | Tested (headless and in the editor) | Same C# and Lua code; **not run on a Mac** |
| Particles, debug lines | Drawn and checked on screen | Implemented as transient geometry; **unverified** (no Mac available) |
| Camera feeds | Drawn and checked on screen | **Not implemented**: `Cameras.FeedsSupported` is false |
| Named cameras, blends, shake, paths | Checked on screen | Same camera code as the gameplay camera; unverified |

## The Gameplay Systems Showcase

One project shows all 15 areas using only the public API.

**Make it** from the repository folder:

```
dotnet run --project Tests/Wisko.Tests -- --make-v5-demo "%USERPROFILE%\Documents\Wisko\Projects"
```

This creates **Gameplay Systems Showcase**: the scene, the RobotExpressive, CesiumMan and Fox models, and the scripts in
`Assets/Scripts`. If the project already exists, it refuses and changes nothing; other projects are never touched.

What is where, and the controls, are in [examples/gameplay-systems/README.md](../examples/gameplay-systems/README.md).
