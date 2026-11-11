using System.Globalization;
using Wisko.Engine.Animation;
using KeraLua;
using LuaState = KeraLua.Lua;

namespace Wisko.Engine.Scripting;

/// <summary>
/// The animation side of a scene object : its imported model's clips and, while playing, its
/// playback. Scripts reach it through object:PlayAnimation and the other animation methods.
/// </summary>
internal interface IScriptAnimated
{
    /// <summary>Whether it is an imported model instance (a primitive Part has no animations).</summary>
    bool IsModel { get; }

    /// <summary>Its model's clips in file order (empty for a primitive, a model without clips or one that did not load).</summary>
    IReadOnlyList<AnimationClip> AnimationClips { get; }

    /// <summary>Its playback, made on first use; null when it has no clips or nothing runs the scene.</summary>
    ModelAnimator? Animator { get; }
}

// hostScene("anim", id, op, ...) and hostScene("onanim", id, f): object animation methods. Arguments were checked in Lua.
public sealed partial class LuaScriptRuntime
{
    private static string AnimationFinishedEventName(int objectId) => "anim:" + objectId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A model instance's current clip, playing once, reached its end: its OnAnimationFinished connections hear about it at
    /// the start of the next frame, with the clip's name and the object.
    /// </summary>
    internal void NoteAnimationFinished(IScriptObject target, string clipName)
    {
        var objectId = IdOfObject(target);
        if (_disposed || objectId <= 0)
        {
            return;
        }

        var name = AnimationFinishedEventName(objectId);
        if (_eventHandlers.Exists(handler => handler.Active && handler.Internal && string.Equals(handler.Name, name, StringComparison.Ordinal))
            && _eventQueue.Count < MaxQueuedEvents)
        {
            _eventQueue.Add(new QueuedEvent(name, [clipName, new EventObject(objectId)], "vo", Internal: true));
        }
    }

    // f -> the connection's number (an Events connection), or false, message.
    private int AnimationFinishedConnection(LuaState lua, int objectId)
    {
        if (_running is not { } watcher || lua.Type(3) != LuaType.Function)
        {
            lua.PushBoolean(false);
            lua.PushString("OnAnimationFinished is not available here");
            return 2;
        }

        if (_eventHandlers.Count(handler => ReferenceEquals(handler.Owner, watcher)) >= MaxHandlersPerScript)
        {
            lua.PushBoolean(false);
            lua.PushString($"this script already has {MaxHandlersPerScript:N0} event connections: disconnect the ones it no longer needs");
            return 2;
        }

        lua.PushCopy(3);
        var handlerId = ++_nextHandlerId;
        _eventHandlers.Add(new EventHandlerEntry(handlerId, AnimationFinishedEventName(objectId), watcher, lua.Ref(LuaRegistry.Index)) { Internal = true });
        lua.PushInteger(handlerId);
        return 1;
    }

    // Answers true[, values] or false, message (the message becomes the script's error). Play and CrossFade instead answer
    // true, played, reason: a clip that does not exist is an ordinary answer, not an error.
    private static int AnimationRequest(LuaState lua, IScriptObject target, string op)
    {
        if (target is not IScriptAnimated animated)
        {
            return Refuse(lua, $"'{target.Name}' has no animations");
        }

        var clips = animated.AnimationClips;
        switch (op)
        {
            case "list":
                lua.PushBoolean(true);
                lua.CreateTable(clips.Count, 0);
                for (var i = 0; i < clips.Count; i++)
                {
                    lua.CreateTable(0, 3);
                    lua.PushString(clips[i].Name);
                    lua.SetField(-2, "Name");
                    lua.PushNumber(clips[i].Duration);
                    lua.SetField(-2, "Duration");
                    lua.PushInteger(i + 1);
                    lua.SetField(-2, "Index");
                    lua.RawSetInteger(-2, i + 1);
                }

                return 2;
            case "has":
                lua.PushBoolean(true);
                lua.PushBoolean(clips.Count > 0);
                return 2;
            case "length":
                // Of a named clip (4: name or number, 5: by number), else of the current one; nil when there is none. (The
                // arguments are read before anything is pushed: a push would take the place of a missing argument.)
                var measured = lua.Type(4) is LuaType.Nil or LuaType.None ? animated.Animator?.CurrentClip : FindClip(lua, animated, 4, out _);
                lua.PushBoolean(true);
                if (measured is null)
                {
                    lua.PushNil();
                }
                else
                {
                    lua.PushNumber(measured.Duration);
                }

                return 2;
        }

        if (op is "play")
        {
            // 4: name or number, 5: by number, 6: loop or nil, 7: speed or nil, 8: fade, 9: start time or nil, 10: restart.
            var clip = FindClip(lua, animated, 4, out var reason);
            var loop = lua.Type(6) == LuaType.Boolean ? lua.ToBoolean(6) : true;
            var speed = lua.Type(7) == LuaType.Number ? (float)lua.ToNumber(7) : 1f;
            var fade = (float)global::System.Math.Max(0d, lua.ToNumber(8));
            double? start = lua.Type(9) == LuaType.Number ? lua.ToNumber(9) : null;
            var restart = lua.ToBoolean(10);
            lua.PushBoolean(true);
            if (clip is null || animated.Animator is not { } player)
            {
                lua.PushBoolean(false);
                lua.PushString(reason ?? $"'{target.Name}' cannot play animations here");
                return 3;
            }

            player.Play(clip, loop, speed, fade, start, restart);
            lua.PushBoolean(true);
            return 2;
        }

        // Everything else is about what plays now: a model without clips answers as if nothing plays.
        var animator = clips.Count == 0 ? null : animated.Animator;
        switch (op)
        {
            case "current":
                lua.PushBoolean(true);
                if (animator?.CurrentClip is { } current)
                {
                    lua.PushString(current.Name);
                }
                else
                {
                    lua.PushNil();
                }

                return 2;
            case "playing":
                // 4: name or number (nil: any clip), 5: by number.
                var anyClip = lua.Type(4) is LuaType.Nil or LuaType.None;
                var asked = anyClip ? null : FindClip(lua, animated, 4, out _);
                var answer = animator is { IsPlaying: true } && (anyClip || ReferenceEquals(asked, animator.CurrentClip));
                lua.PushBoolean(true);
                lua.PushBoolean(answer);
                return 2;
            case "gettime":
                lua.PushBoolean(true);
                lua.PushNumber(animator?.Time ?? 0d);
                return 2;
        }

        if (animator is null)
        {
            // Stopping, pausing or changing nothing is not an error; a time or speed for nothing is.
            return op is "stop" or "pause" or "resume" or "restart"
                ? Done(lua)
                : Refuse(lua, clips.Count == 0 ? NoAnimations(target, animated) : "no animation is playing (start one with PlayAnimation)");
        }

        switch (op)
        {
            case "stop":
                animator.Stop((float)global::System.Math.Max(0d, lua.ToNumber(4)));
                return Done(lua);
            case "pause":
                animator.Pause();
                return Done(lua);
            case "resume":
                animator.Resume();
                return Done(lua);
            case "restart":
                animator.Restart();
                return Done(lua);
            case "speed" when animator.CurrentClip is null:
            case "loop" when animator.CurrentClip is null:
            case "settime" when animator.CurrentClip is null:
                return Refuse(lua, "no animation is playing (start one with PlayAnimation)");
            case "speed":
                animator.SetSpeed((float)lua.ToNumber(4));
                return Done(lua);
            case "loop":
                animator.SetLoop(lua.ToBoolean(4));
                return Done(lua);
            case "settime":
                animator.Seek(lua.ToNumber(4));
                return Done(lua);
            default:
                return Refuse(lua, "unknown animation request");
        }
    }

    // The clip at stack slot `at` (a name, or a number when slot at+1 is true: 1 = the first clip); null and why when
    // there is no such clip.
    private static AnimationClip? FindClip(LuaState lua, IScriptAnimated animated, int at, out string? reason)
    {
        var clips = animated.AnimationClips;
        reason = null;
        if (clips.Count == 0)
        {
            reason = animated.IsModel ? "this model has no animations" : "only imported models have animations";
            return null;
        }

        if (lua.ToBoolean(at + 1))
        {
            var number = lua.ToNumber(at);
            if (number >= 1 && number <= clips.Count)
            {
                return clips[(int)number - 1];
            }

            reason = $"there is no animation number {number.ToString(CultureInfo.InvariantCulture)} (this model has {clips.Count}: 1 to {clips.Count})";
            return null;
        }

        var name = lua.ToString(at, false) ?? string.Empty;
        foreach (var clip in clips)
        {
            if (string.Equals(clip.Name, name, StringComparison.Ordinal))
            {
                return clip;
            }
        }

        foreach (var clip in clips)
        {
            if (string.Equals(clip.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return clip;
            }
        }

        var shown = name.Length > 40 ? name[..40] + "..." : name;
        reason = $"no animation called \"{shown}\" (this model has: {string.Join(", ", clips.Select(clip => clip.Name))})";
        return null;
    }

    private static string NoAnimations(IScriptObject target, IScriptAnimated animated) =>
        animated.IsModel ? $"'{target.Name}' has no animations" : $"'{target.Name}' has no animations (only imported models do)";

    private static int Done(LuaState lua)
    {
        lua.PushBoolean(true);
        return 1;
    }

    private static int Refuse(LuaState lua, string message)
    {
        lua.PushBoolean(false);
        lua.PushString(message);
        return 2;
    }
}
