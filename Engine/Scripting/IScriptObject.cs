using Wisko.Engine.Math;

namespace Wisko.Engine.Scripting;

/// <summary>
/// The host side of a script's <c>self</c>: everything the Lua runtime may read from or write to one scene object.
/// Lua never receives this object (or anything behind it); it sees plain proxy tables whose values the runtime copies
/// in before each script callback and copies back afterwards when the script changed them.
/// </summary>
public interface IScriptObject
{
    string Name { get; }

    ScriptTransform ReadTransform();

    /// <summary>Applies a transform written by a script (values are finite; scale is at least <see cref="MinimumScale"/>).</summary>
    void WriteTransform(ScriptTransform transform);

    /// <summary>Smallest scale a script may set per axis (the editor's minimum object size); 0 = no lower bound.</summary>
    Vector3 MinimumScale { get; }

    /// <summary>What scripts see as its Type (a Part: its shape, e.g. "Block", or "Model").</summary>
    string TypeName => "Object";

    /// <summary>Still in the running scene. False once removed: a script's further access then fails with a message.</summary>
    bool Exists => true;
}

/// <summary>
/// A scene Part beyond what every script object has: its name, its physics settings and, when the session's physics
/// simulates it, its body. Answers are plain values; a write a script cannot make is refused with a short message for
/// the script (null means done). Writes go to the Part (Play restores it at Stop) and reach the physics body at once.
/// </summary>
internal interface IScriptPart : IScriptObject
{
    /// <summary>Anchored, GravityEnabled, CanCollide, IsTrigger (bool), Friction, Mass (number); false for any other key.</summary>
    bool TryGet(string key, out object? value);

    /// <summary>Name (string) or a physics setting; null when done, else why not (nothing changed).</summary>
    string? Set(string key, object? value);

    /// <summary>LinearVelocity (units/s) or AngularVelocity (degrees/s around the world axes); zero without a moving body.</summary>
    Vector3 GetVelocity(string key);

    string? SetVelocity(string key, Vector3 value);

    string? ApplyImpulse(Vector3 impulse);

    /// <summary>
    /// A push on the Part's body: "force" / "torque" for this frame (see PhysicsWorld.TryApplyForce), "angular" an
    /// instant angular impulse, "impulseat" an instant impulse at <paramref name="point"/>, "forceat" a force at it for
    /// this frame. Null when done, else why not.
    /// </summary>
    string? Push(string kind, Vector3 value, Vector3 point);

    /// <summary>How fast a world point fixed to the Part moves now (zero without a moving body).</summary>
    Vector3 VelocityAtPoint(Vector3 point);

    /// <summary>The mass physics uses for it now (manual or density x volume); null when physics does not simulate it.</summary>
    float? EffectiveMass { get; }

    /// <summary>It and <paramref name="other"/> never touch (nor trigger each other) while <paramref name="ignore"/>; null when done.</summary>
    string? IgnoreCollisionWith(IScriptObject other, bool ignore);

    /// <summary>Wakes its body; null when done, else why not (physics does not simulate it).</summary>
    string? Wake();

    /// <summary>The smallest world box around its exact shape where it is now.</summary>
    (Vector3 Min, Vector3 Max) Bounds { get; }

    /// <summary>Its tags and attributes: runtime data scripts share, gone when Play stops.</summary>
    ScriptPartData Data { get; }

    /// <summary>The object it follows (object:SetParent), or null.</summary>
    IScriptObject? Parent { get; }

    /// <summary>The objects following it directly, in scene order.</summary>
    IReadOnlyList<IScriptObject> Children { get; }

    /// <summary>
    /// Makes it follow <paramref name="parent"/> (null: let go, staying where it is) at an offset in the parent's frame:
    /// with neither offset it keeps where it is now; otherwise a missing one is zero. With <paramref name="bone"/> (a model
    /// parent's bone name) it follows that bone's animated pose instead of the parent's origin. Null when done,
    /// else why not.
    /// </summary>
    string? SetParent(IScriptObject? parent, Vector3? offsetPosition, Vector3? offsetRotation, string? bone = null);

    /// <summary>
    /// Whether it stands on something within <paramref name="maxDistance"/> below its bottom (not a trigger, not what it
    /// is attached to): a sphere swept down through its footprint. <paramref name="ground"/>: what, where, the surface
    /// normal, and the gap below its bottom (Distance).
    /// </summary>
    bool TryGetGround(float maxDistance, out ScriptRaycastHit ground);
}

/// <summary>
/// What a script's query (Physics.Raycast, RaycastAll, SphereCast, OverlapSphere, OverlapBox) skips: objects, Parts with
/// any of <see cref="IgnoreTags"/>, Parts without one of <see cref="OnlyTags"/> (when there are any), triggers and the
/// environment.
/// </summary>
internal sealed class ScriptQueryFilter
{
    public List<IScriptObject> Ignore { get; } = [];

    public List<string> IgnoreTags { get; } = [];

    public List<string> OnlyTags { get; } = [];

    public bool IgnoreTriggers { get; set; }

    public bool IgnoreEnvironment { get; set; }

    /// <summary>Query as a body of this collision group: only what that group collides with is found (null: everything).</summary>
    public string? CollisionGroup { get; set; }
}

/// <summary>
/// What scripts keep on one Part while playing: tags (ordered, no duplicates) and attributes (name -> bool, number or
/// string). Shared by every script (it is the Part's, not a script's); it lives in the Play session's scene runtime,
/// so Stop forgets it.
/// </summary>
internal sealed class ScriptPartData
{
    /// <summary>Most tags one Part can have, longest tag and attribute name, most attributes, longest string attribute.</summary>
    public const int MaxTags = 32;

    public const int MaxNameLength = 64;

    public const int MaxAttributes = 64;

    public const int MaxStringLength = 4096;

    public List<string> Tags { get; } = [];

    // Insertion order is kept for GetAttributes (a Lua table has none, but the order values are copied in is fixed).
    public Dictionary<string, object> Attributes { get; } = new(StringComparer.Ordinal);

    public void CopyFrom(ScriptPartData other)
    {
        Tags.AddRange(other.Tags);
        foreach (var (name, value) in other.Attributes)
        {
            Attributes[name] = value;
        }
    }

    /// <summary>Null when <paramref name="name"/> can be a tag or attribute name, else why not.</summary>
    public static string? CheckName(string what, string? name) =>
        name is null || name.Length == 0 ? $"{what} must be a non-empty string"
        : name.Length > MaxNameLength ? $"{what} must be at most {MaxNameLength} characters"
        : null;
}

/// <summary>The nearest thing a script's ray hit: its object (null: the environment), the distance along the ray, the point and the surface normal.</summary>
internal readonly record struct ScriptRaycastHit(IScriptObject? Object, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>
/// One contact as a script's collision or trigger function receives it: the point, the normal pointing toward the
/// receiving object (none for a trigger overlap), the receiver's velocity relative to the other side at that point, and
/// whether the other side is the environment.
/// </summary>
internal readonly record struct ScriptContact(Vector3 Point, Vector3? Normal, Vector3 RelativeVelocity, bool IsEnvironment);

/// <summary>
/// The running scene behind a runtime's Scene.Create, object:Destroy() and Physics.Raycast (Play's scene runtime). Called
/// from inside a script callback: it must not run Lua. Answers are plain values; a refusal is a short message for the
/// script (null means done).
/// </summary>
internal interface IScriptSceneHost
{
    /// <summary>A new Part of this type, registered as a scene object (see LuaScriptRuntime.RegisterSceneObject).</summary>
    string? Create(string typeName, string? name, bool anchored, bool isTrigger, out IScriptObject? created);

    /// <summary>Removes the object from the running scene at once (it exists, checked by the caller).</summary>
    string? Destroy(IScriptObject target);

    /// <summary>The nearest hit along a ray (direction of length 1) within maxDistance, skipping what <paramref name="filter"/> skips.</summary>
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, ScriptQueryFilter? filter, out ScriptRaycastHit hit);

    /// <summary>Every hit along a ray (direction of length 1), nearest first, at most <paramref name="maxHits"/>.</summary>
    List<ScriptRaycastHit> RaycastAll(Vector3 origin, Vector3 direction, float maxDistance, ScriptQueryFilter? filter, int maxHits);

    /// <summary>The first thing a sphere of <paramref name="radius"/> meets moving along a direction of length 1.</summary>
    bool SphereCast(Vector3 origin, Vector3 direction, float radius, float maxDistance, ScriptQueryFilter? filter, out ScriptRaycastHit hit);

    /// <summary>The objects a sphere (size.X = radius, box false) or a box (size, rotation) overlaps, and whether it reaches the environment.</summary>
    (List<IScriptObject> Objects, bool Environment) Overlap(bool box, Vector3 center, Vector3 size, Vector3 rotation, ScriptQueryFilter? filter, int maxObjects);

    /// <summary>The world's gravity (units/s²; zero without physics). Setting it: null when done, else why not.</summary>
    Vector3 Gravity { get; }

    string? SetGravity(Vector3 gravity);

    /// <summary>Whether two collision groups touch (made on first use; every group touches every group until told otherwise). Null when done.</summary>
    string? SetGroupsCollide(string a, string b, bool collide);

    /// <summary>Whether two collision groups touch (unknown names count as new groups: true).</summary>
    bool GroupsCollide(string a, string b);

    /// <summary>The session's collision groups by name (Default first).</summary>
    IReadOnlyList<string> CollisionGroups { get; }

    /// <summary>
    /// A physics joint ("Weld", "Hinge", "BallSocket") between <paramref name="a"/> and <paramref name="b"/> (null: a
    /// fixed point in the world) at a world point (Hinge: around <paramref name="axis"/>). Its number, or why not.
    /// </summary>
    string? CreateJoint(string kind, IScriptObject a, IScriptObject? b, Vector3 point, Vector3 axis, out int joint);

    /// <summary>A joint's value (Type, Active, Enabled, CollideConnected, PartA, PartB, Angle, motor and limit settings); why not when unknown.</summary>
    object? JointGet(int joint, string key, out string? error);

    /// <summary>Changes a joint setting (Enabled, CollideConnected, MotorEnabled, MotorSpeed, MotorMaxTorque, LimitsEnabled, LowerAngle, UpperAngle).</summary>
    string? JointSet(int joint, string key, object? value);

    /// <summary>Destroys a joint for good (nothing when it is already gone).</summary>
    void DestroyJoint(int joint);

    /// <summary>A copy of a scene Part (see SceneScriptRuntime), registered as a new scene object.</summary>
    string? Clone(IScriptObject source, string? name, out IScriptObject? created);

    /// <summary>
    /// Attaches the script file at <paramref name="fullPath"/> (checked: inside Assets/Scripts) to a scene Part while
    /// playing; it loads once the running callback has finished. Returns its handle number, or why not.
    /// </summary>
    string? AttachScript(IScriptObject target, string fullPath, string assetPath, out int handle);

    /// <summary>A runtime-attached script's state: whether it is still active (pending, loaded or running) and its path.</summary>
    bool TryGetAttachedScript(int handle, out bool active, out string assetPath);

    /// <summary>Stops a runtime-attached script (its OnDestroy runs once the running callback has finished).</summary>
    void DetachScript(int handle);

    /// <summary>
    /// The script running on <paramref name="target"/> that <paramref name="name"/> names ("Health", "Health.lua",
    /// "Combat/Health", or its full asset path; case-insensitive; a bare file name matches in any folder). Null and why
    /// when there is none.
    /// </summary>
    (LuaScriptInstance? Instance, string? Problem) FindScript(IScriptObject target, string name);
}

/// <summary>
/// The Play camera as scripts see it (the editor's view camera while playing). Position in world units; Pitch (−89 to
/// 89, positive looks up) and Yaw (degrees; 0 looks along +X, 90 along +Z) — the camera never rolls; vertical field of
/// view in degrees. Screen points are window pixels from the top-left, as Input.mouseX / mouseY. Whatever a script
/// changes is put back when Play stops.
/// </summary>
public interface IScriptCameraHost
{
    Vector3 Position { get; set; }

    float Pitch { get; }

    float Yaw { get; }

    void SetRotation(float pitch, float yaw);

    float FieldOfView { get; set; }

    Vector3 Forward { get; }

    Vector3 Right { get; }

    Vector3 Up { get; }

    int ViewportWidth { get; }

    int ViewportHeight { get; }

    /// <summary>Where a world point is drawn; false when it is behind the camera (no screen point).</summary>
    bool WorldToScreen(Vector3 point, out float x, out float y);

    /// <summary>The ray from the camera through a screen point (direction of length 1).</summary>
    (Vector3 Origin, Vector3 Direction) ScreenPointToRay(float x, float y);

    /// <summary>
    /// The game holds the mouse (Input.SetMouseLocked): the cursor is hidden and kept in the window, and mouse movement
    /// keeps arriving (for mouse look). Play's end lets it go. A host without a cursor ignores it.
    /// </summary>
    bool MouseLocked
    {
        get => false;
        set { }
    }
}

/// <summary>The Play session's Atmosphere as scripts see it: the settings drawn now, replaced as a whole (always sanitized); Stop puts back the project's.</summary>
public interface IScriptAtmosphereHost
{
    Atmosphere.AtmosphereSettings Settings { get; set; }
}

/// <summary>
/// Transform as scripts see it, in scene (Scene.json) terms: Position = the object's origin, Rotation = Euler angles
/// in degrees (X, Y, Z, as in the editor), Scale = scale of the object's base geometry (a primitive's unit shape, so
/// its size; a model's instance scale).
/// </summary>
public readonly record struct ScriptTransform(Vector3 Position, Vector3 Rotation, Vector3 Scale);
