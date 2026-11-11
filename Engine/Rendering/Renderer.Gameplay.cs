using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Gameplay;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.Rendering.Backend.Bgfx.Editor;
using Wisko.Engine.UI;

namespace Wisko.Engine.Rendering;

// Drawn while playing: scripts' debug lines (DebugDraw), particles (Particles: rain, snow...) and
// camera feeds (Cameras: a named camera rendered into a texture that GUI Images show as "camera:<Name>"). OpenGL draws
// all three; bgfx draws lines and particles as transient geometry (unverified on macOS) and has no camera feeds yet.
public sealed partial class Renderer
{
    private const int MaxParticleVertices = ParticleSystem.MaxTotalParticles * 6;

    private readonly List<DebugSegment> _debugSegments = [];
    private readonly Dictionary<int, CameraFeedTarget> _cameraFeeds = [];
    private readonly Dictionary<string, int> _cameraFeedTextures = new(StringComparer.Ordinal);
    private readonly EditorCamera _feedCamera = new() { NearPlane = 0.1f, FarPlane = 2000f };
    private readonly List<Vertex> _bgfxGameplayVertices = [];
    private DynamicColoredMesh? _debugLines;
    private DynamicColoredMesh? _particleQuads;
    private float[] _debugFloats = [];
    private float[] _particleFloats = [];
    private int _debugVersion = -1;
    private int _debugVertexCount;

    /// <summary>Particles drawn last frame (diagnostics).</summary>
    internal int LastParticleCount { get; private set; }

    /// <summary>Debug lines drawn last frame (diagnostics).</summary>
    internal int LastDebugLineCount { get; private set; }

    /// <summary>Camera feeds rendered last frame (diagnostics).</summary>
    internal int LastCameraFeedCount { get; private set; }

    /// <summary>With <see cref="MeasurePartTiming"/>: CPU and GPU milliseconds of last frame's camera feeds (all of them).</summary>
    internal double LastFeedsCpuMs { get; private set; }

    internal double LastFeedsGpuMs => _feedsTimer.LastGpuMs;

    /// <summary>With <see cref="MeasurePartTiming"/>: CPU and GPU milliseconds of last frame's particles and debug lines.</summary>
    internal double LastGameplayDrawCpuMs { get; private set; }

    internal double LastGameplayDrawGpuMs => _overlayTimer.LastGpuMs;

    private readonly GpuTimer _feedsTimer = new();
    private readonly GpuTimer _overlayTimer = new();
    private bool _renderingFeeds;

    /// <summary>One OpenGL time-elapsed query, read back a frame or more later (diagnostics only).</summary>
    private sealed class GpuTimer
    {
        private int _query;
        private bool _pending;
        private bool _running;

        public double LastGpuMs { get; private set; }

        public void Begin()
        {
            if (_pending)
            {
                GL.GetQueryObject(_query, GetQueryObjectParam.QueryResultAvailable, out int available);
                if (available != 0)
                {
                    GL.GetQueryObject(_query, GetQueryObjectParam.QueryResult, out long nanoseconds);
                    LastGpuMs = nanoseconds / 1_000_000d;
                    _pending = false;
                }
            }

            if (!_pending)
            {
                if (_query == 0)
                {
                    _query = GL.GenQuery();
                }

                GL.BeginQuery(QueryTarget.TimeElapsed, _query);
                _running = true;
            }
        }

        public void End()
        {
            if (_running)
            {
                GL.EndQuery(QueryTarget.TimeElapsed);
                _running = false;
                _pending = true;
            }
        }

        public void Dispose()
        {
            if (_query != 0)
            {
                GL.DeleteQuery(_query);
                _query = 0;
            }

            _pending = _running = false;
        }
    }

    /// <summary>Whether this renderer draws camera feeds (render to texture): OpenGL only for now.</summary>
    internal bool SupportsCameraFeeds => _backend == RenderBackendKind.OpenGL;

    private GameplaySession? PlayGameplay => _playMode.IsPlaying ? _playMode.Gameplay : null;

    // ---- OpenGL ---------------------------------------------------------------------------------------------------

    // After the parts: particles (environment shader, so fog and haze apply), then debug lines (unlit, depth-tested).
    private void RenderGameplayGl(Matrix4 view, Matrix4 projection, EditorCamera camera)
    {
        if (PlayGameplay is not { } gameplay)
        {
            LastParticleCount = 0;
            LastDebugLineCount = 0;
            return;
        }

        // (Inside a camera feed the drawing is part of the feed's own timing; GL time queries cannot nest.)
        var timed = MeasurePartTiming && !_renderingFeeds;
        var started = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (timed)
        {
            _overlayTimer.Begin();
        }

        DrawGameplayGl(gameplay, view, projection, camera);
        if (timed)
        {
            _overlayTimer.End();
            LastGameplayDrawCpuMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private void DrawGameplayGl(GameplaySession gameplay, Matrix4 view, Matrix4 projection, EditorCamera camera)
    {
        // With the Universal renderer the particles were drawn inside its scene pass (lit, fogged, HDR).
        var vertices = _universalFrame ? 0 : BuildParticleVertices(gameplay.Particles, camera, ref _particleFloats);
        if (!_universalFrame)
        {
            LastParticleCount = vertices / 6;
        }

        if (vertices > 0)
        {
            _particleQuads ??= new DynamicColoredMesh();
            _particleQuads.Update(_particleFloats, vertices);
            _environmentShader!.Use();
            _environmentShader.SetMatrix4("uView", view);
            _environmentShader.SetMatrix4("uProjection", projection);
            _environmentShader.SetMatrix4("uModel", Matrix4.Identity);
            _particleQuads.Draw(PrimitiveType.Triangles);
        }

        if (gameplay.Debug.Version != _debugVersion)
        {
            _debugVersion = gameplay.Debug.Version;
            _debugVertexCount = BuildDebugVertices(gameplay.Debug, ref _debugFloats);
            if (_debugVertexCount > 0)
            {
                _debugLines ??= new DynamicColoredMesh();
                _debugLines.Update(_debugFloats, _debugVertexCount);
            }
        }

        LastDebugLineCount = _debugVertexCount / 2;
        if (_debugVertexCount > 0 && _debugLines is not null)
        {
            _shader!.Use();
            _shader.SetMatrix4("uView", view);
            _shader.SetMatrix4("uProjection", projection);
            _shader.SetMatrix4("uModel", Matrix4.Identity);
            GL.DepthFunc(DepthFunction.Lequal);
            _debugLines.Draw(PrimitiveType.Lines);
            GL.DepthFunc(DepthFunction.Less);
        }
    }

    // The Lua particles inside the Universal renderer's scene pass (its program is already bound).
    private void DrawUniversalParticles(EditorCamera camera)
    {
        if (PlayGameplay is not { } gameplay)
        {
            LastParticleCount = 0;
            return;
        }

        var vertices = BuildParticleVertices(gameplay.Particles, camera, ref _particleFloats);
        LastParticleCount = vertices / 6;
        if (vertices > 0)
        {
            _particleQuads ??= new DynamicColoredMesh();
            _particleQuads.Update(_particleFloats, vertices);
            _particleQuads.Draw(PrimitiveType.Triangles);
        }
    }

    // Before the frame's world: every camera with a feed is rendered into its own texture (world, particles, lines; no
    // editor overlays, no game GUI).
    private void RenderCameraFeedsGl()
    {
        LastCameraFeedCount = 0;
        if (PlayGameplay is not { } gameplay)
        {
            ReleaseCameraFeeds();
            return;
        }

        _cameraFeedTextures.Clear();
        var timed = MeasurePartTiming;
        var started = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (timed)
        {
            _feedsTimer.Begin();
        }

        _renderingFeeds = true;
        var wanted = 0;
        foreach (var rig in gameplay.Cameras.Cameras)
        {
            if (!rig.HasFeed || rig.Destroyed)
            {
                continue;
            }

            wanted++;
            if (!_cameraFeeds.TryGetValue(rig.Id, out var target) || target.Width != rig.FeedWidth || target.Height != rig.FeedHeight)
            {
                target?.Dispose();
                _cameraFeeds.Remove(rig.Id);
                target = CameraFeedTarget.Create(rig.FeedWidth, rig.FeedHeight);
                if (target is null)
                {
                    continue;
                }

                _cameraFeeds[rig.Id] = target;
            }

            _feedCamera.Position = rig.Pose.Position;
            _feedCamera.SetOrientation(rig.Pose.Yaw, rig.Pose.Pitch);
            _feedCamera.FieldOfViewDegrees = rig.Pose.FieldOfView;
            var aspect = target.Width / (float)target.Height;
            var view = _feedCamera.ViewMatrix;
            var projection = _feedCamera.GetProjectionMatrix(aspect);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
            GL.Viewport(0, 0, target.Width, target.Height);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            RenderWorldGl(view, projection, aspect, _feedCamera);
            RenderGameplayGl(view, projection, _feedCamera);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _cameraFeedTextures[rig.Name] = target.Color;
            LastCameraFeedCount++;
        }

        _renderingFeeds = false;
        if (timed)
        {
            _feedsTimer.End();
            LastFeedsCpuMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // Feeds of cameras that are gone (destroyed, or their feed dropped) are let go.
        if (_cameraFeeds.Count > wanted)
        {
            foreach (var id in _cameraFeeds.Keys.ToList())
            {
                if (gameplay.Cameras.Find(id) is not { HasFeed: true })
                {
                    _cameraFeeds[id].Dispose();
                    _cameraFeeds.Remove(id);
                }
            }
        }

        GL.Viewport(0, 0, _fbWidth, _fbHeight);
    }

    /// <summary>The texture a GUI Image named "camera:Name" shows (OpenGL, while that camera has a feed).</summary>
    internal bool TryGetCameraFeed(string name, out UiTexture texture)
    {
        texture = default;
        if (_cameraFeedTextures.TryGetValue(name, out var id))
        {
            texture = new UiTexture(id);
            return true;
        }

        return false;
    }

    /// <summary>Reads a camera feed's pixels back (tests): RGBA rows bottom-up, or null without that feed.</summary>
    internal byte[]? ReadCameraFeed(string name, out int width, out int height)
    {
        width = height = 0;
        foreach (var target in _cameraFeeds.Values)
        {
            if (_cameraFeedTextures.TryGetValue(name, out var id) && id == target.Color)
            {
                width = target.Width;
                height = target.Height;
                var pixels = new byte[width * height * 4];
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                return pixels;
            }
        }

        return null;
    }

    private void ReleaseCameraFeeds()
    {
        if (_cameraFeeds.Count == 0)
        {
            return;
        }

        foreach (var target in _cameraFeeds.Values)
        {
            target.Dispose();
        }

        _cameraFeeds.Clear();
        _cameraFeedTextures.Clear();
    }

    private void DisposeGameplayRendering()
    {
        if (_backend == RenderBackendKind.OpenGL)
        {
            ReleaseCameraFeeds();
            _debugLines?.Dispose();
            _particleQuads?.Dispose();
            _feedsTimer.Dispose();
            _overlayTimer.Dispose();
        }

        _debugLines = null;
        _particleQuads = null;
    }

    /// <summary>A framebuffer with a colour texture and a depth-stencil buffer, for one camera feed.</summary>
    private sealed class CameraFeedTarget : IDisposable
    {
        private CameraFeedTarget(int framebuffer, int color, int depth, int width, int height)
        {
            Framebuffer = framebuffer;
            Color = color;
            Depth = depth;
            Width = width;
            Height = height;
        }

        public int Framebuffer { get; private set; }

        public int Color { get; private set; }

        public int Depth { get; private set; }

        public int Width { get; }

        public int Height { get; }

        public static CameraFeedTarget? Create(int width, int height)
        {
            var color = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, color);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            var depth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, width, height);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
            var framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depth);
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            var target = new CameraFeedTarget(framebuffer, color, depth, width, height);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                Console.WriteLine($"[Renderer] Camera feed {width}x{height} could not be made ({status})");
                target.Dispose();
                return null;
            }

            return target;
        }

        public void Dispose()
        {
            if (Framebuffer != 0)
            {
                GL.DeleteFramebuffer(Framebuffer);
                Framebuffer = 0;
            }

            if (Depth != 0)
            {
                GL.DeleteRenderbuffer(Depth);
                Depth = 0;
            }

            if (Color != 0)
            {
                GL.DeleteTexture(Color);
                Color = 0;
            }
        }
    }

    // ---- bgfx -----------------------------------------------------------------------------------------------------

    private void SubmitGameplayBgfx(BgfxEditorGraphics graphics, ushort view)
    {
        // Not playing: nothing is drawn, and the counters say so (as on OpenGL).
        if (PlayGameplay is not { } gameplay)
        {
            LastParticleCount = 0;
            LastDebugLineCount = 0;
            return;
        }

        // Particles with the Atmosphere applied (haze, fog, image controls), as OpenGL draws them with its environment
        // shader. A frame whose transient vertex space cannot hold them all draws as many as it can (never none).
        var vertices = BuildParticleVertices(gameplay.Particles, _camera, ref _particleFloats);
        var drawnParticles = 0;
        if (vertices > 0)
        {
            _bgfxGameplayVertices.Clear();
            for (var i = 0; i < vertices; i++)
            {
                var o = i * DynamicColoredMesh.FloatsPerVertex;
                _bgfxGameplayVertices.Add(new Vertex(new Vector3(_particleFloats[o], _particleFloats[o + 1], _particleFloats[o + 2]),
                    new Vector3(_particleFloats[o + 3], _particleFloats[o + 4], _particleFloats[o + 5])));
            }

            drawnParticles = (int)graphics.SubmitTransientBounded(view, _bgfxGameplayVertices, BgfxEditorGraphics.OpaqueState, 6, atmosphere: true) / 6;
            if (drawnParticles < vertices / 6)
            {
                NoteTransientLimit("particles", drawnParticles, vertices / 6);
            }
        }

        LastParticleCount = drawnParticles;
        _debugVertexCount = BuildDebugVertices(gameplay.Debug, ref _debugFloats);
        var drawnLines = 0;
        if (_debugVertexCount > 0)
        {
            _bgfxGameplayVertices.Clear();
            for (var i = 0; i < _debugVertexCount; i++)
            {
                var o = i * DynamicColoredMesh.FloatsPerVertex;
                _bgfxGameplayVertices.Add(new Vertex(new Vector3(_debugFloats[o], _debugFloats[o + 1], _debugFloats[o + 2]),
                    new Vector3(_debugFloats[o + 3], _debugFloats[o + 4], _debugFloats[o + 5])));
            }

            drawnLines = (int)graphics.SubmitTransientBounded(view, _bgfxGameplayVertices,
                BgfxEditorGraphics.OutlineState | (ulong)global::Bgfx.bgfx.StateFlags.PtLines, 2, atmosphere: false) / 2;
            if (drawnLines < _debugVertexCount / 2)
            {
                NoteTransientLimit("debug lines", drawnLines, _debugVertexCount / 2);
            }
        }

        LastDebugLineCount = drawnLines;
    }

    // Once per session: a frame could draw only part of the scripts' particles or debug lines (bgfx's per-frame
    // transient vertex space was full). Logged once, so a long Play session does not flood the log.
    private bool _transientLimitLogged;

    private void NoteTransientLimit(string what, int drawn, int wanted)
    {
        if (_transientLimitLogged)
        {
            return;
        }

        _transientLimitLogged = true;
        Console.WriteLine(FormattableString.Invariant(
            $"[Renderer] bgfx: a frame drew {drawn:N0} of {wanted:N0} {what} (the frame's transient vertex space was full); logged once"));
    }

    // ---- Geometry (both backends) ---------------------------------------------------------------------------------

    // Two vertices per line; returns the vertex count.
    private int BuildDebugVertices(DebugDraw debug, ref float[] floats)
    {
        debug.CopySegments(_debugSegments);
        var count = _debugSegments.Count * 2;
        if (floats.Length < count * DynamicColoredMesh.FloatsPerVertex)
        {
            floats = new float[global::System.Math.Max(count, 256) * DynamicColoredMesh.FloatsPerVertex];
        }

        var o = 0;
        foreach (var segment in _debugSegments)
        {
            Put(floats, ref o, segment.A, segment.Color);
            Put(floats, ref o, segment.B, segment.Color);
        }

        return count;
    }

    // Six vertices (two triangles) per particle facing the camera: streaks along their motion when the emitter has a
    // Length, squares otherwise. Returns the vertex count.
    private static int BuildParticleVertices(ParticleSystem particles, EditorCamera camera, ref float[] floats)
    {
        var total = global::System.Math.Min(particles.TotalCount, MaxParticleVertices / 6);
        if (total == 0)
        {
            return 0;
        }

        var needed = total * 6 * DynamicColoredMesh.FloatsPerVertex;
        if (floats.Length < needed)
        {
            floats = new float[needed];
        }

        var eye = camera.Position;
        var right = camera.Right;
        var up = camera.Up;
        var o = 0;
        var written = 0;
        foreach (var emitter in particles.Emitters)
        {
            var positions = emitter.Positions;
            var velocities = emitter.Velocities;
            var half = emitter.Size * 0.5f;
            var color = emitter.Color;
            for (var i = 0; i < positions.Length && written < total; i++, written++)
            {
                var p = positions[i];
                Vector3 a, b, c, d;
                if (emitter.Length > 0f)
                {
                    var v = velocities[i];
                    var speed = v.Length;
                    var axis = speed > 1e-4f ? v / speed : new Vector3(0f, -1f, 0f);
                    var tail = p - axis * emitter.Length;
                    var side = Vector3.Cross(axis, p - eye);
                    var sideLength = side.Length;
                    side = sideLength > 1e-5f ? side * (half / sideLength) : right * half;
                    (a, b, c, d) = (p + side, p - side, tail - side, tail + side);
                }
                else
                {
                    var r = right * half;
                    var u = up * half;
                    (a, b, c, d) = (p - r - u, p + r - u, p + r + u, p - r + u);
                }

                Put(floats, ref o, a, color);
                Put(floats, ref o, b, color);
                Put(floats, ref o, c, color);
                Put(floats, ref o, a, color);
                Put(floats, ref o, c, color);
                Put(floats, ref o, d, color);
            }
        }

        return written * 6;
    }

    private static void Put(float[] floats, ref int o, Vector3 position, Vector3 color)
    {
        floats[o] = position.X;
        floats[o + 1] = position.Y;
        floats[o + 2] = position.Z;
        floats[o + 3] = color.X;
        floats[o + 4] = color.Y;
        floats[o + 5] = color.Z;
        floats[o + 6] = 0f;
        floats[o + 7] = 0f;
        floats[o + 8] = 0f;
        o += DynamicColoredMesh.FloatsPerVertex;
    }
}
