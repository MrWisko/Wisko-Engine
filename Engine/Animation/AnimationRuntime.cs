using Wisko.Engine.World;

namespace Wisko.Engine.Animation;

/// <summary>
/// A Play session's animation playback: one <see cref="ModelAnimator"/> per model instance that has clips, made when it
/// first plays something (its saved Play on start clip, or a script asking) and attached as <see cref="Part.Animator"/>
/// so the renderer draws its pose. Advanced once per frame after physics; clips that finish playing once are reported
/// afterwards. Disposing it (Stop) detaches every animator, so every model shows its rest pose again: nothing about
/// playback is ever saved.
/// </summary>
public sealed class AnimationRuntime : IDisposable
{
    private readonly Dictionary<Part, ModelAnimator> _animators = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Part Part, AnimationClip Clip)> _finished = [];
    private bool _disposed;

    /// <summary>Told after each <see cref="Advance"/> about every current clip that reached its end playing once.</summary>
    public Action<Part, AnimationClip>? Finished { get; set; }

    /// <summary>Told about a Play on start setting that cannot be followed (e.g. its clip is no longer in the model).</summary>
    public Action<string>? Warning { get; set; }

    /// <summary>Animators made so far this session (diagnostics).</summary>
    public int Count => _animators.Count;

    /// <summary>The animator of a model instance with clips (made and attached on first use); null for anything else.</summary>
    public ModelAnimator? Get(Part part)
    {
        if (_disposed)
        {
            return null;
        }

        if (_animators.TryGetValue(part, out var animator))
        {
            return animator;
        }

        if (part.Model?.Asset is not { Animation.HasClips: true } asset)
        {
            return null;
        }

        animator = new ModelAnimator(asset.Animation) { Finished = (_, clip) => _finished.Add((part, clip)) };
        _animators[part] = animator;
        part.Animator = animator;
        return animator;
    }

    /// <summary>Starts the saved Play on start clip of each of these Parts that has one (looping and speed as saved).</summary>
    public void StartPlayOnStart(IEnumerable<Part> parts)
    {
        foreach (var part in parts)
        {
            if (part.Animation is not { PlayOnStart: true } settings || part.Model is null)
            {
                continue;
            }

            if (part.Model.Asset is not { } asset)
            {
                Warning?.Invoke($"'{part.Name}': its model ({part.Model.AssetPath}) is not loaded, so its Play on start animation does not play");
                continue;
            }

            var clip = settings.Clip is null ? null : asset.Animation.FindClip(settings.Clip);
            if (clip is null)
            {
                Warning?.Invoke(settings.Clip is null
                    ? $"'{part.Name}': Play on start is on but no animation is chosen"
                    : $"'{part.Name}': its Play on start animation \"{settings.Clip}\" is not in {part.Model.AssetPath} (it has: {ClipList(asset.Animation)})");
                continue;
            }

            Get(part)?.Play(clip, settings.Loop, settings.Speed);
        }
    }

    /// <summary>Advances every animator by one frame (real seconds), then reports clips that finished.</summary>
    public void Advance(double deltaTime)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var animator in _animators.Values)
        {
            animator.Advance(deltaTime);
        }

        if (_finished.Count == 0)
        {
            return;
        }

        var finished = _finished.ToArray();
        _finished.Clear();
        foreach (var (part, clip) in finished)
        {
            Finished?.Invoke(part, clip);
        }
    }

    /// <summary>A Part left the running scene (destroyed): its animator goes (it shows its rest pose if it comes back).</summary>
    public void Remove(Part part)
    {
        if (_animators.Remove(part))
        {
            part.Animator = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var part in _animators.Keys)
        {
            part.Animator = null;
        }

        _animators.Clear();
        _finished.Clear();
    }

    /// <summary>"Idle, Walk, Run" (the clip names, in file order), for messages.</summary>
    public static string ClipList(ModelAnimationData data) =>
        data.Clips.Count == 0 ? "no animations" : string.Join(", ", data.Clips.Select(clip => clip.Name));
}
