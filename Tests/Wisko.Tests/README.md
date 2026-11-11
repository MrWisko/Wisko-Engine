# Wisko.Tests

Checks against the real engine: real Lua 5.4, BEPU physics, Assimp and file I/O, and optionally the real editor
window. A plain console runner (no test framework packages); exit code 0 when every check passed.

```
dotnet run --project Tests/Wisko.Tests                  # headless suite (any OS)
dotnet run --project Tests/Wisko.Tests -- --gui-smoke    # the real editor, scripted (Windows)
```

Options: `--screenshots <dir>` saves PNGs from the GUI smoke test; `--work <dir>` writes test projects there instead
of a new temporary folder; `--keep` keeps that folder afterwards; `--only <text>` runs just the sections whose title
contains the text (headless or GUI). Nothing outside the work folder is touched (the user's Documents\Wisko projects
and History are never used).

`--make-demo <projects folder>` runs no tests: it writes the Gameplay Basics Demo project ("Gameplay Basics Demo", scripts from
`examples/gameplay-basics/Scripts`) into that folder, and refuses if a project of that name exists there.
`--make-anim-demo <projects folder>` likewise writes the Animation demo ("Animation Demo": the models of
`examples/animation-demo/Models`, imported through the editor's import, and the scripts of
`examples/animation-demo/Scripts`).
`--make-v5-demo <projects folder>` writes the Gameplay Systems Showcase ("Gameplay Systems Showcase": the scripts of
`examples/gameplay-systems/Scripts` and three models from `examples/animation-demo/Models`), with the same refusal.
`--make-graphics-demo <projects folder>` writes the Graphics Showcase ("Graphics Showcase": the models of
`examples/graphics-demo/Models` and the Fox, generated textures, the scripts of `examples/graphics-demo/Scripts`), with
the same refusal.
`--make-release-demo <projects folder>` writes the editor and chat showcase ("Editor and Chat Showcase":
Parts to drag with any tool, typed rotations, a chat host with the default chat window), with the same refusal.
`--make-template-previews [folder]` renders the New World screen's template pictures with Wisko (a window opens) into
`Engine/Assets/TemplatePreviews` (or the folder given), with `previews.json`.

The animation checks use real, freely licensed models from `examples/animation-demo/Models` (see `CREDITS.md` there),
so the suite needs no network access.

## Headless suite

| Section | What it checks |
|---|---|
| Lua runtime and sandbox | Lua 5.4, no `io`/`os.execute`/`loadfile`/`dofile`/debug hooks, coroutines, project-local `require` (and that it cannot leave `Assets/Scripts`), per-script globals |
| Script API catalog | every global and member `ScriptApiCatalog` documents exists in a live script (editor completion and the sandbox cannot drift apart) |
| Play Mode | through `PlayModeController`: gravity and resting contact, collisions, raycasts, timers, cross-script events, `Scene.Create`, tags/attributes, GUI scripting on the Play copy, exact restore on Stop, deterministic repeat |
| Script faults | runtime and syntax errors with lines, the instruction budget stopping `while true do end`, missing scripts |
| Gameplay scripting: camera and movement | `LateUpdate` after physics, a follow camera keeping its offset, `Camera.Forward`, the mouse lock, `Input.GetAxis` with simulated keys, `RotationLocked`, `IsGrounded`, jumping, landing impact speed |
| Gameplay scripting: script communication | `GetScript` (names, missing and non-exporting scripts, mutual calls), private globals, `Shared` (fresh each session), wrappers equal across scripts, table events (copies, vectors, objects, rejected functions/cycles/metatables/depth/size), `Disconnect`, `OnDestroyed`, listeners removed with their object |
| Gameplay scripting: physics filters, gravity, projectiles | every filter option on every query, option errors, `Physics.Gravity` set and restored, projectiles from `Scene.Create` with `LinearVelocity`, ownership via attributes, a 600 studs/s projectile against a 1-stud wall |
| Gameplay scripting: Play-only attachment | `SetParent` / `Parent` / `GetChildren`, offsets following a turned parent, no collisions within the tree, loop and argument errors, letting go, a destroyed parent releasing its child, Stop restoring poses |
| V4: motion | `GravityScale`, damping, anchored Parts moving and spinning on their own and carrying riders |
| V4: conveyor belts | `SurfaceVelocity`: three belts handing a crate on, stop, reverse, a tall body riding, no teleporting |
| V4: collision groups | `CollisionGroup`, `Physics.SetGroupsCollide`, `IgnoreCollisionWith`, queries as a group, runtime changes |
| V4: joints | weld, hinge motor (windmill) and limits (door), ball socket, joint errors and lifetime |
| V4: object space and signals | `Forward`/`Right`/`Up`, point/vector conversions, `Rotate`/`RotateAround`/`LookAt`, `OnAttributeChanged`, math and Vector3 helpers |
| V4: flight and a raycast car | the Lua-only flight example (orb, keys, GUI button, Stop) and car (same speed at 30/60/144 fps, brake, steer, reverse, wall) |
| V4: World panel groups | group / ungroup / move / nest / rename and their errors, undo and redo, `Scene.json` round trip, older files, Play leaving groups alone |
| V4: GUI images | the Image element in `UI.json`, `UI.Create` options and their errors, cleanup across sessions |
| V4 systems (Lua only) | the `examples/gameplay-basics` scripts with scripted keys: mining, drops and regrowth; stats, coins, objectives and the HUD; damage, knockback, timed effects, X-ray targeting; solid / pass-through / selective / trigger collisions; eating, drinking, feeding |
| V4 demo | the whole demo scene: every script runs 10 s without a fault, the HUD, conveyors and machines work, Stop restores it exactly |
| Project persistence | `ProjectStore` create/list, Scene/UI/Atmosphere JSON round trips (Unicode), thumbnails, name rules (Windows reserved names), deletion safety (outside the root, the root, `..`, wrong Id) |
| Script assets | new-script template and unique names, attaching scripts in place or by copy, CRLF and BOM preserved on save |
| Assimp | OBJ import from a folder with a space and non-ASCII characters, copy into `Assets/Models`, reload |
| Animation: import | real GLB / FBX rigs: bone hierarchies, bone and clip counts, clip names and durations, 4-influence weights summing to 1, >4 influences and unnormalized weights reported, rest pose = bind pose where the file says so, duplicate node names, static models unchanged |
| Animation: evaluation | TRS compose / decompose (non-uniform and mirrored scale), quaternion conventions, linear / spherical / step sampling, clamping, parent-child poses, skin matrices |
| Animation: playback | the same pose at 30, 60 and 144 fps, loop wrap, one-shot end and its single Finished, pause / resume, speed (2x, 0, backwards), seek, restart, stop |
| Animation: blending | crossfade weights, no snap at the start, interrupted crossfades, per-frame movement far below an instant switch, missing channels blending to rest, looping into one-shot, fading stop |
| Animation: instances | one asset, independent animators (clip, time, speed, pose), shared data |
| Animation: bad data | missing bones, unusable bind poses, too many bones, unknown tracks, empty and duplicate and unnamed clips, keyframes out of order (refused with a message), a damaged file, missing files |
| Animation: Lua API | every animation method on a real model in Play: answers, errors, options, events, pause, speed, seek, crossfade, a destroyed object, a clone, primitives, Stop restoring the scene |
| Animation: persistence | Play on start (and a clip the model lacks), `Scene.json` round trip with only non-default values, duplicates, snapshots, a missing model file, reimport, reopening |
| Animation demo | the demo scene headless: the character walks, runs, idles, jumps and waves from its real movement; dancers and foxes play independently; Stop restores it exactly |
| V5: time, random, noise, paths | `Time.OnUpdate` (order, dt, disconnect), `Time.SetTimeScale` (Update's dt, physics, timers, animation and the game clock at half speed; 0 pauses; real time goes on), `Random` checked against an independent C# xoshiro256** (same seed, same numbers), state save/restore, `Noise`, `Path` lengths, sampling, closest points, Bezier and closed curves, `Path.Follow` (a platform carrying a rider, pause and resume, an exact end at any speed, ping-pong, Transform mode, errors), the lazy-library typo check |
| V5: game clock | `GameTime` advance, scale, pause, `Set`, `At` / `After` / `Every` / hour and day signals, errors, state round trip, `Wisko.DayNight` driving the session's Atmosphere (the project's back at Stop) |
| V5: game saves | every saveable type (integers vs floats, vectors, mixed keys, `$` keys, binary strings), refused values with their path, slot name rules (device names, `..`, separators, length), the depth and value-count limits, a damaged file recovering from `.bak`, foreign and newer files refused, a `Saves` folder that is a junction refused (Windows), no temporary files left, the save surviving into the next Play session |
| V5: debug lines and particles | lines for one frame or timed, counts, colour and vector errors, the renderer's list; emitters (options, errors, bursts, particles falling from the spawn box, draining when disabled, destroy), the session total, nothing left after Stop |
| V5: cameras | `Cameras.Create`, `LookAt`, `SetActive` and a smooth blend back, shake, `PlayPath`, `OnActivated`, `Destroy`, `Follow` (offset in the target's axes, looking at it, `StopFollowing`), feeds (refused without a renderer that draws them; with one, four allowed, a fifth refused, sizes checked), the gameplay camera kept scriptable |
| V5: triggers and teleport | trigger connections from any script, `object:Teleport` (velocity kept or cleared, rotation set, a child refused and arriving with its parent, Stop restoring the Part), `Wisko.Teleport` pads (one teleport, no ping-pong, sending back after leaving) and checkpoints (activate, respawn) |
| V5: bones and equipment | `GetBones` / `GetBoneTransform` on an animated RobotExpressive, `SetParent` to a bone following the animation exactly, a bad bone refused, `Wisko.Equipment` equip / unequip (attributes, `Anchored` restored) and a destroyed item leaving its slot |
| V5: navigation | paths around walls (no point inside, every segment walkable, exact ends, clearance from a block whose top is walkable), steps up to `MaxClimb` climbed and higher ones not, a gentle ramp walkable and a steep one not, unreachable and partial paths, rebakes and blocked boxes, option errors; agents patrolling and following a moving target, stuck detection against a wall the grid does not know, `Move = "None"`, an agent ending with its object |
| V5: perception | view cones (ahead, behind, range), line of sight and what blocks it, `CanSee`, `FindVisible`, sensors (detected, lost behind a wall, detected again, destroyed), a noise heard nearby |
| V5 modules | `Wisko.Dialogue`, `Quests`, `Crafting`, `Building`, `Logic`, `Race`, `Generate`, `Weather`, driven with scripted input |
| V5 showcase | the whole `examples/gameplay-systems` scene headless with scripted keys: every script runs without a fault, then F5, Stop, a new session and F9 restore position, inventory, quests, time and equipment |
| Graphics V1: colours, materials, settings, scenes, atmosphere | sRGB / linear round trips, `Part.Color` unchanged, material and preset rules, lights, model material overrides, `Graphics.json` (missing = Classic, defaults, damaged files), quality presets, the editor preference file, `Scene.json` materials and lights (only non-default values, older files), snapshots and duplicates, the Atmosphere's ambient mode, ground colour and reflections |
| Graphics V1: Khronos sample models import | the five sample models' alpha modes and cutoffs, double-sided flags, sampler wrap modes (also after the project copy), emissive strength, texture slots and supplied tangents; an OBJ's external texture kept when its material is edited |
| Graphics V1: Lua | `object.Material` values, names, ranges, presets and textures, `object.Light` with `AddLight` / `RemoveLight`, the `Graphics` global (read-only renderer), the Atmosphere's new members, Stop bringing everything back; a model's materials through the same API |
| Graphics V1: editor panels, texture import | the Materials tab's and the rendering settings' rows and edits (sliders, choices, switches, presets, lights, Custom quality, the 3D View preference); images copied into `Assets/Textures`, reused, numbered, refused |
| Graphics V1: Environment & Graphics panel | every setting the Atmosphere and Rendering panels had is in the panel exactly once and in the tab named for it (Atmosphere, Lighting, Rendering, Post-Processing); Atmosphere sliders set exactly what the Atmosphere panel's did, its switches keep their undo labels and its values read as before; muted (still editable) while the Atmosphere is off; read-only in Play |
| First release: template previews | every picture embedded at 1x / 2x / 3x, a real render, its fingerprints matching the templates as they are now, the density for a display scale, a layout that fits any window |
| First release: deletable baseplate | `Scene.json` (plain list unless deleted; the object form read back; older files), the editor's rules (spawning, drags, collisions) without it, undo, Play without its collider, Lua raycasts |
| First release: numeric rotation | parsing (decimals, negatives, `+`, comma, `°`, limits), normalization, display, the rotation order, mixed selections, the undo command, save |
| First release: dragging a Part's body | no jump at the start, the grab offset kept, the level plane without a surface, groups, overlaps refused, cancel |
| First release: chat | text cleaning (line breaks, controls, bidi, invisible characters, combining marks, length, reserved names); the service (rate limit, channels, bounded history, System / NPC messages, names, mute, block and forged messages through a simulated remote transport, the flood guard); the Lua `Chat` API in Play (next-frame delivery, kinds, impersonation attempts, channels, history copies, filters, rate limit, a failing listener, Stop); filters failing closed; the GUI TextBox (focus, typing, caret, paste, Enter, Escape, pointer, `UI.json`) and from Lua (`OnSubmit`, `Input.IsTyping`, `UI.MeasureText`); the default chat window (`Wisko.ChatUI`); 12,000 messages |
| First release: stress, all together, showcase | 1,000 Parts dragged as a group and among each other (with cancel), typed rotation and saving without a baseplate (timings reported); one world using everything in Play; the showcase made once and its chat script playing |
| Chat: message Data, window options, no chat unless asked, custom chat example | Data checked and cleaned (keys, types, limits, transports), from Lua (styling values, history, copies, refusals; never the sender or kind); the default window's placement, colours and `Data.Color`, option errors; new projects of both templates and a game that never uses Chat show no chat interface and `/` does nothing; `examples/chat/CustomChat.lua` played as it ships (T to type, team colours, `/me`) |
| Save safety | atomic writes: failures injected while writing, flushing and after the rename, a full disk, a held and a read-only destination (Windows) or permissions (macOS), stale temporary files, 300 saves in a row, 8 threads saving at once; every project and user file writer; a real crash test: a process saving a 1,500-part scene killed 200 times at random moments, `Scene.json` always one complete version |
| Lua memory limit | runaway tables, a 200 MB string, a caught error, a coroutine and Update growth stopped as `MemoryLimitExceeded`; memory returned; the host at the limit (events, GUI, queries) in Play; 100 sessions and 300 scripts with no leak; the preference file; every example project's peak against the default; the accounting's cost (information) |
| Syntax validator | valid code, error line (zero-based) |
| Windows frame pacing | `CompositorFramePacer` under a simulated compositor: normal pacing, early returns, DwmFlush failing or not waiting (bounded frame rate, no stall, no retry loop), recovery, bounded logging; on Windows also the real DwmFlush, measured in a fresh process on the pacing thread (at most 500 fps and 50% of a core), with two regression probes: a busy-waiting pacer must fail, a busy background thread must not |
| macOS fix pass | editor shortcuts for both conventions (Windows' Ctrl shortcuts unchanged; macOS Command by the letter on the key, AZERTY included; Command or Ctrl stopping the fly keys; Save through Save Project's path, not while a name is typed); World panel names from the OS's text input (accents, other scripts, Backspace, limit, Enter, Escape); starting without a display (bounded wait, clean refusal); the renderer's capabilities in the Environment & Graphics and Materials tabs (muted Universal-only rows, still editable, settings kept and saved, one notice per project, the New World note); bgfx's per-frame vertex budget for particles and debug lines, MSAA swap chain flags, OpenGL frame pacing on macOS; TGA and BMP textures |

Section titles that begin with an area name and a version label ("V4:", "Graphics V1:", "First release:") name the
stage of development the checks were written in; `--only` matches them as they are.

## GUI smoke test (`--gui-smoke`)

Before anything else the suite keeps the display awake for the whole run (SetThreadExecutionState; no input is
simulated) and checks that the compositor is composing at a real display rate (20-500 Hz), that the session is unlocked
and that it is local: a display that is already off is reported plainly instead of as failed frame-pacing checks.

Runs `EngineApplication` with the editor's own game module (OpenGL) on a project made for the test, and replays a
fixed tour by feeding Wisko's `InputManager` between frames (the OS cursor is never moved): open the project from the
Hub, select, Move/Resize/Rotate, select the Baseplate in the World panel, open a script from the World panel, Play (a Lua ball lands on the baseplate, the game
GUI's button reaches Lua `OnClick`), Stop, minimize, Leave to Hub (thumbnail). It checks:

- no OpenGL errors for the whole session (synchronous KHR_debug output) and none reported by the editor;
- 4x MSAA on the editor window (the sample count the driver provided, from the editor's log);
- compositor frame pacing active, 20-500 fps and a bounded CPU share in the Hub, editor, Play and while minimized;
- every step of the tour, from the editor's log.

Needs Windows with a desktop session and an OpenGL 3.3 GPU. Click positions assume the 1280x720 window at 100% display
scaling (the test says so when the scale differs). The window appears on screen while it runs.

Two more editor sessions follow the tour (`Gui/EditorFlowSmokeTests.cs`). They find what to click with the editor's
own hit tests (toolbar, menus, Hub cards, World panel rows, dialog buttons), so they do not depend on coordinates:

- **Leaving a project:** a new unnamed world left untouched; changed, then Don't Save, Cancel, or Save with a name;
  an existing project changed, then Don't Save (files byte-identical) or Save; the window's close button with the
  same question. Afterwards the projects on disk and the History records are checked.
- **World panel groups:** Ctrl+click multi-select, right-click Group, rename by typing, collapse and expand, drag a
  row into a group, Ctrl+Z / Ctrl+Y, Ctrl+G, nesting a group by dragging, Save, reopen, Play / Stop, Ungroup, and
  quitting with Don't Save.
- **Animated models** (`Gui/EditorFlowSmokeTests.Animation.cs`, on the Animation demo): the rest pose in the editor;
  the Animation panel (ToolBench > Animation) choosing a clip, Play, Pause (the image holds), scrubbing (the time and
  the image change), Stop (the 3D view pixel-identical to before) and the project never marked as changed by the
  preview; Loop / Speed / Play on start with undo and Save; Play with the saved settings, the Lua character walking,
  running, jumping and waving from scripted keys; Escape; leave and reopen (settings kept, skinned GPU meshes not
  accumulating).
- **Gameplay Systems Showcase** (`Gui/EditorFlowSmokeTests.V5.cs`): the flyover camera shown and moving, the blend back, the
  vault camera's feed rendered into a texture and shown by the HUD Image the right way up (compared pixel by pixel),
  rain particles and fog, debug lines, the per-pass GPU and CPU cost of feeds and particles, and Stop leaving no feeds,
  particles or lines behind. It runs a second time with the project switched to the Universal renderer.
- **Graphics** (`Gui/EditorFlowSmokeTests.Graphics.cs`, `GraphicsEditor.cs`, `Showcase.cs`): a Universal test project
  at every quality preset with screenshots, bloom and Filmic, selection outlines and gizmos through the HDR / MSAA
  path, Play / Stop, the Khronos sample models, and the project's textures and meshes released when leaving to the Hub;
  ToolBench > Environment & Graphics driven by clicks (every keyed row of the Atmosphere, Lighting, Rendering and
  Post-Processing tabs scrolled to and hit; Atmosphere edits: Sun Intensity, Fog, Sky Color, the Atmosphere switch, Sky
  Ambient, Reflections, Exposure with undo and redo; the Materials tab: Shading, a Metallic drag and a colour drag with
  undo and redo, add / remove a light; Add Light from the toolbar, a model's own material and Back to Imported, Quality,
  the 3D View preference, Renderer, Bloom, Tone Mapping, the Environment / Material buttons, an Atmosphere preset with
  undo), then Save (checked on disk), Leave to Hub and reopen (everything back); a forced shader
  failure falling back to the Classic renderer; and the Graphics V1 showcase built by its maker and played through its
  tour (night, fog, quality presets, Stop).
- **Editor and chat** (`Gui/EditorFlowSmokeTests.ReleasePrep.cs`):
  - The New World screen's template pictures: loaded, matching the templates, keyboard and mouse, create, cancel, reopen,
    and a small window.
  - Deleting the baseplate: World panel, Delete, Ctrl+click, the context menu, undo, Play, save, reopen, and an old
    project untouched.
  - The rotation fields: typing, Tab, Escape, refusal, undo, gizmo sync, two Parts, save and reopen.
  - Dragging bodies with Rotate and Resize active: handles still working, undo, Escape, groups, another camera angle, no
    baseplate, the Move tool unchanged, save.
  - The text chat in Play: the default window, `/` to type, OS text input, game keys held back while typing, Enter,
    Escape, and a click on the box.

The World panel's row helper scrolls the list to a row that is out of view, as the mouse wheel would.

`--only performance` runs one more session instead: projects of 1, 10 and 50 animated characters, timing the parts'
drawing on the CPU and the GPU (OpenGL timer queries) at rest and animated, and the frame rate in Play.

Scripted input is not physical input: these sessions feed the same InputManager and keyboard state that the window
fills from the OS, but the OS mouse and keyboard are never used. Screenshots of these sessions are read back from the
window's own front buffer, so they also work while the display is asleep.

## Adding checks

Add a method to a class in `Checks/` (or a new class) and call it from `Program.cs` inside `report.Section(...)`.
Use `report.Check(name, condition, detail)` for pass/fail and `TestReport.Info` for context. Lua-side checks print
`CHECK name true|false` through `TestSupport.ChkFunction` and are collected with `TestSupport.AssertLuaChecks`.
