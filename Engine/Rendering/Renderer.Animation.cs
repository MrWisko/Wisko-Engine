using Wisko.Engine.Animation;
using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The Animation panel (ToolBench > Animation), docked on the left beside Physics. Whether it is open is editor UI state
/// for this session only. It shows the one selected imported model: its clips, a preview of the chosen clip and the
/// model's saved animation settings.
/// <para>Preview: editor-only playback with its own <see cref="ModelAnimator"/> attached to the model (so the viewport
/// draws it), advanced every editor frame. It is never saved, never an undo step and never seen by the scene file (the
/// project does not become "changed" by previewing). Stop, closing the panel, selecting something else, leaving the
/// project and starting Play all end it: the model is drawn in its rest pose again.</para>
/// <para>Saved settings (Loop, Speed, Play on start for the chosen clip): one undo step per change; the preview follows
/// Loop and Speed. Edit Mode only. In Play the panel shows the model's running animation, read-only.</para>
/// </summary>
public sealed partial class Renderer
{
    private const string AnimationPlayMessage = "Stop Play Mode to preview or change animations.";

    // The speeds the panel's - and + step through.
    private static readonly float[] AnimationSpeedSteps = [0.1f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f];

    private readonly AnimationPanel _animationPanel = new();
    private bool _animationPanelOn;
    private int _animationScroll;

    // The model the panel shows (the single selected model instance), and the clip chosen in its list.
    private Part? _animationPart;
    private int _animationClip = -1;

    // The editor preview: its animator is attached to _previewPart (Part.Animator) while it exists.
    private Part? _previewPart;
    private ModelAnimator? _preview;
    private bool _scrubbing;

    // Model files whose animation notes were written to Output this session.
    private readonly HashSet<string> _reportedAnimationNotes = new(StringComparer.Ordinal);

    private bool AnimationPanelVisible => _animationPanelOn && _workspace is not null;

    // Under the toolbar, after Physics when that is open; never wider than the room the side columns leave.
    private UiBox AnimationPanelRect
    {
        get
        {
            var left = PhysicsPanelVisible ? PhysicsPanelRect.Width : 0f;
            return new UiBox(left, ToolbarRenderer.ToolbarHeight, MathF.Min(AnimationPanel.Width, MathF.Max(0f, _width - RightPanelWidth - left)),
                MathF.Max(0f, _height - ToolbarRenderer.ToolbarHeight));
        }
    }

    private bool IsOverAnimationPanel(float x, float y) => AnimationPanelVisible && AnimationPanelRect.Contains(x, y);

    /// <summary>The editor preview's model, if a preview is running (tests and diagnostics).</summary>
    internal Part? AnimationPreviewPart => _preview is null ? null : _previewPart;

    private void OpenAnimationPanel()
    {
        if (_animationPanelOn)
        {
            Console.WriteLine("[Animation] Panel already open");
            return;
        }

        _animationPanelOn = true;
        _animationPart = null; // the selection is taken up (and its model's notes reported) on the next frame
        Console.WriteLine("[Animation] Panel open");
    }

    private void CloseAnimationPanel(bool quiet = false)
    {
        StopAnimationPreview();
        _scrubbing = false;
        _animationPanelOn = false;
        _animationPart = null;
        _animationClip = -1;
        if (!quiet)
        {
            Console.WriteLine("[Animation] Panel closed");
        }
    }

    /// <summary>Ends the editor preview: the model is drawn in its rest pose again. Nothing to do when none runs.</summary>
    private void StopAnimationPreview()
    {
        _scrubbing = false;
        if (_preview is null)
        {
            return;
        }

        if (_previewPart is { } part && ReferenceEquals(part.Animator, _preview))
        {
            part.Animator = null;
        }

        Console.WriteLine($"[Animation] Preview stopped: {_previewPart?.Name} back in its rest pose");
        _preview = null;
        _previewPart = null;
    }

    // Every editor frame (Edit Mode): the preview's clock.
    private void UpdateAnimationPreview(float deltaTime)
    {
        if (_preview is null || _playMode.IsPlaying)
        {
            return;
        }

        // A previewed model that left the scene (deleted, undone) ends its preview.
        if (_previewPart is null || _workspace?.Parts.Contains(_previewPart) != true)
        {
            StopAnimationPreview();
            return;
        }

        _preview.Advance(deltaTime);
    }

    // The single selected model is the one the panel shows; another selection ends a preview of the previous one.
    private void SyncAnimationTarget()
    {
        var selected = _selection?.SelectedParts ?? [];
        var target = selected.Count == 1 && selected[0].Model is not null ? selected[0] : null;
        if (ReferenceEquals(target, _animationPart))
        {
            return;
        }

        if (_previewPart is not null && !ReferenceEquals(_previewPart, target))
        {
            StopAnimationPreview();
        }

        _animationPart = target;

        // What was wrong with the model's animation data, once per model file and session, in Output.
        if (_animationPanelOn && target?.Model?.Asset is { Animation.Notes.Count: > 0 } asset && _reportedAnimationNotes.Add(asset.AssetPath))
        {
            foreach (var note in asset.Animation.Notes)
            {
                _output.Add(new OutputEntry(OutputKind.Warning, $"[Animation] {asset.AssetPath}: {note}", null, null, target.Name));
            }
        }

        var clips = ClipsOf(target);
        var saved = target?.Animation?.Clip is { } name ? clips.ToList().FindIndex(clip => clip.Name == name) : -1;
        _animationClip = clips.Count == 0 ? -1 : global::System.Math.Max(saved, 0);
        _animationScroll = ScrollToShow(_animationClip, clips.Count, 0);
    }

    private static IReadOnlyList<AnimationClip> ClipsOf(Part? part) => part?.Model?.Asset?.Animation.Clips ?? (IReadOnlyList<AnimationClip>)[];

    private static int ScrollToShow(int index, int count, int scroll)
    {
        var rows = AnimationPanel.VisibleRows(count);
        if (index < 0 || rows == 0)
        {
            return 0;
        }

        scroll = index < scroll ? index : index >= scroll + rows ? index - rows + 1 : scroll;
        return global::System.Math.Clamp(scroll, 0, global::System.Math.Max(0, count - rows));
    }

    // Everything over the panel is the panel's: the × closes it, the list chooses a clip, the buttons and the timeline
    // drive the preview, the settings rows change the saved settings. A timeline drag continues wherever the mouse goes.
    private void HandleAnimationPanel(InputManager input)
    {
        SyncAnimationTarget();
        if (!AnimationPanelVisible)
        {
            _scrubbing = false;
            return;
        }

        var panel = AnimationPanelRect;
        var clips = ClipsOf(_animationPart);
        if (_scrubbing)
        {
            _toolbarConsumedClick = true;
            if (input.IsLeftMouseHeld && _preview is not null && _animationClip >= 0)
            {
                Scrub(panel, clips, input.MouseX);
            }
            else
            {
                _scrubbing = false;
            }

            return;
        }

        if (!panel.Contains(input.MouseX, input.MouseY))
        {
            return;
        }

        if (input.ScrollDelta != 0f)
        {
            _wheelConsumed = true;
            if (AnimationPanel.ClipList(panel, clips.Count).Contains(input.MouseX, input.MouseY))
            {
                var rows = AnimationPanel.VisibleRows(clips.Count);
                _animationScroll = global::System.Math.Clamp(_animationScroll - global::System.Math.Sign(input.ScrollDelta), 0, global::System.Math.Max(0, clips.Count - rows));
            }
        }

        if (_toolbarConsumedClick || (!input.LeftMousePressed && !input.RightMousePressed))
        {
            return;
        }

        _toolbarConsumedClick = true;
        if (!input.LeftMousePressed)
        {
            return;
        }

        var (x, y) = (input.MouseX, input.MouseY);
        if (AnimationPanel.CloseButton(panel).Contains(x, y))
        {
            CloseAnimationPanel();
            return;
        }

        if (clips.Count == 0 || _animationPart is null)
        {
            return;
        }

        var controls = AnimationPanel.ClipList(panel, clips.Count).Contains(x, y)
            || AnimationPanel.TransportButton(panel, clips.Count, 0).Contains(x, y)
            || AnimationPanel.TransportButton(panel, clips.Count, 1).Contains(x, y)
            || AnimationPanel.TransportButton(panel, clips.Count, 2).Contains(x, y)
            || AnimationPanel.Scrubber(panel, clips.Count).Contains(x, y)
            || AnimationPanel.LoopRow(panel, clips.Count).Contains(x, y)
            || AnimationPanel.SpeedRow(panel, clips.Count).Contains(x, y)
            || AnimationPanel.PlayOnStartRow(panel, clips.Count).Contains(x, y);
        if (controls && _playMode.IsPlaying)
        {
            _toast.Show(AnimationPlayMessage);
            return;
        }

        var clipIndex = AnimationPanel.ClipAt(panel, clips.Count, _animationScroll, x, y);
        if (clipIndex >= 0)
        {
            ChooseAnimationClip(clipIndex);
        }
        else if (AnimationPanel.TransportButton(panel, clips.Count, 0).Contains(x, y))
        {
            PreviewPlay();
        }
        else if (AnimationPanel.TransportButton(panel, clips.Count, 1).Contains(x, y))
        {
            if (_preview is { IsPaused: false } playing)
            {
                playing.Pause();
                Console.WriteLine($"[Animation] Preview paused at {AnimationPanel.FormatSeconds(playing.Time)}");
            }
        }
        else if (AnimationPanel.TransportButton(panel, clips.Count, 2).Contains(x, y))
        {
            StopAnimationPreview();
        }
        else if (AnimationPanel.Scrubber(panel, clips.Count).Contains(x, y))
        {
            BeginScrub(panel, clips, x);
        }
        else if (AnimationPanel.LoopRow(panel, clips.Count).Contains(x, y))
        {
            ChangeAnimationSettings("Loop", settings => settings with { Loop = !settings.Loop });
        }
        else if (AnimationPanel.SpeedControl(panel, clips.Count, 0).Contains(x, y))
        {
            ChangeAnimationSettings("Speed", settings => settings with { Speed = StepSpeed(settings.Speed, -1) });
        }
        else if (AnimationPanel.SpeedControl(panel, clips.Count, 1).Contains(x, y))
        {
            ChangeAnimationSettings("Speed", settings => settings with { Speed = StepSpeed(settings.Speed, +1) });
        }
        else if (AnimationPanel.SpeedControl(panel, clips.Count, 2).Contains(x, y))
        {
            ChangeAnimationSettings("Speed", settings => settings with { Speed = 1f });
        }
        else if (AnimationPanel.PlayOnStartRow(panel, clips.Count).Contains(x, y) && _animationClip >= 0)
        {
            var name = clips[_animationClip].Name;
            ChangeAnimationSettings("Play on start", settings => settings.PlayOnStart && settings.Clip == name
                ? settings with { PlayOnStart = false, Clip = null }
                : settings with { PlayOnStart = true, Clip = name });
        }
    }

    // The next speed step up or down from the current speed.
    private static float StepSpeed(float speed, int direction)
    {
        if (direction > 0)
        {
            foreach (var step in AnimationSpeedSteps)
            {
                if (step > speed + 1e-4f)
                {
                    return step;
                }
            }

            return AnimationSpeedSteps[^1];
        }

        for (var i = AnimationSpeedSteps.Length - 1; i >= 0; i--)
        {
            if (AnimationSpeedSteps[i] < speed - 1e-4f)
            {
                return AnimationSpeedSteps[i];
            }
        }

        return AnimationSpeedSteps[0];
    }

    // Another clip in the list: a running preview switches to it from its start (a paused one stays paused there).
    private void ChooseAnimationClip(int index)
    {
        var clips = ClipsOf(_animationPart);
        _animationClip = index;
        _animationScroll = ScrollToShow(index, clips.Count, _animationScroll);
        Console.WriteLine($"[Animation] Clip chosen: {clips[index].Name} ({AnimationPanel.FormatSeconds(clips[index].Duration)})");
        if (_preview is { } preview && ReferenceEquals(_previewPart, _animationPart))
        {
            var paused = preview.IsPaused;
            var settings = _animationPart!.Animation ?? ModelAnimationSettings.Default;
            preview.Play(clips[index], settings.Loop, settings.Speed, startTime: 0d, restart: true);
            if (paused)
            {
                preview.Pause();
            }
        }
    }

    // Play: resumes a paused preview, restarts a finished one, or starts one with the saved Loop and Speed.
    private void PreviewPlay()
    {
        var part = _animationPart!;
        var clip = ClipsOf(part)[_animationClip];
        var settings = part.Animation ?? ModelAnimationSettings.Default;
        if (_preview is { } preview && ReferenceEquals(_previewPart, part))
        {
            if (preview.IsPaused)
            {
                preview.Resume();
            }
            else if (preview.CurrentClip != clip || preview.IsFinished || preview.IsStopped)
            {
                preview.Play(clip, settings.Loop, settings.Speed, restart: true);
            }

            Console.WriteLine($"[Animation] Preview playing {clip.Name}");
            return;
        }

        StartPreview(part).Play(clip, settings.Loop, settings.Speed);
        Console.WriteLine($"[Animation] Preview playing {clip.Name} on {part.Name} (loop {settings.Loop}, speed {AnimationPanel.FormatSpeed(settings.Speed)})");
    }

    private ModelAnimator StartPreview(Part part)
    {
        StopAnimationPreview();
        var preview = new ModelAnimator(part.Model!.Asset!.Animation);
        part.Animator = preview;
        _preview = preview;
        _previewPart = part;
        return preview;
    }

    // A press on the timeline: the preview pauses at that time (made, paused, when none runs) and follows the drag.
    private void BeginScrub(UiBox panel, IReadOnlyList<AnimationClip> clips, float x)
    {
        var part = _animationPart!;
        var clip = clips[_animationClip];
        if (_preview is not { } preview || !ReferenceEquals(_previewPart, part) || preview.CurrentClip != clip)
        {
            var settings = part.Animation ?? ModelAnimationSettings.Default;
            preview = _preview is not null && ReferenceEquals(_previewPart, part) ? _preview : StartPreview(part);
            preview.Play(clip, settings.Loop, settings.Speed, restart: true);
        }

        preview.Pause();
        _scrubbing = true;
        Scrub(panel, clips, x);
    }

    private void Scrub(UiBox panel, IReadOnlyList<AnimationClip> clips, float x)
    {
        var clip = clips[_animationClip];

        // Just short of the end, so a looping clip shows its last pose rather than wrapping to its first.
        var time = global::System.Math.Min(AnimationPanel.TimeAt(panel, clips.Count, x, clip.Duration), clip.Duration * 0.9999d);
        _preview?.Seek(time);
    }

    // One saved-settings change of the shown model: applied, then one undo step (none when nothing changed). The preview
    // follows Loop and Speed at once.
    private void ChangeAnimationSettings(string label, Func<ModelAnimationSettings, ModelAnimationSettings> change)
    {
        if (_animationPart is not { } part)
        {
            return;
        }

        var before = PartSnapshot.FromPart(part);
        var updated = change(part.Animation ?? ModelAnimationSettings.Default);
        part.Animation = updated.IsDefault ? null : updated;
        if (Equals(before.Animation, part.Animation))
        {
            return;
        }

        _editorHistory.Push(EditorCommand.AnimationChange(part, before, PartSnapshot.FromPart(part), label));
        if (_preview is { } preview && ReferenceEquals(_previewPart, part))
        {
            preview.SetLoop(updated.Loop);
            preview.SetSpeed(updated.Speed);
        }

        Console.WriteLine($"[Animation] {label}: {part.Name} (loop {updated.Loop}, speed {AnimationPanel.FormatSpeed(updated.Speed)}, "
            + $"play on start {(updated.PlayOnStart ? updated.Clip : "off")})");
    }

    private void RenderAnimationPanel(float dpiScale)
    {
        if (!AnimationPanelVisible)
        {
            return;
        }

        _animationPanel.Render(AnimationPanelRect, BuildAnimationPanelView(), _lastMouseX, _lastMouseY, _width, _height, dpiScale);
    }

    // What the panel shows now: in Edit Mode the preview, in Play the model's running animation.
    private AnimationPanelView BuildAnimationPanelView()
    {
        var selected = _selection?.SelectedParts ?? [];
        var part = _animationPart;
        var editable = !_playMode.IsPlaying;
        string selection;
        string message;
        if (selected.Count == 0)
        {
            (selection, message) = ("No object selected", "Select an imported model.");
        }
        else if (selected.Count > 1)
        {
            (selection, message) = ($"{selected.Count} objects selected", "Select one model to see its animations.");
        }
        else if (part is null)
        {
            (selection, message) = ($"Selected: {selected[0].Name}", "Only imported models have animations.");
        }
        else if (part.Model?.Asset is not { } asset)
        {
            (selection, message) = ($"Selected: {part.Name}", "Its model file could not be loaded.");
        }
        else if (!asset.Animation.HasClips)
        {
            (selection, message) = ($"Selected: {part.Name}", "This model has no animations.");
        }
        else
        {
            var notes = asset.Animation.Notes.Count;
            selection = $"Selected: {part.Name}";
            message = $"{asset.Animation.Clips.Count} clip{(asset.Animation.Clips.Count == 1 ? "" : "s")}, {asset.Animation.Skeleton.BoneCount} bones"
                + (notes > 0 ? $", {notes} note{(notes == 1 ? "" : "s")} in Output" : string.Empty);
        }

        var clips = ClipsOf(part);
        var settings = part?.Animation ?? ModelAnimationSettings.Default;
        var playOnStart = settings.PlayOnStart && settings.Clip is { } name ? clips.ToList().FindIndex(clip => clip.Name == name) : -1;
        var shownClip = _animationClip;
        var time = 0d;
        var previewing = false;
        var paused = false;
        string status;
        if (!editable)
        {
            // Play: what the model's running animation does (its current clip highlighted).
            var running = part?.Animator;
            if (running?.CurrentClip is { } current)
            {
                shownClip = current.Index;
                time = running.Time;
                previewing = true;
                paused = running.IsPaused;
                status = running.IsPaused ? "paused" : running.IsFinished ? "finished" : "playing";
            }
            else
            {
                status = "not animated";
            }
        }
        else if (_preview is { } preview && ReferenceEquals(_previewPart, part))
        {
            previewing = true;
            paused = preview.IsPaused;
            time = preview.CurrentClip?.Index == _animationClip ? preview.Time : 0d;
            status = preview.IsPaused ? "paused" : preview.IsFinished ? "finished" : "previewing";
        }
        else
        {
            status = "rest pose";
        }

        return new AnimationPanelView(
            selection,
            message,
            clips.Select(clip => new AnimationPanelClip(clip.Name, clip.Duration)).ToArray(),
            shownClip,
            playOnStart,
            _animationScroll,
            previewing,
            paused,
            time,
            shownClip >= 0 && shownClip < clips.Count ? clips[shownClip].Duration : 0f,
            settings.Loop,
            settings.Speed,
            editable,
            status);
    }
}
