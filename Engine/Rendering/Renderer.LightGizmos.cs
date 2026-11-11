using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.Rendering.Backend.Bgfx.Editor;
using Wisko.Engine.World;
using NVector3 = System.Numerics.Vector3;

namespace Wisko.Engine.Rendering;

/// <summary>
/// Editor marks for lights: every light part has an icon (a small sun facing the camera, with a tick showing
/// a spot or directional light's direction), so an invisible light can be seen and clicked; a selected light also shows
/// its reach: a point light's range (three rings), a spot light's cone (its reach, rim and edges, and the inner cone
/// fainter) or a directional light's direction (an arrow). Unlit, depth-tested lines in the light's colour, drawn in
/// Edit mode only (both renderers); never part of the scene or of Play.
/// </summary>
public sealed partial class Renderer
{
    private const int GizmoSegments = 48;
    private const int IconSegments = 16;
    private DynamicColoredMesh? _lightGizmos;
    private float[] _lightGizmoFloats = new float[DynamicColoredMesh.FloatsPerVertex * 1024];
    private int _lightGizmoVertices;
    private readonly List<Vertex> _bgfxLightGizmoVertices = [];

    private void RenderLightGizmosGl(Matrix4 view, Matrix4 projection)
    {
        if (!BuildLightGizmos())
        {
            return;
        }

        _lightGizmos ??= new DynamicColoredMesh();
        _lightGizmos.Update(_lightGizmoFloats, _lightGizmoVertices);
        _shader!.Use();
        _shader.SetMatrix4("uView", view);
        _shader.SetMatrix4("uProjection", projection);
        _shader.SetMatrix4("uModel", Matrix4.Identity);
        GL.DepthFunc(DepthFunction.Lequal);
        _lightGizmos.Draw(PrimitiveType.Lines);
        GL.DepthFunc(DepthFunction.Less);
    }

    // bgfx: the same lines, unlit and depth-tested (LEQUAL), as the scripts' debug lines are drawn there.
    private void SubmitLightGizmosBgfx(BgfxEditorGraphics graphics, ushort view)
    {
        if (!BuildLightGizmos())
        {
            return;
        }

        _bgfxLightGizmoVertices.Clear();
        for (var i = 0; i < _lightGizmoVertices; i++)
        {
            var o = i * DynamicColoredMesh.FloatsPerVertex;
            _bgfxLightGizmoVertices.Add(new Vertex(new Vector3(_lightGizmoFloats[o], _lightGizmoFloats[o + 1], _lightGizmoFloats[o + 2]),
                new Vector3(_lightGizmoFloats[o + 3], _lightGizmoFloats[o + 4], _lightGizmoFloats[o + 5])));
        }

        graphics.SubmitTransientTriangles(view, _bgfxLightGizmoVertices, BgfxEditorGraphics.OutlineState | (ulong)global::Bgfx.bgfx.StateFlags.PtLines);
    }

    // This frame's light icons and gizmos (and the selected baseplate's outline) as line vertices; false when there are
    // none (Play, no world). Backend-neutral: the GL and bgfx passes only draw what this builds.
    private bool BuildLightGizmos()
    {
        if (_playMode.IsPlaying || _workspace is null)
        {
            return false;
        }

        _lightGizmoVertices = 0;
        foreach (var part in _workspace.Parts)
        {
            if (part.Light is { } light)
            {
                AddLightIcon(part, light);
                if (_selection?.IsSelected(part) == true)
                {
                    AddLightGizmo(part, light);
                }
            }
        }

        // The selected baseplate (World panel row or Ctrl+click) is outlined with the same lines.
        AddEnvironmentOutline();
        return _lightGizmoVertices > 0;
    }

    private void AddLightGizmo(Part part, PartLight light)
    {
        var rotation = PhysicsMath.EulerDegreesToQuaternion(part.Rotation);
        Vector3 Axis(float x, float y, float z) => PhysicsMath.ToWisko(NVector3.Transform(new NVector3(x, y, z), rotation));
        var forward = Axis(0f, 0f, -1f);
        var right = Axis(1f, 0f, 0f);
        var up = Axis(0f, 1f, 0f);
        var color = Brighten(light.Color, light.Enabled ? 1f : 0.45f);
        var faint = Brighten(light.Color, light.Enabled ? 0.55f : 0.3f);
        var p = part.Position;
        switch (light.Type)
        {
            case LightType.Point:
                Ring(p, new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), light.Range, color);
                Ring(p, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), light.Range, faint);
                Ring(p, new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f), light.Range, faint);
                break;

            case LightType.Spot:
                Cone(p, forward, right, up, light.Range, light.Angle * 0.5f, color, edges: 8);
                Cone(p, forward, right, up, light.Range, light.InnerAngle * 0.5f, faint, edges: 0);
                Line(p, p + forward * light.Range, faint);
                break;

            default:
                // An arrow along the light's direction, with a short crossbar at its tail.
                var length = MathF.Max(4f, part.Size.Length * 2f);
                var tip = p + forward * length;
                Line(p, tip, color);
                Line(tip, tip - forward * 1.2f + right * 0.6f, color);
                Line(tip, tip - forward * 1.2f - right * 0.6f, color);
                Line(tip, tip - forward * 1.2f + up * 0.6f, color);
                Line(tip, tip - forward * 1.2f - up * 0.6f, color);
                Line(p - right * 0.8f, p + right * 0.8f, faint);
                break;
        }
    }

    // A small sun facing the camera at the light (about the same size on screen at any distance), and a tick along a
    // spot or directional light's direction.
    private void AddLightIcon(Part part, PartLight light)
    {
        var p = part.Position;
        var scale = global::System.Math.Clamp((p - _camera.Position).Length * 0.02f, 0.3f, 4f);
        var right = _camera.Right;
        var up = _camera.Up;
        var color = Brighten(light.Color, light.Enabled ? 1f : 0.45f);
        var previous = p + right * (0.3f * scale);
        for (var i = 1; i <= IconSegments; i++)
        {
            var t = i * MathF.PI * 2f / IconSegments;
            var next = p + right * (MathF.Cos(t) * 0.3f * scale) + up * (MathF.Sin(t) * 0.3f * scale);
            Line(previous, next, color);
            previous = next;
        }

        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4f;
            var direction = right * MathF.Cos(a) + up * MathF.Sin(a);
            Line(p + direction * (0.45f * scale), p + direction * (0.7f * scale), color);
        }

        if (light.Type != LightType.Point)
        {
            var rotation = PhysicsMath.EulerDegreesToQuaternion(part.Rotation);
            var forward = PhysicsMath.ToWisko(NVector3.Transform(new NVector3(0f, 0f, -1f), rotation));
            Line(p, p + forward * (1.4f * scale), color);
        }
    }

    // The cone of a spot light: where its reach ends (a rim at the cone's half-angle) and edges from the light to it.
    private void Cone(Vector3 apex, Vector3 forward, Vector3 right, Vector3 up, float range, float halfAngleDegrees, Vector3 color, int edges)
    {
        var half = halfAngleDegrees * MathF.PI / 180f;
        var center = apex + forward * (range * MathF.Cos(half));
        var radius = range * MathF.Sin(half);
        Ring(center, right, up, radius, color);
        for (var i = 0; i < edges; i++)
        {
            var a = i * MathF.PI * 2f / edges;
            Line(apex, center + right * (MathF.Cos(a) * radius) + up * (MathF.Sin(a) * radius), color);
        }
    }

    private void Ring(Vector3 center, Vector3 a, Vector3 b, float radius, Vector3 color)
    {
        var previous = center + a * radius;
        for (var i = 1; i <= GizmoSegments; i++)
        {
            var t = i * MathF.PI * 2f / GizmoSegments;
            var next = center + a * (MathF.Cos(t) * radius) + b * (MathF.Sin(t) * radius);
            Line(previous, next, color);
            previous = next;
        }
    }

    private void Line(Vector3 from, Vector3 to, Vector3 color)
    {
        var needed = (_lightGizmoVertices + 2) * DynamicColoredMesh.FloatsPerVertex;
        if (needed > _lightGizmoFloats.Length)
        {
            Array.Resize(ref _lightGizmoFloats, global::System.Math.Max(needed, _lightGizmoFloats.Length * 2));
        }

        Vertex(from, color);
        Vertex(to, color);
    }

    private void Vertex(Vector3 position, Vector3 color)
    {
        var i = _lightGizmoVertices * DynamicColoredMesh.FloatsPerVertex;
        var f = _lightGizmoFloats;
        f[i] = position.X;
        f[i + 1] = position.Y;
        f[i + 2] = position.Z;
        f[i + 3] = color.X;
        f[i + 4] = color.Y;
        f[i + 5] = color.Z;
        f[i + 6] = 0f;
        f[i + 7] = 1f;
        f[i + 8] = 0f;
        _lightGizmoVertices++;
    }

    // The light's colour, lifted towards white so a dark-coloured light still shows.
    private static Vector3 Brighten(Vector3 color, float amount) =>
        new((0.35f + 0.65f * color.X) * amount, (0.35f + 0.65f * color.Y) * amount, (0.35f + 0.65f * color.Z) * amount);

    private void ShutdownLightGizmos()
    {
        _lightGizmos?.Dispose();
        _lightGizmos = null;
    }
}
