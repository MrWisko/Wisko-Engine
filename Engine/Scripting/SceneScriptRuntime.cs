using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.World;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace Wisko.Engine.Scripting;

/// <summary>
/// A structured message from a scene script: print output, a warning or a failure, with the script's project path,
/// the object it runs on and (for failures) the line. Tracebacks are not included (they stay in the log).
/// </summary>
public readonly record struct ScriptOutputEvent(ScriptLogKind Kind, ScriptReference Script, string ObjectName, int? Line, string Text);

/// <summary>
/// A script changed its object's transform: whether the position or rotation changed, and whether the scale (a
/// primitive's Size) did. Reported once per callback that wrote, after the change is applied.
/// </summary>
public readonly record struct ScriptTransformWrite(Part Object, bool PositionOrRotationChanged, bool ScaleChanged);

/// <summary>One script reference on one scene object, and the script instance running it (none when the file is missing).</summary>
public sealed class SceneScript
{
    private readonly ScriptDiagnostic? _missing;

    internal SceneScript(Part target, ScriptReference reference, LuaScriptInstance? instance, ScriptDiagnostic? missing)
    {
        Object = target;
        Reference = reference;
        Instance = instance;
        _missing = missing;
    }

    public Part Object { get; }

    public ScriptReference Reference { get; }

    /// <summary>Its instance's state; Faulted for a missing file; Disposed when it was never loaded because its object was
    /// destroyed while the scene's scripts were loading.</summary>
    public ScriptInstanceState State => Instance?.State ?? (_missing is null ? ScriptInstanceState.Disposed : ScriptInstanceState.Faulted);

    /// <summary>Why the script is not running: a missing file, or its Load/Start/Update failure.</summary>
    public ScriptDiagnostic? Fault => _missing ?? Instance?.Fault;

    internal LuaScriptInstance? Instance { get; }
}

/// <summary>
/// Runs the Lua scripts attached to a scene's objects. Created explicitly by whoever wants scene scripts to run (no
/// editor code starts one on its own; Play Mode will): it takes the objects present at creation, reads each
/// referenced script from the project once (no hot reload), and gives every (object, script) pair its own script
/// instance with its own globals and a self bound to that object. Order is deterministic: objects in the order given,
/// then each object's script list. A missing or failing script is reported and skipped; the others keep running.
/// Script changes go straight to the objects, without editor history. Rendering and editor state are not involved.
/// <para>
/// Every object given is a scene object scripts can find (Scene.Find / FindAll / FindById; Ids 1, 2, ... in the order
/// given), scripted or not. An object still exists while it is in the given collection (when that is a live
/// collection). Scripts change a Part directly; its transform changes are reported (<see cref="TransformWritten"/>)
/// once per callback that made them, after it ran; its physics settings, velocities and impulses reach
/// <paramref name="physics"/> (the session's world) at once.
/// </para>
/// <para>
/// With a <see cref="GuiRuntime"/> (Play's copy of the GUI) scripts reach it through UI. Its Button clicks are queued
/// as they happen (the GUI decides what a click is) and their OnClick callbacks run at the start of the next
/// <see cref="Update(double, ScriptInputState?)"/>, after Time and Input are published and before every script's
/// Update, in click order.
/// </para>
/// <para>
/// Gameplay: scripts can create Parts (Scene.Create: added after every object so far, with new Ids, never reused) and
/// destroy them (object:Destroy(): gone at once from the scene, physics and Scene's searches; the Part's scripts stop
/// at once and, after the callback that destroyed it, run their OnDestroy and are released). Given a
/// <see cref="Workspace"/>, created Parts are added to it and destroyed ones taken out (Stop restores the edit scene);
/// <see cref="PartCreated"/> / <see cref="PartDestroyed"/> tell the host. With physics, the contacts and trigger overlaps
/// of Parts whose scripts define OnCollision* / OnTrigger* functions are collected once per frame and delivered to both
/// sides (other: the other Part, or nil for the environment), never from inside the physics step. Physics.Raycast asks
/// the session's physics after applying the running callback's transform changes.
/// </para>
/// <para>
/// One frame (<see cref="Update(double, ScriptInputState?)"/>): Input and Time, OnClick callbacks, contact and trigger
/// events (from the physics steps since the last frame, in a fixed order: by pair, the Part added first, then its
/// scripts in order, then the other side's), due timers, then every script's Update(dt).
/// </para>
/// </summary>
public sealed partial class SceneScriptRuntime : IDisposable, IScriptSceneHost
{
    /// <summary>Parts scripts may have created and not yet destroyed at once (Scene.Create and Clone refuse more).</summary>
    public const int MaxCreatedParts = 10_000;

    /// <summary>Scripts that may be attached while playing, per Play session (object:AttachScript refuses more).</summary>
    public const int MaxAttachedScripts = 1_000;

    /// <summary>Scripts one Part may run at once (authored and attached while playing).</summary>
    public const int MaxScriptsPerPart = 64;

    // As the editor's rename allows.
    private const int MaxNameLength = 48;

    private static readonly ScriptEventHandler[] ContactHandlers =
    [
        ScriptEventHandler.OnCollisionEnter, ScriptEventHandler.OnCollisionStay, ScriptEventHandler.OnCollisionExit,
        ScriptEventHandler.OnTriggerEnter, ScriptEventHandler.OnTriggerStay, ScriptEventHandler.OnTriggerExit
    ];

    private readonly LuaScriptRuntime _lua;
    private readonly List<SceneScript> _scripts = [];

    // Display name ("Assets/Scripts/Spin.lua (Cube)") -> the script and object behind it, for structured output.
    private readonly Dictionary<string, (ScriptReference Script, string ObjectName)> _sources = new(StringComparer.Ordinal);
    private readonly Action<ScriptLogMessage>? _log;
    private readonly Action<ScriptOutputEvent>? _output;
    private readonly GuiRuntime? _gui;
    private readonly PhysicsWorld? _physics;

    // Model animation playback scripts control (the Play session's, or the runtime's own when it was given none).
    private readonly AnimationRuntime _animations;
    private readonly bool _ownsAnimations;

    // The gameplay services (the Play session's, or the runtime's own) and the Play camera they show through.
    private readonly global::Wisko.Engine.Gameplay.GameplaySession _gameplay;
    private readonly bool _ownsGameplay;
    private readonly IScriptCameraHost? _camera;

    // The running scene: a workspace (created Parts are added to it, destroyed ones taken out), or a live collection.
    // Either way a Part exists while it is in there: _present mirrors the workspace (refreshed when someone else
    // changed it) or holds the Parts given, created and not destroyed (with the collection asked too).
    private readonly Workspace? _workspace;
    private readonly ICollection<Part>? _scene;
    private readonly HashSet<Part> _present = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Part, PartScriptObject> _sceneObjects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Part, List<SceneScript>> _scriptsByPart = new(ReferenceEqualityComparer.Instance);

    // GUI pointer events (clicks, presses, releases, hover) as the GUI raised them, run at the start of the next frame.
    private readonly List<(GuiButtonEvent Kind, int ButtonId)> _pendingGuiEvents = [];

    // Scripts attached while playing, by handle; those waiting to load (after the running callback).
    private readonly Dictionary<int, AttachedScript> _attached = [];
    private readonly List<AttachedScript> _pendingAttaches = [];
    private bool _runningAttaches;

    // Parts a callback changed the transform of (in the order changed), reported when it finishes.
    private readonly List<(Part Part, bool Moved, bool Scaled)> _pendingWrites = [];

    // This frame's contact events (reused); destroyed Parts' Ids, kept for the events that still name them (until the
    // end of the next contact dispatch after they went); destroyed Parts' scripts waiting for OnDestroy and release.
    private readonly List<PhysicsContactEvent> _contactEvents = [];
    private readonly Dictionary<Part, (int Id, long Dispatch)> _destroyedIds = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<List<LuaScriptInstance>> _pendingDestroys = new();
    private long _presentVersion;
    private long _dispatchCount;
    private int _createdCount;
    private bool _loading;
    private bool _runningDestroys;
    private bool _disposed;

    // object:SetParent (Play only, never saved): each Part with a parent follows it at a fixed offset, and the attachment
    // trees' physics groups (Parts of one tree never touch each other), by Part.
    private readonly Dictionary<Part, ParentLink> _parents = new(ReferenceEqualityComparer.Instance);

    // Physics joints scripts made (Physics.CreateWeld / CreateHinge / CreateBallSocket), by number; they die with the world.
    private readonly Dictionary<int, PhysicsConstraint> _joints = [];
    private Dictionary<Part, int> _groups = new(ReferenceEqualityComparer.Instance);
    private int _nextGroup;

    /// <param name="projectRoot">The project folder scripts are read from; null for a world without a project folder
    /// (every attached script is then reported missing).</param>
    /// <param name="output">Optional structured consumer (e.g. an Output panel) of print, warnings and failures, in
    /// addition to the normal log (<see cref="LuaScriptRuntimeOptions.Log"/>, the console by default).</param>
    /// <param name="gui">The Play session's GUI copy scripts reach through UI (null: UI finds nothing).</param>
    /// <param name="physics">The Play session's physics world scripts' physics settings and bodies go to (null: none).</param>
    /// <param name="animations">The session's animation playback scripts control (null: the runtime makes its own, which
    /// its host advances through <see cref="Animations"/>).</param>
    public SceneScriptRuntime(string? projectRoot, IEnumerable<Part> objects, LuaScriptRuntimeOptions? options = null, Action<ScriptOutputEvent>? output = null,
        GuiRuntime? gui = null, PhysicsWorld? physics = null, AnimationRuntime? animations = null)
        : this(projectRoot, objects, null, options, output, gui, physics, animations)
    {
    }

    /// <summary>Runs the scripts of <paramref name="scene"/>'s Parts; Parts scripts create or destroy are added to it or taken out of it.</summary>
    public SceneScriptRuntime(string? projectRoot, Workspace scene, LuaScriptRuntimeOptions? options = null, Action<ScriptOutputEvent>? output = null,
        GuiRuntime? gui = null, PhysicsWorld? physics = null, AnimationRuntime? animations = null)
        : this(projectRoot, scene.Parts, scene, options, output, gui, physics, animations)
    {
    }

    private SceneScriptRuntime(string? projectRoot, IEnumerable<Part> objects, Workspace? workspace, LuaScriptRuntimeOptions? options,
        Action<ScriptOutputEvent>? output, GuiRuntime? gui, PhysicsWorld? physics, AnimationRuntime? animations)
    {
        options ??= new LuaScriptRuntimeOptions();
        _log = options.Log;
        _output = output;
        _gui = gui;
        _physics = physics;
        _ownsAnimations = animations is null;
        _animations = animations ?? new AnimationRuntime();
        _ownsGameplay = options.Gameplay is null;
        _gameplay = options.Gameplay ?? new global::Wisko.Engine.Gameplay.GameplaySession(projectRoot);
        _camera = options.Camera;
        _workspace = workspace;
        _scene = workspace is null ? objects as ICollection<Part> : null;
        _lua = new LuaScriptRuntime(new LuaScriptRuntimeOptions
        {
            InstructionBudget = options.InstructionBudget,
            MemoryLimitBytes = options.MemoryLimitBytes,
            MemoryAccounting = options.MemoryAccounting,
            MaxPrintLength = options.MaxPrintLength,
            Log = OnLog,
            Gui = gui,
            Camera = options.Camera,
            Atmosphere = options.Atmosphere,
            Graphics = options.Graphics,
            Gameplay = _gameplay,
            FixedDeltaTime = physics?.Settings.FixedTimestep ?? PhysicsSettings.Default.FixedTimestep,

            // require() (and object:AttachScript) find the project's own scripts: <project>/Assets/Scripts.
            ModuleRoot = string.IsNullOrEmpty(projectRoot)
                ? null
                : Path.Combine(projectRoot, ProjectAssetPaths.ScriptsFolder.Replace('/', Path.DirectorySeparatorChar))
        });
        _lua.CallbackCompleted = OnCallbackCompleted;
        _lua.SceneHost = this;
        _lua.PartOfObject = target => target is PartScriptObject sceneObject && _sceneObjects.ContainsKey(sceneObject.Part) ? sceneObject.Part : null;
        _lua.FindObjectOfPart = part => _sceneObjects.TryGetValue(part, out var sceneObject) ? sceneObject : null;
        try
        {
            // Every object first (their Ids are their scene order), then the scripts in order. A script's top-level
            // chunk may create or destroy Parts: a destroyed Part's remaining scripts are not loaded, and the OnDestroy
            // calls wait until every script has loaded.
            _loading = true;
            var sceneObjects = objects.Select(target => (Part: target, Object: new PartScriptObject(target, this))).ToList();
            foreach (var (target, sceneObject) in sceneObjects)
            {
                _present.Add(target);
                sceneObject.Id = _lua.AddSceneObject(sceneObject);
                _sceneObjects[target] = sceneObject;
            }

            _presentVersion = workspace?.Version ?? 0;

            foreach (var (target, self) in sceneObjects)
            {
                List<SceneScript>? scripts = null;
                foreach (var reference in target.Scripts.ToArray())
                {
                    var script = IsPresent(target) ? Load(projectRoot, target, self, reference) : new SceneScript(target, reference, null, null);
                    _scripts.Add(script);
                    (scripts ??= []).Add(script);
                }

                if (scripts is not null && IsPresent(target))
                {
                    _scriptsByPart[target] = scripts;
                }
            }

            _loading = false;
            if (_physics is { IsDisposed: false } world)
            {
                foreach (var (target, scripts) in _scriptsByPart)
                {
                    if (scripts.Any(script => script.Instance is { } instance && ContactHandlers.Any(instance.HasHandler)))
                    {
                        world.SetReportsContacts(target, true);
                    }
                }
            }

            RunPendingDestroys();
            RunPendingAttaches();
        }
        catch
        {
            _lua.Dispose();
            throw;
        }

        if (_gui is not null)
        {
            _gui.ButtonClicked += OnButtonClicked;
            _gui.ButtonEvent += OnButtonEvent;
            _gui.TextBoxSubmitted += OnTextBoxSubmitted;
            _gui.TextBoxFocusLost += OnTextBoxFocusLost;
            _gui.TextBoxFocused += OnTextBoxFocused;
        }
    }

    /// <summary>Every attached script, in execution order.</summary>
    public IReadOnlyList<SceneScript> Scripts => _scripts;

    /// <summary>The animation playback scripts control (advance it once per frame; Play's controller does).</summary>
    public AnimationRuntime Animations => _animations;

    /// <summary>
    /// A model's current clip, playing once, reached its end (from <see cref="AnimationRuntime.Finished"/>): its scripts'
    /// OnAnimationFinished connections run at the start of the next frame.
    /// </summary>
    public void NotifyAnimationFinished(Part part, AnimationClip clip)
    {
        if (!_disposed && _sceneObjects.TryGetValue(part, out var sceneObject))
        {
            _lua.NoteAnimationFinished(sceneObject, clip.Name);
        }
    }

    /// <summary>Told whenever a script actually changes its object's transform (reads alone never report).</summary>
    public Action<ScriptTransformWrite>? TransformWritten { get; set; }

    /// <summary>Told when a script created a Part (already in the scene and the physics world).</summary>
    public Action<Part>? PartCreated { get; set; }

    /// <summary>Told when a script destroyed a Part (already out of the scene and the physics world).</summary>
    public Action<Part>? PartDestroyed { get; set; }

    /// <summary>GUI callbacks (OnClick, OnPressed, OnReleased, OnHoverEnter, OnHoverExit) the scripts have registered (diagnostics).</summary>
    public int ClickHandlerCount => _disposed ? 0 : _lua.ClickHandlerCount;

    /// <summary>The most the scripts' Lua heap held this session, and its limit, in bytes (diagnostics).</summary>
    public long LuaMemoryPeakBytes => _disposed ? 0 : _lua.LuaMemoryPeakBytes;

    public long LuaMemoryLimitBytes => _disposed ? 0 : _lua.LuaMemoryLimitBytes;

    /// <summary>Events.On connections the scripts have (diagnostics).</summary>
    public int EventHandlerCount => _disposed ? 0 : _lua.EventHandlerCount;

    /// <summary>Lua heap in use, in bytes (diagnostics).</summary>
    public long LuaMemoryBytes => _disposed ? 0 : _lua.LuaMemoryBytes;

    /// <summary>Timers the scripts have running (diagnostics).</summary>
    public int TimerCount => _disposed ? 0 : _lua.TimerCount;

    /// <summary>Parts scripts created that still exist (diagnostics).</summary>
    public int CreatedPartCount => _disposed ? 0 : _createdCount;

    /// <summary>Failures so far (missing files, load/Start/Update/OnClick errors), in script order.</summary>
    public IEnumerable<ScriptDiagnostic> Diagnostics => _scripts.Select(script => script.Fault).OfType<ScriptDiagnostic>();

    /// <summary>Publishes this frame's keyboard/mouse snapshot to every script's Input (see <see cref="LuaScriptRuntime.SetInput"/>).</summary>
    public void SetInput(ScriptInputState input)
    {
        if (!_disposed)
        {
            _lua.SetInput(input);
        }
    }

    /// <summary>Runs Start for every script that has not started yet.</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        // By index: a Start may attach scripts (they start with the first Update).
        var count = _scripts.Count;
        for (var i = 0; i < count; i++)
        {
            _scripts[i].Instance?.Start();
        }
    }

    /// <summary>One frame: advances Time, then runs every script's Update(dt) (Start first where it has not run).</summary>
    public void Update(double deltaTime) => Update(deltaTime, null);

    /// <summary>
    /// One frame with input: publishes <paramref name="input"/> (when given), advances Time, runs the OnClick callbacks
    /// of the Button clicks since the last frame, the contact and trigger events since then and the due timers, then
    /// runs Update(dt).
    /// </summary>
    public void Update(double deltaTime, ScriptInputState? input) => UpdateFrame(deltaTime, deltaTime, input);

    private void UpdateFrame(double deltaTime, double unscaledDeltaTime, ScriptInputState? input)
    {
        if (_disposed)
        {
            return;
        }

        if (input is not null)
        {
            _lua.SetInput(input);
        }

        BeginGameplayFrame(deltaTime, unscaledDeltaTime);
        _lua.AdvanceTime(deltaTime, unscaledDeltaTime);
        if (_pendingGuiEvents.Count > 0)
        {
            var events = _pendingGuiEvents.ToArray();
            _pendingGuiEvents.Clear();
            foreach (var (kind, buttonId) in events)
            {
                _lua.DispatchGuiEvent(kind, buttonId);
            }
        }

        _lua.DispatchEvents();
        _lua.DispatchChat();
        DispatchContacts();
        _lua.RunTimers();

        // Scripts attached during this pass start with the next frame's.
        var count = _scripts.Count;
        for (var i = 0; i < count; i++)
        {
            _scripts[i].Instance?.Update();
        }

        // Time.OnUpdate callbacks (agents, sensors, path followers and the scripts' own) after every Update.
        _lua.RunFrameCallbacks();
    }

    /// <summary>
    /// The end of a frame, after physics: objects with a parent follow it (object:SetParent), then every started
    /// script's LateUpdate(dt) runs, so a camera or anything else that follows a moving object sees where it is now.
    /// </summary>
    public void LateUpdate()
    {
        if (_disposed)
        {
            return;
        }

        FollowParents();
        var count = _scripts.Count;
        for (var i = 0; i < count; i++)
        {
            _scripts[i].Instance?.LateUpdate();
        }

        EndGameplayFrame();
    }

    /// <summary>
    /// A Part following its parent: its offset in the parent's frame (position, and orientation relative to the
    /// parent's) and its own Anchored setting from before (put back when it lets go: it is anchored while it follows).
    /// </summary>
    private sealed class ParentLink(Part parent, Vector3 offsetPosition, NQuaternion offsetRotation, bool wasAnchored)
    {
        public Part Parent { get; set; } = parent;

        public Vector3 OffsetPosition { get; set; } = offsetPosition;

        public NQuaternion OffsetRotation { get; set; } = offsetRotation;

        public bool WasAnchored { get; } = wasAnchored;

        /// <summary>The parent model's node it follows (a bone, posed by its animation); -1: the parent itself.</summary>
        public int BoneNode { get; set; } = -1;
    }

    // object:SetParent(parent, offsetPosition?, offsetRotation?, bone?): see IScriptPart.SetParent.
    private string? SetParentOf(PartScriptObject child, IScriptObject? parentObject, Vector3? offsetPosition, Vector3? offsetRotation, string? bone = null)
    {
        var part = child.Part;
        if (parentObject is null)
        {
            Unparent(part);
            return null;
        }

        if (parentObject is not PartScriptObject parent || !_sceneObjects.ContainsKey(parent.Part))
        {
            return "SetParent expects a scene object (or nil to let go)";
        }

        if (ReferenceEquals(parent.Part, part))
        {
            return "an object cannot be its own parent";
        }

        for (var above = parent.Part; _parents.TryGetValue(above, out var link); above = link.Parent)
        {
            if (ReferenceEquals(link.Parent, part))
            {
                return $"'{parent.Part.Name}' already follows '{part.Name}': that would make a loop";
            }
        }

        if (offsetPosition is { } position && !PhysicsMath.IsFinite(position) || offsetRotation is { } rotation && !PhysicsMath.IsFinite(rotation))
        {
            return "SetParent expects finite offsets";
        }

        // A bone of the parent model: the child follows that bone's animated pose.
        var boneNode = -1;
        if (bone is not null)
        {
            if (parent.Part.Model?.Asset is null)
            {
                return $"SetParent: '{parent.Part.Name}' is not an imported model, so it has no bones";
            }

            boneNode = NodeOf(parent.Part, bone);
            if (boneNode < 0)
            {
                return "SetParent: " + NoSuchBone(parent.Part, bone);
            }
        }

        // What the running callback moved counts (both poses are read now).
        ReportTransformWrites();
        var (parentPosition, parentRotation) = boneNode >= 0 ? BonePose(parent.Part, boneNode)!.Value : PoseOf(parent);
        Vector3 localPosition;
        NQuaternion localRotation;
        if (offsetPosition is null && offsetRotation is null)
        {
            // Keeps where it is now, relative to the parent.
            var (childPosition, childRotation) = PoseOf(child);
            var inverse = NQuaternion.Inverse(parentRotation);
            localPosition = PhysicsMath.ToWisko(NVector3.Transform(PhysicsMath.ToNumerics(childPosition - parentPosition), inverse));
            localRotation = NQuaternion.Normalize(inverse * childRotation);
        }
        else
        {
            localPosition = offsetPosition ?? Vector3.Zero;
            localRotation = PhysicsMath.EulerDegreesToQuaternion(offsetRotation ?? Vector3.Zero);
        }

        if (_parents.TryGetValue(part, out var existing))
        {
            existing.Parent = parent.Part;
            existing.OffsetPosition = localPosition;
            existing.OffsetRotation = localRotation;
            existing.BoneNode = boneNode;
        }
        else
        {
            _parents[part] = new ParentLink(parent.Part, localPosition, localRotation, part.Anchored) { BoneNode = boneNode };

            // It moves with its parent from now on: anchored, and kinematic (driven) rather than static.
            if (_physics is { IsDisposed: false } physics)
            {
                physics.MarkScripted(part);
            }

            child.Set("Anchored", true);
        }

        RefreshGroups();
        FollowParents();
        return null;
    }

    // It stops following its parent and stays where it is; its own Anchored setting comes back (unanchored: it falls).
    private void Unparent(Part part)
    {
        if (!_parents.Remove(part, out var link))
        {
            return;
        }

        RefreshGroups();
        if (_sceneObjects.TryGetValue(part, out var child) && part.Anchored != link.WasAnchored)
        {
            child.Set("Anchored", link.WasAnchored);
        }
    }

    // Every Part with a parent goes where its parent's pose and its offset put it, parents before their children.
    private void FollowParents()
    {
        if (_parents.Count == 0)
        {
            return;
        }

        foreach (var part in _parents.Keys.OrderBy(Depth).ToList())
        {
            if (!_parents.TryGetValue(part, out var link) || !_sceneObjects.TryGetValue(part, out var child)
                || !_sceneObjects.TryGetValue(link.Parent, out var parent))
            {
                continue;
            }

            // A bone's pose is the animation's this frame (animations advance before parents are followed).
            var (parentPosition, parentRotation) = link.BoneNode >= 0 && BonePose(link.Parent, link.BoneNode) is { } bone ? bone : PoseOf(parent);
            var position = parentPosition + PhysicsMath.ToWisko(NVector3.Transform(PhysicsMath.ToNumerics(link.OffsetPosition), parentRotation));
            var rotation = PhysicsMath.QuaternionToEulerDegrees(parentRotation * link.OffsetRotation);
            child.WriteTransform(new ScriptTransform(position, rotation, child.ReadTransform().Scale));
        }

        ReportTransformWrites();
    }

    private int Depth(Part part)
    {
        var depth = 0;
        for (var above = part; _parents.TryGetValue(above, out var link); above = link.Parent)
        {
            depth++;
        }

        return depth;
    }

    private Part RootOf(Part part)
    {
        var root = part;
        while (_parents.TryGetValue(root, out var link))
        {
            root = link.Parent;
        }

        return root;
    }

    // Each attachment tree is one physics group (its Parts never touch each other); a Part in no tree has none. A tree
    // keeps its root's group number while the root has one.
    private void RefreshGroups()
    {
        var wanted = new Dictionary<Part, int>(ReferenceEqualityComparer.Instance);
        var rootGroups = new Dictionary<Part, int>(ReferenceEqualityComparer.Instance);
        foreach (var child in _parents.Keys)
        {
            var root = RootOf(child);
            if (!rootGroups.TryGetValue(root, out var group))
            {
                group = _groups.TryGetValue(root, out var previous) ? previous : ++_nextGroup;
                rootGroups[root] = group;
            }

            wanted[child] = group;
            wanted[root] = group;
        }

        if (_physics is { IsDisposed: false } physics)
        {
            foreach (var part in _groups.Keys)
            {
                if (!wanted.ContainsKey(part))
                {
                    physics.SetCollisionGroup(part, 0);
                }
            }

            foreach (var (part, group) in wanted)
            {
                physics.SetCollisionGroup(part, group);
            }
        }

        _groups = wanted;
    }

    // Its attachment tree's Parts (empty when it is in none).
    private HashSet<Part> TreeOf(Part part)
    {
        var tree = new HashSet<Part>(ReferenceEqualityComparer.Instance);
        if (!_groups.TryGetValue(part, out var group))
        {
            return tree;
        }

        foreach (var (member, memberGroup) in _groups)
        {
            if (memberGroup == group)
            {
                tree.Add(member);
            }
        }

        return tree;
    }

    // Where an object is and how it is turned, as a quaternion (a model: its instance origin).
    private static (Vector3 Position, NQuaternion Rotation) PoseOf(PartScriptObject sceneObject)
    {
        var transform = sceneObject.ReadTransform();
        return (transform.Position, PhysicsMath.EulerDegreesToQuaternion(transform.Rotation));
    }

    // object:IsGrounded: a sphere through its footprint swept from its middle to maxDistance below its bottom.
    private bool TryGetGroundOf(PartScriptObject sceneObject, float maxDistance, out ScriptRaycastHit ground)
    {
        ground = default;
        ReportTransformWrites();
        if (_physics is not { IsDisposed: false } physics || !float.IsFinite(maxDistance) || maxDistance < 0f)
        {
            return false;
        }

        var (min, max) = sceneObject.Bounds;
        var center = (min + max) * 0.5f;
        var halfHeight = (max.Y - min.Y) * 0.5f;
        var radius = MathF.Min(MathF.Min(max.X - min.X, max.Z - min.Z) * 0.45f, halfHeight * 0.9f);
        if (!(radius > 1e-3f))
        {
            radius = 1e-3f;
        }

        // Down to maxDistance below its bottom; neither triggers nor what it is attached to count as ground.
        var reach = halfHeight - radius;
        var tree = TreeOf(sceneObject.Part);
        var filter = new PhysicsQueryFilter
        {
            Ignore = sceneObject.Part,
            IgnoreTriggers = true,
            AcceptPart = tree.Count == 0 ? null : part => !tree.Contains(part)
        };
        if (!physics.SphereCast(center, new Vector3(0f, -1f, 0f), radius, reach + maxDistance, filter, out var found))
        {
            return false;
        }

        PartScriptObject? groundObject = null;
        if (found.Part is not null && !_sceneObjects.TryGetValue(found.Part, out groundObject))
        {
            return false;
        }

        ground = new ScriptRaycastHit(groundObject, MathF.Max(0f, found.Distance - reach), found.Point, found.Normal);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_gui is not null)
        {
            _gui.ButtonClicked -= OnButtonClicked;
            _gui.ButtonEvent -= OnButtonEvent;
            _gui.TextBoxSubmitted -= OnTextBoxSubmitted;
            _gui.TextBoxFocusLost -= OnTextBoxFocusLost;
            _gui.TextBoxFocused -= OnTextBoxFocused;
        }

        _pendingGuiEvents.Clear();
        _pendingAttaches.Clear();
        _attached.Clear();
        _pendingWrites.Clear();
        _contactEvents.Clear();
        _destroyedIds.Clear();
        _pendingDestroys.Clear(); // Stop is not a destruction: no OnDestroy
        _sceneObjects.Clear();
        _scriptsByPart.Clear();
        _present.Clear();
        _lua.SceneHost = null;
        _lua.Dispose();
        _agents.Clear();
        _navGrid = null;
        if (_ownsAnimations)
        {
            _animations.Dispose();
        }

        if (_ownsGameplay)
        {
            _gameplay.End(_camera);
            _gameplay.Dispose();
        }
    }

    string? IScriptSceneHost.Create(string typeName, string? name, bool anchored, bool isTrigger, out IScriptObject? created)
    {
        created = null;
        if (typeName == "Model")
        {
            return "Scene.Create can't create a Model (models are placed in the editor); use \"Block\", \"Cylinder\", \"Wedge\", \"Sphere\" or \"Pyramid\"";
        }

        if (!Enum.TryParse<ShapeType>(typeName, ignoreCase: false, out var shape) || !Enum.IsDefined(shape) || shape.ToString() != typeName)
        {
            var shown = typeName.Length > 40 ? typeName[..40] + "..." : typeName;
            return $"Scene.Create: unknown type \"{shown}\" (use \"Block\", \"Cylinder\", \"Wedge\", \"Sphere\" or \"Pyramid\")";
        }

        var trimmed = (name ?? typeName).Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return $"Scene.Create: the name must be 1 to {MaxNameLength} characters";
        }

        if (_createdCount >= MaxCreatedParts)
        {
            return $"Scene.Create: {MaxCreatedParts:N0} created Parts already exist; destroy some first";
        }

        // The editor's new Part (size, colour), standing on the ground at the origin; the script places it from there.
        var part = new Part(trimmed, new Vector3(0f, Part.DefaultSize.Y * 0.5f, 0f), Part.DefaultSize, Part.DefaultColor, Vector3.Zero, anchored, shape)
        {
            IsTrigger = isTrigger
        };
        var sceneObject = new PartScriptObject(part, this) { Created = true };
        AddCreated(part, sceneObject);
        created = sceneObject;
        return null;
    }

    /// <summary>
    /// A copy of a scene Part (a primitive or a model instance): same shape or model, transform, colour, Visible, physics
    /// settings, tags and attributes; named <paramref name="name"/> (default: the source's name). It starts at rest and
    /// runs no scripts (attach them with AttachScript). Added like a Scene.Create Part (new Id; gone at Stop).
    /// </summary>
    string? IScriptSceneHost.Clone(IScriptObject source, string? name, out IScriptObject? created)
    {
        created = null;
        if (source is not PartScriptObject original || !_sceneObjects.ContainsKey(original.Part))
        {
            return "only scene Parts can be cloned";
        }

        var trimmed = (name ?? original.Part.Name).Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return $"Clone: the name must be 1 to {MaxNameLength} characters";
        }

        if (_createdCount >= MaxCreatedParts)
        {
            return $"Clone: {MaxCreatedParts:N0} created Parts already exist; destroy some first";
        }

        // Where the source is now (what the running callback moved included).
        ReportTransformWrites();
        var from = original.Part;
        var part = new Part(trimmed, from.Position, from.Size, from.Color, from.Rotation, from.Anchored, from.ShapeType)
        {
            Model = from.Model,
            GravityEnabled = from.GravityEnabled,
            CanCollide = from.CanCollide,
            IsTrigger = from.IsTrigger,
            Friction = from.Friction,
            MassOverride = from.MassOverride,
            Visible = from.Visible,
            Animation = from.Animation,
            Material = from.Material,
            ModelMaterials = from.ModelMaterials,
            Light = from.Light
        };
        var sceneObject = new PartScriptObject(part, this) { Created = true };
        sceneObject.Data.CopyFrom(original.Data);
        AddCreated(part, sceneObject);

        // Its own playback: the saved Play on start clip from its start (not the source's playback state).
        _animations.StartPlayOnStart([part]);
        if (_physics is { IsDisposed: false } physics && physics.IsRotationLocked(from))
        {
            physics.SetRotationLocked(part, true);
        }

        created = sceneObject;
        return null;
    }

    string? IScriptSceneHost.AttachScript(IScriptObject target, string fullPath, string assetPath, out int handle)
    {
        handle = 0;
        if (target is not PartScriptObject sceneObject || !_sceneObjects.ContainsKey(sceneObject.Part))
        {
            return "scripts can only be attached to scene Parts";
        }

        if (!ScriptReference.TryCreate(assetPath, out var reference))
        {
            return $"AttachScript: {assetPath} is not a script inside Assets/Scripts";
        }

        if (_attached.Count >= MaxAttachedScripts)
        {
            return $"AttachScript: {MaxAttachedScripts:N0} scripts were already attached while playing";
        }

        // The scripts the Part runs (or will, once loaded): no file twice, as in the editor.
        var part = sceneObject.Part;
        var running = _scriptsByPart.TryGetValue(part, out var scripts)
            ? scripts.Where(script => script.State is not (ScriptInstanceState.Disposed or ScriptInstanceState.Faulted) && script.Instance?.Destroyed != true)
                .Select(script => script.Reference.AssetPath).ToList()
            : [];
        running.AddRange(_pendingAttaches.Where(pending => ReferenceEquals(pending.Target.Part, part)).Select(pending => pending.Reference.AssetPath));
        if (running.Any(path => string.Equals(path, reference.AssetPath, StringComparison.OrdinalIgnoreCase)))
        {
            return $"AttachScript: '{part.Name}' already runs {reference.AssetPath}";
        }

        if (running.Count >= MaxScriptsPerPart)
        {
            return $"AttachScript: '{part.Name}' already runs {MaxScriptsPerPart} scripts";
        }

        var attached = new AttachedScript(_attached.Count + 1, sceneObject, reference, fullPath);
        _attached.Add(attached.Handle, attached);
        _pendingAttaches.Add(attached);
        handle = attached.Handle;
        if (!_loading && !_lua.IsExecuting)
        {
            RunPendingAttaches();
        }

        return null;
    }

    (LuaScriptInstance? Instance, string? Problem) IScriptSceneHost.FindScript(IScriptObject target, string name)
    {
        if (target is not PartScriptObject sceneObject || !_scriptsByPart.TryGetValue(sceneObject.Part, out var scripts))
        {
            return (null, $"'{target.Name}' runs no scripts");
        }

        var wanted = ScriptKey(name);
        if (wanted.Length == 0)
        {
            return (null, "GetScript expects a script name (e.g. \"Health\")");
        }

        // Its exact path first; a bare name also finds the file in any folder. Of several, the first in the Part's order.
        SceneScript? byFileName = null;
        foreach (var script in scripts)
        {
            var key = ScriptKey(script.Reference.AssetPath);
            if (string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return Found(script);
            }

            if (byFileName is null && !wanted.Contains('/')
                && string.Equals(key[(key.LastIndexOf('/') + 1)..], wanted, StringComparison.OrdinalIgnoreCase))
            {
                byFileName = script;
            }
        }

        return byFileName is not null ? Found(byFileName) : (null, $"'{target.Name}' has no script '{name.Trim()}'");

        (LuaScriptInstance?, string?) Found(SceneScript script) =>
            script.Instance is null ? (null, $"the script file '{script.Reference.AssetPath}' is missing") : (script.Instance, null);

        // "Assets/Scripts/Combat/Health.lua", "Combat\Health.lua", "Combat/Health" -> "Combat/Health".
        static string ScriptKey(string path)
        {
            var key = path.Trim().Replace('\\', '/');
            const string prefix = "Assets/Scripts/";
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                key = key[prefix.Length..];
            }

            return key.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? key[..^4] : key;
        }
    }

    bool IScriptSceneHost.TryGetAttachedScript(int handle, out bool active, out string assetPath)
    {
        active = false;
        assetPath = string.Empty;
        if (!_attached.TryGetValue(handle, out var attached))
        {
            return false;
        }

        assetPath = attached.Reference.AssetPath;
        active = !attached.Detached && IsPresent(attached.Target.Part)
            && (attached.Script is null || attached.Script.State is ScriptInstanceState.Loaded or ScriptInstanceState.Started);
        return true;
    }

    void IScriptSceneHost.DetachScript(int handle)
    {
        if (!_attached.TryGetValue(handle, out var attached) || attached.Detached)
        {
            return;
        }

        attached.Detached = true;
        if (attached.Script is null)
        {
            _pendingAttaches.Remove(attached); // never loaded
            return;
        }

        // It stops now (it stays listed under its Part, stopped, so nothing iterating that list is disturbed); its OnDestroy runs once the running callback has finished, then it is released.
        if (attached.Script.Instance is { } instance && _lua.BeginStop(instance))
        {
            _pendingDestroys.Enqueue([instance]);
            if (!_lua.IsExecuting && !_loading)
            {
                RunPendingDestroys();
            }
        }
    }

    // A Part scripts made (Scene.Create, Clone): into the scene, Scene's searches and physics, and the host is told.
    private void AddCreated(Part part, PartScriptObject sceneObject)
    {
        RefreshPresent();
        _present.Add(part);
        if (_workspace is not null)
        {
            _workspace.AddPart(part);
            _presentVersion = _workspace.Version;
        }
        else if (_scene is { IsReadOnly: false })
        {
            _scene.Add(part);
        }

        sceneObject.Id = _lua.RegisterSceneObject(sceneObject);
        _sceneObjects[part] = sceneObject;
        _createdCount++;
        if (_physics is { IsDisposed: false } physics && !physics.TryAddPart(part, out var reason) && part.Model is null)
        {
            Console.WriteLine($"[Physics] '{part.Name}' not simulated: {reason}");
        }

        Notify(PartCreated, part);
    }

    string? IScriptSceneHost.Destroy(IScriptObject target)
    {
        if (target is not PartScriptObject sceneObject || !_sceneObjects.ContainsKey(sceneObject.Part))
        {
            return "only scene Parts can be destroyed";
        }

        // Gone at once: from the scene, Scene's searches and physics (its contacts end: the others get an Exit).
        var part = sceneObject.Part;

        // What followed it lets go (staying where it is); it stops following its own parent.
        foreach (var child in _parents.Where(link => ReferenceEquals(link.Value.Parent, part)).Select(link => link.Key).ToList())
        {
            Unparent(child);
        }

        if (_parents.Remove(part))
        {
            RefreshGroups();
        }

        RefreshPresent();
        _present.Remove(part);
        if (_workspace is not null)
        {
            _workspace.RemovePart(part);
            _presentVersion = _workspace.Version;
        }
        else if (_scene is { IsReadOnly: false })
        {
            _scene.Remove(part);
        }

        if (_physics is { IsDisposed: false } physics)
        {
            physics.RemovePart(part);
        }

        _sceneObjects.Remove(part);
        _scriptsByPart.Remove(part);
        _animations.Remove(part);
        RemoveAgentsOf(part);
        _destroyedIds[part] = (sceneObject.Id, _dispatchCount);
        _lua.RemoveSceneObject(sceneObject);
        if (sceneObject.Created)
        {
            _createdCount--;
        }

        // Its scripts stop now; their OnDestroy runs once the running callback has finished.
        var stopped = _lua.BeginDestroy(sceneObject);
        if (stopped.Count > 0)
        {
            _pendingDestroys.Enqueue(stopped);
        }

        Notify(PartDestroyed, part);
        return null;
    }

    bool IScriptSceneHost.Raycast(Vector3 origin, Vector3 direction, float maxDistance, ScriptQueryFilter? filter, out ScriptRaycastHit hit)
    {
        hit = default;

        // What the running callback moved counts: physics hears about it first.
        ReportTransformWrites();
        if (_physics is not { IsDisposed: false } physics
            || !physics.Raycast(origin, direction, maxDistance, PhysicsFilterOf(filter), out var found))
        {
            return false;
        }

        PartScriptObject? hitObject = null;
        if (found.Part is not null && !_sceneObjects.TryGetValue(found.Part, out hitObject))
        {
            return false;
        }

        hit = new ScriptRaycastHit(hitObject, found.Distance, found.Point, found.Normal);
        return true;
    }

    List<ScriptRaycastHit> IScriptSceneHost.RaycastAll(Vector3 origin, Vector3 direction, float maxDistance, ScriptQueryFilter? filter, int maxHits)
    {
        ReportTransformWrites();
        var hits = new List<ScriptRaycastHit>();
        if (_physics is not { IsDisposed: false } physics)
        {
            return hits;
        }

        foreach (var found in physics.RaycastAll(origin, direction, maxDistance, PhysicsFilterOf(filter), maxHits))
        {
            PartScriptObject? hitObject = null;
            if (found.Part is null || _sceneObjects.TryGetValue(found.Part, out hitObject))
            {
                hits.Add(new ScriptRaycastHit(hitObject, found.Distance, found.Point, found.Normal));
            }
        }

        return hits;
    }

    bool IScriptSceneHost.SphereCast(Vector3 origin, Vector3 direction, float radius, float maxDistance, ScriptQueryFilter? filter, out ScriptRaycastHit hit)
    {
        hit = default;
        ReportTransformWrites();
        if (_physics is not { IsDisposed: false } physics
            || !physics.SphereCast(origin, direction, radius, maxDistance, PhysicsFilterOf(filter), out var found))
        {
            return false;
        }

        PartScriptObject? hitObject = null;
        if (found.Part is not null && !_sceneObjects.TryGetValue(found.Part, out hitObject))
        {
            return false;
        }

        hit = new ScriptRaycastHit(hitObject, found.Distance, found.Point, found.Normal);
        return true;
    }

    (List<IScriptObject> Objects, bool Environment) IScriptSceneHost.Overlap(bool box, Vector3 center, Vector3 size, Vector3 rotation, ScriptQueryFilter? filter, int maxObjects)
    {
        ReportTransformWrites();
        var objects = new List<IScriptObject>();
        if (_physics is not { IsDisposed: false } physics)
        {
            return (objects, false);
        }

        var physicsFilter = PhysicsFilterOf(filter);
        var result = box
            ? physics.OverlapBox(center, size, rotation, physicsFilter, int.MaxValue)
            : physics.OverlapSphere(center, size.X, physicsFilter, int.MaxValue);
        var found = new List<PartScriptObject>();
        foreach (var part in result.Parts)
        {
            if (_sceneObjects.TryGetValue(part, out var sceneObject))
            {
                found.Add(sceneObject);
            }
        }

        // Scene order (Ids), as Scene's searches answer.
        found.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        for (var i = 0; i < found.Count && i < maxObjects; i++)
        {
            objects.Add(found[i]);
        }

        return (objects, result.Environment);
    }

    Vector3 IScriptSceneHost.Gravity => _physics is { IsDisposed: false } physics ? physics.Gravity : Vector3.Zero;

    string? IScriptSceneHost.SetGravity(Vector3 gravity)
    {
        if (_physics is not { IsDisposed: false } physics)
        {
            return "Physics.Gravity can only be set while physics runs (Play)";
        }

        physics.Gravity = gravity;
        return null;
    }

    string? IScriptSceneHost.SetGroupsCollide(string a, string b, bool collide)
    {
        if (_physics is not { IsDisposed: false } physics)
        {
            return "collision groups only exist while physics runs (Play)";
        }

        var first = physics.CollisionLayerIndex(a, create: true);
        var second = physics.CollisionLayerIndex(b, create: true);
        if (first < 0 || second < 0)
        {
            return $"there can be at most {PhysicsMaterials.MaxLayers} collision groups (Default included)";
        }

        physics.SetLayersCollide(first, second, collide);
        return null;
    }

    bool IScriptSceneHost.GroupsCollide(string a, string b)
    {
        if (_physics is not { IsDisposed: false } physics)
        {
            return true;
        }

        var first = physics.CollisionLayerIndex(a, create: false);
        var second = physics.CollisionLayerIndex(b, create: false);
        return first < 0 || second < 0 || physics.LayersCollide(first, second);
    }

    IReadOnlyList<string> IScriptSceneHost.CollisionGroups => _physics is { IsDisposed: false } physics ? physics.CollisionLayerNames : ["Default"];

    string? IScriptSceneHost.CreateJoint(string kind, IScriptObject a, IScriptObject? b, Vector3 point, Vector3 axis, out int joint)
    {
        joint = 0;
        if (_physics is not { IsDisposed: false } physics)
        {
            return "joints only exist while physics runs (Play)";
        }

        if (a is not PartScriptObject first || !first.Exists || (b is not null && (b is not PartScriptObject { Exists: true })))
        {
            return "a joint connects scene objects that still exist";
        }

        if (!Enum.TryParse<PhysicsConstraintKind>(kind, ignoreCase: false, out var constraintKind))
        {
            return $"unknown joint kind '{kind}'";
        }

        ReportTransformWrites();
        var constraint = physics.CreateConstraint(constraintKind, first.Part, (b as PartScriptObject)?.Part, point, axis, out var error);
        if (constraint is null)
        {
            return error;
        }

        _joints[constraint.Id] = constraint;
        joint = constraint.Id;
        return null;
    }

    object? IScriptSceneHost.JointGet(int joint, string key, out string? error)
    {
        error = null;
        if (!_joints.TryGetValue(joint, out var constraint))
        {
            error = "that joint no longer exists";
            return null;
        }

        var hinge = constraint.Kind == PhysicsConstraintKind.Hinge;
        return key switch
        {
            "Type" => constraint.Kind.ToString(),
            "Active" => !constraint.IsDestroyed && constraint.IsActive,
            "Destroyed" => constraint.IsDestroyed,
            "Enabled" => constraint.Enabled,
            "CollideConnected" => constraint.CollideConnected,
            "PartA" => _sceneObjects.GetValueOrDefault(constraint.PartA),
            "PartB" => constraint.PartB is { } partB ? _sceneObjects.GetValueOrDefault(partB) : null,
            "Angle" => hinge && _physics is { IsDisposed: false } physics ? (double)physics.ConstraintAngle(constraint) : 0d,
            "MotorEnabled" => constraint.MotorEnabled,
            "MotorSpeed" => (double)constraint.MotorSpeed,
            "MotorMaxTorque" => (double)constraint.MotorMaxTorque,
            "LimitsEnabled" => constraint.LimitsEnabled,
            "LowerAngle" => (double)constraint.LowerAngle,
            "UpperAngle" => (double)constraint.UpperAngle,
            _ => Unknown(out error)
        };

        object? Unknown(out string? problem)
        {
            problem = $"{key} is not a valid member of Joint";
            return null;
        }
    }

    string? IScriptSceneHost.JointSet(int joint, string key, object? value)
    {
        if (!_joints.TryGetValue(joint, out var constraint) || constraint.IsDestroyed)
        {
            return "that joint no longer exists";
        }

        var motorOrLimit = key is "MotorEnabled" or "MotorSpeed" or "MotorMaxTorque" or "LimitsEnabled" or "LowerAngle" or "UpperAngle";
        if (motorOrLimit && constraint.Kind != PhysicsConstraintKind.Hinge)
        {
            return $"{key} is for hinges only (this joint is a {constraint.Kind})";
        }

        switch (key)
        {
            case "Enabled" or "CollideConnected" or "MotorEnabled" or "LimitsEnabled":
                if (value is not bool flag)
                {
                    return $"{key} must be true or false";
                }

                if (key == "Enabled")
                {
                    constraint.Enabled = flag;
                }
                else if (key == "CollideConnected")
                {
                    constraint.CollideConnected = flag;
                }
                else if (key == "MotorEnabled")
                {
                    constraint.MotorEnabled = flag;
                }
                else
                {
                    constraint.LimitsEnabled = flag;
                }

                return null;
            case "MotorSpeed" or "MotorMaxTorque" or "LowerAngle" or "UpperAngle":
                if (value is not double number || !double.IsFinite(number) || global::System.Math.Abs(number) > 1e9)
                {
                    return $"{key} must be a finite number";
                }

                if (key == "MotorMaxTorque" && number < 0d)
                {
                    return "MotorMaxTorque must be 0 or more";
                }

                if (key is "LowerAngle" or "UpperAngle" && global::System.Math.Abs(number) > 180d)
                {
                    return $"{key} must be within -180 to 180 degrees";
                }

                if (key == "MotorSpeed")
                {
                    constraint.MotorSpeed = (float)number;
                }
                else if (key == "MotorMaxTorque")
                {
                    constraint.MotorMaxTorque = (float)number;
                }
                else if (key == "LowerAngle")
                {
                    constraint.LowerAngle = (float)number;
                }
                else
                {
                    constraint.UpperAngle = (float)number;
                }

                return null;
            case "Type" or "Active" or "Destroyed" or "PartA" or "PartB" or "Angle":
                return $"{key} is read-only";
            default:
                return $"{key} is not a valid member of Joint";
        }
    }

    void IScriptSceneHost.DestroyJoint(int joint)
    {
        if (_joints.TryGetValue(joint, out var constraint))
        {
            constraint.Destroy();
        }
    }

    // A script's query filter in physics terms: objects become their Parts, tags are the Parts' own (scripts' tags).
    private PhysicsQueryFilter? PhysicsFilterOf(ScriptQueryFilter? filter)
    {
        if (filter is null)
        {
            return null;
        }

        var ignored = new HashSet<Part>(ReferenceEqualityComparer.Instance);
        foreach (var target in filter.Ignore)
        {
            if (target is PartScriptObject sceneObject)
            {
                ignored.Add(sceneObject.Part);
            }
        }

        var ignoreTags = filter.IgnoreTags;
        var onlyTags = filter.OnlyTags;
        Func<Part, bool>? accept = null;
        if (ignored.Count > 0 || ignoreTags.Count > 0 || onlyTags.Count > 0)
        {
            accept = part =>
            {
                if (ignored.Contains(part))
                {
                    return false;
                }

                if (ignoreTags.Count == 0 && onlyTags.Count == 0)
                {
                    return true;
                }

                var tags = _sceneObjects.TryGetValue(part, out var sceneObject) ? sceneObject.Data.Tags : null;
                if (tags is null)
                {
                    return onlyTags.Count == 0;
                }

                if (ignoreTags.Exists(tags.Contains))
                {
                    return false;
                }

                return onlyTags.Count == 0 || onlyTags.Exists(tags.Contains);
            };
        }

        // Queried as a body of that group would collide (a group named for the first time collides with everything).
        int? layer = filter.CollisionGroup is { } group && _physics is { IsDisposed: false } physics && physics.CollisionLayerIndex(group, create: true) is >= 0 and var index
            ? index
            : null;
        return new PhysicsQueryFilter
        {
            AcceptPart = accept, IgnoreTriggers = filter.IgnoreTriggers, IgnoreEnvironment = filter.IgnoreEnvironment, Layer = layer
        };
    }

    private bool IsPresent(Part part)
    {
        RefreshPresent();
        return _present.Contains(part) && (_scene is null || _scene.Contains(part));
    }

    // Someone else added or removed workspace Parts (scripts' own changes keep the mirror in step as they make them).
    private void RefreshPresent()
    {
        if (_workspace is null || _workspace.Version == _presentVersion)
        {
            return;
        }

        _present.Clear();
        foreach (var part in _workspace.Parts)
        {
            _present.Add(part);
        }

        _presentVersion = _workspace.Version;
    }

    private static void Notify(Action<Part>? listener, Part part)
    {
        try
        {
            listener?.Invoke(part);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Lua] Scene listener failed: {ex.Message}");
        }
    }

    // A callback finished: physics follows each Part whose transform it changed (once per Part) and the host is told;
    // then the scripts of Parts it destroyed run their OnDestroy and are released.
    private void OnCallbackCompleted()
    {
        ReportTransformWrites();
        if (!_loading)
        {
            RunPendingDestroys();
            RunPendingAttaches();
        }
    }

    // Scripts attached while a callback ran load now, in the order attached: their top-level chunk runs at once (bound
    // to their Part); Start and Update follow with the frame's scripts. One pass at a time.
    private void RunPendingAttaches()
    {
        if (_runningAttaches || _disposed || _pendingAttaches.Count == 0)
        {
            return;
        }

        _runningAttaches = true;
        try
        {
            while (_pendingAttaches.Count > 0)
            {
                var attached = _pendingAttaches[0];
                _pendingAttaches.RemoveAt(0);
                var part = attached.Target.Part;
                if (attached.Detached || !IsPresent(part))
                {
                    attached.Detached = true;
                    continue;
                }

                var name = $"{attached.Reference.AssetPath} ({part.Name})";
                _sources.TryAdd(name, (attached.Reference, part.Name));
                var instance = _lua.LoadFile(attached.FullPath, name, attached.Target);
                var script = new SceneScript(part, attached.Reference, instance, null);
                attached.Script = script;
                _scripts.Add(script);

                // Its own chunk may have destroyed its Part (then it is already stopped).
                if (!IsPresent(part))
                {
                    continue;
                }

                if (!_scriptsByPart.TryGetValue(part, out var scripts))
                {
                    _scriptsByPart[part] = scripts = [];
                }

                scripts.Add(script);
                if (_physics is { IsDisposed: false } physics)
                {
                    // Anchored, it is now a scripted (kinematic) Part; its contacts are tracked when it listens to them.
                    physics.MarkScripted(part);
                    if (ContactHandlers.Any(instance.HasHandler))
                    {
                        physics.SetReportsContacts(part, true);
                    }
                }
            }
        }
        finally
        {
            _runningAttaches = false;
        }

        RunPendingDestroys();
    }

    private void ReportTransformWrites()
    {
        if (_pendingWrites.Count == 0)
        {
            return;
        }

        var writes = _pendingWrites.ToArray();
        _pendingWrites.Clear();
        foreach (var write in writes)
        {
            if (_physics is { IsDisposed: false } physics)
            {
                physics.ApplyScriptWrite(write.Part, write.Moved, write.Scaled);
            }

            TransformWritten?.Invoke(new ScriptTransformWrite(write.Part, write.Moved, write.Scaled));
        }
    }

    // OnDestroy of each destroyed Part's scripts (in load order; a failure is reported and the rest carry on), then
    // they are released. One pass at a time: Parts destroyed meanwhile join the queue.
    private void RunPendingDestroys()
    {
        if (_runningDestroys || _disposed || _pendingDestroys.Count == 0)
        {
            return;
        }

        _runningDestroys = true;
        try
        {
            while (_pendingDestroys.Count > 0)
            {
                var instances = _pendingDestroys.Dequeue();
                foreach (var instance in instances)
                {
                    _lua.RunDestroy(instance);
                }

                foreach (var instance in instances)
                {
                    instance.Dispose();
                }
            }
        }
        finally
        {
            _runningDestroys = false;
        }
    }

    // The contacts and trigger overlaps since the last frame, to both sides' scripts.
    private void DispatchContacts()
    {
        if (_physics is not { IsDisposed: false } physics)
        {
            return;
        }

        _dispatchCount++;
        _contactEvents.Clear();
        physics.CollectContactEvents(_contactEvents);
        foreach (var contact in _contactEvents)
        {
            var handler = (contact.IsTrigger, contact.Phase) switch
            {
                (false, PhysicsContactPhase.Enter) => ScriptEventHandler.OnCollisionEnter,
                (false, PhysicsContactPhase.Stay) => ScriptEventHandler.OnCollisionStay,
                (false, _) => ScriptEventHandler.OnCollisionExit,
                (true, PhysicsContactPhase.Enter) => ScriptEventHandler.OnTriggerEnter,
                (true, PhysicsContactPhase.Stay) => ScriptEventHandler.OnTriggerStay,
                _ => ScriptEventHandler.OnTriggerExit
            };
            Deliver(contact.Part, contact.Other, handler, contact.Contact);
            if (contact.Other is { } other)
            {
                Deliver(other, contact.Part, handler, contact.Contact?.Flipped);
            }

            // Any script's object:OnTriggerEntered / OnTriggerExited connections, for whichever side is a trigger.
            if (contact.IsTrigger && contact.Phase != PhysicsContactPhase.Stay)
            {
                var entered = contact.Phase == PhysicsContactPhase.Enter;
                if (contact.Part.IsTrigger)
                {
                    DispatchTriggerConnections(contact.Part, contact.Other, entered);
                }

                if (contact.Other is { IsTrigger: true } otherTrigger)
                {
                    DispatchTriggerConnections(otherTrigger, contact.Part, entered);
                }
            }
        }

        _contactEvents.Clear();

        // Parts destroyed before this dispatch have had their last events.
        if (_destroyedIds.Count > 0)
        {
            foreach (var (part, destroyed) in _destroyedIds)
            {
                if (destroyed.Dispatch < _dispatchCount)
                {
                    _destroyedIds.Remove(part);
                }
            }
        }
    }

    // One event to the receiving Part's scripts (none once it is destroyed), other as each script's own wrapper, with the
    // contact as the receiver sees it (none for an Exit).
    private void Deliver(Part receiver, Part? other, ScriptEventHandler handler, PhysicsContactPoint? contact)
    {
        if (!_scriptsByPart.TryGetValue(receiver, out var scripts))
        {
            return;
        }

        var otherId = other is null ? 0
            : _sceneObjects.TryGetValue(other, out var otherObject) ? otherObject.Id
            : _destroyedIds.TryGetValue(other, out var destroyed) ? destroyed.Id
            : 0;
        ScriptContact? scriptContact = contact is { } point ? new ScriptContact(point.Point, point.Normal, point.RelativeVelocity, other is null) : null;
        // By index: a handler may attach a script to this Part (it joins the list, and hears the next event).
        var count = scripts.Count;
        for (var i = 0; i < count; i++)
        {
            if (scripts[i].Instance is { } instance && instance.HasHandler(handler))
            {
                _lua.CallHandler(instance, handler, otherId, scriptContact);
            }
        }
    }

    private void NoteTransformWrite(Part part, bool moved, bool scaled)
    {
        for (var i = 0; i < _pendingWrites.Count; i++)
        {
            if (ReferenceEquals(_pendingWrites[i].Part, part))
            {
                _pendingWrites[i] = (part, _pendingWrites[i].Moved || moved, _pendingWrites[i].Scaled || scaled);
                return;
            }
        }

        _pendingWrites.Add((part, moved, scaled));
    }

    // The GUI decided a click happened (during pointer input): its callbacks run in the next Update.
    private void OnButtonClicked(int buttonId)
    {
        if (!_disposed)
        {
            _pendingGuiEvents.Add((GuiButtonEvent.Click, buttonId));
        }
    }

    // A press, release or hover change (in the order the GUI raised them, clicks included): run in the next Update.
    private void OnButtonEvent(GuiButtonEvent kind, int buttonId)
    {
        if (!_disposed)
        {
            _pendingGuiEvents.Add((kind, buttonId));
        }
    }

    // A TextBox was submitted, let go of the keyboard or got it: its callbacks run with the next Update's events.
    private void OnTextBoxSubmitted(int id, string text)
    {
        if (!_disposed)
        {
            _lua.QueueTextBoxEvent("Submit", id, text);
        }
    }

    private void OnTextBoxFocusLost(int id, bool submitted)
    {
        if (!_disposed)
        {
            _lua.QueueTextBoxEvent("FocusLost", id, submitted);
        }
    }

    private void OnTextBoxFocused(int id)
    {
        if (!_disposed)
        {
            _lua.QueueTextBoxEvent("Focused", id, null);
        }
    }

    // A script attached while playing: its Part, file and, once loaded, its scene script.
    private sealed class AttachedScript(int handle, PartScriptObject target, ScriptReference reference, string fullPath)
    {
        public int Handle { get; } = handle;

        public PartScriptObject Target { get; } = target;

        public ScriptReference Reference { get; } = reference;

        public string FullPath { get; } = fullPath;

        public SceneScript? Script { get; set; }

        public bool Detached { get; set; }
    }

    private SceneScript Load(string? projectRoot, Part target, PartScriptObject self, ScriptReference reference)
    {
        // Display name: which file, on which object (diagnostics and print output).
        var name = $"{reference.AssetPath} ({target.Name})";
        _sources.TryAdd(name, (reference, target.Name));
        if (string.IsNullOrEmpty(projectRoot)
            || !ProjectAssetPaths.TryResolve(projectRoot, reference.AssetPath, out var fullPath)
            || !File.Exists(fullPath))
        {
            var missing = new ScriptDiagnostic(name, ScriptPhase.Load, ScriptDiagnosticKind.MissingScript, null, $"Missing script: {reference.AssetPath}");
            _lua.Report(missing);
            return new SceneScript(target, reference, null, missing);
        }

        return new SceneScript(target, reference, _lua.LoadFile(fullPath, name, self), null);
    }

    // Every message still goes to the normal log; script messages also go out structured (tracebacks stay log-only).
    private void OnLog(ScriptLogMessage message)
    {
        if (_log is not null)
        {
            _log(message);
        }
        else
        {
            Console.WriteLine(message.ToString());
        }

        if (_output is null || message.Kind == ScriptLogKind.Detail || !_sources.TryGetValue(message.ScriptName, out var source))
        {
            return;
        }

        var line = message.Diagnostic?.Line;
        var text = message.Diagnostic?.Message ?? message.Text;
        try
        {
            _output(new ScriptOutputEvent(message.Kind, source.Script, source.ObjectName, line, text));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Lua] Output consumer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A scene Part as scripts see it, in the units Scene.json uses: a primitive's Position is its centre and its Scale
    /// is its size (the scale of its unit shape); a model's Position/Scale are its instance origin and scale. Rotation
    /// is the editor's Euler degrees. Scale writes respect the editor's minimum object size.
    /// </summary>
    private sealed class PartScriptObject(Part part, SceneScriptRuntime runtime) : IScriptPart, IScriptAnimated, IScriptGraphicsTarget
    {
        public Part Part => part;

        public bool IsModel => part.Model is not null;

        public IReadOnlyList<AnimationClip> AnimationClips => part.Model?.Asset?.Animation.Clips ?? (IReadOnlyList<AnimationClip>)[];

        public ModelAnimator? Animator => runtime._disposed ? null : runtime._animations.Get(part);

        /// <summary>Its Id in this runtime.</summary>
        public int Id { get; set; }

        /// <summary>Made by a script (Scene.Create).</summary>
        public bool Created { get; init; }

        public string Name => part.Name;

        /// <summary>The Part's shape ("Block", "Sphere", ...), or "Model" for an imported model.</summary>
        public string TypeName => part.Model is null ? part.ShapeType.ToString() : "Model";

        public bool Exists => runtime.IsPresent(part);

        public Vector3 MinimumScale
        {
            get
            {
                if (part.Model is not { } model)
                {
                    return new Vector3(PartCollision.MinPartSize, PartCollision.MinPartSize, PartCollision.MinPartSize);
                }

                // Size = bounds size x scale; an axis with no extent keeps scale 1 (see ModelTransform): no bound there.
                var bounds = model.BoundsSize;
                return new Vector3(MinimumAxisScale(bounds.X), MinimumAxisScale(bounds.Y), MinimumAxisScale(bounds.Z));
            }
        }

        public ScriptTransform ReadTransform() =>
            part.Model is null
                ? new ScriptTransform(part.Position, part.Rotation, part.Size)
                : new ScriptTransform(ModelTransform.GetOrigin(part), part.Rotation, ModelTransform.GetScale(part));

        public void WriteTransform(ScriptTransform transform)
        {
            if (!IsFinite(transform.Position) || !IsFinite(transform.Rotation) || !IsFinite(transform.Scale))
            {
                return;
            }

            var before = ReadTransform();
            if (part.Model is null)
            {
                part.Position = transform.Position;
                part.Rotation = transform.Rotation;
                part.Size = transform.Scale;
            }
            else
            {
                ModelTransform.SetInstanceTransform(part, transform.Position, transform.Rotation, transform.Scale);
            }

            // A script writes its whole transform back even when it changed one value: only real changes are reported.
            var moved = !before.Position.Equals(transform.Position) || !before.Rotation.Equals(transform.Rotation);
            var scaled = !before.Scale.Equals(transform.Scale);
            if (moved || scaled)
            {
                runtime.NoteTransformWrite(part, moved, scaled);
            }
        }

        public ScriptPartData Data { get; } = new();

        public IScriptObject? Parent =>
            runtime._parents.TryGetValue(part, out var link) && runtime._sceneObjects.TryGetValue(link.Parent, out var parent) ? parent : null;

        public IReadOnlyList<IScriptObject> Children =>
            runtime._parents.Where(link => ReferenceEquals(link.Value.Parent, part))
                .Select(link => runtime._sceneObjects.TryGetValue(link.Key, out var child) ? child : null)
                .OfType<PartScriptObject>()
                .OrderBy(child => child.Id)
                .ToList();

        public string? SetParent(IScriptObject? parent, Vector3? offsetPosition, Vector3? offsetRotation, string? bone) =>
            runtime.SetParentOf(this, parent, offsetPosition, offsetRotation, bone);

        public bool TryGetGround(float maxDistance, out ScriptRaycastHit ground) => runtime.TryGetGroundOf(this, maxDistance, out ground);

        public (Vector3 Min, Vector3 Max) Bounds
        {
            get
            {
                // The exact shape's extremes along each world axis (a tight box, not the rotated Size box).
                var shape = PartShape.Of(part);
                return (new Vector3(shape.Support(new Vector3(-1f, 0f, 0f)).X, shape.Support(new Vector3(0f, -1f, 0f)).Y, shape.Support(new Vector3(0f, 0f, -1f)).Z),
                    new Vector3(shape.Support(Vector3.UnitX).X, shape.Support(Vector3.UnitY).Y, shape.Support(new Vector3(0f, 0f, 1f)).Z));
            }
        }

        public bool TryGet(string key, out object? value)
        {
            value = key switch
            {
                "Anchored" => part.Anchored,
                "GravityEnabled" => part.GravityEnabled,
                "CanCollide" => part.CanCollide,
                "IsTrigger" => part.IsTrigger,
                "Friction" => (double)part.Friction,
                "Mass" => (double)(part.MassOverride ?? PhysicsWorld.AutoMassOf(part, runtime._physics?.Settings) ?? 0f),
                "Color" => ScriptObjectAccess.ToHex(part.Color),
                "Visible" => part.Visible,
                "IsSleeping" => runtime._physics is { IsDisposed: false } physics && physics.KindOf(part) is { } kind && kind != PhysicsBodyKind.Static
                    && !physics.IsAwake(part),
                "RotationLocked" => runtime._physics is { IsDisposed: false } lockWorld && lockWorld.IsRotationLocked(part),
                "GravityScale" => (double)(runtime._physics is { IsDisposed: false } scaleWorld ? scaleWorld.GravityScaleOf(part) : 1f),
                "LinearDamping" => (double)(runtime._physics is { IsDisposed: false } linearWorld ? linearWorld.DampingOf(part).Linear : 0f),
                "AngularDamping" => (double)(runtime._physics is { IsDisposed: false } angularWorld ? angularWorld.DampingOf(part).Angular : 0f),
                "CollisionGroup" => runtime._physics is { IsDisposed: false } groupWorld ? groupWorld.CollisionLayerName(groupWorld.CollisionLayerOf(part)) : "Default",
                _ => null
            };
            return value is not null;
        }

        public string? Set(string key, object? value)
        {
            switch (key)
            {
                case "Name":
                    if (value is not string name || name.Trim().Length is 0 or > MaxNameLength)
                    {
                        return $"Name must be a non-empty string of at most {MaxNameLength} characters";
                    }

                    part.Name = name.Trim();
                    return null;
                case "Color":
                    // The renderer rebuilds the Part's mesh when its colour changes (a model's tint likewise); Stop restores it.
                    if (value is not string hex || !ScriptObjectAccess.TryParseHex(hex, out var color))
                    {
                        return "Color must be \"#RRGGBB\" (six hex digits)";
                    }

                    part.Color = color;
                    return null;
                case "Visible":
                    if (value is not bool visible)
                    {
                        return "Visible must be true or false";
                    }

                    part.Visible = visible;
                    return null;
                case "Anchored":
                    if (value is not bool anchored)
                    {
                        return "Anchored must be true or false";
                    }

                    if (part.Anchored != anchored)
                    {
                        // Its body changes kind now (an unanchored Part starts falling from where it is).
                        part.Anchored = anchored;
                        runtime.ReportTransformWrites();
                        if (runtime._physics is { IsDisposed: false } world)
                        {
                            world.ApplyKindChange(part);
                        }
                    }

                    return null;
                case "RotationLocked":
                    if (value is not bool locked)
                    {
                        return "RotationLocked must be true or false";
                    }

                    // Runtime only (the physics body's): Stop forgets it with the session's physics.
                    return runtime._physics is { IsDisposed: false } lockWorld && lockWorld.SetRotationLocked(part, locked)
                        ? null
                        : ScriptObjectAccess.NotSimulated(this);
                case "GravityScale" or "LinearDamping" or "AngularDamping":
                    // Runtime only, like RotationLocked: the physics body's, forgotten at Stop.
                    if (value is not double amount || !double.IsFinite(amount) || global::System.Math.Abs(amount) > 1e6)
                    {
                        return $"{key} must be a finite number";
                    }

                    if (key != "GravityScale" && amount < 0d)
                    {
                        return $"{key} must be 0 or more (0: no drag)";
                    }

                    if (runtime._physics is not { IsDisposed: false } tuneWorld)
                    {
                        return ScriptObjectAccess.NotSimulated(this);
                    }

                    var (linear, angular) = tuneWorld.DampingOf(part);
                    var tuned = key switch
                    {
                        "GravityScale" => tuneWorld.SetGravityScale(part, (float)amount),
                        "LinearDamping" => tuneWorld.SetDamping(part, (float)amount, angular),
                        _ => tuneWorld.SetDamping(part, linear, (float)amount)
                    };
                    return tuned ? null : ScriptObjectAccess.NotSimulated(this);
                case "CollisionGroup":
                    if (value is not string group || group.Length is 0 or > ScriptPartData.MaxNameLength)
                    {
                        return $"CollisionGroup must be a group name (a string of 1 to {ScriptPartData.MaxNameLength} characters)";
                    }

                    if (runtime._physics is not { IsDisposed: false } groupWorld)
                    {
                        return ScriptObjectAccess.NotSimulated(this);
                    }

                    var layer = groupWorld.CollisionLayerIndex(group, create: true);
                    if (layer < 0)
                    {
                        return $"there can be at most {PhysicsMaterials.MaxLayers} collision groups (Default included)";
                    }

                    return groupWorld.SetCollisionLayer(part, layer) ? null : ScriptObjectAccess.NotSimulated(this);
                case "IsTrigger":
                    if (value is not bool trigger)
                    {
                        return "IsTrigger must be true or false";
                    }

                    part.IsTrigger = trigger;
                    break;
                case "GravityEnabled" or "CanCollide":
                    if (value is not bool flag)
                    {
                        return $"{key} must be true or false";
                    }

                    if (key == "GravityEnabled")
                    {
                        part.GravityEnabled = flag;
                    }
                    else
                    {
                        part.CanCollide = flag;
                    }

                    break;
                case "Friction":
                    if (value is not double friction || !double.IsFinite(friction))
                    {
                        return "Friction must be a finite number (0 to 1)";
                    }

                    part.Friction = (float)global::System.Math.Clamp(friction, 0d, 1d);
                    break;
                case "Mass":
                    // nil: Auto (density x volume); otherwise a manual mass, kept within the editor's range.
                    if (value is null)
                    {
                        part.MassOverride = null;
                    }
                    else if (value is double mass && double.IsFinite(mass) && mass > 0d)
                    {
                        part.MassOverride = (float)global::System.Math.Clamp(mass, Part.MinMass, Part.MaxMass);
                    }
                    else
                    {
                        return "Mass must be a positive number, or nil for Auto";
                    }

                    break;
                default:
                    return $"{key} is not a valid member of ScriptObject";
            }

            if (runtime._physics is { IsDisposed: false } settingsWorld)
            {
                settingsWorld.ApplySettingsChange(part);
            }

            return null;
        }

        public string? Push(string kind, Vector3 value, Vector3 point)
        {
            var what = kind switch
            {
                "force" => "ApplyForce", "torque" => "ApplyTorque", "angular" => "ApplyAngularImpulse", "forceat" => "ApplyForceAtPoint",
                _ => "ApplyImpulseAtPoint"
            };
            if (BodyProblem(what) is { } problem)
            {
                return problem;
            }

            // What the running callback moved counts first (an impulse at a point turns about where the body is now).
            runtime.ReportTransformWrites();
            var physics = runtime._physics!;
            var done = kind switch
            {
                "force" => physics.TryApplyForce(part, value),
                "torque" => physics.TryApplyTorque(part, value),
                "angular" => physics.TryApplyAngularImpulse(part, value),
                "forceat" => physics.TryApplyForceAtPoint(part, value, point),
                _ => physics.TryApplyImpulseAtPoint(part, value, point)
            };
            return done ? null : $"{what} expects finite numbers";
        }

        public string? Wake() =>
            runtime._physics is { IsDisposed: false } physics && physics.TryWake(part) ? null : ScriptObjectAccess.NotSimulated(this);

        public Vector3 GetVelocity(string key) =>
            runtime._physics is not { IsDisposed: false } physics ? Vector3.Zero
            : key == "AngularVelocity" ? physics.AngularVelocityOf(part)
            : key == "SurfaceVelocity" ? physics.SurfaceVelocityOf(part)
            : physics.LinearVelocityOf(part);

        public string? SetVelocity(string key, Vector3 value)
        {
            if (runtime._physics is not { IsDisposed: false } physics || physics.KindOf(part) is null)
            {
                return ScriptObjectAccess.NotSimulated(this);
            }

            // A conveyor surface: anchored Parts only (what rests on it is carried; the Part stays where it is).
            if (key == "SurfaceVelocity")
            {
                return !part.Anchored ? $"SurfaceVelocity works on an anchored Part only ('{part.Name}' is unanchored)"
                    : physics.SetSurfaceVelocity(part, value) ? null : "SurfaceVelocity must be finite";
            }

            // An anchored Part moves on its own at this velocity (a moving or spinning platform); its other velocity stays.
            if (part.Anchored)
            {
                runtime.ReportTransformWrites();
                var linear = key == "LinearVelocity" ? value : KinematicVelocity().Linear;
                var angular = key == "AngularVelocity" ? value : KinematicVelocity().Angular;
                return physics.SetKinematicVelocity(part, linear, angular) ? null : $"{key} must be finite";
            }

            return (key == "AngularVelocity" ? physics.TrySetAngularVelocity(part, value) : physics.TrySetLinearVelocity(part, value))
                ? null
                : $"{key} must be finite";
        }

        public Vector3 VelocityAtPoint(Vector3 point) =>
            runtime._physics is { IsDisposed: false } physics ? physics.VelocityAtPoint(part, point) : Vector3.Zero;

        public float? EffectiveMass => runtime._physics is { IsDisposed: false } physics ? physics.EffectiveMassOf(part) : null;

        public string? IgnoreCollisionWith(IScriptObject other, bool ignore)
        {
            if (other is not PartScriptObject otherPart || ReferenceEquals(otherPart, this))
            {
                return "IgnoreCollisionWith expects another scene object";
            }

            return runtime._physics is { IsDisposed: false } physics && physics.SetCollisionIgnored(part, otherPart.Part, ignore)
                ? null
                : ScriptObjectAccess.NotSimulated(this);
        }

        // The velocities an anchored Part moves on its own with now (zero when it does not).
        private (Vector3 Linear, Vector3 Angular) KinematicVelocity() =>
            runtime._physics is { IsDisposed: false } physics && physics.KindOf(part) == PhysicsBodyKind.Kinematic
                ? physics.KinematicVelocityOf(part)
                : (Vector3.Zero, Vector3.Zero);

        public string? ApplyImpulse(Vector3 impulse)
        {
            if (BodyProblem("ApplyImpulse") is { } problem)
            {
                return problem;
            }

            return runtime._physics!.TryApplyImpulse(part, impulse) ? null : "ApplyImpulse expects three finite numbers (x, y, z)";
        }

        // Velocities and impulses act on a dynamic body only: anchored Parts follow their Transform.
        private string? BodyProblem(string what) =>
            runtime._physics is not { IsDisposed: false } physics || physics.KindOf(part) is not { } kind ? $"'{part.Name}' is not simulated by physics"
            : kind != PhysicsBodyKind.Dynamic ? $"{what} works on an unanchored Part only ('{part.Name}' is anchored: move it through its Transform)"
            : null;

        private static float MinimumAxisScale(float boundsSize) =>
            boundsSize > 1e-6f ? PartCollision.MinPartSize / boundsSize : 0f;

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
