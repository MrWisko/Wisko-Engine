using Wisko.Engine.Math;
using Wisko.Engine.Scripting;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The editor's view camera as Play scripts reach it (Camera): the same camera the 3D view draws with, on both backends.
/// The first script change in a Play session remembers the camera as it was, and <see cref="End"/> puts it back. Once a
/// script places the camera (Position, Rotation, LookAt), the editor's free-fly controls leave it alone for the rest
/// of the session (<see cref="ScriptControlled"/>); a FieldOfView change alone does not take it over. Screen points are
/// window pixels from the top-left (as the mouse), the view the camera projects to being the whole window.
/// </summary>
internal sealed class PlayCamera(EditorCamera camera, Func<(int Width, int Height)> viewport, Action<bool>? lockCursor = null) : IScriptCameraHost
{
    private (Vector3 Position, float Yaw, float Pitch, float FieldOfView)? _saved;
    private bool _mouseLocked;

    /// <summary>A script locked the mouse (Input.SetMouseLocked): the window holds and hides the cursor until unlocked or Play stops.</summary>
    public bool MouseLocked
    {
        get => _mouseLocked;
        set
        {
            if (_mouseLocked == value)
            {
                return;
            }

            _mouseLocked = value;
            lockCursor?.Invoke(value);
        }
    }

    /// <summary>A script placed the camera this session: the editor's look / fly controls do nothing until Play stops.</summary>
    public bool ScriptControlled { get; private set; }

    public Vector3 Position
    {
        get => camera.Position;
        set
        {
            TakeControl();
            camera.Position = value;
        }
    }

    public float Pitch => camera.PitchDegrees;

    public float Yaw => Physics.PhysicsMath.NormalizeDegrees(camera.YawDegrees);

    public float FieldOfView
    {
        get => camera.FieldOfViewDegrees;
        set
        {
            Remember();
            camera.FieldOfViewDegrees = value;
        }
    }

    public Vector3 Forward => camera.Forward;

    public Vector3 Right => camera.Right;

    public Vector3 Up => camera.Up;

    public int ViewportWidth => global::System.Math.Max(viewport().Width, 1);

    public int ViewportHeight => global::System.Math.Max(viewport().Height, 1);

    /// <summary>A Play session begins: nothing remembered, the editor controls the camera.</summary>
    public void Begin()
    {
        _saved = null;
        ScriptControlled = false;
    }

    /// <summary>Play stopped: the camera goes back to where it was before the first script change (if any).</summary>
    public void End()
    {
        MouseLocked = false;
        if (_saved is { } saved)
        {
            camera.Position = saved.Position;
            camera.SetOrientation(saved.Yaw, saved.Pitch);
            camera.FieldOfViewDegrees = saved.FieldOfView;
        }

        _saved = null;
        ScriptControlled = false;
    }

    public void SetRotation(float pitch, float yaw)
    {
        TakeControl();
        camera.SetOrientation(yaw, pitch);
    }

    public bool WorldToScreen(Vector3 point, out float x, out float y) =>
        ScreenProjection.TryProjectWorldPoint(point, camera, ViewportWidth, ViewportHeight, out x, out y);

    public (Vector3 Origin, Vector3 Direction) ScreenPointToRay(float x, float y)
    {
        var ray = MouseRay.FromScreen(x, y, ViewportWidth, ViewportHeight, camera);
        return (ray.Origin, ray.Direction);
    }

    private void Remember() => _saved ??= (camera.Position, camera.YawDegrees, camera.PitchDegrees, camera.FieldOfViewDegrees);

    private void TakeControl()
    {
        Remember();
        ScriptControlled = true;
    }
}
