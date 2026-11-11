namespace Wisko.Engine.Scripting;

public enum ScriptApiKind
{
    /// <summary>A global table with members (self, Time, Input, math, ...).</summary>
    Object,
    Function,

    /// <summary>A value (read-only where noted).</summary>
    Property,
    Constant,
    Keyword,

    /// <summary>A lifecycle or event function the script defines (Start, Update, OnCollisionEnter, OnDestroy, ...).</summary>
    Lifecycle
}

/// <summary>
/// One name scripts can use. <see cref="MemberTable"/> names the member list reached through it (e.g. self →
/// "ScriptObject"), so completion can follow chains like <c>self.Transform.Position.x</c>.
/// </summary>
public sealed record ScriptApiMember(string Name, ScriptApiKind Kind, string Signature, string Summary, string? MemberTable = null);

/// <summary>
/// What scripts see: the Wisko engine API (self, Scene, Transform, Vector3, Time, Input, UI, GuiElement, Timer, TimerHandle,
/// Physics, RaycastResult, Contact, Events, Connection, Camera, Atmosphere, ScriptHandle), the lifecycle and event functions the engine calls, and the standard Lua globals and libraries
/// the sandbox offers (<see cref="IsBuiltinGlobal"/> tells the two apart). The one engine-owned source for editor
/// completion, highlighting and help; tests check it against the live sandbox so the two cannot silently drift.
/// Key and mouse button names come from <see cref="ScriptInputState"/>.
/// </summary>
public static class ScriptApiCatalog
{
    private static readonly ScriptApiMember[] GlobalMembers =
    [
        new("self", ScriptApiKind.Object, "self", "The scene object this script is attached to.", "ScriptObject"),
        new("Scene", ScriptApiKind.Object, "Scene", "The running scene's objects (changes last until Stop).", "Scene"),
        new("Time", ScriptApiKind.Object, "Time", "Frame timing (read-only).", "Time"),
        new("Input", ScriptApiKind.Object, "Input", "Keyboard and mouse state for this frame (read-only).", "Input"),
        new("UI", ScriptApiKind.Object, "UI", "The game GUI while playing (changes last until Stop).", "UI"),
        new("Timer", ScriptApiKind.Object, "Timer", "Runs functions later, in game time (this script's timers end with it).", "Timer"),
        new("Physics", ScriptApiKind.Object, "Physics", "Questions about the running physics world (raycasts, sphere casts, overlaps).", "Physics"),
        new("Vector3", ScriptApiKind.Object, "Vector3", "Vector values and math (Vector3.new(x, y, z), add, dot, cross, ...).", "Vector3Library"),
        new("Events", ScriptApiKind.Object, "Events", "Named messages between scripts, delivered at the start of the next frame.", "Events"),
        new("Camera", ScriptApiKind.Object, "Camera", "The Play view's camera (changes last until Stop).", "Camera"),
        new("Atmosphere", ScriptApiKind.Object, "Atmosphere", "Sky, sun, ambient light, fog and image settings (changes last until Stop).", "Atmosphere"),
        new("Graphics", ScriptApiKind.Object, "Graphics", "Rendering settings: tone mapping, bloom, vignette, ambient occlusion, white balance, shadows, quality (changes last until Stop; Universal renderer).", "Graphics"),
        new("Shared", ScriptApiKind.Object, "Shared", "One plain table every script of this Play session sees (empty at each Play)."),
        new("Navigation", ScriptApiKind.Object, "Navigation", "Walkable-area paths, reachability and agents that walk objects along them.", "Navigation"),
        new("Perception", ScriptApiKind.Object, "Perception", "View cones, line of sight, sensors and developer-defined noises.", "Perception"),
        new("Random", ScriptApiKind.Object, "Random", "Seeded random generators: the same seed, the same numbers on every platform.", "RandomLibrary"),
        new("Noise", ScriptApiKind.Object, "Noise", "Seeded smooth noise (Perlin, fractal) for generation.", "Noise"),
        new("Path", ScriptApiKind.Object, "Path", "Curves through points (linear, Catmull-Rom, Bezier) and objects that follow them.", "PathLibrary"),
        new("GameTime", ScriptApiKind.Object, "GameTime", "The game's clock: days, hours, day length, schedules.", "GameTime"),
        new("GameSave", ScriptApiKind.Object, "GameSave", "Saves and loads plain Lua data in named slots of the project's Saves folder.", "GameSave"),
        new("Cameras", ScriptApiKind.Object, "Cameras", "Named cameras: switching, blends, shake, paths and feeds for GUI images.", "Cameras"),
        new("Particles", ScriptApiKind.Object, "Particles", "Particle emitters (rain, snow, sparks), drawn while playing.", "Particles"),
        new("DebugDraw", ScriptApiKind.Object, "DebugDraw", "Lines, boxes, spheres and paths drawn while playing, for debugging.", "DebugDraw"),
        new("Chat", ScriptApiKind.Object, "Chat", "Text chat while playing: channels, messages, filters, mute and block, a ready-made chat window (on this machine only: Wisko has no networking).", "Chat"),
        new("print", ScriptApiKind.Function, "print(...)", "Writes its arguments to the Output panel and the log."),
        new("assert", ScriptApiKind.Function, "assert(value, message?)", "Raises an error when value is false or nil."),
        new("error", ScriptApiKind.Function, "error(message, level?)", "Raises an error."),
        new("pcall", ScriptApiKind.Function, "pcall(f, ...)", "Calls f in protected mode; returns ok, results-or-error."),
        new("xpcall", ScriptApiKind.Function, "xpcall(f, handler, ...)", "Like pcall, with an error handler."),
        new("pairs", ScriptApiKind.Function, "pairs(t)", "Iterates over all key/value pairs of a table."),
        new("ipairs", ScriptApiKind.Function, "ipairs(t)", "Iterates over t[1], t[2], ... until the first nil."),
        new("next", ScriptApiKind.Function, "next(t, key?)", "The next key/value pair of a table."),
        new("select", ScriptApiKind.Function, "select(n, ...)", "Arguments after the n-th, or their count with \"#\"."),
        new("tonumber", ScriptApiKind.Function, "tonumber(value, base?)", "Converts to a number, or nil."),
        new("tostring", ScriptApiKind.Function, "tostring(value)", "Converts any value to a string."),
        new("type", ScriptApiKind.Function, "type(value)", "The type name of a value (\"number\", \"table\", ...)."),
        new("rawequal", ScriptApiKind.Function, "rawequal(a, b)", "Equality without metamethods."),
        new("rawget", ScriptApiKind.Function, "rawget(t, key)", "Table read without metamethods."),
        new("rawset", ScriptApiKind.Function, "rawset(t, key, value)", "Table write without metamethods."),
        new("rawlen", ScriptApiKind.Function, "rawlen(v)", "Length without metamethods."),
        new("getmetatable", ScriptApiKind.Function, "getmetatable(t)", "The metatable of a value."),
        new("setmetatable", ScriptApiKind.Function, "setmetatable(t, mt)", "Sets a table's metatable (__gc/__close not allowed)."),
        new("require", ScriptApiKind.Function, "require(name)", "Loads a Lua module from Assets/Scripts (\"Utils\", \"Combat.Damage\") once and returns it."),
        new("load", ScriptApiKind.Function, "load(chunk, chunkname?, mode?, env?)", "Compiles Lua source text into a function (or nil, message)."),
        new("warn", ScriptApiKind.Function, "warn(message, ...)", "Writes a warning to the Output (after warn(\"@on\"))."),
        new("collectgarbage", ScriptApiKind.Function, "collectgarbage(option?)", "\"count\": memory in use (KB); \"collect\" / \"step\": run the collector."),
        new("math", ScriptApiKind.Object, "math", "Math functions and constants.", "math"),
        new("string", ScriptApiKind.Object, "string", "String functions.", "string"),
        new("table", ScriptApiKind.Object, "table", "Table functions.", "table"),
        new("utf8", ScriptApiKind.Object, "utf8", "UTF-8 functions.", "utf8"),
        new("coroutine", ScriptApiKind.Object, "coroutine", "Coroutines (functions that can pause and resume).", "coroutine"),
        new("package", ScriptApiKind.Object, "package", "The module system behind require.", "package"),
        new("os", ScriptApiKind.Object, "os", "Time and date functions.", "os"),
        new("debug", ScriptApiKind.Object, "debug", "debug.traceback.", "debug"),
        new("_G", ScriptApiKind.Object, "_G", "This script's own global table."),
        new("_VERSION", ScriptApiKind.Constant, "_VERSION", "The Lua version string."),
    ];

    private static readonly ScriptApiMember[] LifecycleMembers =
    [
        new("Start", ScriptApiKind.Lifecycle, "function Start()", "Runs once when Play begins."),
        new("Update", ScriptApiKind.Lifecycle, "function Update(dt)", "Runs every frame while playing; dt = seconds since the last frame."),
        new("LateUpdate", ScriptApiKind.Lifecycle, "function LateUpdate(dt)", "Runs every frame after physics has moved everything (follow cameras read final positions here)."),
        new("OnCollisionEnter", ScriptApiKind.Lifecycle, "function OnCollisionEnter(other, contact)", "This object started touching other (nil: the environment, e.g. the baseplate); contact: where and how hard."),
        new("OnCollisionStay", ScriptApiKind.Lifecycle, "function OnCollisionStay(other, contact)", "Once per frame while this object keeps touching other (nil: the environment); contact: the latest touch."),
        new("OnCollisionExit", ScriptApiKind.Lifecycle, "function OnCollisionExit(other)", "This object stopped touching other (it may no longer exist: check other.Exists)."),
        new("OnTriggerEnter", ScriptApiKind.Lifecycle, "function OnTriggerEnter(other, contact)", "A trigger started overlapping: this object is the trigger or the Part entering it (contact.Normal is nil)."),
        new("OnTriggerStay", ScriptApiKind.Lifecycle, "function OnTriggerStay(other, contact)", "Once per frame while the trigger overlap lasts."),
        new("OnTriggerExit", ScriptApiKind.Lifecycle, "function OnTriggerExit(other)", "The trigger overlap ended (other may no longer exist: check other.Exists)."),
        new("OnDestroy", ScriptApiKind.Lifecycle, "function OnDestroy()", "Runs once when this object is destroyed while playing (not at Stop); self no longer exists."),
    ];

    private static readonly Dictionary<string, ScriptApiMember[]> Tables = new(StringComparer.Ordinal)
    {
        ["Scene"] =
        [
            new("Find", ScriptApiKind.Function, "Find(name)", "The first object with this name (scene order), or nil.", "ScriptObject"),
            new("FindAll", ScriptApiKind.Function, "FindAll(name)", "Every object with this name, as a list (empty when none)."),
            new("FindById", ScriptApiKind.Function, "FindById(id)", "The object with this Id, or nil.", "ScriptObject"),
            new("Create", ScriptApiKind.Function, "Create(type, name? | options?, anchored?, isTrigger?)", "A new Part (\"Block\", \"Cylinder\", \"Wedge\", \"Sphere\", \"Pyramid\"); anchored by default; gone at Stop. Options: Name, Anchored, IsTrigger, Position, Rotation, Size, Color, CanCollide, GravityEnabled, Friction, Mass, Visible, RotationLocked, LinearVelocity, GravityScale, CollisionGroup, Tags, Attributes.", "ScriptObject"),
            new("GetAll", ScriptApiKind.Function, "GetAll()", "Every object in the scene, in scene order."),
            new("FindByType", ScriptApiKind.Function, "FindByType(type)", "The first object of this Type (\"Block\", \"Sphere\", ..., \"Model\"), or nil.", "ScriptObject"),
            new("FindAllByType", ScriptApiKind.Function, "FindAllByType(type)", "Every object of this Type, as a list."),
            new("FindByTag", ScriptApiKind.Function, "FindByTag(tag)", "The first object with this tag, or nil.", "ScriptObject"),
            new("FindAllByTag", ScriptApiKind.Function, "FindAllByTag(tag)", "Every object with this tag, as a list."),
            new("Count", ScriptApiKind.Function, "Count()", "How many objects the scene has."),
            new("CountByType", ScriptApiKind.Function, "CountByType(type)", "How many objects of this Type the scene has."),
            new("CountByTag", ScriptApiKind.Function, "CountByTag(tag)", "How many objects have this tag."),
        ],
        ["ScriptObject"] =
        [
            new("Name", ScriptApiKind.Property, "Name: string", "The object's name (a Part's can be changed while playing; still readable once destroyed)."),
            new("Transform", ScriptApiKind.Property, "Transform", "Position, rotation and scale of the object.", "Transform"),
            new("Material", ScriptApiKind.Property, "Material", "Its surface: shading, metallic, roughness, glow, transparency, textures (Universal renderer; until Stop).", "Material"),
            new("Light", ScriptApiKind.Property, "Light?", "The light it gives, or nil (AddLight gives it one).", "Light"),
            new("Id", ScriptApiKind.Property, "Id: number", "The object's Id in this Play session (read-only; still readable once destroyed)."),
            new("Type", ScriptApiKind.Property, "Type: string", "The shape (\"Block\", \"Sphere\", ...) or \"Model\" (read-only)."),
            new("Exists", ScriptApiKind.Property, "Exists: boolean", "Whether the object is still in the scene (read-only)."),
            new("Anchored", ScriptApiKind.Property, "Anchored: boolean", "Anchored Parts are not moved by physics; unanchoring one makes it fall from where it is."),
            new("GravityEnabled", ScriptApiKind.Property, "GravityEnabled: boolean", "Whether gravity pulls the Part while it is unanchored."),
            new("CanCollide", ScriptApiKind.Property, "CanCollide: boolean", "Whether the Part collides with others."),
            new("IsTrigger", ScriptApiKind.Property, "IsTrigger: boolean", "A trigger detects overlaps (OnTrigger...) and never blocks anything."),
            new("Friction", ScriptApiKind.Property, "Friction: number", "Surface friction, 0 (slippery) to 1 (grippy)."),
            new("Mass", ScriptApiKind.Property, "Mass: number", "The mass physics uses; set a number, or nil for Auto."),
            new("LinearVelocity", ScriptApiKind.Property, "LinearVelocity: Vector3", "Velocity in units per second; on an anchored Part it moves the Part on its own (a moving platform).", "Vector3"),
            new("AngularVelocity", ScriptApiKind.Property, "AngularVelocity: Vector3", "Spin in degrees per second; on an anchored Part it turns the Part on its own (spinning machinery).", "Vector3"),
            new("SurfaceVelocity", ScriptApiKind.Property, "SurfaceVelocity: Vector3", "Anchored Parts only: a conveyor surface carrying what rests on it at this velocity, while the Part stays put.", "Vector3"),
            new("GravityScale", ScriptApiKind.Property, "GravityScale: number", "How strongly gravity pulls it (1 normal, 0 floats, negative falls up); Play only."),
            new("LinearDamping", ScriptApiKind.Property, "LinearDamping: number", "Drag on its movement per second (0 none; velocity keeps e^-damping each second)."),
            new("AngularDamping", ScriptApiKind.Property, "AngularDamping: number", "Drag on its spin per second (0 none)."),
            new("CollisionGroup", ScriptApiKind.Property, "CollisionGroup: string", "Its collision group (\"Default\"); Physics.SetGroupsCollide decides which groups touch."),
            new("Forward", ScriptApiKind.Property, "Forward: Vector3", "Its forward direction in the world (local -Z; read-only).", "Vector3"),
            new("Right", ScriptApiKind.Property, "Right: Vector3", "Its right direction in the world (local +X; read-only).", "Vector3"),
            new("Up", ScriptApiKind.Property, "Up: Vector3", "Its up direction in the world (local +Y; read-only).", "Vector3"),
            new("Color", ScriptApiKind.Property, "Color: string", "The Part's colour \"#RRGGBB\" (a model's tint)."),
            new("Visible", ScriptApiKind.Property, "Visible: boolean", "Whether the Part is drawn (it still collides and is found while hidden)."),
            new("IsSleeping", ScriptApiKind.Property, "IsSleeping: boolean", "Whether physics has put the moving body to sleep (read-only)."),
            new("RotationLocked", ScriptApiKind.Property, "RotationLocked: boolean", "Physics never turns the Part (a character stays upright); scripts still can."),
            new("Parent", ScriptApiKind.Property, "Parent: ScriptObject", "The object it follows (SetParent), or nil; setting it keeps the current offset.", "ScriptObject"),
            new("ApplyImpulse", ScriptApiKind.Function, "ApplyImpulse(x, y, z)", "Pushes an unanchored Part: its velocity changes by impulse / mass."),
            new("ApplyForce", ScriptApiKind.Function, "ApplyForce(x, y, z)", "Pushes an unanchored Part during this frame (apply it every frame to keep pushing)."),
            new("ApplyTorque", ScriptApiKind.Function, "ApplyTorque(x, y, z)", "Turns an unanchored Part during this frame (around the world axes)."),
            new("ApplyImpulseAtPoint", ScriptApiKind.Function, "ApplyImpulseAtPoint(impulse, point)", "An instant push at a world point (two vectors or six numbers): moves and turns it."),
            new("ApplyAngularImpulse", ScriptApiKind.Function, "ApplyAngularImpulse(x, y, z)", "An instant turn around the world axes."),
            new("Wake", ScriptApiKind.Function, "Wake()", "Wakes a sleeping body."),
            new("AddTag", ScriptApiKind.Function, "AddTag(tag)", "Gives the object a tag (Scene.FindAllByTag finds it; at most 32)."),
            new("RemoveTag", ScriptApiKind.Function, "RemoveTag(tag)", "Takes a tag off."),
            new("HasTag", ScriptApiKind.Function, "HasTag(tag)", "Whether the object has the tag."),
            new("GetTags", ScriptApiKind.Function, "GetTags()", "Its tags, as a list."),
            new("SetAttribute", ScriptApiKind.Function, "SetAttribute(name, value)", "Stores a number, string or boolean on the object (nil removes it); every script sees it."),
            new("GetAttribute", ScriptApiKind.Function, "GetAttribute(name)", "A stored value, or nil."),
            new("HasAttribute", ScriptApiKind.Function, "HasAttribute(name)", "Whether the value is stored."),
            new("RemoveAttribute", ScriptApiKind.Function, "RemoveAttribute(name)", "Removes a stored value."),
            new("GetAttributes", ScriptApiKind.Function, "GetAttributes()", "Every stored value, as a table name -> value."),
            new("GetBounds", ScriptApiKind.Function, "GetBounds()", "The smallest world box around its shape: min and max corners."),
            new("Clone", ScriptApiKind.Function, "Clone(name?)", "A copy in the running scene (no scripts; gone at Stop).", "ScriptObject"),
            new("AttachScript", ScriptApiKind.Function, "AttachScript(path)", "Runs a script from Assets/Scripts on the object while playing (\"Spin.lua\").", "ScriptHandle"),
            new("Destroy", ScriptApiKind.Function, "Destroy()", "Removes the object from the running scene now (Stop brings an authored one back)."),
            new("AddLight", ScriptApiKind.Function, "AddLight([type])", "Gives it a light: \"Point\" (default), \"Spot\" or \"Directional\"; returns object.Light."),
            new("RemoveLight", ScriptApiKind.Function, "RemoveLight()", "Takes its light away (object.Light becomes nil)."),
            new("IsGrounded", ScriptApiKind.Function, "IsGrounded(maxDistance?)", "Whether it stands on something within maxDistance (0.15) below it; also the surface as a RaycastResult."),
            new("SetParent", ScriptApiKind.Function, "SetParent(parent, offsetPosition?, offsetRotation?, bone?)", "Follows parent (or one of a model parent's bones) every frame after physics (nil lets go); this Play session only."),
            new("GetChildren", ScriptApiKind.Function, "GetChildren()", "The objects that follow it (SetParent), as a list."),
            new("GetScript", ScriptApiKind.Function, "GetScript(name)", "What one of its scripts returned (return M), or nil and why."),
            new("OnDestroyed", ScriptApiKind.Function, "OnDestroyed(function(object) ... end)", "Runs the function once after the object is destroyed (next frame).", "Connection"),
            new("ApplyForceAtPoint", ScriptApiKind.Function, "ApplyForceAtPoint(force, point)", "Pushes at a world point during this frame: moves and turns it (wheels, thrusters)."),
            new("GetVelocityAtPoint", ScriptApiKind.Function, "GetVelocityAtPoint(point)", "How fast a world point fixed to it moves now (velocity plus spin).", "Vector3"),
            new("GetMass", ScriptApiKind.Function, "GetMass()", "The mass physics uses now (Mass, or density x volume on Auto)."),
            new("Rotate", ScriptApiKind.Function, "Rotate(x, y, z, space?)", "Turns it by degrees around its own axes (\"Local\", default) or the world's (\"World\")."),
            new("RotateAround", ScriptApiKind.Function, "RotateAround(pivot, axis, degrees)", "Swings it around a world point and axis (its position too)."),
            new("LookAt", ScriptApiKind.Function, "LookAt(point, up?)", "Turns it so its Forward points at a world point."),
            new("PointToWorld", ScriptApiKind.Function, "PointToWorld(localPoint)", "A point in its own space, in world space.", "Vector3"),
            new("PointToLocal", ScriptApiKind.Function, "PointToLocal(worldPoint)", "A world point, in its own space.", "Vector3"),
            new("VectorToWorld", ScriptApiKind.Function, "VectorToWorld(localVector)", "A direction in its own axes, in world axes.", "Vector3"),
            new("VectorToLocal", ScriptApiKind.Function, "VectorToLocal(worldVector)", "A world direction, in its own axes.", "Vector3"),
            new("IgnoreCollisionWith", ScriptApiKind.Function, "IgnoreCollisionWith(other, ignore?)", "It and other pass through each other (true, default) or collide again (false)."),
            new("OnAttributeChanged", ScriptApiKind.Function, "OnAttributeChanged(name, function(value, object) ... end)", "Runs the function (next frame) each time that attribute changes.", "Connection"),
            new("HasAnimations", ScriptApiKind.Function, "HasAnimations()", "Whether it is an imported model with animation clips."),
            new("GetAnimations", ScriptApiKind.Function, "GetAnimations()", "Its model's clips as a list of { Name, Duration (seconds), Index }."),
            new("PlayAnimation", ScriptApiKind.Function, "PlayAnimation(name, options?)", "Plays a clip (name or number); options Loop, Speed, FadeTime, StartTime, Restart. True, or false and why."),
            new("CrossFadeAnimation", ScriptApiKind.Function, "CrossFadeAnimation(name, fadeTime, options?)", "Blends from what shows now to a clip over fadeTime seconds. True, or false and why."),
            new("StopAnimation", ScriptApiKind.Function, "StopAnimation(fadeTime?)", "Back to the rest pose (at once, or fading over fadeTime seconds)."),
            new("PauseAnimation", ScriptApiKind.Function, "PauseAnimation()", "Freezes the animation where it is."),
            new("ResumeAnimation", ScriptApiKind.Function, "ResumeAnimation()", "Continues a paused animation."),
            new("RestartAnimation", ScriptApiKind.Function, "RestartAnimation()", "Plays the current clip again from its start."),
            new("SetAnimationSpeed", ScriptApiKind.Function, "SetAnimationSpeed(speed)", "Playback speed of the current clip (1 as authored, 0 holds, negative backwards)."),
            new("SetAnimationLooping", ScriptApiKind.Function, "SetAnimationLooping(loop)", "Whether the current clip loops or plays once."),
            new("GetAnimationTime", ScriptApiKind.Function, "GetAnimationTime()", "Where the current clip is, in seconds."),
            new("SetAnimationTime", ScriptApiKind.Function, "SetAnimationTime(seconds)", "Moves the current clip to a time (wrapped when looping)."),
            new("GetAnimationLength", ScriptApiKind.Function, "GetAnimationLength(name?)", "A clip's length in seconds (the current clip's without a name), or nil."),
            new("GetCurrentAnimation", ScriptApiKind.Function, "GetCurrentAnimation()", "The name of the clip playing or fading in, or nil."),
            new("IsAnimationPlaying", ScriptApiKind.Function, "IsAnimationPlaying(name?)", "Whether a clip (or that clip) is playing: not paused, not finished."),
            new("OnAnimationFinished", ScriptApiKind.Function, "OnAnimationFinished(function(name, object) ... end)", "Runs the function (next frame) when the current clip, playing once, ends.", "Connection"),
            new("Teleport", ScriptApiKind.Function, "Teleport(position, rotation?, options?)", "Moves it at once, its physics body too (KeepVelocity, RotateVelocity); what follows it comes along."),
            new("GetBones", ScriptApiKind.Function, "GetBones()", "A model's bone names (empty for a Part)."),
            new("HasBone", ScriptApiKind.Function, "HasBone(name)", "Whether its model has that bone."),
            new("GetBoneTransform", ScriptApiKind.Function, "GetBoneTransform(name)", "A bone's world position and rotation in the pose shown now, or nil and why."),
            new("OnTriggerEntered", ScriptApiKind.Function, "OnTriggerEntered(function(other, trigger) ... end)", "Runs when something starts overlapping this trigger (any script can listen).", "Connection"),
            new("OnTriggerExited", ScriptApiKind.Function, "OnTriggerExited(function(other, trigger) ... end)", "Runs when something stops overlapping this trigger.", "Connection"),
        ],
        ["ScriptHandle"] =
        [
            new("Active", ScriptApiKind.Property, "Active: boolean", "Whether the attached script still runs (read-only)."),
            new("Path", ScriptApiKind.Property, "Path: string", "The script's path in the project (read-only)."),
            new("Detach", ScriptApiKind.Function, "Detach()", "Stops the script (its OnDestroy runs)."),
        ],
        ["Transform"] =
        [
            new("Position", ScriptApiKind.Property, "Position: Vector3", "World position (centre of a primitive, origin of a model).", "Vector3"),
            new("Rotation", ScriptApiKind.Property, "Rotation: Vector3", "Rotation in degrees around x, y and z.", "Vector3"),
            new("Scale", ScriptApiKind.Property, "Scale: Vector3", "Size of a primitive, scale of a model (minimum enforced).", "Vector3"),
        ],
        ["Vector3"] =
        [
            new("x", ScriptApiKind.Property, "x: number", "X component (read-write)."),
            new("y", ScriptApiKind.Property, "y: number", "Y component (read-write)."),
            new("z", ScriptApiKind.Property, "z: number", "Z component (read-write)."),
        ],
        ["Vector3Library"] =
        [
            new("new", ScriptApiKind.Function, "new(x, y, z)", "A vector value (no arguments: 0, 0, 0); +, -, * and / work on vectors.", "Vector3"),
            new("add", ScriptApiKind.Function, "add(a, b)", "a + b.", "Vector3"),
            new("sub", ScriptApiKind.Function, "sub(a, b)", "a - b.", "Vector3"),
            new("mul", ScriptApiKind.Function, "mul(v, n)", "v scaled by the number n.", "Vector3"),
            new("dot", ScriptApiKind.Function, "dot(a, b)", "The dot product."),
            new("cross", ScriptApiKind.Function, "cross(a, b)", "The cross product.", "Vector3"),
            new("magnitude", ScriptApiKind.Function, "magnitude(v)", "The length."),
            new("normalize", ScriptApiKind.Function, "normalize(v)", "Length 1 in the same direction (zero stays zero).", "Vector3"),
            new("distance", ScriptApiKind.Function, "distance(a, b)", "The distance between two points."),
            new("lerp", ScriptApiKind.Function, "lerp(a, b, t)", "a + (b - a) * t.", "Vector3"),
            new("clampMagnitude", ScriptApiKind.Function, "clampMagnitude(v, maxLength)", "The same direction, at most maxLength long (a speed limit).", "Vector3"),
            new("moveTowards", ScriptApiKind.Function, "moveTowards(a, b, maxDelta)", "From a toward b by at most maxDelta, never past b (steady acceleration).", "Vector3"),
            new("project", ScriptApiKind.Function, "project(v, onto)", "The part of v along onto.", "Vector3"),
            new("angle", ScriptApiKind.Function, "angle(a, b)", "The angle between two vectors in degrees (0 to 180)."),
        ],
        ["Time"] =
        [
            new("deltaTime", ScriptApiKind.Property, "deltaTime: number", "Seconds since the last frame (same as Update's dt)."),
            new("elapsedTime", ScriptApiKind.Property, "elapsedTime: number", "Seconds since Play began."),
            new("frameCount", ScriptApiKind.Property, "frameCount: number", "Frames played so far (0 in Start)."),
            new("fixedDeltaTime", ScriptApiKind.Property, "fixedDeltaTime: number", "The physics step in seconds (1/60)."),
            new("unscaledDeltaTime", ScriptApiKind.Property, "unscaledDeltaTime: number", "Real seconds since the last frame, whatever the time scale."),
            new("timeScale", ScriptApiKind.Property, "timeScale: number", "How fast the simulation runs (1 normal, 0 paused; Time.SetTimeScale)."),
            new("realTime", ScriptApiKind.Property, "realTime: number", "Real seconds since Play began (high resolution, never scaled)."),
            new("SetTimeScale", ScriptApiKind.Function, "SetTimeScale(scale)", "Slows, speeds up (up to 100) or pauses (0) scripts' dt, timers, physics and animation; from the next frame."),
            new("OnUpdate", ScriptApiKind.Function, "OnUpdate(function(dt) ... end)", "Runs the function every frame after every script's Update (owned by this script).", "Connection"),
        ],
        ["Navigation"] =
        [
            F("Build", "Build(options?)", "Bakes the walkable grid (Min, Max, CellSize, AgentRadius, AgentHeight, MaxClimb, MaxSlope, IgnoreTags, ...); returns its info."),
            F("GetInfo", "GetInfo()", "The grid's size, walkable cells and areas (baked with the defaults on first use)."),
            F("Update", "Update(center, size)", "Bakes the cells in a box again (a door opened, something was built)."),
            F("SetBlocked", "SetBlocked(center, size, blocked)", "Marks a box not walkable (true) or walkable again (false)."),
            F("FindPath", "FindPath(from, to, options?)", "{ Points, Length, Complete } between points or objects, or nil and why (AllowPartial)."),
            F("IsReachable", "IsReachable(from, to)", "Whether a walk exists: true, or false and why."),
            F("IsWalkable", "IsWalkable(point)", "Whether a point is on walkable ground, and its height."),
            F("GetNearestWalkable", "GetNearestWalkable(point, maxDistance?)", "The nearest walkable point, or nil."),
            F("Raycast", "Raycast(from, to)", "Whether a straight walk from a to b stays on walkable ground."),
            F("DrawPath", "DrawPath(path, color?, seconds?)", "Draws a path's points as debug lines."),
            F("DrawGrid", "DrawGrid(center, radius?, seconds?)", "Draws the walkable cells around a point as debug lines."),
            new("CreateAgent", ScriptApiKind.Function, "CreateAgent(object, options?)", "An agent that walks the object along paths (Speed, Move \"Velocity\"/\"Transform\"/\"None\", FaceYawOffset, ...); call agent:Update(dt) every frame.", "NavAgent"),
        ],
        ["NavAgent"] =
        [
            F("SetDestination", "SetDestination(point)", "Finds a path there now: true, or false and why."),
            F("Follow", "Follow(object, distance?)", "Follows a moving object, stopping about distance studs from it (re-paths as it moves)."),
            F("Patrol", "Patrol(points, options?)", "Walks a route of points (Loop, PingPong, Wait, StartIndex)."),
            F("Stop", "Stop()", "Stops (no destination)."),
            F("Update", "Update(dt?)", "One step: moves the object (by its Move mode) and returns the status and the velocity it wants."),
            F("GetPath", "GetPath()", "The current path's points."),
            new("OnArrived", ScriptApiKind.Function, "OnArrived(function(agent) ... end)", "Runs when it reaches its destination (from Update).", "Connection"),
            new("OnFailed", ScriptApiKind.Function, "OnFailed(function(reason, agent) ... end)", "Runs when no path is found or it is stuck.", "Connection"),
            new("OnWaypoint", ScriptApiKind.Function, "OnWaypoint(function(index, agent) ... end)", "Runs when it passes a corner of its path.", "Connection"),
            F("DrawPath", "DrawPath(color?, seconds?)", "Draws its current path as debug lines."),
            F("Destroy", "Destroy()", "Ends the agent (it also ends with its object or script)."),
            new("Status", ScriptApiKind.Property, "Status: string", "\"Idle\", \"Moving\", \"Arrived\" or \"Failed\" (read-only)."),
            new("RemainingDistance", ScriptApiKind.Property, "RemainingDistance: number", "Studs left along the path (read-only)."),
            new("Velocity", ScriptApiKind.Property, "Velocity: Vector3", "The velocity it wants now (read-only).", "Vector3"),
            new("Destination", ScriptApiKind.Property, "Destination: Vector3", "Where it is going, or nil (read-only).", "Vector3"),
            new("Target", ScriptApiKind.Property, "Target: ScriptObject", "The object it follows, or nil (read-only).", "ScriptObject"),
            new("FailureReason", ScriptApiKind.Property, "FailureReason: string", "Why it failed, or nil (read-only)."),
            new("Speed", ScriptApiKind.Property, "Speed: number", "Top speed in studs per second."),
            new("Acceleration", ScriptApiKind.Property, "Acceleration: number", "How quickly it reaches its speed."),
            new("StoppingDistance", ScriptApiKind.Property, "StoppingDistance: number", "How close to the destination counts as arrived."),
            new("Radius", ScriptApiKind.Property, "Radius: number", "How much room it keeps from other agents."),
            new("Patrolling", ScriptApiKind.Property, "Patrolling: boolean", "Whether it walks a Patrol route (read-only)."),
            new("Exists", ScriptApiKind.Property, "Exists: boolean", "Whether the agent still exists (read-only)."),
            new("Object", ScriptApiKind.Property, "Object: ScriptObject", "The object it walks (read-only).", "ScriptObject"),
        ],
        ["Perception"] =
        [
            F("InFieldOfView", "InFieldOfView(observer, target, degrees, range?)", "Whether a target is within the observer's view cone (and range): inView, angle, distance."),
            F("HasLineOfSight", "HasLineOfSight(from, to, filter?)", "Whether nothing solid is between two points or objects: clear, and what is in the way."),
            F("CanSee", "CanSee(observer, target, options?)", "Range, view cone and line of sight together (FieldOfView, Range, EyeOffset, Filter): seen, info."),
            F("FindVisible", "FindVisible(observer, options?)", "The objects the observer sees now (OnlyTags, IgnoreTags narrow them)."),
            new("CreateSensor", ScriptApiKind.Function, "CreateSensor(observer, options?)", "Watches for objects coming into and out of view (OnDetected, OnLost, Memory, Debug).", "Sensor"),
            F("EmitNoise", "EmitNoise(position, options?)", "A noise listeners within Radius hear next frame (Kind, Source, Loudness, Data)."),
            new("OnNoise", ScriptApiKind.Function, "OnNoise(listener, function(noise, listener) ... end, options?)", "Runs for every noise the listener hears (Hearing multiplies the radius).", "Connection"),
        ],
        ["Sensor"] =
        [
            new("OnDetected", ScriptApiKind.Function, "OnDetected(function(object, sensor) ... end)", "Runs when an object comes into view.", "Connection"),
            new("OnLost", ScriptApiKind.Function, "OnLost(function(object, sensor) ... end)", "Runs when an object is lost from view (after Memory seconds) or destroyed.", "Connection"),
            F("GetDetected", "GetDetected()", "The objects detected now."),
            F("IsDetected", "IsDetected(object)", "Whether this object is detected now."),
            F("Update", "Update()", "Looks now instead of at the next interval."),
            F("Destroy", "Destroy()", "Ends the sensor."),
            new("Enabled", ScriptApiKind.Property, "Enabled: boolean", "Whether it looks (off: everything is lost)."),
            new("Debug", ScriptApiKind.Property, "Debug: boolean", "Draws its view cone and lines to what it sees."),
            new("Range", ScriptApiKind.Property, "Range: number", "How far it sees."),
            new("FieldOfView", ScriptApiKind.Property, "FieldOfView: number", "The view cone's full angle in degrees."),
            new("Count", ScriptApiKind.Property, "Count: number", "How many objects are detected (read-only)."),
            new("Exists", ScriptApiKind.Property, "Exists: boolean", "Whether the sensor still exists (read-only)."),
            new("Observer", ScriptApiKind.Property, "Observer: ScriptObject", "The object that sees (read-only).", "ScriptObject"),
        ],
        ["RandomLibrary"] =
        [
            new("new", ScriptApiKind.Function, "new(seed?)", "A generator: a whole number or string seed gives the same sequence every time.", "Random"),
        ],
        ["Random"] =
        [
            F("Number", "Number(min?, max?)", "A number in 0..1 (1 excluded), 0..max or min..max."),
            F("Integer", "Integer(min, max)", "A whole number from min to max (both included)."),
            F("Bool", "Bool(probability?)", "true with that probability (0.5)."),
            F("Choice", "Choice(list)", "One item of a list."),
            F("Weighted", "Weighted(entries)", "An item by weight: { {item, weight}, ... } or { item = weight }."),
            F("Shuffle", "Shuffle(list)", "Shuffles the list in place (and returns it)."),
            new("UnitVector", ScriptApiKind.Function, "UnitVector()", "A random direction of length 1.", "Vector3"),
            new("PointInBox", ScriptApiKind.Function, "PointInBox(min, max)", "A point inside a box.", "Vector3"),
            new("PointInCircle", ScriptApiKind.Function, "PointInCircle(radius)", "A point (x, 0, z) within radius of the origin.", "Vector3"),
            new("Clone", ScriptApiKind.Function, "Clone()", "A generator that continues from this one's state.", "Random"),
            F("GetState", "GetState()", "The state as plain data (for GameSave)."),
            F("SetState", "SetState(state)", "Continues from a state GetState gave."),
            new("Seed", ScriptApiKind.Property, "Seed", "The seed it started from (read-only)."),
        ],
        ["Noise"] =
        [
            F("Perlin", "Perlin(x, y?, z?, seed?)", "Smooth noise in about -1..1; the same seed, the same field."),
            F("Fractal", "Fractal(x, y?, z?, options?)", "Octaves of Perlin noise (Octaves, Persistence, Lacunarity, Scale, Seed), about -1..1."),
        ],
        ["PathLibrary"] =
        [
            new("new", ScriptApiKind.Function, "new(points, options?)", "A path through points (Type \"CatmullRom\", \"Linear\" or \"Bezier\"; Closed).", "Path"),
            new("Follow", ScriptApiKind.Function, "Follow(object, path, options?)", "Moves an object along a path every frame (Speed, Loop, PingPong, Orient, Mode).", "PathFollower"),
        ],
        ["Path"] =
        [
            new("GetPointAtDistance", ScriptApiKind.Function, "GetPointAtDistance(distance)", "The point that far along the path.", "Vector3"),
            new("GetTangentAtDistance", ScriptApiKind.Function, "GetTangentAtDistance(distance)", "The direction of travel there (length 1).", "Vector3"),
            new("GetPointAt", ScriptApiKind.Function, "GetPointAt(t)", "The point at t (0 start, 1 end; by distance).", "Vector3"),
            new("GetTangentAt", ScriptApiKind.Function, "GetTangentAt(t)", "The direction of travel at t.", "Vector3"),
            F("GetClosestDistance", "GetClosestDistance(point)", "The distance along the path of the point nearest to a point, and that point."),
            F("GetControlPoints", "GetControlPoints()", "The points it was made from (copies)."),
            new("Reverse", ScriptApiKind.Function, "Reverse()", "The same curve the other way.", "Path"),
            F("Draw", "Draw(color?, seconds?)", "Draws the curve as debug lines."),
            new("Length", ScriptApiKind.Property, "Length: number", "Its length in studs (read-only)."),
            new("Closed", ScriptApiKind.Property, "Closed: boolean", "Whether it loops round (read-only)."),
            new("Type", ScriptApiKind.Property, "Type: string", "\"CatmullRom\", \"Linear\" or \"Bezier\" (read-only)."),
            new("Count", ScriptApiKind.Property, "Count: number", "How many points it was made from (read-only)."),
        ],
        ["PathFollower"] =
        [
            F("Pause", "Pause()", "Stops where it is (resume later)."),
            F("Resume", "Resume()", "Goes on."),
            F("Stop", "Stop()", "Ends it for good."),
            F("Reverse", "Reverse()", "Turns round where it is."),
            new("OnFinished", ScriptApiKind.Function, "OnFinished(function(follower) ... end)", "Runs when it reaches the end (not looping).", "Connection"),
            new("OnLooped", ScriptApiKind.Function, "OnLooped(function(follower) ... end)", "Runs each time it loops or turns round.", "Connection"),
            new("Speed", ScriptApiKind.Property, "Speed: number", "Studs per second."),
            new("Distance", ScriptApiKind.Property, "Distance: number", "How far along the path it is."),
            new("Direction", ScriptApiKind.Property, "Direction: number", "1 forward, -1 backward (read-only)."),
            new("Paused", ScriptApiKind.Property, "Paused: boolean", "Whether it is paused (read-only)."),
            new("Finished", ScriptApiKind.Property, "Finished: boolean", "Whether it ended (read-only)."),
            new("Object", ScriptApiKind.Property, "Object: ScriptObject", "The object it moves (read-only).", "ScriptObject"),
            new("Path", ScriptApiKind.Property, "Path: Path", "The path (read-only).", "Path"),
        ],
        ["GameTime"] =
        [
            new("Day", ScriptApiKind.Property, "Day: number", "The day, from 1 (read-only)."),
            new("Hour", ScriptApiKind.Property, "Hour: number", "The hour of the day, 0 up to 24 with its fraction (read-only)."),
            new("Minute", ScriptApiKind.Property, "Minute: number", "The minute of the hour (read-only)."),
            new("TimeOfDay", ScriptApiKind.Property, "TimeOfDay: number", "0 at midnight, 0.5 at noon (read-only)."),
            new("TotalHours", ScriptApiKind.Property, "TotalHours: number", "Game hours since day 1, 00:00 (read-only)."),
            new("DayLength", ScriptApiKind.Property, "DayLength: number", "Real seconds per game day at scale 1."),
            new("Scale", ScriptApiKind.Property, "Scale: number", "How fast the clock runs (1 normal, 0 stopped)."),
            new("Paused", ScriptApiKind.Property, "Paused: boolean", "Whether the clock stands still."),
            F("Pause", "Pause()", "Stops the clock."),
            F("Resume", "Resume()", "Starts it again."),
            F("Set", "Set(day, hour)", "Jumps to a day and hour."),
            new("At", ScriptApiKind.Function, "At(hour, function(day) ... end)", "Runs every day when the clock passes that hour.", "Connection"),
            new("After", ScriptApiKind.Function, "After(hours, function() ... end)", "Runs once after that many game hours.", "Connection"),
            new("Every", ScriptApiKind.Function, "Every(hours, function() ... end)", "Runs every that many game hours.", "Connection"),
            new("OnHourChanged", ScriptApiKind.Function, "OnHourChanged(function(hour, day) ... end)", "Runs when a new hour begins.", "Connection"),
            new("OnDayChanged", ScriptApiKind.Function, "OnDayChanged(function(day) ... end)", "Runs when a new day begins.", "Connection"),
            F("IsBetween", "IsBetween(fromHour, toHour)", "Whether the time of day is in that range (wraps past midnight)."),
            F("Clock", "Clock()", "The time as \"HH:MM\"."),
            F("GetState", "GetState()", "The clock as plain data (for GameSave)."),
            F("SetState", "SetState(state)", "Puts a saved clock back."),
        ],
        ["GameSave"] =
        [
            F("Save", "Save(slot, data, options?)", "Saves plain data in a slot (Version, Metadata): true, or false and why."),
            F("Load", "Load(slot)", "The data and its info { Slot, Version, SavedAt, Metadata, Recovered }, or nil and why."),
            F("Exists", "Exists(slot)", "Whether the slot has a save."),
            F("Delete", "Delete(slot)", "Removes a slot: true, or false and why."),
            F("List", "List()", "Every slot's info."),
            F("GetInfo", "GetInfo(slot)", "A slot's info (without loading its data), or nil and why."),
            new("Available", ScriptApiKind.Property, "Available: boolean", "Whether saving is possible (the game has a project folder; read-only)."),
        ],
        ["Cameras"] =
        [
            new("Create", ScriptApiKind.Function, "Create(name, options?)", "A named camera (Position, Rotation, LookAt, FieldOfView).", "GameCamera"),
            new("Find", ScriptApiKind.Function, "Find(name)", "A camera by name, or nil.", "GameCamera"),
            F("GetAll", "GetAll()", "Every named camera."),
            F("SetActive", "SetActive(camera?, options?)", "Shows a camera (nil: the gameplay camera), blending over Blend seconds."),
            new("GetActive", ScriptApiKind.Function, "GetActive()", "The camera shown, or nil for the gameplay camera.", "GameCamera"),
            F("Shake", "Shake(intensity, duration?, frequency?)", "Shakes what is shown, fading out."),
            new("OnActivated", ScriptApiKind.Function, "OnActivated(function(camera, previous) ... end)", "Runs (next frame) when the shown camera changes.", "Connection"),
            F("CreateFeed", "CreateFeed(camera, options?)", "Renders a camera into an image for GUI Images (\"camera:Name\"): true, or false and why."),
            new("FeedsSupported", ScriptApiKind.Property, "FeedsSupported: boolean", "Whether this renderer can draw camera feeds (read-only)."),
        ],
        ["GameCamera"] =
        [
            new("Name", ScriptApiKind.Property, "Name: string", "Its name (read-only)."),
            new("Position", ScriptApiKind.Property, "Position: Vector3", "Where it is.", "Vector3"),
            new("Rotation", ScriptApiKind.Property, "Rotation: Vector3", "x: pitch, y: yaw (degrees); it never rolls.", "Vector3"),
            new("Forward", ScriptApiKind.Property, "Forward: Vector3", "Where it looks (read-only).", "Vector3"),
            new("FieldOfView", ScriptApiKind.Property, "FieldOfView: number", "Vertical field of view, 10 to 120 degrees."),
            new("Active", ScriptApiKind.Property, "Active: boolean", "Whether it is the camera shown (read-only)."),
            new("Exists", ScriptApiKind.Property, "Exists: boolean", "Whether it still exists (read-only)."),
            new("Following", ScriptApiKind.Property, "Following: boolean", "Whether it follows an object (read-only)."),
            new("HasFeed", ScriptApiKind.Property, "HasFeed: boolean", "Whether it renders into an image (read-only)."),
            F("LookAt", "LookAt(point)", "Turns it to look at a point or object."),
            F("Follow", "Follow(object, options?)", "Follows an object every frame (Offset, LookAt, LookOffset, Smoothing)."),
            F("StopFollowing", "StopFollowing()", "Stays where it is."),
            new("PlayPath", ScriptApiKind.Function, "PlayPath(path, options?)", "Moves along a Path (Duration or Speed, LookAt, Loop, Easing).", "CameraPlayback"),
            F("StopPath", "StopPath()", "Stops its path."),
            F("CreateFeed", "CreateFeed(options?)", "Renders it into an image for GUI Images (\"camera:Name\"; Width, Height)."),
            F("Destroy", "Destroy()", "Removes it (the gameplay camera is shown if it was active)."),
        ],
        ["CameraPlayback"] =
        [
            F("Stop", "Stop()", "Stops the movement."),
            new("OnFinished", ScriptApiKind.Function, "OnFinished(function(camera) ... end)", "Runs when the path ends.", "Connection"),
            new("Playing", ScriptApiKind.Property, "Playing: boolean", "Whether it still moves (read-only)."),
            new("Progress", ScriptApiKind.Property, "Progress: number", "0 at the start, 1 at the end (read-only)."),
        ],
        ["Particles"] =
        [
            new("CreateEmitter", ScriptApiKind.Function, "CreateEmitter(options?)", "A particle emitter (Position, Area, Rate, Lifetime, Velocity, Spread, Gravity, Color, Size, Length, FollowCamera, MaxParticles).", "ParticleEmitter"),
            new("Count", ScriptApiKind.Property, "Count: number", "Particles alive in the session (read-only)."),
        ],
        ["ParticleEmitter"] =
        [
            new("Position", ScriptApiKind.Property, "Position: Vector3", "Where it spawns (with FollowCamera: an offset from the camera).", "Vector3"),
            new("Area", ScriptApiKind.Property, "Area: Vector3", "The size of the box particles spawn in.", "Vector3"),
            new("Velocity", ScriptApiKind.Property, "Velocity: Vector3", "Particles' starting velocity.", "Vector3"),
            new("Rate", ScriptApiKind.Property, "Rate: number", "Particles per second."),
            new("Lifetime", ScriptApiKind.Property, "Lifetime: number", "Seconds a particle lives."),
            new("Spread", ScriptApiKind.Property, "Spread: number", "Random speed in any direction, as a fraction of Velocity."),
            new("Gravity", ScriptApiKind.Property, "Gravity: number", "How much the world's gravity pulls particles."),
            new("Color", ScriptApiKind.Property, "Color: string", "\"#RRGGBB\"."),
            new("Size", ScriptApiKind.Property, "Size: number", "Width in studs."),
            new("Length", ScriptApiKind.Property, "Length: number", "Streak length along the motion (0: square particles)."),
            new("FollowCamera", ScriptApiKind.Property, "FollowCamera: boolean", "Spawn around the camera (rain, snow)."),
            new("MaxParticles", ScriptApiKind.Property, "MaxParticles: number", "Most particles alive at once."),
            new("Enabled", ScriptApiKind.Property, "Enabled: boolean", "Whether it spawns particles."),
            new("Count", ScriptApiKind.Property, "Count: number", "Particles alive (read-only)."),
            new("Exists", ScriptApiKind.Property, "Exists: boolean", "Whether it still exists (read-only)."),
            F("Burst", "Burst(count)", "Spawns that many particles now."),
            F("Clear", "Clear()", "Removes its particles."),
            F("Destroy", "Destroy()", "Removes the emitter."),
        ],
        ["DebugDraw"] =
        [
            F("Line", "Line(a, b, color?, seconds?)", "A line from a to b (\"#RRGGBB\"; 0 seconds: this frame)."),
            F("Ray", "Ray(origin, direction, color?, seconds?)", "A line from origin along direction."),
            F("Sphere", "Sphere(center, radius, color?, seconds?)", "Three circles around a point."),
            F("Box", "Box(center, size, color?, seconds?, rotation?)", "A box's edges."),
            F("Point", "Point(point, size?, color?, seconds?)", "A small cross."),
            F("Path", "Path(points, color?, seconds?)", "Lines through points (a list, a Path or a navigation path)."),
            F("Clear", "Clear()", "Removes every debug line."),
            new("Count", ScriptApiKind.Property, "Count: number", "Lines shown now (read-only)."),
        ],
        ["Input"] =
        [
            new("IsKeyDown", ScriptApiKind.Function, "IsKeyDown(name)", "Returns true while the named key is held."),
            new("IsKeyPressed", ScriptApiKind.Function, "IsKeyPressed(name)", "Returns true only in the frame the key went down."),
            new("IsKeyReleased", ScriptApiKind.Function, "IsKeyReleased(name)", "Returns true only in the frame the key went up."),
            new("IsMouseButtonDown", ScriptApiKind.Function, "IsMouseButtonDown(name)", "Returns true while the mouse button is held (\"Left\", \"Right\", \"Middle\")."),
            new("IsMouseButtonPressed", ScriptApiKind.Function, "IsMouseButtonPressed(name)", "Returns true only in the frame the button went down."),
            new("IsMouseButtonReleased", ScriptApiKind.Function, "IsMouseButtonReleased(name)", "Returns true only in the frame the button went up."),
            new("mouseX", ScriptApiKind.Property, "mouseX: number", "Cursor x in window pixels (from the left)."),
            new("mouseY", ScriptApiKind.Property, "mouseY: number", "Cursor y in window pixels (from the top)."),
            new("mouseDeltaX", ScriptApiKind.Property, "mouseDeltaX: number", "Cursor movement this frame, x."),
            new("mouseDeltaY", ScriptApiKind.Property, "mouseDeltaY: number", "Cursor movement this frame, y (down is positive)."),
            new("scrollDelta", ScriptApiKind.Property, "scrollDelta: number", "Vertical scroll this frame."),
            new("GetMousePosition", ScriptApiKind.Function, "GetMousePosition()", "Returns the cursor's x, y in window pixels."),
            new("GetAxis", ScriptApiKind.Function, "GetAxis(negativeKey, positiveKey)", "-1, 0 or 1 from two held keys (GetAxis(\"A\", \"D\"))."),
            new("SetMouseLocked", ScriptApiKind.Function, "SetMouseLocked(locked)", "Locks and hides the cursor for mouse look (mouseDeltaX / Y keep moving); Stop frees it."),
            new("MouseLocked", ScriptApiKind.Property, "MouseLocked: boolean", "Whether a script has locked the mouse (read-only)."),
            new("IsTyping", ScriptApiKind.Property, "IsTyping: boolean", "Whether a GUI TextBox has the keyboard (scripts then see no keys; read-only)."),
        ],
        ["UI"] =
        [
            new("Find", ScriptApiKind.Function, "Find(name)", "The first GUI element with this name (tree order), or nil.", "GuiElement"),
            new("FindAll", ScriptApiKind.Function, "FindAll(name)", "Every GUI element with this name, as a list (empty when none)."),
            new("FindPath", ScriptApiKind.Function, "FindPath(path)", "The element at a path of names from the Canvas (\"HUD/Menu/Play\"), or nil.", "GuiElement"),
            new("FindById", ScriptApiKind.Function, "FindById(id)", "The GUI element with this Id, or nil.", "GuiElement"),
            new("Create", ScriptApiKind.Function, "Create(type, name? | options?, parent?)", "A new \"Frame\", \"Text\", \"Button\", \"Image\" or \"TextBox\" (gone at Stop). Options: Name, Parent, Position, Size, Anchor, Text, TextColor, Color, FontSize, Visible, ZOrder, Opacity, Image, Enabled, HorizontalAlignment, VerticalAlignment, PlaceholderText, MaxLength, ClearOnSubmit.", "GuiElement"),
            new("MeasureText", ScriptApiKind.Function, "MeasureText(text, fontSize?)", "Returns the text's width and one line's height in pixels in the game GUI's font (for wrapping and fitting)."),
            new("Root", ScriptApiKind.Property, "Root", "The Canvas every top-level element is inside.", "GuiRoot"),
            new("GetCanvasRect", ScriptApiKind.Function, "GetCanvasRect()", "Returns the game view's x, y, width, height in window pixels (to place GUI at a Camera.WorldToScreen point)."),
        ],
        ["GuiRoot"] =
        [
            new("GetChildren", ScriptApiKind.Function, "GetChildren()", "The top-level elements, in order."),
            new("FindChild", ScriptApiKind.Function, "FindChild(name)", "The first top-level element with this name, or nil.", "GuiElement"),
        ],
        ["GuiElement"] =
        [
            new("Id", ScriptApiKind.Property, "Id: number", "The element's Id (read-only)."),
            new("Name", ScriptApiKind.Property, "Name: string", "The element's name (read-only)."),
            new("Type", ScriptApiKind.Property, "Type: string", "\"Frame\", \"Text\", \"Button\", \"Image\" or \"TextBox\" (read-only)."),
            new("Visible", ScriptApiKind.Property, "Visible: boolean", "Whether it is drawn (and, for a Button, clickable)."),
            new("Parent", ScriptApiKind.Property, "Parent", "The element it is inside, or UI.Root (set it to move it).", "GuiElement"),
            new("Destroy", ScriptApiKind.Function, "Destroy()", "Removes it and everything inside it (this Play session)."),
            new("SetParent", ScriptApiKind.Function, "SetParent(parent)", "Moves it inside another element or UI.Root."),
            new("GetChildren", ScriptApiKind.Function, "GetChildren()", "The elements directly inside it, in order."),
            new("FindChild", ScriptApiKind.Function, "FindChild(name)", "The first element directly inside it with this name, or nil.", "GuiElement"),
            new("ZOrder", ScriptApiKind.Property, "ZOrder: number", "Drawing order among its siblings (higher on top; a whole number)."),
            new("Text", ScriptApiKind.Property, "Text: string", "The label of a Text or Button; what is typed in a TextBox."),
            new("FontSize", ScriptApiKind.Property, "FontSize: number", "Text size in pixels, 8 to 72 (Text, Button and TextBox)."),
            new("Enabled", ScriptApiKind.Property, "Enabled: boolean", "Whether a Button can be clicked."),
            new("SetPosition", ScriptApiKind.Function, "SetPosition(xScale, xOffset, yScale, yOffset)", "Moves it: a fraction of the parent plus pixels, per axis."),
            new("SetSize", ScriptApiKind.Function, "SetSize(widthScale, widthOffset, heightScale, heightOffset)", "Resizes it: a fraction of the parent plus pixels, per axis."),
            new("GetPosition", ScriptApiKind.Function, "GetPosition()", "Returns xScale, xOffset, yScale, yOffset."),
            new("GetSize", ScriptApiKind.Function, "GetSize()", "Returns widthScale, widthOffset, heightScale, heightOffset."),
            new("GetAnchor", ScriptApiKind.Function, "GetAnchor()", "Returns the anchor point x, y (0 to 1)."),
            new("SetAnchor", ScriptApiKind.Function, "SetAnchor(x, y)", "Which point of it sits at its Position (0 to 1 each)."),
            new("Color", ScriptApiKind.Property, "Color: string", "Background colour \"#RRGGBB\" (Frame, Button and TextBox)."),
            new("Opacity", ScriptApiKind.Property, "Opacity: number", "A Frame's opacity, 0 (invisible) to 1."),
            new("TextColor", ScriptApiKind.Property, "TextColor: string", "The text colour \"#RRGGBB\" (Text, Button and TextBox)."),
            new("HorizontalAlignment", ScriptApiKind.Property, "HorizontalAlignment: string", "\"Left\", \"Center\" or \"Right\" (Text)."),
            new("VerticalAlignment", ScriptApiKind.Property, "VerticalAlignment: string", "\"Top\", \"Middle\" or \"Bottom\" (Text)."),
            new("Image", ScriptApiKind.Property, "Image: string", "An Image's picture: a project path (\"Assets/Images/Coin.png\", PNG or JPEG), or \"\" for none."),
            new("OnClick", ScriptApiKind.Function, "OnClick(function() ... end)", "Runs the function each time the Button is clicked (this Play session).", "Connection"),
            new("OnPressed", ScriptApiKind.Function, "OnPressed(function() ... end)", "Runs when a press begins on the Button.", "Connection"),
            new("OnReleased", ScriptApiKind.Function, "OnReleased(function() ... end)", "Runs when the press on the Button ends (released anywhere, or let go).", "Connection"),
            new("OnHoverEnter", ScriptApiKind.Function, "OnHoverEnter(function() ... end)", "Runs when the pointer comes over the Button.", "Connection"),
            new("OnHoverExit", ScriptApiKind.Function, "OnHoverExit(function() ... end)", "Runs when the pointer leaves the Button.", "Connection"),
            new("PlaceholderText", ScriptApiKind.Property, "PlaceholderText: string", "Shown dimmed while a TextBox is empty (\"Press / to chat\")."),
            new("MaxLength", ScriptApiKind.Property, "MaxLength: number", "The longest text a TextBox takes, 1 to 1000 (default 200)."),
            new("ClearOnSubmit", ScriptApiKind.Property, "ClearOnSubmit: boolean", "Whether a TextBox empties itself when Enter submits it."),
            new("Focused", ScriptApiKind.Property, "Focused: boolean", "Whether the TextBox has the keyboard (read-only)."),
            new("Focus", ScriptApiKind.Function, "Focus()", "Gives the TextBox the keyboard: true, or false and why."),
            new("Release", ScriptApiKind.Function, "Release()", "The TextBox lets go of the keyboard."),
            new("OnSubmit", ScriptApiKind.Function, "OnSubmit(function(text) ... end)", "Runs when Enter is pressed in the TextBox (next frame).", "Connection"),
            new("OnFocusLost", ScriptApiKind.Function, "OnFocusLost(function(submitted) ... end)", "Runs when the TextBox lets go of the keyboard (Enter, Escape, a click elsewhere).", "Connection"),
            new("OnFocused", ScriptApiKind.Function, "OnFocused(function() ... end)", "Runs when the TextBox gets the keyboard.", "Connection"),
        ],
        ["Chat"] =
        [
            F("Send", "Send(text, channel?)", "The local player says text (default channel \"General\"): the message's id, or nil and why (too fast, empty)."),
            F("Post", "Post(text, options?)", "The game's own message, Kind \"System\" (options: a channel name, or Channel and SenderName for an NPC): its id, or nil and why."),
            new("OnMessage", ScriptApiKind.Function, "OnMessage(function(message) ... end, channel?)", "Runs for every delivered message (of one channel when given), the frame after it was sent.", "Connection"),
            new("SetFilter", ScriptApiKind.Function, "SetFilter(function(message) ... end)", "Sees every message first: nil or true keeps it, false withholds it, a string replaces its text.", "Connection"),
            new("CreateChannel", ScriptApiKind.Function, "CreateChannel(name, options?)", "A new channel (MaxLength, HistorySize, PlayersCanSend).", "ChatChannel"),
            new("GetChannel", ScriptApiKind.Function, "GetChannel(name)", "A channel by name, or nil.", "ChatChannel"),
            F("GetChannels", "GetChannels()", "Every channel, \"General\" first."),
            F("GetHistory", "GetHistory(channel?, count?)", "A channel's recent messages, oldest first."),
            F("Mute", "Mute(senderId)", "Hides a sender's messages from the local player (a message's Sender works too)."),
            F("Unmute", "Unmute(senderId)", "Shows a muted sender's messages again."),
            F("IsMuted", "IsMuted(senderId)", "Whether a sender is muted."),
            F("Block", "Block(senderId)", "As Mute; a networked transport would also stop them seeing yours (Wisko is local only)."),
            F("Unblock", "Unblock(senderId)", "Undoes Block."),
            F("IsBlocked", "IsBlocked(senderId)", "Whether a sender is blocked."),
            F("SetPlayerName", "SetPlayerName(name)", "The local player's chat name (not a reserved name such as \"System\"): true, or false and why."),
            F("SetRateLimit", "SetRateLimit(burst, perSecond)", "How fast the local player may send (default: 5 at once, then 1 per second)."),
            F("EnableDefaultUI", "EnableDefaultUI(options?)", "The ready-made chat window (Channel, FontSize, Width, Height, FocusKey, Placeholder)."),
            F("DisableDefaultUI", "DisableDefaultUI()", "Removes the ready-made chat window."),
            new("LocalPlayer", ScriptApiKind.Property, "LocalPlayer: table", "{ Id, Name } of the person playing (read-only).", "ChatSender"),
            new("Transport", ScriptApiKind.Property, "Transport: string", "\"Local\": messages never leave this machine (read-only)."),
            new("IsNetworked", ScriptApiKind.Property, "IsNetworked: boolean", "false: Wisko has no networking (read-only)."),
            new("DefaultChannel", ScriptApiKind.Property, "DefaultChannel: string", "\"General\" (read-only)."),
        ],
        ["ChatChannel"] =
        [
            new("Name", ScriptApiKind.Property, "Name: string", "The channel's name (read-only)."),
            new("MaxLength", ScriptApiKind.Property, "MaxLength: number", "The longest message (read-only)."),
            new("HistorySize", ScriptApiKind.Property, "HistorySize: number", "How many recent messages it keeps (read-only)."),
            new("PlayersCanSend", ScriptApiKind.Property, "PlayersCanSend: boolean", "Whether players may send here (read-only)."),
            new("Count", ScriptApiKind.Property, "Count: number", "Messages in its history (read-only)."),
            F("Send", "Send(text)", "The local player says text here: the message's id, or nil and why."),
            F("Post", "Post(text, options?)", "The game's own message here (SenderName for an NPC): its id, or nil and why."),
            new("OnMessage", ScriptApiKind.Function, "OnMessage(function(message) ... end)", "Runs for every message delivered here.", "Connection"),
            F("GetHistory", "GetHistory(count?)", "Its recent messages, oldest first."),
        ],
        ["ChatMessage"] =
        [
            new("Id", ScriptApiKind.Property, "Id: number", "The message's number (unique in the session)."),
            new("Text", ScriptApiKind.Property, "Text: string", "What was said (one line, cleaned)."),
            new("Channel", ScriptApiKind.Property, "Channel: string", "The channel's name."),
            new("Sender", ScriptApiKind.Property, "Sender: table", "{ Id, Name } of who sent it.", "ChatSender"),
            new("Kind", ScriptApiKind.Property, "Kind: string", "\"Player\" (typed by a player) or \"System\" (the game's own Chat.Post)."),
            new("Time", ScriptApiKind.Property, "Time: number", "Seconds since Play started when it was sent."),
            new("Timestamp", ScriptApiKind.Property, "Timestamp: number", "When it was sent, in Unix seconds (UTC)."),
        ],
        ["ChatSender"] =
        [
            new("Id", ScriptApiKind.Property, "Id: string", "\"local\" (the person playing), \"system\" or \"npc:Name\"."),
            new("Name", ScriptApiKind.Property, "Name: string", "The name shown."),
        ],
        ["Connection"] =
        [
            new("Connected", ScriptApiKind.Property, "Connected: boolean", "Whether the function is still connected (read-only)."),
            new("Disconnect", ScriptApiKind.Function, "Disconnect()", "Stops the function from running (doing it again does nothing)."),
        ],
        ["Events"] =
        [
            new("On", ScriptApiKind.Function, "On(name, function(...) ... end)", "Runs the function for every event of this name (this script's).", "Connection"),
            new("Emit", ScriptApiKind.Function, "Emit(name, ...)", "Sends an event with values (nil, booleans, numbers, strings, vectors, objects, GUI elements, plain tables: copied) to every script; delivered next frame."),
        ],
        ["Timer"] =
        [
            new("After", ScriptApiKind.Function, "After(seconds, function(...) ... end, ...)", "Runs the function once, seconds of game time from now (0: next frame), with the extra values.", "TimerHandle"),
            new("Every", ScriptApiKind.Function, "Every(seconds, function(...) ... end, ...)", "Runs the function every seconds of game time (at most once per frame), with the extra values.", "TimerHandle"),
        ],
        ["TimerHandle"] =
        [
            new("Active", ScriptApiKind.Property, "Active: boolean", "Whether the timer will still run (read-only)."),
            new("Remaining", ScriptApiKind.Property, "Remaining: number", "Seconds until it next runs (0 once it has ended; read-only)."),
            new("Paused", ScriptApiKind.Property, "Paused: boolean", "Whether it is paused (read-only)."),
            new("Cancel", ScriptApiKind.Function, "Cancel()", "Stops the timer (doing it again does nothing)."),
            new("Pause", ScriptApiKind.Function, "Pause()", "Holds the timer where it is."),
            new("Resume", ScriptApiKind.Function, "Resume()", "Lets a paused timer run on."),
            new("Restart", ScriptApiKind.Function, "Restart()", "A full delay or interval from now; false when the timer has ended."),
        ],
        ["Physics"] =
        [
            new("Raycast", ScriptApiKind.Function, "Raycast(x, y, z, dx, dy, dz, maxDistance, filter?)", "The nearest Part or environment along a ray, or nil (vectors may replace the numbers; filter: an object to ignore, or { Ignore, IgnoreTags, OnlyTags, IgnoreTriggers, IgnoreEnvironment, CollisionGroup }).", "RaycastResult"),
            new("RaycastAll", ScriptApiKind.Function, "RaycastAll(x, y, z, dx, dy, dz, maxDistance, filter?)", "Everything along a ray, nearest first (at most 1000 results)."),
            new("SphereCast", ScriptApiKind.Function, "SphereCast(x, y, z, dx, dy, dz, radius, maxDistance, filter?)", "The first thing a moving sphere meets, or nil.", "RaycastResult"),
            new("OverlapSphere", ScriptApiKind.Function, "OverlapSphere(x, y, z, radius, filter?)", "The objects inside a sphere, and whether it reaches the environment."),
            new("OverlapBox", ScriptApiKind.Function, "OverlapBox(x, y, z, sizeX, sizeY, sizeZ, rx?, ry?, rz?, filter?)", "The objects inside a (turned) box, and whether it reaches the environment."),
            new("Gravity", ScriptApiKind.Property, "Gravity: Vector3", "The world's gravity in studs per second squared (set a vector to change it until Stop).", "Vector3"),
            new("SetGroupsCollide", ScriptApiKind.Function, "SetGroupsCollide(groupA, groupB, collide)", "Whether objects of two collision groups touch (every group touches every group until told otherwise)."),
            new("GroupsCollide", ScriptApiKind.Function, "GroupsCollide(groupA, groupB)", "Whether two collision groups touch."),
            new("GetCollisionGroups", ScriptApiKind.Function, "GetCollisionGroups()", "The session's collision groups by name (Default first)."),
            new("CreateWeld", ScriptApiKind.Function, "CreateWeld(a, b?)", "A physical joint keeping a and b (or a and the world) in their current relative pose.", "Joint"),
            new("CreateHinge", ScriptApiKind.Function, "CreateHinge(a, b?, point, axis)", "A physical hinge at a world point around an axis (doors, wheels, windmills); motor and limits optional.", "Joint"),
            new("CreateBallSocket", ScriptApiKind.Function, "CreateBallSocket(a, b?, point)", "A physical joint sharing a world point, free to turn (chains, pendulums, trailers).", "Joint"),
        ],
        ["Joint"] =
        [
            new("Type", ScriptApiKind.Property, "Type: string", "\"Weld\", \"Hinge\" or \"BallSocket\" (read-only)."),
            new("Active", ScriptApiKind.Property, "Active: boolean", "Whether the solver holds it now (enabled, both objects still there; read-only)."),
            new("Destroyed", ScriptApiKind.Property, "Destroyed: boolean", "Whether it was destroyed (read-only)."),
            new("Enabled", ScriptApiKind.Property, "Enabled: boolean", "False lets the objects move freely (it keeps its frames); true holds them again."),
            new("CollideConnected", ScriptApiKind.Property, "CollideConnected: boolean", "Whether the two objects still collide with each other (default false)."),
            new("PartA", ScriptApiKind.Property, "PartA: ScriptObject", "The first object (read-only).", "ScriptObject"),
            new("PartB", ScriptApiKind.Property, "PartB: ScriptObject", "The second object, or nil for a fixed point in the world (read-only).", "ScriptObject"),
            new("Angle", ScriptApiKind.Property, "Angle: number", "A hinge's angle in degrees from where it was made (read-only)."),
            new("MotorEnabled", ScriptApiKind.Property, "MotorEnabled: boolean", "Hinge: drive it at MotorSpeed."),
            new("MotorSpeed", ScriptApiKind.Property, "MotorSpeed: number", "Hinge: the motor's target speed in degrees per second."),
            new("MotorMaxTorque", ScriptApiKind.Property, "MotorMaxTorque: number", "Hinge: the most torque the motor may use."),
            new("LimitsEnabled", ScriptApiKind.Property, "LimitsEnabled: boolean", "Hinge: keep Angle within LowerAngle..UpperAngle."),
            new("LowerAngle", ScriptApiKind.Property, "LowerAngle: number", "Hinge limit in degrees (-180 to 180)."),
            new("UpperAngle", ScriptApiKind.Property, "UpperAngle: number", "Hinge limit in degrees (-180 to 180)."),
            new("Destroy", ScriptApiKind.Function, "Destroy()", "Removes the joint for good (the objects move freely)."),
        ],
        ["Contact"] =
        [
            new("Point", ScriptApiKind.Property, "Point: Vector3", "Where the two touch (read-only).", "Vector3"),
            new("Normal", ScriptApiKind.Property, "Normal: Vector3", "The surface direction pointing toward this object; nil for a trigger (read-only).", "Vector3"),
            new("RelativeVelocity", ScriptApiKind.Property, "RelativeVelocity: Vector3", "How fast this object moves relative to the other at the contact (read-only).", "Vector3"),
            new("IsEnvironment", ScriptApiKind.Property, "IsEnvironment: boolean", "Whether the other side is the environment (read-only)."),
        ],
        ["Camera"] =
        [
            new("Position", ScriptApiKind.Property, "Position: Vector3", "Where the camera is (placing it takes it from the fly controls until Stop).", "Vector3"),
            new("Rotation", ScriptApiKind.Property, "Rotation: Vector3", "x: pitch (-89 to 89, up is positive), y: yaw in degrees, z: always 0.", "Vector3"),
            new("FieldOfView", ScriptApiKind.Property, "FieldOfView: number", "Vertical field of view in degrees (10 to 120)."),
            new("Forward", ScriptApiKind.Property, "Forward: Vector3", "The direction the camera looks (set a direction to turn it).", "Vector3"),
            new("Right", ScriptApiKind.Property, "Right: Vector3", "The camera's right (read-only).", "Vector3"),
            new("Up", ScriptApiKind.Property, "Up: Vector3", "The camera's up (read-only).", "Vector3"),
            new("ViewportWidth", ScriptApiKind.Property, "ViewportWidth: number", "The view's width in pixels (read-only)."),
            new("ViewportHeight", ScriptApiKind.Property, "ViewportHeight: number", "The view's height in pixels (read-only)."),
            new("LookAt", ScriptApiKind.Function, "LookAt(x, y, z)", "Turns the camera to look at a point."),
            new("WorldToScreen", ScriptApiKind.Function, "WorldToScreen(x, y, z)", "Returns the screen x, y of a world point and whether it is on screen."),
            new("ScreenPointToRay", ScriptApiKind.Function, "ScreenPointToRay(x, y)", "Returns the origin and direction of the ray through a screen point (e.g. the mouse)."),
        ],
        ["Atmosphere"] =
        [
            new("Enabled", ScriptApiKind.Property, "Enabled: boolean", "Atmosphere on (off: the plain environment)."),
            new("SkyColor", ScriptApiKind.Property, "SkyColor: string", "\"#RRGGBB\"."),
            new("HorizonColor", ScriptApiKind.Property, "HorizonColor: string", "\"#RRGGBB\"."),
            new("SkyIntensity", ScriptApiKind.Property, "SkyIntensity: number", "0 to 3."),
            new("SunColor", ScriptApiKind.Property, "SunColor: string", "\"#RRGGBB\"."),
            new("SunIntensity", ScriptApiKind.Property, "SunIntensity: number", "0 to 4."),
            new("SunElevation", ScriptApiKind.Property, "SunElevation: number", "Degrees above the horizon, -10 to 90."),
            new("SunHeading", ScriptApiKind.Property, "SunHeading: number", "Compass degrees, 0 to 359."),
            new("SunDiscSize", ScriptApiKind.Property, "SunDiscSize: number", "0 to 3."),
            new("AmbientColor", ScriptApiKind.Property, "AmbientColor: string", "\"#RRGGBB\"."),
            new("AmbientIntensity", ScriptApiKind.Property, "AmbientIntensity: number", "0 to 2."),
            new("FogEnabled", ScriptApiKind.Property, "FogEnabled: boolean", "Fog on or off."),
            new("FogColor", ScriptApiKind.Property, "FogColor: string", "\"#RRGGBB\"."),
            new("FogDensity", ScriptApiKind.Property, "FogDensity: number", "0 to 1."),
            new("FogStart", ScriptApiKind.Property, "FogStart: number", "Distance where fog begins, 0 to 1999."),
            new("FogEnd", ScriptApiKind.Property, "FogEnd: number", "Distance where fog is full, 1 to 2000 (beyond FogStart)."),
            new("Haze", ScriptApiKind.Property, "Haze: number", "Distance haze, 0 to 1."),
            new("Exposure", ScriptApiKind.Property, "Exposure: number", "Brightness in stops, -3 to 3."),
            new("Saturation", ScriptApiKind.Property, "Saturation: number", "0 to 2."),
            new("Contrast", ScriptApiKind.Property, "Contrast: number", "0.5 to 1.5."),
            new("AmbientMode", ScriptApiKind.Property, "AmbientMode: string", "\"Sky\" (ambient from above, GroundColor from below) or \"Flat\" (Universal renderer)."),
            new("GroundColor", ScriptApiKind.Property, "GroundColor: string", "\"#RRGGBB\": ambient light from below in Sky mode (Universal renderer)."),
            new("ReflectionIntensity", ScriptApiKind.Property, "ReflectionIntensity: number", "Sky reflections on smooth and metal surfaces, 0 to 2 (Universal renderer)."),
        ],
        ["Material"] =
        [
            new("Preset", ScriptApiKind.Property, "Preset: string", "The editor preset it matches (\"Plastic\", \"Metal\", \"Glass\", \"Neon\", \"Toon\", ...) or \"Custom\"; setting it applies that preset."),
            new("ApplyPreset", ScriptApiKind.Function, "ApplyPreset(name)", "Applies an editor preset (keeps colour, textures and tiling)."),
            new("Reset", ScriptApiKind.Function, "Reset()", "Back to the default material (a model: its imported materials)."),
            new("Shading", ScriptApiKind.Property, "Shading: string", "\"Lit\", \"SimpleLit\", \"Unlit\" or \"Toon\"."),
            new("Tint", ScriptApiKind.Property, "Tint: string", "\"#RRGGBB\" multiplied with the object's Color (white: unchanged)."),
            new("Metallic", ScriptApiKind.Property, "Metallic: number", "0 (plastic, stone) to 1 (metal)."),
            new("Roughness", ScriptApiKind.Property, "Roughness: number", "0 (mirror smooth) to 1 (matte)."),
            new("Opacity", ScriptApiKind.Property, "Opacity: number", "0 to 1; see-through when AlphaMode is \"Blend\"."),
            new("AlphaMode", ScriptApiKind.Property, "AlphaMode: string", "\"Opaque\", \"Mask\" (cut-out at AlphaCutoff) or \"Blend\" (see-through)."),
            new("AlphaCutoff", ScriptApiKind.Property, "AlphaCutoff: number", "0 to 1 (Mask)."),
            new("EmissiveColor", ScriptApiKind.Property, "EmissiveColor: string", "\"#RRGGBB\": the colour it glows (black: no glow)."),
            new("EmissiveIntensity", ScriptApiKind.Property, "EmissiveIntensity: number", "Glow strength, 0 to 100 (above 1 blooms when Bloom is on)."),
            new("NormalStrength", ScriptApiKind.Property, "NormalStrength: number", "Normal map strength, 0 to 4."),
            new("OcclusionStrength", ScriptApiKind.Property, "OcclusionStrength: number", "Occlusion map strength, 0 to 1."),
            new("ToonBands", ScriptApiKind.Property, "ToonBands: number", "Light bands of Toon shading, 2 to 8."),
            new("Outline", ScriptApiKind.Property, "Outline: boolean", "A drawn outline (stylized; not the editor's selection outline)."),
            new("OutlineColor", ScriptApiKind.Property, "OutlineColor: string", "\"#RRGGBB\"."),
            new("OutlineThickness", ScriptApiKind.Property, "OutlineThickness: number", "In studs, 0.005 to 2."),
            new("FlatShading", ScriptApiKind.Property, "FlatShading: boolean", "Faceted look."),
            new("DoubleSided", ScriptApiKind.Property, "DoubleSided: boolean", "Both sides of its faces drawn."),
            new("CastShadows", ScriptApiKind.Property, "CastShadows: boolean", "It casts shadows."),
            new("ReceiveShadows", ScriptApiKind.Property, "ReceiveShadows: boolean", "Shadows fall on it."),
            new("BaseColorTexture", ScriptApiKind.Property, "BaseColorTexture: string?", "A project image path (\"Assets/Textures/brick.png\") or nil."),
            new("NormalTexture", ScriptApiKind.Property, "NormalTexture: string?", "A project image path or nil (OpenGL convention: green up)."),
            new("MetallicRoughnessTexture", ScriptApiKind.Property, "MetallicRoughnessTexture: string?", "A project image path or nil (green: roughness, blue: metallic)."),
            new("OcclusionTexture", ScriptApiKind.Property, "OcclusionTexture: string?", "A project image path or nil (red: occlusion)."),
            new("EmissiveTexture", ScriptApiKind.Property, "EmissiveTexture: string?", "A project image path or nil."),
            new("UvMode", ScriptApiKind.Property, "UvMode: string", "\"Mesh\" or \"Studs\" (textures tile by world size on primitives)."),
            new("UvScaleX", ScriptApiKind.Property, "UvScaleX: number", "Texture tiling across, 0.01 to 1000."),
            new("UvScaleY", ScriptApiKind.Property, "UvScaleY: number", "Texture tiling down, 0.01 to 1000."),
        ],
        ["Light"] =
        [
            new("Type", ScriptApiKind.Property, "Type: string", "\"Point\", \"Spot\" or \"Directional\" (a new type starts from its defaults)."),
            new("Enabled", ScriptApiKind.Property, "Enabled: boolean", "On or off."),
            new("Color", ScriptApiKind.Property, "Color: string", "\"#RRGGBB\"."),
            new("Intensity", ScriptApiKind.Property, "Intensity: number", "0 to 100000 (a white surface 1 stud away gets Intensity / distance²)."),
            new("Range", ScriptApiKind.Property, "Range: number", "Studs it reaches, 0.1 to 1000 (Point and Spot)."),
            new("Angle", ScriptApiKind.Property, "Angle: number", "Spot cone in degrees, 1 to 179."),
            new("InnerAngle", ScriptApiKind.Property, "InnerAngle: number", "Full-brightness cone in degrees (at most Angle)."),
            new("CastShadows", ScriptApiKind.Property, "CastShadows: boolean", "Shadows from this light (Spot and Point; limited by the quality level)."),
        ],
        ["Graphics"] =
        [
            new("Renderer", ScriptApiKind.Property, "Renderer: string", "\"Universal\" or \"Classic\" (read-only: the project chooses it)."),
            new("Quality", ScriptApiKind.Property, "Quality: string", "\"Low\", \"Medium\", \"High\", \"Ultra\" or \"Custom\"."),
            new("ToneMapping", ScriptApiKind.Property, "ToneMapping: string", "\"None\", \"Neutral\" or \"Filmic\"."),
            new("Bloom", ScriptApiKind.Property, "Bloom: boolean", "Glow around very bright things."),
            new("BloomThreshold", ScriptApiKind.Property, "BloomThreshold: number", "0 to 10."),
            new("BloomIntensity", ScriptApiKind.Property, "BloomIntensity: number", "0 to 5."),
            new("BloomRadius", ScriptApiKind.Property, "BloomRadius: number", "0 to 1."),
            new("Vignette", ScriptApiKind.Property, "Vignette: boolean", "Darker edges."),
            new("VignetteIntensity", ScriptApiKind.Property, "VignetteIntensity: number", "0 to 1."),
            new("AmbientOcclusion", ScriptApiKind.Property, "AmbientOcclusion: boolean", "Soft contact shadows in corners."),
            new("AmbientOcclusionIntensity", ScriptApiKind.Property, "AmbientOcclusionIntensity: number", "0 to 4."),
            new("Temperature", ScriptApiKind.Property, "Temperature: number", "White balance, -1 (cool) to 1 (warm)."),
            new("Tint", ScriptApiKind.Property, "Tint: number", "White balance, -1 (green) to 1 (magenta)."),
            new("Shadows", ScriptApiKind.Property, "Shadows: boolean", "Real-time shadows."),
            new("ShadowDistance", ScriptApiKind.Property, "ShadowDistance: number", "Studs from the camera that the sun's shadows reach, 10 to 2000."),
        ],
        ["RaycastResult"] =
        [
            new("Object", ScriptApiKind.Property, "Object: ScriptObject", "The Part hit, or nil for the environment (read-only).", "ScriptObject"),
            new("IsEnvironment", ScriptApiKind.Property, "IsEnvironment: boolean", "Whether the ray hit the environment, e.g. the baseplate (read-only)."),
            new("Distance", ScriptApiKind.Property, "Distance: number", "How far along the ray the hit is (read-only)."),
            new("Point", ScriptApiKind.Property, "Point: Vector3", "Where the ray hit (read-only x, y, z).", "Vector3"),
            new("Normal", ScriptApiKind.Property, "Normal: Vector3", "The surface direction where it hit (read-only x, y, z).", "Vector3"),
        ],
        ["math"] =
        [
            F("abs", "abs(x)", "Absolute value."),
            F("acos", "acos(x)", "Arc cosine (radians)."),
            F("asin", "asin(x)", "Arc sine (radians)."),
            F("atan", "atan(y, x?)", "Arc tangent (radians)."),
            F("ceil", "ceil(x)", "Smallest integer >= x."),
            F("clamp", "clamp(x, min, max)", "x kept within min..max (Wisko helper)."),
            F("cos", "cos(x)", "Cosine of x (radians)."),
            F("deg", "deg(x)", "Radians to degrees."),
            F("exp", "exp(x)", "e to the power x."),
            F("floor", "floor(x)", "Largest integer <= x."),
            F("fmod", "fmod(x, y)", "Remainder of x / y (towards zero)."),
            C("huge", "huge", "Infinity."),
            F("lerp", "lerp(a, b, t)", "a + (b - a) * t (Wisko helper)."),
            F("log", "log(x, base?)", "Logarithm (natural by default)."),
            F("max", "max(x, ...)", "Largest argument."),
            C("maxinteger", "maxinteger", "Largest integer."),
            F("min", "min(x, ...)", "Smallest argument."),
            C("mininteger", "mininteger", "Smallest integer."),
            F("modf", "modf(x)", "Integral and fractional parts."),
            C("pi", "pi", "The value of pi."),
            F("rad", "rad(x)", "Degrees to radians."),
            F("random", "random(m?, n?)", "Random number: [0,1), [1,m] or [m,n]."),
            F("randomseed", "randomseed(x?, y?)", "Seeds math.random (one generator for every script in the game)."),
            F("round", "round(x)", "The nearest whole number, halves away from zero (Wisko helper)."),
            F("sign", "sign(x)", "-1, 0 or 1 (Wisko helper)."),
            F("sin", "sin(x)", "Sine of x (radians)."),
            F("sqrt", "sqrt(x)", "Square root."),
            F("tan", "tan(x)", "Tangent of x (radians)."),
            F("tointeger", "tointeger(x)", "x as an integer, or nil."),
            F("type", "type(x)", "\"integer\", \"float\" or nil."),
            F("ult", "ult(m, n)", "Unsigned integer comparison m < n."),
        ],
        ["string"] =
        [
            F("byte", "byte(s, i?, j?)", "Character codes of s[i..j]."),
            F("char", "char(...)", "String from character codes."),
            F("find", "find(s, pattern, init?, plain?)", "Start and end of the first match, or nil."),
            F("format", "format(fmt, ...)", "Formatted string (like C printf)."),
            F("gmatch", "gmatch(s, pattern)", "Iterator over all matches."),
            F("gsub", "gsub(s, pattern, repl, n?)", "Copy with matches replaced; also returns the count."),
            F("len", "len(s)", "Length in bytes."),
            F("lower", "lower(s)", "Lowercase copy."),
            F("match", "match(s, pattern, init?)", "Captures of the first match, or nil."),
            F("pack", "pack(fmt, ...)", "Binary-packs values into a string."),
            F("packsize", "packsize(fmt)", "Size of a pack format."),
            F("rep", "rep(s, n, sep?)", "s repeated n times."),
            F("reverse", "reverse(s)", "Reversed copy (bytes)."),
            F("sub", "sub(s, i, j?)", "Substring s[i..j]."),
            F("unpack", "unpack(fmt, s, pos?)", "Values packed with string.pack."),
            F("upper", "upper(s)", "Uppercase copy."),
        ],
        ["table"] =
        [
            F("concat", "concat(t, sep?, i?, j?)", "Joins t[i..j] into a string."),
            F("insert", "insert(t, pos?, value)", "Inserts a value (at the end by default)."),
            F("move", "move(a1, f, e, t, a2?)", "Copies a1[f..e] into a2 at t."),
            F("pack", "pack(...)", "Table of the arguments with field n."),
            F("remove", "remove(t, pos?)", "Removes and returns an element (the last by default)."),
            F("sort", "sort(t, comp?)", "Sorts t in place."),
            F("unpack", "unpack(t, i?, j?)", "Returns t[i..j] as values."),
        ],
        ["utf8"] =
        [
            F("char", "char(...)", "UTF-8 string from code points."),
            C("charpattern", "charpattern", "Pattern matching one UTF-8 character."),
            F("codepoint", "codepoint(s, i?, j?)", "Code points of s[i..j]."),
            F("codes", "codes(s)", "Iterator over positions and code points."),
            F("len", "len(s, i?, j?)", "Number of UTF-8 characters."),
            F("offset", "offset(s, n, i?)", "Byte position of the n-th character."),
        ],
        ["coroutine"] =
        [
            F("close", "close(co)", "Closes a suspended or dead coroutine."),
            F("create", "create(f)", "A new coroutine running f."),
            F("isyieldable", "isyieldable(co?)", "Whether the coroutine can yield."),
            F("resume", "resume(co, ...)", "Starts or continues co; returns true and its values, or false and an error."),
            F("running", "running()", "The running coroutine, and whether it is the main one."),
            F("status", "status(co)", "\"suspended\", \"running\", \"normal\" or \"dead\"."),
            F("wrap", "wrap(f)", "A function that resumes a new coroutine running f."),
            F("yield", "yield(...)", "Pauses the running coroutine, handing values to resume."),
        ],
        ["package"] =
        [
            new("config", ScriptApiKind.Constant, "config", "Path separators and marks (standard format)."),
            new("cpath", ScriptApiKind.Property, "cpath", "Native module path: empty (native modules are not available)."),
            new("loaded", ScriptApiKind.Property, "loaded", "Modules require has loaded, by name (this script's)."),
            new("path", ScriptApiKind.Property, "path", "Where require looks: \"./?.lua;./?/init.lua\" (\".\" is Assets/Scripts)."),
            new("preload", ScriptApiKind.Property, "preload", "Loader functions for module names, tried first."),
            new("searchers", ScriptApiKind.Property, "searchers", "The functions require uses to find a module."),
            F("searchpath", "searchpath(name, path, sep?, rep?)", "The first file for name along path, or nil and the files tried."),
        ],
        ["os"] =
        [
            F("clock", "clock()", "CPU time used by the game, in seconds."),
            F("date", "date(format?, time?)", "A formatted date, or a table with \"*t\"."),
            F("difftime", "difftime(t2, t1)", "Seconds between two times."),
            F("time", "time(t?)", "The current time (or a date table's) as a number."),
        ],
        ["debug"] =
        [
            F("traceback", "traceback(message?, level?)", "The message followed by the call stack (e.g. for xpcall)."),
        ],
    };

    private static readonly HashSet<string> EngineGlobals = new(StringComparer.Ordinal)
    {
        "self", "Scene", "Time", "Input", "UI", "Timer", "Physics", "Vector3", "Events", "Camera", "Atmosphere", "Graphics", "Shared",
        "Navigation", "Perception", "Random", "Noise", "Path", "GameTime", "GameSave", "Cameras", "Particles", "DebugDraw"
    };
    private static readonly HashSet<string> BuiltinGlobals = new(
        GlobalMembers.Select(member => member.Name).Where(name => !EngineGlobals.Contains(name)),
        StringComparer.Ordinal);

    /// <summary>Every global a script environment contains (engine objects and the sandbox's standard set).</summary>
    public static IReadOnlyList<ScriptApiMember> Globals => GlobalMembers;

    /// <summary>The functions a script may define for the engine to call.</summary>
    public static IReadOnlyList<ScriptApiMember> Lifecycle => LifecycleMembers;

    /// <summary>Names of the member lists (Scene, ScriptObject, ScriptHandle, Transform, Vector3, Vector3Library, Time, Input, UI,
    /// GuiRoot, GuiElement, Connection, Events, Timer, TimerHandle, Physics, RaycastResult, Contact, Camera, Atmosphere, and the standard libraries math, string, table, utf8, coroutine, package, os, debug).</summary>
    public static IEnumerable<string> MemberTables => Tables.Keys;

    public static IReadOnlyList<ScriptApiMember> GetMembers(string memberTable) =>
        Tables.TryGetValue(memberTable, out var members) ? members : [];

    public static bool TryGetGlobal(string name, out ScriptApiMember member)
    {
        foreach (var global in GlobalMembers)
        {
            if (global.Name == name)
            {
                member = global;
                return true;
            }
        }

        member = null!;
        return false;
    }

    public static bool IsEngineGlobal(string name) => EngineGlobals.Contains(name);

    public static bool IsBuiltinGlobal(string name) => BuiltinGlobals.Contains(name);

    /// <summary>The Input functions whose argument is a key name / mouse button name.</summary>
    public static bool TakesKeyName(string inputFunction) => inputFunction is "IsKeyDown" or "IsKeyPressed" or "IsKeyReleased" or "GetAxis";

    public static bool TakesMouseButtonName(string inputFunction) =>
        inputFunction is "IsMouseButtonDown" or "IsMouseButtonPressed" or "IsMouseButtonReleased";

    private static ScriptApiMember F(string name, string signature, string summary) => new(name, ScriptApiKind.Function, signature, summary);

    private static ScriptApiMember C(string name, string signature, string summary) => new(name, ScriptApiKind.Constant, signature, summary);
}
