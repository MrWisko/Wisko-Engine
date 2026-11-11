using System.Globalization;
using System.Text;
using System.Text.Json;
using Wisko.Engine.Gameplay;
using Wisko.Engine.Gameplay.Navigation;
using Wisko.Engine.Math;
using KeraLua;
using LuaState = KeraLua.Lua;

namespace Wisko.Engine.Scripting;

// hostGameplay(op, ...) from the gameplay prelude (Engine/Scripting/Lua/gameplay.lua; arguments
// checked there), Time.OnUpdate callbacks, trigger connections and game saves. Like the other host functions it never
// throws and never raises a Lua error: it answers with values (false/nil and a message when something is not possible).
public sealed partial class LuaScriptRuntime
{
    private const string FrameEventName = "frame:update";
    private const string CameraActivatedEventName = "camera:activated";

    /// <summary>Format marker and version of save files (the developer's own data version is separate).</summary>
    public const string SaveFormat = "wisko-save";

    public const int SaveFormatVersion = 1;

    private const int MaxSaveDepth = 70;
    private const int MaxLoadValues = 1_000_000;

    // Handles scripts made (agents 'a', emitters 'p', cameras 'c'): they go with the script that made them.
    private readonly List<(LuaScriptInstance Owner, char Kind, int Id)> _owned = [];

    private static string TriggerEventName(int objectId, bool entered) =>
        (entered ? "trigger+:" : "trigger-:") + objectId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Time.OnUpdate connections (diagnostics).</summary>
    public int FrameCallbackCount => _eventHandlers.Count(handler => handler.Active && handler.Internal && handler.Name == FrameEventName);

    private GameplaySession? Gameplay => _options.Gameplay;

    private IScriptGameplayScene? GameplayScene => SceneHost as IScriptGameplayScene;

    /// <summary>
    /// Runs every Time.OnUpdate callback once, in connection order, each as the script that connected it, with this
    /// frame's dt (after the scripts' Update, before physics).
    /// </summary>
    internal void RunFrameCallbacks()
    {
        ThrowIfUnusable();

        // A reused list (no allocation per frame): the callbacks connected before this pass, in connection order.
        _frameCallbacks.Clear();
        foreach (var handler in _eventHandlers)
        {
            if (handler.Active && handler.Internal && string.Equals(handler.Name, FrameEventName, StringComparison.Ordinal))
            {
                _frameCallbacks.Add(handler);
            }
        }

        foreach (var handler in _frameCallbacks)
        {
            if (handler.Active && IsRunnable(handler.Owner))
            {
                // f(dt), as Update is called.
                Call(handler.Owner, handler.FunctionReference, ScriptPhase.FrameCallback, passDeltaTime: true);
            }
        }

        _frameCallbacks.Clear();
    }

    private readonly List<EventHandlerEntry> _frameCallbacks = [];

    /// <summary>
    /// A trigger's overlap began or ended: its OnTriggerEntered / OnTriggerExited connections run now (with the other
    /// object, nil for the environment), each as the script that connected it.
    /// </summary>
    internal void DispatchTriggerConnections(IScriptObject trigger, IScriptObject? other, bool entered)
    {
        var triggerId = IdOfObject(trigger);
        if (_disposed || triggerId <= 0)
        {
            return;
        }

        var name = TriggerEventName(triggerId, entered);
        List<EventHandlerEntry>? handlers = null;
        foreach (var handler in _eventHandlers)
        {
            if (handler.Active && handler.Internal && string.Equals(handler.Name, name, StringComparison.Ordinal))
            {
                (handlers ??= []).Add(handler);
            }
        }

        if (handlers is null)
        {
            return;
        }

        var otherId = other is null ? 0 : IdOfObject(other);
        var call = new QueuedEvent(name, [otherId > 0 ? new EventObject(otherId) : null, new EventObject(triggerId)], otherId > 0 ? "oo" : "vo", Internal: true);
        foreach (var handler in handlers)
        {
            if (handler.Active && IsRunnable(handler.Owner))
            {
                CallEventHandler(handler, call, ScriptPhase.TriggerCallback);
            }
        }
    }

    /// <summary>Whether any script listens to this object's trigger events (the host keeps its contacts reported).</summary>
    internal bool HasTriggerConnections(IScriptObject trigger)
    {
        var id = IdOfObject(trigger);
        if (id <= 0)
        {
            return false;
        }

        var entered = TriggerEventName(id, true);
        var exited = TriggerEventName(id, false);
        return _eventHandlers.Exists(handler => handler.Active && handler.Internal && (handler.Name == entered || handler.Name == exited));
    }

    // f -> a connection number (an internal handler of that name, owned by the running script), or false, message.
    private int ConnectInternal(LuaState lua, int functionIndex, string name, string what)
    {
        if (_running is not { } watcher || lua.Type(functionIndex) != LuaType.Function)
        {
            lua.PushBoolean(false);
            lua.PushString($"{what} is not available here");
            return 2;
        }

        if (_eventHandlers.Count(handler => ReferenceEquals(handler.Owner, watcher)) >= MaxHandlersPerScript)
        {
            lua.PushBoolean(false);
            lua.PushString($"this script already has {MaxHandlersPerScript:N0} event connections: disconnect the ones it no longer needs");
            return 2;
        }

        lua.PushCopy(functionIndex);
        var handlerId = ++_nextHandlerId;
        _eventHandlers.Add(new EventHandlerEntry(handlerId, name, watcher, lua.Ref(LuaRegistry.Index)) { Internal = true });
        lua.PushInteger(handlerId);
        return 1;
    }

    // hostScene("ontrigger", id, entered, f): object:OnTriggerEntered / OnTriggerExited.
    private int TriggerConnection(LuaState lua, IScriptObject target, bool entered)
    {
        var answers = ConnectInternal(lua, 4, TriggerEventName(IdOfObject(target), entered), entered ? "OnTriggerEntered" : "OnTriggerExited");
        if (answers == 1)
        {
            GameplayScene?.WatchTriggers(target);
        }

        return answers;
    }

    // A script's handles go with it (faulted, released, its object destroyed, stopped).
    private void RemoveOwned(LuaScriptInstance instance)
    {
        if (_owned.Count == 0)
        {
            return;
        }

        foreach (var (owner, kind, id) in _owned)
        {
            if (ReferenceEquals(owner, instance))
            {
                ReleaseHandle(kind, id);
            }
        }

        _owned.RemoveAll(entry => ReferenceEquals(entry.Owner, instance));
    }

    private void ReleaseHandle(char kind, int id)
    {
        switch (kind)
        {
            case 'a' when GameplayScene?.FindAgent(id) is { } agent:
                GameplayScene.DestroyAgent(agent);
                break;
            case 'p' when Gameplay?.Particles.Find(id) is { } emitter:
                Gameplay.Particles.Destroy(emitter);
                break;
            case 'c' when Gameplay?.Cameras.Find(id) is { } camera:
                DestroyCamera(camera);
                break;
        }
    }

    private void Own(char kind, int id)
    {
        if (_running is { } owner)
        {
            _owned.Add((owner, kind, id));
        }
    }

    private void DestroyCamera(ScriptCameraRig camera)
    {
        var director = Gameplay!.Cameras;
        var wasActive = ReferenceEquals(director.Active, camera);
        director.Destroy(camera);
        if (wasActive)
        {
            QueueCameraActivated(null, camera.Name);
        }
    }

    private void QueueCameraActivated(string? shown, string? previous)
    {
        if (_eventHandlers.Exists(handler => handler.Active && handler.Internal && handler.Name == CameraActivatedEventName) && _eventQueue.Count < MaxQueuedEvents)
        {
            _eventQueue.Add(new QueuedEvent(CameraActivatedEventName, [shown, previous], "vv", Internal: true));
        }
    }

    /// <summary>Publishes the Time values (time scale, unscaled delta, real time) for this frame.</summary>
    private void PublishGameplayTime(double unscaledDeltaTime)
    {
        _main.PushString("unscaledDeltaTime");
        _main.PushNumber(unscaledDeltaTime);
        _main.RawSet(-3);
        _main.PushString("timeScale");
        _main.PushNumber(Gameplay?.TimeScale ?? 1d);
        _main.RawSet(-3);
        _main.PushString("realTime");
        _main.PushNumber(Gameplay?.RealTime ?? ElapsedTime);
        _main.RawSet(-3);
    }

    private int HostGameplay(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false) ?? string.Empty;
            if (request == "onupdate")
            {
                return ConnectInternal(lua, 2, FrameEventName, "Time.OnUpdate");
            }

            if (request == "camactivated")
            {
                return ConnectInternal(lua, 2, CameraActivatedEventName, "Cameras.OnActivated");
            }

            if (request.StartsWith("nav", StringComparison.Ordinal) || request.StartsWith("agent", StringComparison.Ordinal))
            {
                return NavigationRequest(lua, request);
            }

            if (Gameplay is not { } session)
            {
                return Answer(lua, "this is only available while the game plays");
            }

            switch (request)
            {
                case "settimescale":
                    return Answer(lua, session.SetTimeScale(lua.ToNumber(2)));
                case "clock":
                    return ClockValue(lua, session.Clock, lua.ToString(2, false));
                case "clockset":
                    return Answer(lua, (lua.ToString(2, false)) switch
                    {
                        "DayLength" => session.Clock.SetDayLength(lua.ToNumber(3)),
                        "Scale" => session.Clock.SetScale(lua.ToNumber(3)),
                        "Paused" => SetPaused(session.Clock, lua.ToBoolean(3)),
                        var key => $"{key} cannot be set"
                    });
                case "clockjump":
                    return Answer(lua, session.Clock.Set(lua.ToNumber(2), lua.ToNumber(3)));
                case "dline":
                    lua.PushBoolean(session.Debug.AddLine(VectorAt(lua, 2), VectorAt(lua, 5), VectorAt(lua, 8), lua.ToNumber(11)));
                    return 1;
                case "dsphere":
                    lua.PushBoolean(session.Debug.AddSphere(VectorAt(lua, 2), (float)lua.ToNumber(5), VectorAt(lua, 6), lua.ToNumber(9)));
                    return 1;
                case "dbox":
                    lua.PushBoolean(session.Debug.AddBox(VectorAt(lua, 2), VectorAt(lua, 5), VectorAt(lua, 8), VectorAt(lua, 11), lua.ToNumber(14)));
                    return 1;
                case "dclear":
                    session.Debug.Clear();
                    return 0;
                case "dcount":
                    lua.PushInteger(session.Debug.Count);
                    return 1;
            }

            if (request.StartsWith("chat", StringComparison.Ordinal))
            {
                return ChatRequest(lua, session, request);
            }

            if (request.Length > 0 && request[0] == 'p')
            {
                return ParticleRequest(lua, session, request);
            }

            if (request.StartsWith("cam", StringComparison.Ordinal))
            {
                return CameraRequest(lua, session, request);
            }

            return SaveRequest(lua, session, request);
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] Gameplay request failed: {ex}");
            lua.PushBoolean(false);
            lua.PushString("the request failed");
            return 2;
        }
    }

    private static string? SetPaused(GameClock clock, bool paused)
    {
        clock.Paused = paused;
        return null;
    }

    private static int ClockValue(LuaState lua, GameClock clock, string? key)
    {
        switch (key)
        {
            case "Day":
                lua.PushInteger(clock.Day);
                break;
            case "Hour":
                lua.PushNumber(clock.Hour);
                break;
            case "Minute":
                lua.PushInteger((long)global::System.Math.Floor((clock.Hour - global::System.Math.Floor(clock.Hour)) * 60d + 1e-9));
                break;
            case "TimeOfDay":
                lua.PushNumber(clock.Hour / 24d);
                break;
            case "TotalHours":
                lua.PushNumber(clock.TotalHours);
                break;
            case "DayLength":
                lua.PushNumber(clock.DayLength);
                break;
            case "Scale":
                lua.PushNumber(clock.Scale);
                break;
            case "Paused":
                lua.PushBoolean(clock.Paused);
                break;
            default:
                lua.PushNil();
                break;
        }

        return 1;
    }

    // Particles: pnew -> id; pget (id, key) -> value(s); pset (id, key, values); pburst (id, n) -> made; pclear / pdestroy (id); pcount -> total.
    private int ParticleRequest(LuaState lua, GameplaySession session, string request)
    {
        var particles = session.Particles;
        if (request == "pnew")
        {
            if (particles.Create() is not { } made)
            {
                return Answer(lua, $"there can be at most {ParticleSystem.MaxEmitters} particle emitters at once: destroy the ones no longer needed");
            }

            Own('p', made.Id);
            lua.PushInteger(made.Id);
            return 1;
        }

        if (request == "pcount")
        {
            lua.PushInteger(particles.TotalCount);
            return 1;
        }

        var emitter = particles.Find((int)lua.ToInteger(2));
        if (emitter is null)
        {
            if (request is "pget" && lua.ToString(3, false) == "Exists")
            {
                lua.PushBoolean(false);
                return 1;
            }

            return request is "pdestroy" or "pclear" ? 0 : Answer(lua, "that particle emitter no longer exists");
        }

        switch (request)
        {
            case "pdestroy":
                particles.Destroy(emitter);
                return 0;
            case "pclear":
                emitter.Clear();
                return 0;
            case "pburst":
                lua.PushInteger(emitter.Burst((int)global::System.Math.Clamp(lua.ToNumber(3), 0d, ParticleEmitter.MaxParticlesPerEmitter),
                    _options.Camera?.Position ?? Vector3.Zero, particles.Room));
                return 1;
            case "pget":
                switch (lua.ToString(3, false))
                {
                    case "Position": return PushVector(lua, emitter.Position);
                    case "Area": return PushVector(lua, emitter.Area);
                    case "Velocity": return PushVector(lua, emitter.Velocity);
                    case "Color": return PushVector(lua, emitter.Color);
                    case "Rate": lua.PushNumber(emitter.Rate); return 1;
                    case "Lifetime": lua.PushNumber(emitter.Lifetime); return 1;
                    case "Spread": lua.PushNumber(emitter.Spread); return 1;
                    case "Gravity": lua.PushNumber(emitter.Gravity); return 1;
                    case "Size": lua.PushNumber(emitter.Size); return 1;
                    case "Length": lua.PushNumber(emitter.Length); return 1;
                    case "MaxParticles": lua.PushInteger(emitter.MaxParticles); return 1;
                    case "FollowCamera": lua.PushBoolean(emitter.FollowCamera); return 1;
                    case "Enabled": lua.PushBoolean(emitter.Enabled); return 1;
                    case "Count": lua.PushInteger(emitter.Count); return 1;
                    case "Exists": lua.PushBoolean(true); return 1;
                    default: lua.PushNil(); return 1;
                }

            case "pset":
                var key = lua.ToString(3, false);
                var number = (float)lua.ToNumber(4);
                switch (key)
                {
                    case "Position": emitter.Position = VectorAt(lua, 4); break;
                    case "Area": emitter.Area = Abs(VectorAt(lua, 4)); break;
                    case "Velocity": emitter.Velocity = VectorAt(lua, 4); break;
                    case "Color": emitter.Color = VectorAt(lua, 4); break;
                    case "Rate": emitter.Rate = global::System.Math.Clamp(number, 0f, ParticleEmitter.MaxRate); break;
                    case "Lifetime": emitter.Lifetime = global::System.Math.Clamp(number, 0.01f, ParticleEmitter.MaxLifetime); break;
                    case "Spread": emitter.Spread = global::System.Math.Clamp(number, 0f, 10f); break;
                    case "Gravity": emitter.Gravity = global::System.Math.Clamp(number, -100f, 100f); break;
                    case "Size": emitter.Size = global::System.Math.Clamp(number, 0.005f, 50f); break;
                    case "Length": emitter.Length = global::System.Math.Clamp(number, 0f, 100f); break;
                    case "MaxParticles": emitter.MaxParticles = (int)global::System.Math.Clamp(lua.ToNumber(4), 1d, ParticleEmitter.MaxParticlesPerEmitter); break;
                    case "FollowCamera": emitter.FollowCamera = lua.ToBoolean(4); break;
                    case "Enabled": emitter.Enabled = lua.ToBoolean(4); break;
                    default: return Answer(lua, $"{key} cannot be set");
                }

                return Answer(lua, null);
            default:
                return Answer(lua, "unknown particle request");
        }
    }

    private static Vector3 Abs(Vector3 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));

    // Cameras: see the gameplay prelude's Cameras for what each answers.
    private int CameraRequest(LuaState lua, GameplaySession session, string request)
    {
        var director = session.Cameras;
        switch (request)
        {
            case "camnew":
                var pose = new CameraPose(VectorAt(lua, 3), (float)lua.ToNumber(6), (float)lua.ToNumber(7), (float)lua.ToNumber(8));
                if (director.Create(lua.ToString(2, false) ?? string.Empty, pose, out var made) is { } problem)
                {
                    return Answer(lua, problem);
                }

                Own('c', made!.Id);
                lua.PushInteger(made.Id);
                return 1;
            case "camfind":
                if (director.Find(lua.ToString(2, false) ?? string.Empty) is { } found)
                {
                    lua.PushInteger(found.Id);
                    return 1;
                }

                return 0;
            case "camall":
                var all = director.Cameras.OrderBy(camera => camera.Id).ToList();
                lua.CreateTable(all.Count, 0);
                for (var i = 0; i < all.Count; i++)
                {
                    lua.PushInteger(all[i].Id);
                    lua.RawSetInteger(-2, i + 1);
                }

                return 1;
            case "camgetactive":
                lua.PushInteger(director.Active?.Id ?? 0);
                return 1;
            case "camshake":
                director.Shake(lua.ToNumber(2), lua.ToNumber(3), lua.ToNumber(4));
                return Answer(lua, null);
            case "camfeeds":
                lua.PushBoolean(session.FeedsSupported);
                return 1;
            case "camactive":
                var id = (int)lua.ToInteger(2);
                var next = id == 0 ? null : director.Find(id);
                if (id != 0 && next is null)
                {
                    return Answer(lua, "that camera no longer exists");
                }

                var previous = director.Active;
                if (!ReferenceEquals(previous, next))
                {
                    director.SetActive(next, global::System.Math.Max(0d, lua.ToNumber(3)), lua.ToBoolean(4));
                    QueueCameraActivated(next?.Name, previous?.Name);
                }

                return Answer(lua, null);
        }

        var camera = director.Find((int)lua.ToInteger(2));
        if (camera is null)
        {
            if (request == "camget" && lua.ToString(3, false) == "Exists")
            {
                lua.PushBoolean(false);
                return 1;
            }

            return request == "camdestroy" ? 0 : Answer(lua, "that camera no longer exists");
        }

        switch (request)
        {
            case "camdestroy":
                DestroyCamera(camera);
                return 0;
            case "camget":
                switch (lua.ToString(3, false))
                {
                    case "Position": return PushVector(lua, camera.Pose.Position);
                    case "Rotation": return PushVector(lua, new Vector3(camera.Pose.Pitch, camera.Pose.Yaw, 0f));
                    case "Forward": return PushVector(lua, camera.Pose.Forward);
                    case "FieldOfView": lua.PushNumber(camera.Pose.FieldOfView); return 1;
                    case "Name": lua.PushString(camera.Name); return 1;
                    case "Active": lua.PushBoolean(ReferenceEquals(director.Active, camera)); return 1;
                    case "Exists": lua.PushBoolean(true); return 1;
                    case "Following": lua.PushBoolean(camera.FollowTarget is not null); return 1;
                    case "HasFeed": lua.PushBoolean(camera.HasFeed); return 1;
                    default: lua.PushNil(); return 1;
                }

            case "camset":
                switch (lua.ToString(3, false))
                {
                    case "Position":
                        camera.Pose = camera.Pose with { Position = VectorAt(lua, 4) };
                        break;
                    case "Rotation":
                        camera.Pose = CameraDirector.Sanitize(camera.Pose with { Pitch = (float)lua.ToNumber(4), Yaw = (float)lua.ToNumber(5) });
                        break;
                    case "FieldOfView":
                        camera.Pose = CameraDirector.Sanitize(camera.Pose with { FieldOfView = (float)lua.ToNumber(4) });
                        break;
                    default:
                        return Answer(lua, "that camera value cannot be set");
                }

                return Answer(lua, null);
            case "camlook":
                var (pitch, yaw) = CameraPose.LookRotation(camera.Pose.Position, VectorAt(lua, 3), camera.Pose.Pitch, camera.Pose.Yaw);
                camera.Pose = camera.Pose with { Pitch = pitch, Yaw = yaw };
                return Answer(lua, null);
            case "camfollow":
                var targetId = lua.ToInteger(3);
                if (targetId == 0)
                {
                    camera.FollowTarget = null;
                    return Answer(lua, null);
                }

                var target = targetId > 0 && targetId <= _objects.Count ? _objects[(int)targetId - 1] : null;
                if (target is not { Exists: true } || PartOfObject(target) is not { } targetPart)
                {
                    return Answer(lua, "Follow: that object no longer exists");
                }

                camera.FollowTarget = targetPart;
                camera.FollowOffset = VectorAt(lua, 4);
                camera.FollowLookAt = lua.ToBoolean(7);
                camera.LookOffset = VectorAt(lua, 8);
                camera.FollowSmoothing = (float)global::System.Math.Max(0d, lua.ToNumber(11));
                return Answer(lua, null);
            case "camfeed":
                if (!session.FeedsSupported)
                {
                    return Answer(lua, "camera feeds (render to texture) are not available with this renderer");
                }

                return Answer(lua, director.RequestFeed(camera, (int)lua.ToNumber(3), (int)lua.ToNumber(4)));
            default:
                return Answer(lua, "unknown camera request");
        }
    }

    /// <summary>The Part behind a scene object (scene runtime objects only).</summary>
    internal Func<IScriptObject, World.Part?> PartOfObject { get; set; } = static _ => null;

    // Navigation: the grid and agents live with the scene runtime (they need its physics and objects).
    private int NavigationRequest(LuaState lua, string request)
    {
        if (GameplayScene is not { } scene)
        {
            return Answer(lua, "navigation is only available while the game plays");
        }

        switch (request)
        {
            case "navbuild":
            {
                var tags = new List<string>();
                if (lua.Type(16) == LuaType.Table)
                {
                    var count = lua.RawLen(16);
                    for (var i = 1; i <= count && i <= 64; i++)
                    {
                        lua.RawGetInteger(16, i);
                        if (lua.Type(-1) == LuaType.String)
                        {
                            tags.Add(lua.ToString(-1, false)!);
                        }

                        lua.Pop(1);
                    }
                }

                var build = new ScriptNavBuild(lua.ToBoolean(2) ? VectorAt(lua, 3) : null, lua.ToBoolean(2) ? VectorAt(lua, 6) : null,
                    (float)lua.ToNumber(9), (float)lua.ToNumber(10), (float)lua.ToNumber(11), (float)lua.ToNumber(12), (float)lua.ToNumber(13),
                    lua.ToBoolean(14), lua.ToBoolean(15), tags);
                if (scene.BuildNavigation(build, out var built, out var milliseconds) is { } failure)
                {
                    return Answer(lua, failure);
                }

                return PushNavInfo(lua, built!, milliseconds);
            }

            case "navinfo":
            {
                var grid = scene.Navigation(out var error);
                return grid is null ? Answer(lua, error) : PushNavInfo(lua, grid, 0d);
            }

            case "navupdate":
                return Answer(lua, scene.RebakeNavigation(VectorAt(lua, 2), Abs(VectorAt(lua, 5))));
            case "navblock":
            {
                var grid = scene.Navigation(out var error);
                if (grid is null)
                {
                    return Answer(lua, error);
                }

                grid.SetBlocked(VectorAt(lua, 2), Abs(VectorAt(lua, 5)), lua.ToBoolean(8));
                return Answer(lua, null);
            }

            case "navpath" or "navreach":
            {
                var grid = scene.Navigation(out var error);
                if (grid is null)
                {
                    return Answer(lua, error);
                }

                if (PointSpec(lua, scene, 2) is not { } from || PointSpec(lua, scene, 6) is not { } to)
                {
                    return Answer(lua, "that object no longer exists");
                }

                var path = grid.FindPath(from, to, request == "navpath" && lua.ToBoolean(10), global::System.Math.Max(3f, grid.CellSize * 3f), out var reason);
                if (request == "navreach")
                {
                    lua.PushBoolean(path is { Complete: true });
                    if (path is { Complete: true })
                    {
                        return 1;
                    }

                    lua.PushString(reason ?? "the destination cannot be reached");
                    return 2;
                }

                if (path is null)
                {
                    return Answer(lua, reason);
                }

                lua.PushBoolean(true);
                lua.PushBoolean(path.Complete);
                lua.PushNumber(path.Length);
                PushPoints(lua, path.Points);
                return 4;
            }

            case "navwalk":
            {
                var grid = scene.Navigation(out var error);
                if (grid is null)
                {
                    return Answer(lua, error);
                }

                var walkable = grid.IsWalkable(VectorAt(lua, 2), out var height);
                lua.PushBoolean(walkable);
                if (!walkable)
                {
                    return 1;
                }

                lua.PushNumber(height);
                return 2;
            }

            case "navnear":
            {
                var grid = scene.Navigation(out var error);
                if (grid is null)
                {
                    return Answer(lua, error);
                }

                var cell = grid.NearestWalkable(VectorAt(lua, 2), (float)global::System.Math.Clamp(lua.ToNumber(5), 0d, 10_000d));
                if (cell < 0)
                {
                    lua.PushBoolean(false);
                    return 1;
                }

                lua.PushBoolean(true);
                PushVector(lua, grid.PointOf(cell));
                return 4;
            }

            case "navray":
            {
                var grid = scene.Navigation(out var error);
                if (grid is null)
                {
                    return Answer(lua, error);
                }

                lua.PushBoolean(grid.LineWalkable(VectorAt(lua, 2), VectorAt(lua, 5)));
                return 1;
            }

            case "navdrawgrid":
            {
                var grid = scene.Navigation(out var error);
                if (grid is null || Gameplay is not { } session)
                {
                    return Answer(lua, error ?? "navigation is only available while the game plays");
                }

                lua.PushInteger(DrawGrid(grid, session.Debug, VectorAt(lua, 2), (float)lua.ToNumber(5), lua.ToNumber(6)));
                return 1;
            }

            case "agentnew":
            {
                var objectId = lua.ToInteger(2);
                var target = objectId > 0 && objectId <= _objects.Count ? _objects[(int)objectId - 1] : null;
                if (target is not { Exists: true })
                {
                    return Answer(lua, "Navigation.CreateAgent: that object no longer exists");
                }

                if (scene.CreateAgent(target, out var agent) is { } failure)
                {
                    return Answer(lua, failure);
                }

                agent!.Speed = (float)lua.ToNumber(3);
                agent.Acceleration = (float)lua.ToNumber(4);
                agent.StoppingDistance = (float)lua.ToNumber(5);
                agent.Radius = (float)lua.ToNumber(6);
                agent.RepathInterval = lua.ToNumber(7);
                agent.Move = (lua.ToString(8, false)) switch { "Transform" => NavAgentMove.Transform, "None" => NavAgentMove.None, _ => NavAgentMove.Velocity };
                agent.FaceMovement = lua.ToBoolean(9);
                agent.Avoidance = lua.ToBoolean(10);
                agent.FaceYawOffset = (float)lua.ToNumber(11);
                Own('a', agent.Id);
                lua.PushInteger(agent.Id);
                return 1;
            }
        }

        var found = scene.FindAgent((int)lua.ToInteger(2));
        if (found is null || found.Destroyed)
        {
            if (request == "agentget" && lua.ToString(3, false) == "Exists")
            {
                lua.PushBoolean(false);
                return 1;
            }

            return request == "agentdestroy" ? 0 : Answer(lua, "that navigation agent no longer exists (its object was destroyed, or it was destroyed)");
        }

        switch (request)
        {
            case "agentdestroy":
                scene.DestroyAgent(found);
                return 0;
            case "agentstop":
                found.Stop();
                return Answer(lua, null);
            case "agentdest":
                return Answer(lua, scene.SetAgentDestination(found, VectorAt(lua, 3)));
            case "agentfollow":
            {
                var objectId = lua.ToInteger(3);
                var target = objectId > 0 && objectId <= _objects.Count ? _objects[(int)objectId - 1] : null;
                return target is not { Exists: true }
                    ? Answer(lua, "Follow: that object no longer exists")
                    : Answer(lua, scene.FollowWithAgent(found, target, (float)global::System.Math.Max(0d, lua.ToNumber(4))));
            }

            case "agentupdate":
            {
                if (scene.StepAgent(found, global::System.Math.Max(0d, lua.ToNumber(3)), out var error) is not { } step)
                {
                    return Answer(lua, error);
                }

                lua.PushString(step.Status.ToString());
                lua.PushNumber(step.Velocity.X);
                lua.PushNumber(step.Velocity.Y);
                lua.PushNumber(step.Velocity.Z);
                lua.PushNumber(step.Remaining);
                lua.PushBoolean(step.ArrivedNow);
                lua.PushBoolean(step.FailedNow);
                lua.PushInteger(step.WaypointReached);
                if (found.FailureReason is { } why)
                {
                    lua.PushString(why);
                }
                else
                {
                    lua.PushNil();
                }

                return 9;
            }

            case "agentpath":
                PushPoints(lua, found.Path);
                return 1;
            case "agentget":
                switch (lua.ToString(3, false))
                {
                    case "Status": lua.PushString(found.Status.ToString()); return 1;
                    case "RemainingDistance": lua.PushNumber(found.Remaining); return 1;
                    case "Speed": lua.PushNumber(found.Speed); return 1;
                    case "Acceleration": lua.PushNumber(found.Acceleration); return 1;
                    case "StoppingDistance": lua.PushNumber(found.StoppingDistance); return 1;
                    case "Radius": lua.PushNumber(found.Radius); return 1;
                    case "Velocity": return PushVector(lua, found.Velocity);
                    case "Exists": lua.PushBoolean(true); return 1;
                    case "FailureReason":
                        if (found.FailureReason is { } reason)
                        {
                            lua.PushString(reason);
                        }
                        else
                        {
                            lua.PushNil();
                        }

                        return 1;
                    case "Destination":
                        if (found.Destination is { } destination)
                        {
                            return PushVector(lua, destination);
                        }

                        return 0;
                    case "Target":
                        lua.PushInteger(found.Target is { } followed && FindObjectOfPart(followed) is { } followedObject ? IdOfObject(followedObject) : 0);
                        return 1;
                    default:
                        lua.PushNil();
                        return 1;
                }

            case "agentset":
                var value = (float)lua.ToNumber(4);
                switch (lua.ToString(3, false))
                {
                    case "Speed": found.Speed = global::System.Math.Clamp(value, 0f, 1000f); break;
                    case "Acceleration": found.Acceleration = global::System.Math.Clamp(value, 0.01f, 100_000f); break;
                    case "StoppingDistance": found.StoppingDistance = global::System.Math.Clamp(value, 0f, 1000f); break;
                    case "Radius": found.Radius = global::System.Math.Clamp(value, 0.01f, 100f); break;
                    default: return Answer(lua, "that agent value cannot be set");
                }

                return Answer(lua, null);
            default:
                return Answer(lua, "unknown navigation request");
        }
    }

    /// <summary>The scene object behind a Part (scene runtime objects only).</summary>
    internal Func<World.Part, IScriptObject?> FindObjectOfPart { get; set; } = static _ => null;

    // A point argument: (object id, x, y, z) at that slot; an object (id > 0) gives where it stands. Null: the object is gone.
    private Vector3? PointSpec(LuaState lua, IScriptGameplayScene scene, int at)
    {
        var id = lua.ToInteger(at);
        if (id <= 0)
        {
            return VectorAt(lua, at + 1);
        }

        var target = id <= _objects.Count ? _objects[(int)id - 1] : null;
        return target is { Exists: true } ? scene.FeetOf(target) : null;
    }

    private static int PushNavInfo(LuaState lua, NavGrid grid, double milliseconds)
    {
        lua.PushBoolean(true);
        lua.CreateTable(0, 9);
        lua.PushInteger(grid.Width);
        lua.SetField(-2, "Width");
        lua.PushInteger(grid.Depth);
        lua.SetField(-2, "Depth");
        lua.PushInteger(grid.CellCount);
        lua.SetField(-2, "Cells");
        lua.PushInteger(grid.WalkableCount);
        lua.SetField(-2, "Walkable");
        lua.PushInteger(grid.RegionCount);
        lua.SetField(-2, "Regions");
        lua.PushNumber(grid.CellSize);
        lua.SetField(-2, "CellSize");
        lua.PushNumber(milliseconds);
        lua.SetField(-2, "Milliseconds");
        return 2;
    }

    // The walkable cells within a radius, as small crosses (green), and blocked ones as red ticks; returns lines drawn.
    private static int DrawGrid(NavGrid grid, DebugDraw debug, Vector3 center, float radius, double seconds)
    {
        var (cx, cz) = grid.CellOf(center);
        var reach = (int)MathF.Ceiling(global::System.Math.Clamp(radius, 0f, 200f) / grid.CellSize);
        var drawn = 0;
        var green = new Vector3(0.2f, 0.9f, 0.35f);
        for (var z = cz - reach; z <= cz + reach; z++)
        {
            for (var x = cx - reach; x <= cx + reach; x++)
            {
                if (!grid.Contains(x, z))
                {
                    continue;
                }

                var cell = z * grid.Width + x;
                if (!grid.IsWalkableCell(cell))
                {
                    continue;
                }

                var p = grid.PointOf(cell) + new Vector3(0f, 0.08f, 0f);
                var h = grid.CellSize * 0.3f;
                if (!debug.AddLine(p - new Vector3(h, 0f, 0f), p + new Vector3(h, 0f, 0f), green, seconds)
                    || !debug.AddLine(p - new Vector3(0f, 0f, h), p + new Vector3(0f, 0f, h), green, seconds))
                {
                    return drawn;
                }

                drawn += 2;
            }
        }

        return drawn;
    }

    private static void PushPoints(LuaState lua, IReadOnlyList<Vector3> points)
    {
        lua.CreateTable(points.Count * 3, 0);
        for (var i = 0; i < points.Count; i++)
        {
            lua.PushNumber(points[i].X);
            lua.RawSetInteger(-2, i * 3 + 1);
            lua.PushNumber(points[i].Y);
            lua.RawSetInteger(-2, i * 3 + 2);
            lua.PushNumber(points[i].Z);
            lua.RawSetInteger(-2, i * 3 + 3);
        }
    }

    private static void PushNames(LuaState lua, IReadOnlyList<string> names)
    {
        lua.CreateTable(names.Count, 0);
        for (var i = 0; i < names.Count; i++)
        {
            lua.PushString(names[i]);
            lua.RawSetInteger(-2, i + 1);
        }
    }

    private static Vector3 VectorAt(LuaState lua, int at) => new((float)lua.ToNumber(at), (float)lua.ToNumber(at + 1), (float)lua.ToNumber(at + 2));

    // true (null message) or false, message.
    private static int Answer(LuaState lua, string? problem)
    {
        if (problem is null)
        {
            lua.PushBoolean(true);
            return 1;
        }

        lua.PushBoolean(false);
        lua.PushString(problem);
        return 2;
    }

    // ---- Game saves ----------------------------------------------------------------------------------------------

    // save (slot, data, metadata, version) -> true | false, message; load (slot) -> true, data, metadata, version, savedAt,
    // recovered | false, message; exists (slot) -> bool; delete (slot) -> true, existed | false, message; list -> {info...};
    // info (slot) -> true, version, savedAt, metadata | false, message. data and metadata are the prelude's tagged copies.
    private int SaveRequest(LuaState lua, GameplaySession session, string request)
    {
        var store = session.Saves;
        var slot = lua.ToString(2, false) ?? string.Empty;
        switch (request)
        {
            case "slotcheck":
                // nothing when the name is a usable slot, else why not.
                if (SaveStore.CheckSlot(slot) is { } slotProblem)
                {
                    lua.PushString(slotProblem);
                    return 1;
                }

                return 0;
            case "savesavailable":
                lua.PushBoolean(store.Available);
                return 1;
            case "save":
            {
                if (SaveStore.CheckSlot(slot) is { } badSlot)
                {
                    return Answer(lua, badSlot);
                }

                byte[] bytes;
                try
                {
                    bytes = EncodeSave(lua, slot, 3, 4, lua.ToInteger(5));
                }
                catch (SaveEncodingException ex)
                {
                    return Answer(lua, ex.Message);
                }

                return Answer(lua, store.Write(slot, bytes));
            }

            case "exists":
                lua.PushBoolean(store.Exists(slot));
                return 1;
            case "delete":
            {
                if (store.Delete(slot, out var existed) is { } problem)
                {
                    return Answer(lua, problem);
                }

                lua.PushBoolean(true);
                lua.PushBoolean(existed);
                return 2;
            }

            case "load" or "info":
            {
                var headerOnly = request == "info";
                if (TryDecode(store, slot, backup: false, headerOnly, out var document, out var problem) is { } decoded)
                {
                    return PushDecoded(lua, decoded, document!, recovered: false, headerOnly);
                }

                // A damaged slot whose previous version is intact gives that version (said in the answer and the log).
                if (problem is not null && store.Exists(slot) && store.BackupExists(slot)
                    && TryDecode(store, slot, backup: true, headerOnly, out var backupDocument, out _) is { } fromBackup)
                {
                    Emit(new ScriptLogMessage(ScriptLogKind.Warning, _running?.Name ?? "GameSave", $"the save \"{slot}\" is damaged ({problem}); its previous version was loaded instead"));
                    return PushDecoded(lua, fromBackup, backupDocument!, recovered: true, headerOnly);
                }

                return Answer(lua, problem);
            }

            case "list":
            {
                var slots = store.ListSlots();
                lua.CreateTable(slots.Count, 0);
                var index = 0;
                foreach (var name in slots)
                {
                    if (TryDecode(store, name, backup: false, headerOnly: true, out var document, out _) is not { } header)
                    {
                        continue;
                    }

                    using (document)
                    {
                        lua.CreateTable(0, 4);
                        lua.PushString(name);
                        lua.SetField(-2, "Slot");
                        lua.PushString(header.SavedAt);
                        lua.SetField(-2, "SavedAt");
                        lua.PushInteger(header.Version);
                        lua.SetField(-2, "Version");
                        if (header.Metadata is { } metadata && PushJson(lua, metadata, new int[1]))
                        {
                            lua.SetField(-2, "Metadata");
                        }

                        lua.RawSetInteger(-2, ++index);
                    }
                }

                return 1;
            }

            default:
                return Answer(lua, "unknown request");
        }
    }

    private sealed class SaveEncodingException(string message) : Exception(message);

    private sealed record DecodedSave(long Version, string SavedAt, JsonElement? Metadata, JsonElement? Data);

    private int PushDecoded(LuaState lua, DecodedSave decoded, JsonDocument document, bool recovered, bool headerOnly)
    {
        using (document)
        {
            var top = lua.GetTop();
            lua.PushBoolean(true);
            var counter = new int[1];
            if (headerOnly)
            {
                lua.PushInteger(decoded.Version);
                lua.PushString(decoded.SavedAt);
                if (decoded.Metadata is not { } headerMetadata || !PushJson(lua, headerMetadata, counter))
                {
                    lua.PushNil();
                }

                return 4;
            }

            if (decoded.Data is not { } data || !PushJson(lua, data, counter))
            {
                lua.SetTop(top);
                return Answer(lua, "the save is damaged (its data could not be read back)");
            }

            if (decoded.Metadata is not { } metadata || !PushJson(lua, metadata, counter))
            {
                lua.PushNil();
            }

            lua.PushInteger(decoded.Version);
            lua.PushString(decoded.SavedAt);
            lua.PushBoolean(recovered);
            return 6;
        }
    }

    // Reads and checks a save file's envelope; the caller disposes the document. Null and why when it is not usable.
    private static DecodedSave? TryDecode(SaveStore store, string slot, bool backup, bool headerOnly, out JsonDocument? document, out string? problem)
    {
        document = null;
        if (store.Read(slot, backup, out var bytes) is { } readProblem)
        {
            problem = readProblem;
            return null;
        }

        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxSaveDepth * 2 + 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            problem = $"the save \"{slot}\" is damaged (not valid JSON: {ex.Message})";
            return null;
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != SaveFormat
            || !root.TryGetProperty("formatVersion", out var formatVersion) || !formatVersion.TryGetInt32(out var fileFormat))
        {
            document.Dispose();
            document = null;
            problem = $"\"{slot}\" is not a Wisko game save (or is damaged)";
            return null;
        }

        if (fileFormat > SaveFormatVersion || fileFormat < 1)
        {
            document.Dispose();
            document = null;
            problem = $"the save \"{slot}\" was written by a newer Wisko (save format {fileFormat}; this one reads {SaveFormatVersion})";
            return null;
        }

        var version = root.TryGetProperty("version", out var versionElement) && versionElement.TryGetInt64(out var v) ? v : 1L;
        var savedAt = root.TryGetProperty("savedAt", out var savedElement) && savedElement.ValueKind == JsonValueKind.String ? savedElement.GetString() ?? string.Empty : string.Empty;
        JsonElement? metadata = root.TryGetProperty("metadata", out var metadataElement) ? metadataElement : null;
        JsonElement? data = null;
        if (!headerOnly)
        {
            if (!root.TryGetProperty("data", out var dataElement))
            {
                document.Dispose();
                document = null;
                problem = $"the save \"{slot}\" is damaged (it has no data)";
                return null;
            }

            data = dataElement;
        }

        problem = null;
        return new DecodedSave(version, savedAt, metadata, data);
    }

    private byte[] EncodeSave(LuaState lua, string slot, int dataIndex, int metadataIndex, long version)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", SaveFormat);
            writer.WriteNumber("formatVersion", SaveFormatVersion);
            writer.WriteString("slot", slot);
            writer.WriteString("savedAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            writer.WriteNumber("version", version);
            if (lua.Type(metadataIndex) == LuaType.Table)
            {
                writer.WritePropertyName("metadata");
                WriteLuaValue(lua, metadataIndex, writer, 0);
            }

            writer.WritePropertyName("data");
            WriteLuaValue(lua, dataIndex, writer, 0);
            writer.WriteEndObject();
        }

        if (stream.Length > SaveStore.MaxFileBytes)
        {
            throw new SaveEncodingException($"the save is too big ({stream.Length / 1024 / 1024} MB; at most {SaveStore.MaxFileBytes / 1024 / 1024} MB)");
        }

        return stream.ToArray();
    }

    // The prelude's tagged copy: booleans, numbers (integers stay integers), strings, sequences (arrays) and tables with
    // string keys only ($vector, $map and $bytes forms included); anything else was refused in Lua already.
    private static void WriteLuaValue(LuaState lua, int index, Utf8JsonWriter writer, int depth)
    {
        if (depth > MaxSaveDepth)
        {
            throw new SaveEncodingException("the data is nested too deeply");
        }

        index = lua.AbsIndex(index);
        switch (lua.Type(index))
        {
            case LuaType.Boolean:
                writer.WriteBooleanValue(lua.ToBoolean(index));
                break;
            case LuaType.Number:
                if (lua.IsInteger(index))
                {
                    writer.WriteNumberValue(lua.ToInteger(index));
                }
                else
                {
                    var number = lua.ToNumber(index);
                    if (!double.IsFinite(number))
                    {
                        throw new SaveEncodingException("a number is not finite (NaN or infinite numbers cannot be saved)");
                    }

                    // Always with a point or exponent, so it loads back as a float.
                    var text = number.ToString("R", CultureInfo.InvariantCulture);
                    if (text.IndexOfAny(['.', 'E', 'e']) < 0)
                    {
                        text += ".0";
                    }

                    writer.WriteRawValue(text, skipInputValidation: true);
                }

                break;
            case LuaType.String:
                var bytes = lua.ToBuffer(index) ?? [];
                if (IsValidUtf8(bytes))
                {
                    writer.WriteStringValue(Encoding.UTF8.GetString(bytes));
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString("$bytes", Convert.ToBase64String(bytes));
                    writer.WriteEndObject();
                }

                break;
            case LuaType.Table:
                var length = (int)global::System.Math.Min(lua.RawLen(index), int.MaxValue);
                var keys = 0;
                var onlyStrings = true;
                lua.PushNil();
                while (lua.Next(index))
                {
                    keys++;
                    if (lua.Type(-2) != LuaType.String)
                    {
                        onlyStrings = false;
                    }

                    lua.Pop(1);
                }

                if (length > 0 && keys == length)
                {
                    writer.WriteStartArray();
                    for (var i = 1; i <= length; i++)
                    {
                        lua.RawGetInteger(index, i);
                        WriteLuaValue(lua, -1, writer, depth + 1);
                        lua.Pop(1);
                    }

                    writer.WriteEndArray();
                }
                else if (onlyStrings)
                {
                    writer.WriteStartObject();
                    lua.PushNil();
                    while (lua.Next(index))
                    {
                        writer.WritePropertyName(Encoding.UTF8.GetString(lua.ToBuffer(-2) ?? []));
                        WriteLuaValue(lua, -1, writer, depth + 1);
                        lua.Pop(1);
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    throw new SaveEncodingException("a table mixes key kinds (the prelude should have tagged it)");
                }

                break;
            default:
                throw new SaveEncodingException($"a {lua.TypeName(lua.Type(index))} value cannot be saved");
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // A JSON value as Lua: objects -> tables with string keys ({"$bytes": base64} -> the raw string), arrays -> sequences,
    // numbers with a point or exponent -> floats, other numbers -> integers. False (nothing pushed) past the value limit.
    private static bool PushJson(LuaState lua, JsonElement element, int[] counter)
    {
        if (++counter[0] > MaxLoadValues || !lua.CheckStack(4))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("$bytes", out var raw) && raw.ValueKind == JsonValueKind.String && element.EnumerateObject().Count() == 1)
                {
                    try
                    {
                        lua.PushBuffer(Convert.FromBase64String(raw.GetString() ?? string.Empty));
                        return true;
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
                }

                lua.CreateTable(0, 4);
                foreach (var property in element.EnumerateObject())
                {
                    lua.PushString(property.Name);
                    if (!PushJson(lua, property.Value, counter))
                    {
                        lua.Pop(2);
                        return false;
                    }

                    lua.RawSet(-3);
                }

                return true;
            case JsonValueKind.Array:
                lua.CreateTable(element.GetArrayLength(), 0);
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (!PushJson(lua, item, counter))
                    {
                        lua.Pop(1);
                        return false;
                    }

                    lua.RawSetInteger(-2, ++i);
                }

                return true;
            case JsonValueKind.String:
                lua.PushString(element.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.Number:
                var text = element.GetRawText();
                if (text.IndexOfAny(['.', 'e', 'E']) < 0 && element.TryGetInt64(out var integer))
                {
                    lua.PushInteger(integer);
                }
                else
                {
                    lua.PushNumber(element.GetDouble());
                }

                return true;
            case JsonValueKind.True:
                lua.PushBoolean(true);
                return true;
            case JsonValueKind.False:
                lua.PushBoolean(false);
                return true;
            default:
                lua.PushNil();
                return true;
        }
    }
}
