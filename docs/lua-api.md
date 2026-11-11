# Lua scripting API

Wisko scripts are standard **Lua 5.4** with an engine API on top. This page documents the core gameplay API in detail:

- **Scripting basics:** the frame order, lifecycle functions, input, the camera, moving and grounding objects,
  attachment, physics queries, gravity and script-to-script communication.
- **Physics, movement and world tools:** gravity scale and damping, anchored Parts that move and spin on their own,
  conveyor surfaces, forces at a point, object-space helpers, collision groups and pair rules, physical joints (weld,
  hinge with motor and limits, ball socket), attribute change signals, `UI.Create` with options and Image elements, math
  helpers.
- **Animation:** playing, blending, pausing, seeking and timing the animation clips of imported rigged models, and
  hearing when a clip finishes. The whole system (formats, editor preview, limits) is described in
  [animation.md](animation.md).

More pages:

- [lua-gameplay-systems.md](lua-gameplay-systems.md): navigation agents, perception, game time, saved games, cameras and
  camera feeds, particles, debug drawing, seeded random numbers and noise, paths, teleport, bones, trigger connections,
  time scale, and the optional `Wisko.*` Lua modules (dialogue, quests, crafting, building, logic, teleport pads, races,
  equipment, weather, day/night, generation).
- [chat.md](chat.md): text chat building blocks and text boxes.
- [graphics.md](graphics.md#lua): materials, lights and rendering settings from Lua.
- [lua-limits.md](lua-limits.md): the instruction budget, the memory limit and other bounds.

The rest of the API (Scene, UI, Timer, Vector3, Atmosphere, tags, attributes, collision callbacks...) is summarised in
[Existing API](#existing-api-summary); the editor's code completion lists every member with a one-line description.

Nothing here is a ready-made game system: there is no built-in character controller, vehicle, health, hunger,
inventory, currency, resource or weapon system. These are building blocks; the examples show how to put them
together. Many examples are the scripts of the Gameplay Basics Demo, kept in the repository as plain Lua files in
[`examples/gameplay-basics/Scripts`](../examples/gameplay-basics/Scripts) and played by the automated tests.

**Animation**

- [Animation](#animation)

**Physics, movement and world tools**

- [Overview](#overview)
- [Conventions](#conventions)
- [Motion: gravity scale, damping, moving and spinning Parts, conveyors](#motion-gravity-scale-damping-moving-and-spinning-parts-conveyors)
- [Forces at a point, mass, local space](#forces-at-a-point-mass-local-space)
- [Object space and rotation](#object-space-and-rotation)
- [Collision control](#collision-control)
- [Joints](#joints)
- [Attribute change signals](#attribute-change-signals)
- [Math and Vector3 helpers](#math-and-vector3-helpers)
- [GUI from Lua: UI.Create options, Image, lifecycle](#gui-from-lua-uicreate-options-image-lifecycle)
- [Where data lives (object, script, session, saved)](#where-data-lives-object-script-session-saved)
- ["X-ray" damage: how it is meant](#x-ray-damage-how-it-is-meant)
- [World panel groups (editor)](#world-panel-groups-editor)
- [Gameplay Basics examples](#gameplay-basics-examples)
- [Errors and limits](#errors-and-limits)

**Scripting basics**

- [Frame order](#frame-order)
- [Lifecycle functions](#lifecycle-functions)
- [Input](#input)
- [Camera](#camera)
- [Objects: movement, grounding, attachment](#objects-movement-grounding-attachment)
- [Physics queries and gravity](#physics-queries-and-gravity)
- [Script communication](#script-communication)
- [Examples](#examples)
- [Common errors](#common-errors)
- [Limits and behaviour notes](#limits-and-behaviour-notes)
- [Existing API summary](#existing-api-summary)

# Animation

Imported models with animation clips (GLB, glTF, FBX) can be played from Lua. Every scene object has these methods;
they act on imported model instances with clips and answer "no animations" for anything else. A clip is named by its
name (a string) or its number (1 = the first, in the order `GetAnimations` lists them). Everything about formats,
skeletons, blending rules, the editor's Animation panel, limits and performance is in [animation.md](animation.md);
complete examples (idle / walk / run from real movement, playback speed, jump, a one-shot interaction, several
independent characters) are in [animation.md#examples](animation.md#examples) and in
[`examples/animation-demo/Scripts`](../examples/animation-demo/Scripts).

| Method | Returns | What it does |
|---|---|---|
| `object:HasAnimations()` | `boolean` | Whether it is an imported model with clips. |
| `object:GetAnimations()` | `{ {Name, Duration, Index}, ... }` | Its clips in file order (Duration in seconds, Index from 1). |
| `object:PlayAnimation(clip[, options])` | `true` or `false, reason` | Plays a clip. Options: `Loop` (default true), `Speed` (default 1), `FadeTime` (seconds, default 0), `StartTime` (seconds), `Restart` (restart even when already playing). |
| `object:CrossFadeAnimation(clip, fadeTime[, options])` | `true` or `false, reason` | Blends from what shows now to the clip over `fadeTime` seconds. Options: `Loop`, `Speed`, `StartTime`, `Restart`. |
| `object:StopAnimation([fadeTime])` | — | Back to the rest pose (fading when `fadeTime` is given). |
| `object:PauseAnimation()`, `object:ResumeAnimation()` | — | Freeze / continue. |
| `object:RestartAnimation()` | — | The current clip from its start. |
| `object:SetAnimationSpeed(speed)` | — | Speed of the current clip (1 as authored, 0 holds, negative backwards). |
| `object:SetAnimationLooping(loop)` | — | Whether the current clip loops. |
| `object:GetAnimationTime()`, `object:SetAnimationTime(seconds)` | `number` / — | The current clip's time (set: wrapped when looping, clamped otherwise). |
| `object:GetAnimationLength([clip])` | `number` or `nil` | A clip's length (the current clip's without an argument). |
| `object:GetCurrentAnimation()` | `string` or `nil` | The clip playing or fading in. |
| `object:IsAnimationPlaying([clip])` | `boolean` | Whether a clip (or that clip) is playing: not paused, not finished. |
| `object:OnAnimationFinished(function(name, object) ... end)` | `Connection` | Next frame after the current clip, playing once, reaches its end. |

```lua
local robot = Scene.Find("Robot")

function Start()
  robot:PlayAnimation("Idle")
  robot:OnAnimationFinished(function(name) print(name .. " finished") end)
end

function Update(dt)
  if Input.IsKeyPressed("E") then
    robot:CrossFadeAnimation("Wave", 0.15, { Loop = false })
  end
end
```

Rules worth knowing:

- An unknown clip is an ordinary answer, not an error: `local ok, reason = obj:PlayAnimation("Wlak")` gives `false`
  and `no animation called "Wlak" (this model has: Idle, Walking, ...)`.
- Playing (or crossfading to) the clip that is already playing keeps it where it is; only `Loop` and `Speed` change.
  Calling it every frame from `Update` is therefore fine. `Restart = true` starts it over.
- Errors (raised): a clip that is not a string or whole number, an unknown or mistyped option, a negative fade time,
  a non-finite speed or time, `SetAnimationSpeed` / `SetAnimationLooping` / `SetAnimationTime` while nothing plays,
  `.` instead of `:`, an object that no longer exists.
- Animations advance once per frame after physics and before `LateUpdate`, by real time (frame-rate independent).
- The saved Play on start clip (Animation panel) starts before any script runs; scripts can change it from their first
  line on. Nothing a script does to an animation is saved; Stop returns every model to its rest pose.
- Animation never moves the object or its physics body (no root motion): move characters from Lua.

# Physics, movement and world tools

## Overview

| Area | New API |
|---|---|
| Motion | `object.GravityScale`, `object.LinearDamping`, `object.AngularDamping`; `LinearVelocity` / `AngularVelocity` on an **anchored** Part (it moves or spins on its own); `object.SurfaceVelocity` (conveyors) |
| Forces | `object:ApplyForceAtPoint(force, point)`, `object:GetVelocityAtPoint(point)`, `object:GetMass()`; `"Local"` / `"World"` as the last argument of `ApplyImpulse`, `ApplyForce`, `ApplyTorque`, `ApplyAngularImpulse` |
| Object space | `object.Forward`, `object.Right`, `object.Up`; `object:PointToWorld`, `PointToLocal`, `VectorToWorld`, `VectorToLocal`; `object:Rotate`, `RotateAround`, `LookAt` |
| Collisions | `object.CollisionGroup`; `Physics.SetGroupsCollide`, `Physics.GroupsCollide`, `Physics.GetCollisionGroups`; `object:IgnoreCollisionWith(other, ignore)`; query filter option `CollisionGroup` |
| Joints | `Physics.CreateWeld`, `Physics.CreateHinge`, `Physics.CreateBallSocket` and the `Joint` object |
| Signals | `object:OnAttributeChanged(name, function(value, object) ... end)` |
| Helpers | `math.clamp`, `math.lerp`, `math.sign`, `math.round`; `Vector3.clampMagnitude`, `Vector3.moveTowards`, `Vector3.project`, `Vector3.angle` |
| GUI | `UI.Create(type, options)`; the `"Image"` element and its `Image` property |
| Scene.Create | options `GravityScale` and `CollisionGroup` |

## Conventions

- A Part's local axes: **Forward is local −Z**, Right is +X, Up is +Y (the same convention as the camera).
- Setting `LinearVelocity` / `AngularVelocity` on an anchored Part moves it (see below); on an unanchored Part it
  sets its physical velocity.
- Every setting here (gravity scale, damping, collision groups and rules, joints, surface velocities) lasts until Stop.
  Stop restores the scene exactly as it was before Play; nothing is written to the project.
- World panel groups add one optional `"Group"` field per Part to `Scene.json`.

## Motion: gravity scale, damping, moving and spinning Parts, conveyors

### `object.GravityScale` → `number`

How strongly gravity pulls this object: `1` normal, `0` floats, `0.5` half, a negative value falls up. Any finite
number up to ±1,000,000. Unanchored Parts only (anchored Parts are not moved by gravity).

```lua
self.GravityScale = 0   -- flying or floating: no gravity, everything else still applies
```

Errors: `GravityScale must be a finite number`; `'Crate' is not simulated by physics` (anchored or not in Play).

### `object.LinearDamping`, `object.AngularDamping` → `number`

Drag, per second: each second the velocity (or spin) keeps `e^-damping` of itself (`2` keeps about 14 %). `0` (the
default) is no drag. Use it for air resistance, water, hovering vehicles.

Errors: `LinearDamping must be 0 or more (0: no drag)`, `LinearDamping must be a finite number`.

### Anchored Parts that move on their own

Set `LinearVelocity` (studs per second) or `AngularVelocity` (**degrees** per second, around each world axis) on an
anchored Part and it moves or turns at that speed every physics step: a moving platform, a lift, a spinning
platform, a fan, a door slide. It is still anchored: gravity and collisions never push it, but it pushes what it
touches, and **what stands on it is carried along** by friction (players ride a turning platform round).

```lua
-- Spinner.lua (shortened)
function Start()
  self.AngularVelocity = Vector3.new(0, 40, 0)   -- 40 degrees per second around the world's Y axis
end
```

```lua
-- A lift that goes up and down between two heights
local BOTTOM, TOP, SPEED = 1, 12, 4
function Update(dt)
  local y = self.Transform.Position.y
  if y >= TOP then self.LinearVelocity = Vector3.new(0, -SPEED, 0)
  elseif y <= BOTTOM then self.LinearVelocity = Vector3.new(0, SPEED, 0) end
end
```

Setting one keeps the other (a Part can move and spin at once). `Vector3.new(0, 0, 0)` stops it. Reading them returns
what was set. The Part's `Transform` follows, and Stop puts it back.

### `object.SurfaceVelocity` → `Vector3`

**Anchored Parts only.** The Part stays where it is, but its surface moves at this velocity (studs per second, world
axes): whatever rests on it is carried by friction. That is a conveyor belt; belts in a row hand things on, and a
belt turned 90° makes a corner.

```lua
-- Conveyor.lua: along the belt's own Forward, switchable by an event
local SPEED = 6
function Start()
  self.SurfaceVelocity = self.Forward * SPEED
  Events.On("ConveyorMode", function(mode)   -- 1 forward, 0 stopped, -1 reverse
    self.SurfaceVelocity = self.Forward * (SPEED * mode)
  end)
end
```

Nothing is teleported: crates and players are moved by contact friction, so a tall body rides as well as a flat crate
and stops when the belt stops.

Errors: `SurfaceVelocity works on an anchored Part only ('Crate' is unanchored)`, `SurfaceVelocity must be finite`.

## Forces at a point, mass, local space

### `object:ApplyForceAtPoint(force, point)`

A force (in world axes) at a world point, applied during this frame. Off the centre of mass it also turns the object.
It takes two vectors or six numbers. Wheels, thrusters, buoyancy points and the raycast car below are built from it.

### `object:GetVelocityAtPoint(point)` → `Vector3`

How fast a world point fixed to the object moves now (its velocity plus the effect of its spin).

### `object:GetMass()` → `number`

The mass physics uses now (the `Mass` set in the editor, or density × volume when it is Auto).

### Local or world pushes

`ApplyImpulse`, `ApplyForce`, `ApplyTorque` and `ApplyAngularImpulse` take `"Local"` or `"World"` (the default) as
their last argument. `"Local"` reads the vector in the object's own axes:

```lua
self:ApplyForce(0, 0, -thrust, "Local")   -- push along its own Forward (local -Z), whichever way it faces
```

Errors: `ApplyForceAtPoint expects a force and a world point: two vectors or six finite numbers`,
`ApplyForce: the space must be "World" or "Local", got Sideways`.

## Object space and rotation

| Member | Returns / does |
|---|---|
| `object.Forward`, `.Right`, `.Up` | Its axes in the world, length 1: Forward = local −Z, Right = local +X, Up = local +Y. Read-only. |
| `object:PointToWorld(localPoint)` | A point given in its own space, in world space (e.g. a wheel mount). |
| `object:PointToLocal(worldPoint)` | A world point, in its own space. |
| `object:VectorToWorld(localVector)` | A direction in its axes, in world axes (no position). |
| `object:VectorToLocal(worldVector)` | A world direction, in its axes. |
| `object:Rotate(x, y, z, space?)` | Turns it by these degrees around its own axes (`"Local"`, default) or the world's (`"World"`). |
| `object:RotateAround(pivot, axis, degrees)` | Swings it around a world point and axis; its position moves too. |
| `object:LookAt(point, up?)` | Turns it so its Forward points at a world point (up defaults to world Y). |

```lua
local muzzle = gun:PointToWorld(Vector3.new(0, 0, -1.5))   -- 1.5 studs in front of the gun, wherever it points
turret:LookAt(target.Transform.Position)
door:RotateAround(hingePoint, Vector3.new(0, 1, 0), 90)
```

`Forward = ...` is an error: `Forward is read-only (turn the object with Rotate, LookAt or Transform.Rotation)`.

## Collision control

| What | How | Effect |
|---|---|---|
| Solid | default | Collides with everything. |
| Pass-through | `object.CanCollide = false` (or the editor's Physics panel) | Collides with nothing, and physics queries (rays, overlaps) skip it too. |
| Trigger | `object.IsTrigger = true` | No push; reports `OnTriggerEnter` / `Stay` / `Exit`. Queries hit triggers unless the filter says `IgnoreTriggers`. |
| Groups | `object.CollisionGroup = "Ghosts"` + `Physics.SetGroupsCollide(a, b, false)` | Objects of two groups pass through each other; everything else stays solid. |
| One pair | `a:IgnoreCollisionWith(b)` / `a:IgnoreCollisionWith(b, false)` | Just these two pass through each other (or collide again). |

- **Collision groups** are named. Every object starts in `"Default"`. A group is made the first time it is used,
  up to 32 groups including Default. Every group touches every group until told otherwise.
- `Physics.SetGroupsCollide(groupA, groupB, collide)` applies both ways and can be changed at any time.
  `Physics.GroupsCollide(a, b)` reads it, and `Physics.GetCollisionGroups()` lists the session's groups (Default
  first).
- Changes take effect from the next physics step: a wall made solid in the middle of Play stops the next object.
- **Queries follow the same rules.** A filter with `CollisionGroup = "Player"` only hits what the Player group
  collides with. A ray "as the player" passes a player-only door that stops a ray of the Default group.

```lua
-- Rules.lua (the demo): the player walks through the PlayerDoor; crates and dummies cannot.
Physics.SetGroupsCollide("Player", "PlayerDoor", false)
Scene.Find("PlayerDoor").CollisionGroup = "PlayerDoor"
Scene.Find("Player").CollisionGroup = "Player"

local hit = Physics.Raycast(eye, Camera.Forward, 50, { CollisionGroup = "Player", IgnoreTriggers = true })
```

`Scene.Create` takes `CollisionGroup = "..."` too. Errors:

- `CollisionGroup must be a group name (a string of 1 to 64 characters)`
- `there can be at most 32 collision groups (Default included)`
- `Physics.SetGroupsCollide expects collide to be true or false, got a string value`
- `IgnoreCollisionWith expects another scene object`
- `collision groups only exist while physics runs (Play)`

## Joints

Physical constraints solved by the physics engine. Unlike `SetParent`, which makes an object follow another
kinematically and never transmits forces, a joint is real physics: welded objects fall and tumble as one, a hinged
door swings when pushed, a chain of ball sockets hangs and sways.

| Function | Makes |
|---|---|
| `Physics.CreateWeld(a, b?)` | Keeps a and b (or a and its place in the world) in their current relative pose. |
| `Physics.CreateHinge(a, b?, point, axis)` | Turns around a world point and axis: doors, wheels, windmills, flaps. |
| `Physics.CreateBallSocket(a, b?, point)` | Shares a world point, free to turn every way: chains, pendulums, trailers. |

`b = nil` attaches `a` to a fixed point in the world. All three return a **Joint**:

| Property | Type | Notes |
|---|---|---|
| `Type` | string | `"Weld"`, `"Hinge"` or `"BallSocket"` (read-only). |
| `Active` | boolean | Held by the solver now: enabled, and both objects still exist (read-only). |
| `Destroyed` | boolean | `joint:Destroy()` was called, or one of its objects was destroyed (read-only). |
| `Enabled` | boolean | `false` lets the objects move freely (the joint keeps its frames); `true` holds them again. |
| `CollideConnected` | boolean | Whether the two objects still collide with each other (default `false`). |
| `PartA`, `PartB` | ScriptObject | The objects (`PartB` is `nil` for the world). |
| `Angle` | number | Hinge: degrees turned since it was made (read-only). |
| `MotorEnabled`, `MotorSpeed`, `MotorMaxTorque` | boolean, deg/s, number ≥ 0 | Hinge motor: drives the hinge toward that speed with at most that torque. |
| `LimitsEnabled`, `LowerAngle`, `UpperAngle` | boolean, degrees (−180 to 180) | Hinge limits: keep `Angle` between them. |

```lua
-- A windmill: a hinge to the world, driven by its motor
local hinge = Physics.CreateHinge(blades, nil, blades.Transform.Position, Vector3.new(0, 0, 1))
hinge.MotorEnabled, hinge.MotorSpeed, hinge.MotorMaxTorque = true, 90, 5000

-- A door that opens 0..100 degrees and swings when pushed
local door = Physics.CreateHinge(panel, nil, panel:PointToWorld(Vector3.new(-1.5, 0, 0)), Vector3.new(0, 1, 0))
door.LimitsEnabled, door.LowerAngle, door.UpperAngle = true, 0, 100
```

At least one of the objects must be unanchored. A joint is removed with `joint:Destroy()`, when either object is
destroyed, and at Stop. Errors:

- `a joint needs two different objects`
- `a joint needs at least one unanchored object (anchored ones do not move by physics)`
- `a joint needs a finite point (and, for a hinge, an axis that is not zero)`
- `MotorSpeed is for hinges only (this joint is a Weld)`
- `MotorMaxTorque must be 0 or more`
- `that joint no longer exists`
- `joints only exist while physics runs (Play)`

The example car does **not** use joints: it is a raycast vehicle (rays for suspension, forces at the wheel points), which is
steadier at every frame rate. Hinges are there for doors, wheels on carts, windmills and similar machinery.

## Attribute change signals

### `object:OnAttributeChanged(name, function(value, object) ... end)` → `Connection`

Runs the function each time that attribute is set (or removed: `value` is then `nil`), by any script. It runs at the
start of the next frame, like events. `connection:Disconnect()` stops it; it also ends when the object is destroyed
and at Stop.

```lua
-- HUD.lua: the health bar follows the player's "Health" attribute, whoever changes it
player:OnAttributeChanged("Health", function(value)
  healthFill:SetSize(math.clamp(value / 100, 0, 1), 0, 1, 0)
end)
```

Errors: `OnAttributeChanged expects an attribute name (a string), got a number value`,
`OnAttributeChanged expects a function, got a nil value`.

## Math and Vector3 helpers

| Function | Result |
|---|---|
| `math.clamp(x, min, max)` | `x` kept within `min..max`. |
| `math.lerp(a, b, t)` | `a + (b - a) * t`. |
| `math.sign(x)` | `-1`, `0` or `1`. |
| `math.round(x)` | The nearest whole number (an integer), halves away from zero: `math.round(2.5) == 3`, `math.round(-2.5) == -3`. |
| `Vector3.clampMagnitude(v, maxLength)` | The same direction, at most `maxLength` long: a speed limit. |
| `Vector3.moveTowards(a, b, maxDelta)` | From `a` toward `b` by at most `maxDelta`, never past `b`: steady, frame-rate independent acceleration. |
| `Vector3.project(v, onto)` | The part of `v` along `onto`. |
| `Vector3.angle(a, b)` | The angle between two vectors in degrees (0 to 180). |

## GUI from Lua: UI.Create options, Image, lifecycle

### `UI.Create(type, options)` → GUI element

`type` is `"Frame"`, `"Text"`, `"Button"`, `"Image"` or `"TextBox"`. The older form `UI.Create(type, name, parent)`
still works. A TextBox (a one-line field the player types into) and its options (`PlaceholderText`, `MaxLength`,
`ClearOnSubmit`), events (`OnSubmit`, `OnFocusLost`, `OnFocused`), `Focus` / `Release`, `Input.IsTyping` and
`UI.MeasureText`, and the text chat (`Chat`) are described in [chat.md](chat.md).
The options table can hold:

| Option | Value |
|---|---|
| `Name` | string |
| `Parent` | an element, or `UI.Root` (the default) |
| `Position`, `Size` | `{ xScale, xOffset, yScale, yOffset }` (scale of the parent plus pixels) |
| `Anchor` | `{ x, y }`, 0 to 1 (which point of the element sits at Position) |
| `Text`, `FontSize`, `TextColor`, `HorizontalAlignment`, `VerticalAlignment` | Text and Button |
| `Color`, `Opacity` | Frame (`Color` also for Button); colours are `"#RRGGBB"` |
| `Visible`, `ZOrder`, `Enabled` | as the properties |
| `Image` | Image: a project path such as `"Assets/Images/Coin.png"` (PNG or JPEG under `Assets/`), or `""` |

Every option is applied as if the script had set the property. If one is wrong, the half-made element is removed and
the call fails with the property's own error (`UI.Create: Color must be "#RRGGBB" (six hex digits)`). An unknown
option is an error too (`UI.Create: Colour is not an option (Name, Parent, ...)`).

```lua
local bar = UI.Create("Frame", { Name = "HealthBar", Position = { 0, 16, 0, 16 }, Size = { 0, 200, 0, 18 }, Color = "#2E323C" })
local fill = UI.Create("Frame", { Name = "HealthFill", Parent = bar, Size = { 1, 0, 1, 0 }, Color = "#E0483E" })
local icon = UI.Create("Image", { Name = "CoinIcon", Image = "Assets/Images/Coin.png", Size = { 0, 32, 0, 32 } })
```

An Image draws the picture stretched over its rectangle. It has no tint or opacity. Images can be created from Lua or
written in `UI.json`; the editor's UI panel does not offer an Image button yet.

### Lifecycle

- Everything a script creates or changes in the GUI during Play exists only in that session. At Stop the GUI goes
  back to the project's `UI.json`, and the next Play starts from it again. Nothing is duplicated across sessions: the
  tests check this.
- `element:Destroy()` removes an element and everything inside it, and disconnects its handlers.
- `OnClick` and the other handlers end when their element is destroyed, when the script's object is destroyed, and
  at Stop.

## Where data lives (object, script, session, saved)

| Scope | Where to keep it | Lasts | Example |
|---|---|---|---|
| Per object | Attributes: `obj:SetAttribute("Coins", 3)` | Until Stop (or the object is destroyed) | Health, Hunger, Coins, a tree's hits left |
| Per script | Locals in the script and the table it returns (`GetScript`) | While the script runs (until Stop or its object is destroyed) | An inventory table, a cooldown |
| Per session | `Shared.player = self` | Until Stop | The player, the vehicle being driven |
| Saved | `GameSave.Save("Slot1", data)` | Across Play sessions and restarts, in the project's `Saves` folder | Position, inventory, quest log, game clock |

Custom stats and currencies are attributes (numbers on an object), so other scripts read them, `OnAttributeChanged`
reacts to them, and the HUD shows them. `GameSave` keeps plain data (tables,
numbers, strings, booleans, vectors) in named slots; see [Saved games](lua-gameplay-systems.md#15-saved-games). Attributes and
scene changes made during Play are still not written to the scene.

## "X-ray" damage: how it is meant

"X-ray damage" is read here as **filtered targeting through walls**: an attack that reaches its targets whatever is in
between, but only the targets its filter chooses. It is not a see-through rendering effect. It is two general
calls, not a special feature:

- **Through walls:** an overlap query (`Physics.OverlapSphere` / `OverlapBox`) has no line of sight. It returns
  everything inside the shape.
- **Filtered:** the query's filter (`OnlyTags = "Enemy"`, `CollisionGroup`, `Ignore`) decides who can be hit at all.
- **A normal attack** adds a line-of-sight check: a `Physics.Raycast` to the target must reach the target itself.

```lua
-- Pulse.lua (shortened): V = blast that needs sight, X = X-ray blast
local function blast(throughWalls)
  for _, target in ipairs(Physics.OverlapSphere(self.Transform.Position, 20, { OnlyTags = "Enemy", IgnoreEnvironment = true, Ignore = self })) do
    local status = target:GetScript("Status")
    local seen = function()
      local hit = Physics.Raycast(self.Transform.Position, target.Transform.Position - self.Transform.Position, 21, { Ignore = self, IgnoreTriggers = true })
      return hit ~= nil and hit.Object == target
    end
    if status and (throughWalls or seen()) then
      status.Damage(25, throughWalls and "X-ray" or "Blast")
    end
  end
end
```

## World panel groups (editor)

Groups organise the World panel's list; they do not change the scene.

- **Select several rows.** Ctrl+click adds or removes a row; Shift+click selects the range from the last clicked
  row. Clicking a group row selects all its Parts, so Move, Rotate and Resize act on the group as a unit.
- **Make a group.** Right-click a selected row and choose **Group**, or press Ctrl+G. The new group ("Group",
  "Group2", ...) appears where the first of its Parts was.
- **Open and close it.** A group row has an arrow: pointing right when it is closed, down when it is open. Its
  contents are indented under it.
- **Right-click a group** for Rename (type the name, Enter keeps it, Escape cancels), Group (put it inside a new
  group) and Ungroup (Ctrl+Shift+G). Ungroup moves its Parts and the groups inside it up one level.
- **Right-click a Part in a group** for Remove from Group.
- **Drag** selected rows onto a group to move them into it. Drag a group onto another group to nest it, or onto the
  empty space below the list to move it to the top level. A group cannot be dropped into itself.
- **Undo / Redo** (Ctrl+Z / Ctrl+Y) cover every grouping change.
- **Saved** in `Scene.json` as one optional field per Part, for example `"Group": "Playground/Hazards"`. A group
  exists while it contains Parts (directly or inside groups within it). Which groups are open or closed is not saved.

Groups are **organisation only**. Grouping never moves, parents or welds anything. In Play, scripts do not see groups:
for gameplay structure use tags, attributes, `SetParent` or joints. Play and Stop leave groups exactly as they
were.

## Gameplay Basics examples

The full scripts are in [`examples/gameplay-basics/Scripts`](../examples/gameplay-basics/Scripts) (27 files). They are the Gameplay Basics
Demo's scripts, and the automated tests play them with scripted input: the scenes and checks are in
`Tests/Wisko.Tests/Checks/GameplaySystemsV4Checks.cs`.

**Making the demo project.** Run this once from the repository folder:

```
dotnet run --project Tests/Wisko.Tests -- --make-demo "%USERPROFILE%\Documents\Wisko\Projects"
```

It writes a project called **Gameplay Basics Demo** with its scripts in `Assets/Scripts`. If a project of that name already
exists, it refuses and changes nothing. Open it from the Hub and press Play:

- **Moving:** WASD walks (camera-relative), Space jumps, LeftShift sprints, the mouse looks, and Tab frees the mouse.
- **E** uses what is in front of you. You can chop the tree, mine the rock, pick up an apple, drink from the well,
  feed the goat, drive the car (E again gets out) or switch the conveyors at the lever.
- **Q** eats an apple; **I** shows or hides the inventory.
- **Flying:** G or the Fly button flies; touching the cyan orb starts flight too.
- **Abilities:** F shoves, V blasts what you can see, X blasts enemies through walls.

The HUD shows health, hunger and thirst, coins, the current objective, the inventory and what E will do.

### 1. Flight

```lua
-- Flight.lua: gravity off, velocity steered toward camera-relative input, accelerated smoothly and capped.
local SPEED, ACCEL = 20, 40
local flying = false
local button

local function setFlying(on)
  flying = on
  self.GravityScale = on and 0 or 1   -- off: normal physics again (it falls)
  button.Text = on and "Land" or "Fly"
  Events.Emit("FlightChanged", { object = self, flying = on })
end

function Start()
  self.RotationLocked = true
  button = UI.Create("Button", { Name = "FlyButton", Anchor = { 1, 0 }, Position = { 1, -16, 0, 16 }, Size = { 0, 120, 0, 40 }, Text = "Fly" })
  button:OnClick(function() setFlying(not flying) end)
end

function Update(dt)
  if Input.IsKeyPressed("G") then setFlying(not flying) end
  if not flying then return end
  local move = Camera.Forward * Input.GetAxis("S", "W") + Camera.Right * Input.GetAxis("A", "D")
    + Vector3.new(0, 1, 0) * Input.GetAxis("LeftControl", "Space")
  local target = Vector3.clampMagnitude(move * SPEED, SPEED)
  self.LinearVelocity = Vector3.moveTowards(self.LinearVelocity, target, ACCEL * dt)
end

return { SetFlying = setFlying, IsFlying = function() return flying end }
```

```lua
-- FlightOrb.lua: a trigger; touching it starts flight for whoever has a Flight script.
function OnTriggerEnter(other)
  local flight = other and other:GetScript("Flight")
  if flight and not flight.IsFlying() then flight.SetFlying(true) end
end
```

Tested: hovering in place, W capped at 20 studs/s, Space climbing, the button and G toggling, landing under normal
physics, Stop restoring.

### 2. Basic car

`Car.lua` is a raycast vehicle. Four suspension rays push the body up like springs. Each tyre grips sideways, the
rear wheels drive and every wheel brakes. It only reacts to input while `Shared.vehicle` is this car. The core of its
`Update`:

```lua
for _, wheel in ipairs(wheels) do
  local origin = self:PointToWorld(wheel.mount)
  local hit = Physics.Raycast(origin, -self.Up, REST + RADIUS, { Ignore = self, IgnoreTriggers = true })
  if hit then
    local compression = REST + RADIUS - hit.Distance
    local spring = stiffness * compression - damping * Vector3.dot(self:GetVelocityAtPoint(origin), self.Up)
    self:ApplyForceAtPoint(self.Up * math.max(0, spring), origin)                      -- suspension
    local velocity = self:GetVelocityAtPoint(origin)
    local force = wheelRight * (-Vector3.dot(velocity, wheelRight) * load * GRIP)       -- sideways grip
    if braking then
      force = force + wheelForward * -math.clamp(Vector3.dot(velocity, wheelForward) * load * 8, -brake, brake)
    elseif not wheel.front and throttle ~= 0 then
      force = force + wheelForward * (throttle * engine)                                -- rear-wheel drive
    end
    self:ApplyForceAtPoint(force, origin)
  end
end
```

`CarSeat.lua` gets in and out with E. While you drive, the player rides along hidden, attached with `SetParent`.
`Player.lua` switches the camera to a chase view behind the car. Tested: the same top speed at 30, 60 and 144 fps;
riding level, braking, steering, reversing, and stopping at a wall.

### 3. Spinning platform

```lua
-- Spinner.lua: degrees per second; what stands on it is carried round.
function Start()
  self.AngularVelocity = Vector3.new(0, 40, 0)
end
return { SetSpeed = function(speed) self.AngularVelocity = speed end }
```

Tested: an anchored Part turns 90° in a second at `(0, 90, 0)`, and a box resting on a turning platform goes round
with it.

### 4. Conveyor

`Conveyor.lua` is shown [above](#objectsurfacevelocity--vector3). `Lever.lua` switches every belt at once:

```lua
local MODES = { { mode = 1, name = "forward" }, { mode = 0, name = "stopped" }, { mode = -1, name = "reverse" } }
local index = 1
return {
  Prompt = function() return "switch the conveyors (now " .. MODES[index].name .. ")" end,
  Interact = function()
    index = index % #MODES + 1
    Events.Emit("ConveyorMode", MODES[index].mode)
  end,
}
```

Tested with three belts: a crate is handed from belt to belt and turns the corner. It stops when the belts stop and
goes back when they reverse; a tall, player-sized body rides too.

### 5. Mining and resource drops

```lua
-- ResourceNode.lua (shortened): E hits it (through Interact.lua); at 0 it drops items, vanishes and regrows.
local kind = { item = "Wood", hits = 3, drops = 3, regrow = 8 }
local depleted = false

local function hit()
  if depleted then return false end
  local health = self:GetAttribute("Health") - 1
  self:SetAttribute("Health", health)
  if health <= 0 then
    depleted = true
    for i = 1, kind.drops do
      local angle = (i / kind.drops) * math.pi * 2
      local out = Vector3.new(math.cos(angle), 0, math.sin(angle))
      Scene.Create("Block", { Name = kind.item .. "Drop", Anchored = false, Size = Vector3.new(0.8, 0.8, 0.8),
        Position = self.Transform.Position + out * 2 + Vector3.new(0, 1, 0), LinearVelocity = out * 4 + Vector3.new(0, 8, 0),
        Tags = { "Drop" }, Attributes = { Item = kind.item, Count = 1 } })
    end
    self.Visible, self.CanCollide = false, false
    self:RemoveTag("Interactable")
    Timer.After(kind.regrow, function()
      depleted = false
      self:SetAttribute("Health", kind.hits)
      self.Visible, self.CanCollide = true, true
      self:AddTag("Interactable")
    end)
  end
  return true
end
```

```lua
-- Collector.lua (on the player): drops fly to you and go into the inventory.
function Update(dt)
  local here = self.Transform.Position
  for _, drop in ipairs(Physics.OverlapSphere(here, 8, { OnlyTags = "Drop", IgnoreEnvironment = true })) do
    local to = here - drop.Transform.Position
    if Vector3.magnitude(to) <= 2.5 then
      local item, count = drop:GetAttribute("Item"), drop:GetAttribute("Count") or 1
      drop:Destroy()
      self:GetScript("Inventory").Add(item, count)
    else
      drop.LinearVelocity = Vector3.normalize(to) * 18
    end
  end
end
```

`Interact.lua` is the general "press E" script. Any object can be interactable: it adds the `Interactable` tag, names
its script in the `Interact` attribute and returns `Prompt(player)` and `Interact(player)`. Trees, food, the well,
the goat, the lever and the car all use it, and `Interact.lua` knows none of them. Tested: 3 presses take the
tree's 3 hits and 3 Wood drops land in the inventory. A depleted tree cannot be hit, and it is back and harvestable
8 s later.

### 6. Custom stats and GUI

```lua
-- Survival.lua (shortened): stats are attributes, so anything can read them and the HUD can follow them.
local function stat(name) return self:GetAttribute(name) or 0 end
local function set(name, value) self:SetAttribute(name, math.clamp(value, 0, 100)) end
function Start()
  for _, name in ipairs({ "Health", "Hunger", "Thirst" }) do
    if not self:HasAttribute(name) then set(name, 100) end
  end
  Timer.Every(1, function()
    set("Hunger", stat("Hunger") - 0.5)
    set("Thirst", stat("Thirst") - 0.8)
    if stat("Hunger") == 0 or stat("Thirst") == 0 then set("Health", stat("Health") - 2) end
  end)
end
```

```lua
-- Coin.lua: a currency is a number on the object.
function OnTriggerEnter(other)
  if other ~= nil and other == Shared.player then
    other:SetAttribute("Coins", (other:GetAttribute("Coins") or 0) + 1)
    self:Destroy()
  end
end
```

`HUD.lua` builds the whole HUD with `UI.Create`: bars for Health, Hunger and Thirst, a coin counter, the objective,
an inventory panel (its rows are destroyed and recreated on each change) and the interaction prompt. It only listens:
`OnAttributeChanged` for stats and coins, and the events `InventoryChanged`, `PromptChanged`, `StatusChanged`,
`ObjectiveChanged` and `Hint`. `Objectives.lua` tracks a list of goals. Tested: bars follow the stats, walking
through 3 coins shows `Coins: 3` and completes objective 1, the prompt appears near the well, I toggles the
inventory, and a second Play session builds exactly the same HUD.

### 7. Status effects

```lua
-- Status.lua (shortened): effects are timers owned by the target's own script.
local EFFECTS = {
  Burn = { every = 0.5, tick = function() damage(5, "Burn") end },
  Regen = { every = 0.5, tick = function() heal(5) end },
  Slow = { begin = function() self:SetAttribute("SpeedMultiplier", 0.5) end,
           finish = function() self:SetAttribute("SpeedMultiplier", 1) end },
}
local function apply(name, seconds)            -- applying again restarts the clock (no stacking)
  local def, effect = EFFECTS[name], active[name]
  if effect then effect.timer:Cancel() effect.timer = Timer.After(seconds, function() clear(name) end) return end
  effect = { timer = Timer.After(seconds, function() clear(name) end) }
  if def.every then effect.ticker = Timer.Every(def.every, def.tick) end
  if def.begin then def.begin() end
  active[name] = effect
end
local function knockback(from, speed)          -- away from a point, a little up, the same for any mass
  local away = self.Transform.Position - from
  local push = Vector3.normalize(Vector3.new(away.x, 0, away.z)) * speed + Vector3.new(0, speed * 0.4, 0)
  self:ApplyImpulse(push * self.Mass)
  self:SetAttribute("KnockbackUntil", Time.elapsedTime + 0.4)   -- Player.lua lets go of the controls meanwhile
end
```

`Hazard.lua` (lava, mud, heal pad) is a trigger that calls `Apply` on whoever stands in it, and `Bumper.lua` knocks
back whatever runs into it. The blasts are in [X-ray damage](#x-ray-damage-how-it-is-meant). Tested: lava burns
every 0.5 s and stops 2 s after you leave, mud halves walking speed and wears off, the pad heals, and the bumper
throws you back. The X-ray blast reaches the enemy behind the wall but never the non-enemy; a dummy at 0 Health
emits `Died` and is healed 3 s later.

### 8. Collision filtering

See [Collision control](#collision-control) for `Rules.lua`. The demo's collision playground has four walls side by
side: SolidWall, GhostWall (`CanCollide` off in the Physics panel), PlayerDoor (group rule) and TriggerZone
(`Zone.lua` lights up and reports enter and exit). Tested: the solid wall stops everything and the ghost wall stops
nothing. The player-group object passes the door while the other is stopped, both pass the trigger zone (reported),
and the ghost wall made solid at runtime stops the next object. Rays "as the player" pass the door.

### 9. Eating and feeding

```lua
-- FoodDefs.lua: a module, so a new food is one line.
return { Apple = { Hunger = 25, Thirst = 5 }, Berry = { Hunger = 10 }, Water = { Thirst = 35 } }
```

```lua
-- Animal.lua (shortened): the goat has its own stats (Survival.lua on it too); feeding gives it your apple.
return {
  Interact = function(player)
    if player:GetScript("Inventory").Remove("Apple", 1) then
      self:GetScript("Survival").Eat("Apple")       -- the goat's Hunger, not yours
      Events.Emit("Fed", { animal = self, by = player })
    else
      Events.Emit("Hint", "You have no Apple")
    end
  end,
}
```

`Food.lua` makes apples pickable (into the inventory) and the well drinkable (it never runs out); `Interact.lua`
eats an apple on Q. Tested: picking up two apples, eating one (+25 Hunger, +5 Thirst), drinking (+35 Thirst),
feeding the goat (its Hunger rises, yours does not), and being refused with a hint when there is no apple.

## Errors and limits

| Message | Cause and fix |
|---|---|
| `SurfaceVelocity works on an anchored Part only ('Crate' is unanchored)` | Conveyors are anchored. Move a loose object with `LinearVelocity` or forces. |
| `'Platform' is not simulated by physics` (GravityScale, damping) | Anchored Parts have no gravity or drag; use `LinearVelocity` / `AngularVelocity` to move them. |
| `Forward is read-only (turn the object with Rotate, LookAt or Transform.Rotation)` | Turn the object instead. |
| `a joint needs at least one unanchored object (...)` | Two anchored Parts cannot be joined; unanchor one. |
| `MotorSpeed is for hinges only (this joint is a Weld)` | Motors and limits exist on hinges only. |
| `there can be at most 32 collision groups (Default included)` | Reuse groups; use `IgnoreCollisionWith` for one-off pairs. |
| `UI.Create: Colour is not an option (...)` | A typo in an option name; options are case-sensitive. |

- **Moving anchored Parts** follow their velocity exactly: nothing pushes them back. A rider is carried by friction,
  so a very slippery rider (`Friction = 0`) slides.
- **Conveyors** carry by friction too. A sphere rolls rather than rides, and a crate sitting across two belts follows
  both.
- **Joints** are solved with the rest of physics (4 substeps per 1/60 s step). Very heavy objects on very light ones
  stretch them a little. Hinge angles are limited to −180..180.
- **The raycast car** in the examples is tuned for its own mass and size (it computes its spring stiffness from its
  mass). A much heavier or longer car needs its constants (`REST`, `GRIP`, the engine and brake accelerations)
  adjusted.
- **Collision groups and pair rules** apply to physics, queries and triggers alike: a trigger only reports objects
  whose group touches the trigger's group (and no pair told to ignore each other).
- **Attribute signals and events** arrive at the start of the next frame, so a handler sees the value as it was set.
- **Persistence:** stats, inventories, coins and objectives reset at Stop unless a script saves them with `GameSave`
  ([lua-gameplay-systems.md](lua-gameplay-systems.md#15-saved-games)); the scene itself is still never changed by Play.

# Scripting basics

## Frame order

Every frame of Play runs these steps, in this order:

1. **Input** is sampled once; every script sees the same snapshot.
2. **GUI button callbacks** (`OnClick`...) for clicks since the last frame run.
3. **Events** sent last frame (`Events.Emit`, `OnDestroyed`) are delivered.
4. **Collision and trigger callbacks** for what physics found in the last step run.
5. **Timers** that are due run.
6. **`Update(dt)`** runs on every script.
7. **Physics** steps (fixed 1/60 s steps; a frame may run zero, one or several).
8. **Attached objects follow their parents** (see [`SetParent`](#objectsetparentparent-offsetposition-offsetrotation-bonename)).
9. **`LateUpdate(dt)`** runs on every script.

In `Update`, positions are where physics left them *last* frame. In `LateUpdate`, everything has moved for this frame,
so `LateUpdate` is where a follow camera belongs: a camera placed in `Update` would trail one step behind a moving
character.

## Lifecycle functions

Define these as globals in a script; the engine calls them on the script's object.

| Function | When |
|---|---|
| `Start()` | Once, before the first `Update`. |
| `Update(dt)` | Every frame. `dt`: seconds since the last frame. |
| **`LateUpdate(dt)`** *(new)* | Every frame, after physics and after attached objects followed their parents. Same `dt` as `Update`. |
| `OnCollisionEnter(other, contact)` / `OnCollisionStay` / `OnCollisionExit(other)` | Touching starts / continues / ends. `other` is `nil` for the environment (the Baseplate). |
| `OnTriggerEnter(other, contact)` / `OnTriggerStay` / `OnTriggerExit(other)` | Trigger overlaps. |
| `OnDestroy()` | This script's object was destroyed during Play. |

A script's file may also end with `return <table>`: that table is the script's **exports**, which other scripts reach
with [`object:GetScript(name)`](#objectgetscriptname--tablenil-reason).

## Input

Key names are the engine's physical key names: `"A"`–`"Z"`, `"0"`–`"9"`, `"Space"`, `"LeftShift"`, `"Tab"`,
`"Enter"`, `"Escape"`, arrows `"Left"`/`"Right"`/`"Up"`/`"Down"`, `"F1"`–`"F12"`... (case-insensitive). They name key
**positions on a US layout**: on an AZERTY keyboard, `"W"` is the key printed **Z** and `"A"` is the key printed **Q**.
Mouse buttons: `"Left"`, `"Right"`, `"Middle"`.

### `Input.GetAxis(negativeKey, positiveKey)` → `number`

`-1` while only `negativeKey` is held, `1` while only `positiveKey` is held, otherwise `0`.

```lua
local strafe = Input.GetAxis("A", "D")   -- -1, 0 or 1
local forward = Input.GetAxis("S", "W")
```

An unknown key name is not an error: it counts as not held, and the Output gets one warning for it.

### `Input.SetMouseLocked(locked)` → `boolean`

`true` locks the mouse for mouse look:

- The window hides the cursor and holds it.
- `Input.mouseDeltaX` / `mouseDeltaY` keep reporting movement.
- The editor's toolbar and panels ignore the mouse.

`false` frees it. Returns whether the mouse is locked now; without a Play view (e.g. a headless run) that is `false`.

- **Stop always frees the mouse**, and **Escape always stops Play**, so a game can never trap the cursor.
- Errors: `Input.SetMouseLocked expects true or false, got a string value`.

### `Input.MouseLocked` → `boolean` (read-only)

Whether the mouse is locked.

```lua
function Start()
  Input.SetMouseLocked(true)
end

function Update(dt)
  if Input.IsKeyPressed("Tab") then
    Input.SetMouseLocked(not Input.MouseLocked)   -- let the player reach the editor
  end
end
```

The existing members are unchanged:

- functions `IsKeyDown`, `IsKeyPressed`, `IsKeyReleased`, `IsMouseButtonDown`, `IsMouseButtonPressed`,
  `IsMouseButtonReleased` and `GetMousePosition`;
- values `mouseX`, `mouseY`, `mouseDeltaX`, `mouseDeltaY` (down is positive) and `scrollDelta`.

## Camera

`Camera` is the Play view's camera. Placing it (`Position`, `Rotation`, `Forward`, `LookAt`) takes it from the
editor's fly controls until Stop, which puts the editor camera back where it was.

### `Camera.Forward` → `Vector3` (now writable)

Reading it gives the direction the camera looks (length 1). **Assigning** a direction turns the camera to look along
it (any length but zero), the same as `Camera.LookAt(Camera.Position + direction)`:

```lua
Camera.Forward = Vector3.new(0, 0, 1)   -- look along +Z
```

The camera never rolls, and its pitch stays within ±89°. Errors: `Camera.Forward must be a direction: a vector
(finite x, y, z) that is not zero`.

`Camera.Right` and `Camera.Up` stay read-only. For camera-relative movement, flatten `Forward` and `Right` onto the
ground:

```lua
local function flat(v) return Vector3.normalize(Vector3.new(v.x, 0, v.z)) end
local move = flat(Camera.Forward) * Input.GetAxis("S", "W") + flat(Camera.Right) * Input.GetAxis("A", "D")
```

Other members, unchanged:

- `Position` and `Rotation` (x: pitch, up positive; y: yaw, where 0 looks along +X and 90 along +Z);
- `FieldOfView` (10–120), `ViewportWidth`, `ViewportHeight`;
- `LookAt(point)`, `WorldToScreen(point)` and `ScreenPointToRay(x, y)`.

## Objects: movement, grounding, attachment

These are new members of every scene object (`self`, and what `Scene` finds).

### `object.RotationLocked` → `boolean`

While `true`, physics never turns the Part: collisions, friction and torques leave its rotation alone, so a box
character stays upright. Scripts can still set `Transform.Rotation`, e.g. to face where the character walks. It is
Play-only (not saved), and it works on any Part the physics simulates. Errors:

- `RotationLocked must be true or false`;
- `'Crate' is not simulated by physics`, for a Part physics does not simulate.

### `object:IsGrounded([maxDistance])` → `boolean, RaycastResult|nil`

Whether something is under the object, within `maxDistance` studs below its underside (default **0.15**).

- **How it tests:** it sweeps a sphere through the object's footprint, so standing on an edge counts.
- **What it skips:** triggers, the object itself and the objects attached to it (its attachment tree).
- **Returns** `true` and the surface as a [RaycastResult](#existing-api-summary), or `false, nil`. The result's
  fields:
  - `Object`: `nil` for the environment, with `IsEnvironment` true;
  - `Distance`: the gap left below the object, 0 when touching;
  - `Point` and `Normal`.

```lua
local grounded, ground = self:IsGrounded()
if grounded and ground.Normal.y > 0.7 and Input.IsKeyPressed("Space") then
  local v = self.LinearVelocity
  self.LinearVelocity = Vector3.new(v.x, 17, v.z)   -- jump: 17 up, 4.1 studs high under gravity 35
end
```

Errors: `IsGrounded expects maxDistance to be a finite number of at least 0 (or nothing)`.

### `object:SetParent(parent[, offsetPosition[, offsetRotation[, boneName]]])`

Attaches the object to `parent` for the rest of this Play session; `nil` lets it go.

- **Bones.** With `boneName`, a model parent's bone is followed in the animated pose shown each frame (a tool
  in a hand); the offsets are in the bone's space. `parent:GetBones()` lists the names. See
  [Equipment and bone attachments](lua-gameplay-systems.md#11-equipment-and-bone-attachments).

- **Offsets.**
  - With offsets, the object sits at `offsetPosition` in the parent's space, turned by `offsetRotation` (Euler
    degrees). A missing one is zero.
  - Without offsets, it stays exactly where it is relative to the parent.
- **Following.** The object follows its parent every frame, after physics and before `LateUpdate`, and at once when
  attached. Moving or turning the parent moves and turns the child; trees of any depth work (a lid on a chest on a
  cart).
- **Visual attachment, not a joint.** While attached, physics does not move the object: it is anchored, and
  `Anchored` reads `true`. Letting go restores the `Anchored` it had before. Its own velocity and forces do nothing
  while attached.
- **No self-collisions.** Objects in one attachment tree (parent, children, siblings) never collide with each other,
  so a tool held inside the character does not push it. They still collide with everything else.
- **Play only.** Nothing about attachment is saved. Stop puts every object back where it was in the editor.
- **Destruction.** Destroying a parent lets its children go: they stay in the scene, with `Parent` nil and
  `Anchored` restored.
- **Switching parents.** Calling `SetParent` again moves the object to the new parent or offsets (e.g. from a hand to
  a holster).

```lua
-- Equip: in the right hand. Holster: on the back, turned 90 degrees.
tool:SetParent(player, Vector3.new(1.4, 0.2, 1.0))
tool:SetParent(player, Vector3.new(0, 0.6, -1.3), Vector3.new(0, 0, 90))
tool:SetParent(nil)   -- drop it: physics owns it again
```

The child's **local +Z** is the parent's forward: a parent turned by `Rotation.y = θ` carries an offset `(0, 0, 1)`
to `(sin θ, 0, cos θ)`.

Errors:

- `SetParent expects a scene object or nil (to let go), got a table value`
- `SetParent expects offsetPosition to be a vector (finite x, y, z) or nil`
- `an object cannot be its own parent`
- `'Holder' already follows 'Tool': that would make a loop`: a parent cannot follow one of its own descendants
- `SetParent: that parent no longer exists`

### `object.Parent` → `ScriptObject|nil`

Its parent, or `nil`. Assigning works like `SetParent(value)` without offsets (it keeps its current pose relative to
the new parent); assigning `nil` lets go.

### `object:GetChildren()` → `{ScriptObject}`

The objects attached directly to it, as a new list.

### `object.Name` / `object.Id` after destruction

Both stay readable on a destroyed object (its last name and its Id), so `OnDestroyed`, `OnCollisionExit` and messages
can still say what it was. Everything else on a destroyed object is an error:
`ScriptObject 'Coin' (#4) no longer exists`. Check `object.Exists` first.

### Comparing objects across scripts

Each script has its own wrapper table per object, and `==` compares them by object: `a == b` is true when both refer
to the same object, even when they come from different scripts. For example, in a `Died` handler, `info.object ==
self` works. To key a table by object, use `object.Id`; the wrapper tables are different tables.

### `Scene.Create` options

Two new options:

- `RotationLocked` (boolean).
- `LinearVelocity` (vector). It is applied last, after size, mass and position, so a projectile leaves at that speed.
  It needs `Anchored = false`, because an anchored Part has no velocity.

```lua
local bullet = Scene.Create("Sphere", {
  Name = "Bullet", Anchored = false, GravityEnabled = false, Mass = 0.2,
  Position = muzzle + direction * 1.2, Size = Vector3.new(0.4, 0.4, 0.4),
  LinearVelocity = direction * 140,
  Tags = { "Projectile" }, Attributes = { Damage = 20, OwnerId = owner.Id },
})
bullet:AttachScript("Projectile.lua")   -- created Parts can run scripts too
```

## Physics queries and gravity

### Query filters

The last argument of `Physics.Raycast`, `RaycastAll`, `SphereCast`, `OverlapSphere` and `OverlapBox` can be:

- `nil`: no filter.
- **a scene object**: ignore it (as before).
- **an options table**:

| Option | Type | Effect |
|---|---|---|
| `Ignore` | object or list of objects | Skip these objects. |
| `[1]`, `[2]`, ... | objects | Also skipped (`{ self, tool }` works as a shorthand). |
| `IgnoreTags` | tag or list of tags | Skip objects that have any of these tags. |
| `OnlyTags` | tag or list of tags | Only objects with at least one of these tags. It applies to objects only: add `IgnoreEnvironment` to skip the Baseplate too. |
| `IgnoreTriggers` | boolean | Skip trigger Parts. Without it, queries **do** hit triggers. |
| `IgnoreEnvironment` | boolean | Skip the environment (the Baseplate). |

```lua
-- What the crosshair points at, not counting the player, its tools, bullets and pickups.
local origin, direction = Camera.ScreenPointToRay(Camera.ViewportWidth / 2, Camera.ViewportHeight / 2)
local hit = Physics.Raycast(origin, direction, 500,
  { Ignore = { player, tool }, IgnoreTriggers = true, IgnoreTags = { "Projectile", "Tool" } })

-- Interactables within 6 studs.
local nearby = Physics.OverlapSphere(self.Transform.Position, 6, { OnlyTags = "Interactable" })
```

An unknown option is an error, so a typo fails loudly:

```
Physics.Raycast: Ignor is not an option (Ignore, IgnoreTags, OnlyTags, IgnoreTriggers, IgnoreEnvironment)
```

Other errors:

- `Physics.Raycast: Ignore[2] must be a scene object, got a number value`
- `Physics.Raycast: IgnoreTriggers must be true or false, got a string value`
- `Physics.Raycast: IgnoreTags must be a tag (a string) or a list of tags`

### `Physics.Gravity` → `Vector3` (now writable)

The world's gravity in studs/s² (the default is `(0, -35, 0)`). Assign a vector to change it for this Play session:

- every axis is kept within ±1000;
- sleeping bodies wake up;
- Stop brings the project's gravity back.

```lua
Physics.Gravity = Vector3.new(0, -12, 0)   -- moon jump
```

Errors: `Physics.Gravity must be a vector (finite x, y, z), got a number value`. `Physics.Raycast = nil` and other
assignments are errors, too.

### Impact speed (landing, crashes)

The `contact` of `OnCollisionEnter` carries `RelativeVelocity`: the receiving object's velocity relative to the
other side, measured before the collision resolved it. The speed *into* the surface is that velocity against the
contact normal, which points toward the receiving object:

```lua
local impact = -Vector3.dot(contact.RelativeVelocity, contact.Normal)   -- studs/s into the surface
```

A 7-stud drop under the default gravity lands at about 21.6 studs/s; the demo's jump pad lands at about 57 studs/s.

## Script communication

Scripts never share globals: each script has its own environment, so two scripts can both define `health` or
`Update` without conflict. They communicate in four deliberate ways.

| Way | Use it for | Timing | Values |
|---|---|---|---|
| `object:GetScript(name)` | Calling another script's functions, reading its state (`health.Damage(10)`) | Immediate (a function call) | Shared: the exports table itself |
| `Events.On` / `Events.Emit` | Announcing something to whoever listens (`"Died"`, `"InventoryChanged"`) | Delivered at the start of the next frame | Copied for each receiver |
| `Shared` | Session-wide state every script reads (`Shared.player`, a score) | Immediate | Shared: one table |
| `require(name)` | Reusing code and data from a module file | Loaded once per requiring script | A separate copy per script |

### `object:GetScript(name)` → `table|nil, reason`

The **exports** of one of the scripts running on `object`: the table that script's file returned. The table is
shared, not copied, so its functions run as that script wrote them, with that script's own `self`, globals and
state.

```lua
-- Health.lua (on the player and on every dummy)
local Health = {}
local health = 100
function Health.Damage(amount, source)
  health = math.max(0, health - amount)
  Events.Emit("HealthChanged", { object = self, health = health, amount = -amount, source = source })
  if health == 0 then Events.Emit("Died", { object = self, by = source }) end
  return health
end
function Health.Get() return health end
return Health
```

```lua
-- Projectile.lua: damage whatever has a Health script.
function OnCollisionEnter(other, contact)
  local health = other and other:GetScript("Health")
  if health then health.Damage(20, "a bullet") end
  self:Destroy()
end
```

- **Names.** Case-insensitive: `"Health"`, `"Health.lua"`, `"Assets/Scripts/Health.lua"` or `"Combat/Health"` for a
  script in a folder. A bare name also finds the file in any folder.
- **When there is nothing to return,** it returns `nil` and a reason instead of raising an error:
  - `'Crate' runs no scripts`
  - `'Player' has no script 'Helth'`
  - `the script file 'Assets/Scripts/Health.lua' is missing`
  - `the script 'Health' on 'Dummy' is not running (it failed or was stopped)`
  - `the script 'Health' on 'Dummy' exports nothing: end it with return and a table (return M)`
- **Errors** only for a non-string name (`GetScript expects a script name ...`) or a destroyed object.
- Exports are available from `Start` on: every script's file runs before any `Start`.
- Scripts may call each other both ways (A's function calls B's, which calls A's); that is ordinary function calls.
  A circular `require` is still detected as an error.

### `Events.Emit(name, ...)` with tables

Events can now carry **tables** and **vectors**, besides nil, booleans, numbers, strings, scene objects and GUI
elements.

- **Copied at Emit.** A table is copied when it is emitted, so later changes by the sender do not travel.
- **Copied again for every receiver.** Two receivers never share a table, and one receiver changing its copy does not
  affect the others.
- **Converted per receiver.** Vectors arrive as `Vector3` values with arithmetic. Objects and GUI elements arrive as
  the receiver's own wrappers, which compare equal with `==`.
- **What may be inside.**
  - Keys: strings, numbers, booleans.
  - Values: booleans, numbers, strings, vectors, objects, GUI elements and plain tables of those.
  - No functions, and no tables with metatables.
- **Limits.** At most 16 levels deep and 10,000 values per Emit, and no cycles. A table referenced twice arrives as
  two copies.

```lua
Events.Emit("InventoryChanged", { owner = self, items = items, item = "Coin", delta = 1 })

Events.On("InventoryChanged", function(info)
  for item, count in pairs(info.items) do print(item, count) end
end)
```

Errors name the exact place:

```
Events.Emit: argument #2.loot[3].onPickup is a function value (events carry nil, booleans, numbers, strings, vectors, scene objects, GUI elements and plain tables of those)
Events.Emit: argument #2 contains itself (a table cycle cannot travel)
Events.Emit: argument #2.state is a table with a metatable (...); only plain tables travel
```

### `object:OnDestroyed(function(object) ... end)` → `Connection`

Runs the function **once**, at the start of the frame after the object is destroyed. `object.Exists` is `false`
then, while `Name` and `Id` are still readable.

- The connection belongs to the calling script and goes when that script stops.
- `connection:Disconnect()` cancels it. `connection.Connected` is `false` once it has fired.

```lua
local boss = Scene.Find("Boss")
boss:OnDestroyed(function(object)
  print(object.Name .. " is gone")   -- "Boss is gone"
  Events.Emit("Victory")
end)
```

A script that watches its *own* object uses `OnDestroy()` instead: its scripts stop as the object goes.

### `Shared`

One plain table that every script of the Play session sees: the same table, not copies. Each Play starts with an
empty one, and Stop throws it away.

```lua
-- Player.lua
function Start() Shared.player = self end

-- Collectible.lua
function OnTriggerEnter(other)
  if other == Shared.player then other:GetScript("Inventory").Add("Coin", 1) end
end
```

Keep names distinct (`Shared.player`, `Shared.score`), since every script writes to the same table. Values are stored
as they are: tables, functions and object wrappers included.

### Subscriptions and cleanup

Everything a script connects belongs to that script and is let go when the script stops (its object destroyed, the
script detached or failed, or Stop):

- `Events.On` connections, `OnDestroyed` connections and GUI callbacks;
- timers.

A destroyed object's listeners never run again, and no wrapper outlives its object unsafely: using a destroyed
object's wrapper is a clear error. Each script can hold up to 1,000 connections; disconnect the ones it no longer
needs.

## Examples

These come from an earlier demo, where they run together; some are shortened here. The scripts are attached as follows:

- **Player** (an unanchored 2×4×2 block): `Player.lua`, `Health.lua`, `FallDamage.lua`, `Inventory.lua` and
  `HUD.lua`.
- **Each coin** (an anchored trigger): `Collectible.lua`.
- **The chest:** `Chest.lua`, with a separate lid Part.
- **Each tool:** `Tool.lua`.
- **Each training dummy:** `Health.lua` and `Dummy.lua`.

### Camera controller (third person, mouse look, smoothing, wall avoidance)

```lua
-- Player.lua (camera part)
local yaw, pitch, distance = 90, 12, 14
local cameraPosition

function Start()
  Input.SetMouseLocked(true)
end

function Update(dt)
  if Input.MouseLocked then
    yaw = (yaw + Input.mouseDeltaX * 0.15) % 360
    pitch = math.max(-35, math.min(70, pitch + Input.mouseDeltaY * 0.15))
  end
  distance = math.max(6, math.min(30, distance - Input.scrollDelta * 2))   -- zoom
end

function LateUpdate(dt)
  local pivot = self.Transform.Position + Vector3.new(0, 2, 0)
  local y, p = math.rad(yaw), math.rad(pitch)
  local look = Vector3.new(math.cos(p) * math.cos(y), -math.sin(p), math.cos(p) * math.sin(y))
  local desired = pivot - look * distance

  -- Keep the camera in front of walls between it and the player.
  local hit = Physics.Raycast(pivot, desired - pivot, distance,
    { Ignore = self, IgnoreTriggers = true, IgnoreTags = { "Projectile", "Tool" } })
  if hit then
    desired = pivot + Vector3.normalize(desired - pivot) * math.max(1, hit.Distance - 0.5)
  end

  -- Frame-rate independent smoothing.
  cameraPosition = cameraPosition and Vector3.lerp(cameraPosition, desired, 1 - math.exp(-20 * dt)) or desired
  Camera.Position = cameraPosition
  Camera.LookAt(pivot)
end
```

### Movement controller (camera-relative, sprint, jump, air control)

```lua
-- Player.lua (movement part)
local WALK, SPRINT, JUMP = 12, 22, 17
local function flat(v) return Vector3.normalize(Vector3.new(v.x, 0, v.z)) end

function Start()
  self.RotationLocked = true
end

function Update(dt)
  local move = flat(Camera.Forward) * Input.GetAxis("S", "W") + flat(Camera.Right) * Input.GetAxis("A", "D")
  if Vector3.magnitude(move) > 0 then move = Vector3.normalize(move) end
  local target = move * (Input.IsKeyDown("LeftShift") and SPRINT or WALK)

  local grounded = self:IsGrounded()
  local v = self.LinearVelocity
  local blend = math.min(1, (grounded and 14 or 3) * dt)   -- full grip on the ground, little in the air
  local vy = v.y
  if grounded and Input.IsKeyPressed("Space") then vy = JUMP end
  self.LinearVelocity = Vector3.new(v.x + (target.x - v.x) * blend, vy, v.z + (target.z - v.z) * blend)

  if Vector3.magnitude(move) > 0 then
    self.Transform.Rotation = Vector3.new(0, math.deg(math.atan(move.x, move.z)), 0)   -- face the way we walk
  end
end
```

### Health and damage (fall damage, death, respawn)

`Health.lua` is the module shown in [GetScript](#objectgetscriptname--tablenil-reason). Fall damage is a separate script on the same
object:

```lua
-- FallDamage.lua
local SAFE_SPEED, LETHAL_SPEED = 30, 70

function OnCollisionEnter(other, contact)
  if contact == nil or contact.Normal == nil then return end
  local impact = -Vector3.dot(contact.RelativeVelocity, contact.Normal)
  if impact > SAFE_SPEED then
    local damage = math.floor((impact - SAFE_SPEED) / (LETHAL_SPEED - SAFE_SPEED) * 100 + 0.5)
    self:GetScript("Health").Damage(damage, "a fall")
  end
end
```

Reacting to death from another script, by listening for the event rather than knowing who did the damage:

```lua
-- Player.lua (respawn part)
Events.On("Died", function(info)
  if info.object == self then
    Timer.After(2, function()
      self.Transform.Position = spawnPoint
      self.LinearVelocity = Vector3.new(0, 0, 0)
      self:GetScript("Health").Reset()
    end)
  end
end)
```

### Interaction and inventory (pickups, a timed chest)

```lua
-- Inventory.lua: plain Lua data, reached with player:GetScript("Inventory")
local Inventory, items = {}, {}
function Inventory.Add(item, count)
  items[item] = (items[item] or 0) + (count or 1)
  Events.Emit("InventoryChanged", { owner = self, items = items, item = item })
end
function Inventory.Remove(item, count)
  count = count or 1
  if (items[item] or 0) < count then return false end
  items[item] = items[item] - count
  if items[item] == 0 then items[item] = nil end
  Events.Emit("InventoryChanged", { owner = self, items = items, item = item })
  return true
end
function Inventory.Count(item) return items[item] or 0 end
return Inventory
```

```lua
-- Collectible.lua: touch to pick up (the coin is a trigger)
function OnTriggerEnter(other)
  if other ~= nil and other == Shared.player then
    other:GetScript("Inventory").Add("Coin", 1)
    self:Destroy()
  end
end
```

```lua
-- Chest.lua: hold F for one second within 5 studs; the lid is another Part, attached and then swung open
local held, opened, lid = 0, false, nil
function Start()
  lid = Scene.Find("ChestLid")
  lid:SetParent(self, Vector3.new(0, 1.15, 0))
end
function Update(dt)
  local player = Shared.player
  if opened or player == nil then return end
  local near = Vector3.distance(player.Transform.Position, self.Transform.Position) <= 5
  if near and Input.IsKeyDown("F") then
    held = held + dt
    Events.Emit("Progress", math.min(1, held))
    if held >= 1 then
      opened = true
      lid:SetParent(self, Vector3.new(0, 1.9, -1.1), Vector3.new(-70, 0, 0))
      local inventory = player:GetScript("Inventory")
      inventory.Add("Gem", 2)
      inventory.Add("Ammo", 30)
      Events.Emit("Progress", nil)
    end
  elseif held > 0 then
    held = 0
    Events.Emit("Progress", nil)
  end
end
```

### Tools (one reusable script, slots, cooldown, holster)

```lua
-- Tool.lua: the same script on every tool; what "use" does comes from a module, by the tool's name
local def = require("ToolDefs")[self.Name]
local owner, equipped, nextUse = nil, false, 0

local function equip(on)
  equipped = on
  if on then self:SetParent(owner, def.hold) else self:SetParent(owner, def.holster, def.holsterRotation) end
  Events.Emit("ToolChanged", { tool = self.Name, equipped = on })
end

function Start()
  self:AddTag("Tool")
  Events.On("EquipSlot", function(slot) if owner then equip(slot == def.slot and not equipped) end end)
end

function Update(dt)
  if owner == nil then
    local player = Shared.player
    if player and Vector3.distance(player.Transform.Position, self.Transform.Position) <= 4 and Input.IsKeyPressed("E") then
      owner = player
      equip(true)
    end
    return
  end
  if Input.IsKeyPressed(def.slot) then Events.Emit("EquipSlot", def.slot) end   -- the others holster
  if equipped and Input.IsMouseButtonDown("Left") and Time.elapsedTime >= nextUse then
    nextUse = Time.elapsedTime + def.cooldown
    def.activate(self, owner)
  end
end
```

### Script communication: who talks to whom in the demo

- **Calls through `GetScript`:**
  - `Projectile.lua` calls the hit object's `Health.Damage`;
  - `FallDamage.lua` calls its own object's `Health`;
  - collectibles, the chest and the tools call the player's `Inventory`.
- **Events:**
  - `Health` emits `HealthChanged` and `Died`; `Inventory` emits `InventoryChanged`.
  - The tools emit `ToolChanged`, `ToolPicked` and `EquipSlot`; the chest emits `Progress` and `ChestOpened`.
  - `HUD.lua` only listens, so none of the others knows the HUD exists. `Dummy.lua` reacts to its own `Health`'s
    events.
- **`Shared`:**
  - `Shared.player` lets every script find the player without searching by name.
- **`require`:**
  - `Tool.lua` requires `ToolDefs.lua`, the data and functions for each tool.

## Common errors

| Message | Cause and fix |
|---|---|
| `X is not a valid member of ScriptObject` | A typo, or a member that does not exist. Members are case-sensitive (`self:IsGrounded()`, not `self:isGrounded()`). |
| `call it with a colon: object:SetParent(...)` | `obj.SetParent(x)` instead of `obj:SetParent(x)`. |
| `ScriptObject 'Coin' (#4) no longer exists` | The object was destroyed. Check `obj.Exists` first (`Name` and `Id` stay readable). |
| `GetScript` returned `nil, "... exports nothing ..."` | The other script does not end with `return M`. |
| `GetScript` returned `nil, "... has no script ..."` | The name is wrong, or the script is attached to a different object. |
| `Events.Emit: argument #2.x is a function value` | Events carry data only. Pass a name or an Id, and look the function up on the receiving side (`GetScript`). |
| `Physics.Raycast: Foo is not an option (...)` | A typo in a filter option. |
| `Scene.Create: LinearVelocity works on an unanchored Part only (...)` | Add `Anchored = false` to the options. |
| `'Crate' is not simulated by physics` (setting `RotationLocked`) | The Part has no physics body. |
| An attached object does not move when its velocity is set | Attached objects are moved by their parent. Let go first (`obj:SetParent(nil)`). |
| `info.object == self` is false in an event handler | The event carried a different object. Wrappers of the same object compare equal across scripts. |
| W/A/S/D seem wrong on AZERTY | Key names are US positions: `"W"` is the key printed Z, `"A"` is Q. |

## Limits and behaviour notes

- **Attachment** is kinematic following, not a physics joint: an attached object pushes others but is never pushed
  or swung. Play-only; nothing is saved.
- **Collision groups:** each attachment tree gets its own group, and Parts in one tree never collide with each other.
- **Mouse lock:**
  - Escape always stops Play; Stop always unlocks.
  - While the mouse is locked, the editor's toolbar and panels ignore the mouse.
  - `Tab` in the demo is the demo's own choice, not engine behaviour.
- **Events** are delivered once per frame, at its start. An event emitted while handling an event arrives next frame.
  At most 10,000 events can wait at once.
- **Table events** cost a copy per receiver: keep them small (a few hundred values is fine).
- **Fast projectiles:** a 0.4-stud sphere at 600 studs/s (10 studs per frame) hit a 1-stud wall in testing (physics
  runs 4 substeps per 1/60 s step). Much faster or thinner cases can still pass through: use `Physics.Raycast` or
  `SphereCast` along the path for hitscan weapons.
- **Gravity, `RotationLocked`, tags, attributes and created objects** last until Stop: each Play session starts from
  the project.

## The Baseplate in the World panel

The World panel lists the **Baseplate** first in the Workspace list, labelled *Environment*. Click it to select it.
The section under the list then shows its details:

- its size (512 × 1 × 512);
- the height of its top surface and its centre;
- its collider: a solid box, the same as what is drawn.

It is the environment, not a Part, so it cannot be moved, resized, rotated, recoloured, renamed or deleted. Selecting
it clears the Part selection, so no tool acts on it. Selecting a Part again ends the Baseplate's selection. It has no
grid or border, and nothing about it is saved in the project.

## Existing API summary

For reference:

- **`Scene`:**
  - finding objects: `Find`, `FindAll`, `FindById`, `GetAll`, `FindByType`, `FindAllByType`, `FindByTag`,
    `FindAllByTag`;
  - counting: `Count`, `CountByType`, `CountByTag`;
  - `Create(type, options)`.
- **ScriptObject:**
  - properties: `Name`, `Id`, `Type`, `Exists`, `Transform.Position` / `Rotation` / `Scale`, `Anchored`,
    `GravityEnabled`, `CanCollide`, `IsTrigger`, `Friction`, `Mass`, `Color`, `Visible`, `IsSleeping`,
    `LinearVelocity`, `AngularVelocity`;
  - forces: `ApplyImpulse`, `ApplyForce`, `ApplyTorque`, `ApplyImpulseAtPoint`, `ApplyAngularImpulse`, `Wake`;
  - tags: `AddTag`, `RemoveTag`, `HasTag`, `GetTags`;
  - attributes: `SetAttribute`, `GetAttribute`, `HasAttribute`, `RemoveAttribute`, `GetAttributes`;
  - other: `GetBounds`, `Clone`, `AttachScript`, `Destroy`.
- **RaycastResult:** `Object`, `IsEnvironment`, `Distance`, `Point`, `Normal`.
- **Contact:** `Point`, `Normal` (nil for triggers), `RelativeVelocity`, `IsEnvironment`.
- **`Timer`:**
  - `After(seconds, f, ...)` and `Every(seconds, f, ...)`;
  - handles with `Active`, `Remaining`, `Paused`, `Cancel`, `Pause`, `Resume`, `Restart`.
- **`Time`:** `deltaTime`, `elapsedTime`, `frameCount`, `fixedDeltaTime`.
- **`UI`:** `Find`, `FindAll`, `FindPath`, `FindById`, `Create`, `Root`, `GetCanvasRect`.
  - GUI elements: `Text`, `Visible`, `Color`, `SetPosition`, `SetSize`, `SetAnchor`, `OnClick`...
- **`Vector3`:** `new`, `add`, `sub`, `mul`, `dot`, `cross`, `magnitude`, `normalize`, `distance`, `lerp`, plus `+ - * /`
  on vector values.
- **`Atmosphere`:** sky, sun, fog and image settings.
- **Standard Lua** libraries, sandboxed:
  - `math`, `string`, `table`, `utf8`, `coroutine`;
  - `os.clock` / `date` / `time`, `debug.traceback`;
  - `require` for modules in `Assets/Scripts`.
  - There is no file or OS access.
