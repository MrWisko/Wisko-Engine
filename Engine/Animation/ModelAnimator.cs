namespace Wisko.Engine.Animation;

/// <summary>One clip (or the rest pose, <see cref="Clip"/> null) playing in an animator, with its fade-in weight.</summary>
internal sealed class AnimationLayer
{
    public AnimationClip? Clip { get; init; }

    /// <summary>Playback position in seconds (0..Duration).</summary>
    public double Time { get; set; }

    public float Speed { get; set; } = 1f;

    public bool Loop { get; set; } = true;

    /// <summary>A one-shot clip that reached its end (it holds its last pose).</summary>
    public bool Finished { get; set; }

    public float FadeDuration { get; init; }

    public float FadeElapsed { get; set; }

    /// <summary>How much of the pose this layer decides: 0 to 1 over its fade, 1 without one.</summary>
    public float Weight => FadeDuration <= 0f ? 1f : global::System.Math.Clamp(FadeElapsed / FadeDuration, 0f, 1f);
}

/// <summary>
/// Playback state of one animated object: what plays, where, how fast, looping or once, paused or not, and the
/// crossfades in progress. Every instance has its own (the clips and skeleton are the asset's, shared). A crossfade adds
/// a layer that fades in over the layers before it; a crossfade started while another is still running simply adds
/// another layer, so nothing snaps (layers that are fully covered are dropped). The pose is evaluated only when asked
/// for after something changed, so an animator nobody draws costs only its clock.
/// <para>Times are in seconds of real time scaled by Speed (fades are not scaled); frame-rate independent: what plays
/// depends only on the total time advanced, not on how it was split into frames.</para>
/// </summary>
public sealed class ModelAnimator
{
    /// <summary>Most layers blending at once; starting another crossfade drops the oldest (by then nearly covered).</summary>
    public const int MaxLayers = 8;

    private readonly List<AnimationLayer> _layers = [];
    private readonly ModelPose _pose;
    private bool _dirty = true;

    public ModelAnimator(ModelAnimationData data)
    {
        Data = data;
        _pose = new ModelPose(data);
    }

    public ModelAnimationData Data { get; }

    /// <summary>Told when the current clip, playing once, reaches its end (not when it is interrupted, nor on a seek).</summary>
    public Action<ModelAnimator, AnimationClip>? Finished { get; set; }

    /// <summary>Nothing plays: the object shows its rest pose.</summary>
    public bool IsStopped => _layers.Count == 0;

    public bool IsPaused { get; private set; }

    /// <summary>The clip playing or fading in (the latest one asked for); null when stopped or fading back to rest.</summary>
    public AnimationClip? CurrentClip => Top?.Clip;

    /// <summary>A clip is advancing: one is current, not paused and not finished.</summary>
    public bool IsPlaying => Top is { Clip: not null, Finished: false } && !IsPaused;

    /// <summary>The current clip played once and holds its last pose.</summary>
    public bool IsFinished => Top is { Clip: not null, Finished: true };

    public double Time => Top?.Time ?? 0d;

    public float Speed => Top?.Speed ?? 1f;

    public bool Loop => Top?.Loop ?? true;

    /// <summary>Layers blending now (diagnostics and tests).</summary>
    public int LayerCount => _layers.Count;

    /// <summary>The weight of the current clip's layer (1 once its crossfade is done).</summary>
    public float CurrentWeight => Top?.Weight ?? 0f;

    private AnimationLayer? Top => _layers.Count == 0 ? null : _layers[^1];

    /// <summary>The pose now (evaluated on demand; the rest pose while stopped).</summary>
    public ModelPose Pose
    {
        get
        {
            if (_dirty)
            {
                _dirty = false;
                if (_layers.Count == 0)
                {
                    _pose.CopyFrom(Data.RestPose);
                }
                else
                {
                    _pose.Evaluate(_layers);
                }
            }

            return _pose;
        }
    }

    /// <summary>
    /// Plays <paramref name="clip"/>: at once (<paramref name="fade"/> 0) or crossfading from what shows now over
    /// <paramref name="fade"/> seconds. When that clip is already current and still playing it keeps playing where it is
    /// (only loop and speed change) unless <paramref name="restart"/>, so calling this every frame is harmless.
    /// </summary>
    public void Play(AnimationClip clip, bool loop = true, float speed = 1f, float fade = 0f, double? startTime = null, bool restart = false)
    {
        if (clip.Index >= Data.Clips.Count || !ReferenceEquals(Data.Clips[clip.Index], clip))
        {
            throw new ArgumentException($"Clip '{clip.Name}' belongs to another model.", nameof(clip));
        }

        IsPaused = false;
        if (!restart && startTime is null && Top is { Clip: { } current, Finished: false } top && ReferenceEquals(current, clip))
        {
            top.Loop = loop;
            top.Speed = speed;
            _dirty = true;
            return;
        }

        var layer = new AnimationLayer
        {
            Clip = clip,
            Loop = loop,
            Speed = speed,
            FadeDuration = fade > 0f ? fade : 0f,
            Time = Wrap(clip, startTime ?? (speed < 0f ? clip.Duration : 0d), loop)
        };
        Push(layer);
    }

    /// <summary>Back to the rest pose: at once, or fading to it over <paramref name="fade"/> seconds.</summary>
    public void Stop(float fade = 0f)
    {
        IsPaused = false;
        if (fade <= 0f || _layers.Count == 0)
        {
            _layers.Clear();
            _dirty = true;
            return;
        }

        if (Top is { Clip: null })
        {
            return; // already fading to rest
        }

        Push(new AnimationLayer { Clip = null, FadeDuration = fade });
    }

    /// <summary>Freezes everything (clips and fades) where it is.</summary>
    public void Pause() => IsPaused = !IsStopped;

    public void Resume() => IsPaused = false;

    /// <summary>The current clip from its start (its end when playing backwards), playing.</summary>
    public void Restart()
    {
        if (Top is not { Clip: { } clip } top)
        {
            return;
        }

        top.Time = top.Speed < 0f ? clip.Duration : 0d;
        top.Finished = false;
        IsPaused = false;
        _dirty = true;
    }

    /// <summary>Moves the current clip to <paramref name="seconds"/> (wrapped when looping, else clamped to its length).</summary>
    public void Seek(double seconds)
    {
        if (Top is not { Clip: { } clip } top || !double.IsFinite(seconds))
        {
            return;
        }

        // A one-shot moved to its end (or start, backwards) holds there as finished; a seek never raises Finished.
        top.Time = Wrap(clip, seconds, top.Loop);
        top.Finished = !top.Loop && (top.Speed >= 0f ? top.Time >= clip.Duration : top.Time <= 0d);
        _dirty = true;
    }

    public void SetSpeed(float speed)
    {
        if (Top is { } top && float.IsFinite(speed))
        {
            top.Speed = speed;
        }
    }

    public void SetLoop(bool loop)
    {
        if (Top is { Clip: { } clip } top)
        {
            top.Loop = loop;
            if (loop)
            {
                top.Finished = false;
                top.Time = Wrap(clip, top.Time, true);
            }
        }
    }

    /// <summary>
    /// Advances every layer by <paramref name="deltaTime"/> real seconds (nothing while paused): clip times by their
    /// speeds, fades by real time. Layers a finished fade fully covers are dropped; a finished fade to rest stops.
    /// </summary>
    public void Advance(double deltaTime)
    {
        if (IsPaused || _layers.Count == 0 || !(deltaTime > 0d) || !double.IsFinite(deltaTime))
        {
            return;
        }

        AnimationClip? finished = null;
        for (var i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (layer.FadeDuration > 0f && layer.FadeElapsed < layer.FadeDuration)
            {
                layer.FadeElapsed = (float)global::System.Math.Min(layer.FadeDuration, layer.FadeElapsed + deltaTime);
            }

            if (layer.Clip is not { } clip || layer.Finished)
            {
                continue;
            }

            var time = layer.Time + deltaTime * layer.Speed;
            if (layer.Loop)
            {
                layer.Time = Wrap(clip, time, true);
            }
            else if (layer.Speed >= 0f && time >= clip.Duration || layer.Speed < 0f && time <= 0d)
            {
                layer.Time = layer.Speed >= 0f ? clip.Duration : 0d;
                layer.Finished = true;
                if (i == _layers.Count - 1)
                {
                    finished = clip;
                }
            }
            else
            {
                layer.Time = time;
            }
        }

        // The newest fully faded-in layer covers everything below it.
        for (var i = _layers.Count - 1; i > 0; i--)
        {
            if (_layers[i].Weight >= 1f)
            {
                _layers.RemoveRange(0, i);
                break;
            }
        }

        if (_layers.Count == 1 && _layers[0].Clip is null)
        {
            _layers.Clear(); // the fade back to rest is complete
        }

        _dirty = true;
        if (finished is not null)
        {
            Finished?.Invoke(this, finished);
        }
    }

    private void Push(AnimationLayer layer)
    {
        if (layer.FadeDuration <= 0f)
        {
            _layers.Clear();
        }
        else if (_layers.Count == 0)
        {
            // Fading in from nothing fades in from the rest pose.
            _layers.Add(new AnimationLayer { Clip = null });
        }

        if (_layers.Count >= MaxLayers)
        {
            _layers.RemoveAt(0);
        }

        _layers.Add(layer);
        _dirty = true;
    }

    private static double Wrap(AnimationClip clip, double time, bool loop)
    {
        var duration = (double)clip.Duration;
        if (!(duration > 0d))
        {
            return 0d;
        }

        if (!loop)
        {
            return global::System.Math.Clamp(time, 0d, duration);
        }

        var wrapped = time % duration;
        return wrapped < 0d ? wrapped + duration : wrapped;
    }
}
