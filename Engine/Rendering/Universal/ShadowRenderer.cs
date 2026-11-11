using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// Draws items into depth-only targets (shadow maps, the depth prepass): one program for rigid meshes and one for
/// skinned ones, alpha cut-outs honoured, bone palettes uploaded per skinned draw.
/// </summary>
internal sealed class DepthDrawer
{
    private readonly GlProgram _static;
    private readonly GlProgram _skinned;
    private readonly GlProgram _staticDistance;
    private readonly GlProgram _skinnedDistance;
    private readonly BoneUniformBuffer _bones;
    private readonly TextureSamplers _samplers;

    public DepthDrawer(GlProgram rigid, GlProgram skinned, GlProgram rigidDistance, GlProgram skinnedDistance, BoneUniformBuffer bones,
        TextureSamplers samplers)
    {
        _static = rigid;
        _skinned = skinned;
        _staticDistance = rigidDistance;
        _skinnedDistance = skinnedDistance;
        _bones = bones;
        _samplers = samplers;
        foreach (var program in new[] { rigid, skinned, rigidDistance, skinnedDistance })
        {
            program.Sampler("uBaseColorTex", 0);
            program.BindBlock(BoneUniformBuffer.BlockName, BoneUniformBuffer.BindingPoint);
        }
    }

    /// <summary>Draw calls issued since the last <see cref="TakeDrawCalls"/>.</summary>
    public int DrawCalls { get; private set; }

    public int TakeDrawCalls()
    {
        var calls = DrawCalls;
        DrawCalls = 0;
        return calls;
    }

    /// <summary>
    /// Draws the items <paramref name="include"/> accepts with <paramref name="viewProjection"/>. Distance mode
    /// (point-light cube faces) writes distance to <paramref name="lightPosition"/> / <paramref name="range"/> as depth.
    /// <paramref name="cull"/> culls back faces exactly as the surface pass does (the depth prepass must match it, or a
    /// culled single-sided face would still hide what is behind it); shadow maps draw both sides.
    /// </summary>
    public void Draw(List<DrawItem> items, in Matrix4 viewProjection, Func<DrawItem, bool> include, bool distance = false,
        Vector3 lightPosition = default, float range = 0f, bool cull = false)
    {
        GlProgram? current = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!include(item))
            {
                continue;
            }

            var skinned = item.Mesh.Skinned && item.Palette is { Length: > 0 };
            var program = (skinned, distance) switch
            {
                (true, true) => _skinnedDistance,
                (true, false) => _skinned,
                (false, true) => _staticDistance,
                _ => _static
            };
            if (!ReferenceEquals(program, current))
            {
                program.Use();
                program.Set("uViewProjection", viewProjection);
                if (distance)
                {
                    program.Set("uLightPosition", lightPosition);
                    program.Set("uDistanceRange", range);
                }

                current = program;
            }

            var material = item.Material;
            if (cull && material.Cull)
            {
                GL.Enable(EnableCap.CullFace);
                GL.CullFace(TriangleFace.Back);
                GL.FrontFace(item.Mirrored ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);
            }
            else if (cull)
            {
                GL.Disable(EnableCap.CullFace);
            }

            var masked = material.AlphaMode == AlphaMode.Mask;
            program.Set("uModel", item.Model);
            program.Set("uAlphaCutoff", masked ? material.AlphaCutoff : 0f);
            if (masked)
            {
                program.Set("uBaseAlpha", material.Opacity);
                program.Set("uHasBaseTexture", material.BaseTexture is not null ? 1 : 0);
                program.Set("uUvScale", material.UvScaleX, material.UvScaleY);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, material.BaseTexture?.Id ?? 0);
                _samplers.Bind(0, material.WrapOf(0));
            }

            if (skinned)
            {
                _bones.Upload(item.Palette!);
            }

            item.Mesh.Draw();
            DrawCalls++;
        }

        TextureSamplers.Unbind(0, 1);
        if (cull)
        {
            GL.Disable(EnableCap.CullFace);
            GL.FrontFace(FrontFaceDirection.Ccw);
        }
    }
}

/// <summary>
/// Real-time shadows (docs/graphics.md, "Shadows"):
/// <list type="bullet">
/// <item>Sun: 1-4 cascades over the shadow distance, each fitted around its slice of the view in a sphere and snapped to
/// whole shadow-map texels, so shadows do not shimmer as the camera moves; faded out at the far end.</item>
/// <item>Spot lights: one tile each of a shared depth atlas (quality: 0-8 lights).</item>
/// <item>Point lights: a distance cube map each (quality: 0-2 lights; six views each, so they are the costliest).</item>
/// </list>
/// Spot and point maps are re-rendered only when their light, or anything within its range, changed (moved, resized,
/// animated, or its material's casting changed). Depth bias (in texels) plus a normal offset fight acne; slope-scaled
/// polygon offset is applied while rendering. Transparent (blend) materials cast no shadow; alpha-masked ones cut out.
/// </summary>
internal sealed class ShadowRenderer : IDisposable
{
    public const int MaxSpotShadows = 8;
    public const int MaxPointShadows = 2;
    private const int AtlasColumns = 4;
    private const int AtlasRows = 2;

    private int _sunArray;
    private int _sunFbo;
    private int _sunSize;
    private int _sunLayers;
    private int _spotAtlas;
    private int _spotFbo;
    private int _spotTile;
    private readonly int[] _pointCubes = new int[MaxPointShadows];
    private readonly int[] _pointSizes = new int[MaxPointShadows];
    private int _pointFbo;
    private readonly long[] _spotSignatures = new long[MaxSpotShadows];
    private readonly long[] _pointSignatures = new long[MaxPointShadows];
    private readonly Matrix4[] _sunMatrices = new Matrix4[4];
    private readonly Matrix4[] _spotMatrices = new Matrix4[MaxSpotShadows];
    private readonly float[] _spotRects = new float[MaxSpotShadows * 4];
    private readonly float[] _spotTexels = new float[MaxSpotShadows * 4];

    public bool SunActive { get; private set; }

    public int CascadeCount { get; private set; }

    public float[] CascadeFar { get; } = new float[4];

    public float[] CascadeTexel { get; } = new float[4];

    public float FadeStart { get; private set; }

    public ReadOnlySpan<Matrix4> SunMatrices => _sunMatrices.AsSpan(0, global::System.Math.Max(CascadeCount, 1));

    public int SunTexture => _sunArray;

    public int SpotAtlas => _spotAtlas;

    public ReadOnlySpan<Matrix4> SpotMatrices => _spotMatrices;

    public ReadOnlySpan<float> SpotRects => _spotRects;

    public ReadOnlySpan<float> SpotTexels => _spotTexels;

    public int PointCube(int slot) => _pointCubes[slot];

    /// <summary>Shadow maps rendered (not reused) last frame: sun cascades, spot tiles, point faces.</summary>
    public int MapsRendered { get; private set; }

    public long Bytes { get; private set; }

    public void BeginFrame() => MapsRendered = 0;

    // ---- sun ----

    public void RenderSun(DepthDrawer drawer, List<DrawItem> items, in ViewSetup view, Vector3 towardSun, int cascades, int resolution, float distance)
    {
        SunActive = false;
        if (cascades <= 0 || towardSun.Y < -0.02f)
        {
            return;
        }

        EnsureSunTargets(resolution, cascades);
        if (_sunArray == 0)
        {
            return;
        }

        var near = MathF.Max(view.Near, 0.5f);
        var far = MathF.Max(near + 1f, MathF.Min(distance, view.Far));
        CascadeCount = cascades;
        FadeStart = far * 0.85f;
        var lightDir = towardSun * -1f;
        var up = MathF.Abs(lightDir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var tanY = MathF.Tan(view.VerticalFovRadians * 0.5f);
        var tanX = tanY * view.Aspect;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sunFbo);
        GL.Viewport(0, 0, resolution, resolution);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        GL.ColorMask(false, false, false, false);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1.6f, 2f);

        var previous = near;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var c = 0; c < cascades; c++)
        {
            var t = (c + 1f) / cascades;
            var uniform = near + (far - near) * t;
            var logarithmic = near * MathF.Pow(far / near, t);
            var split = c == cascades - 1 ? far : uniform + (logarithmic - uniform) * 0.75f;

            // Bounding sphere of the slice [previous, split] (stable under camera rotation).
            var center = Vector3.Zero;
            var k = 0;
            foreach (var d in new[] { previous, split })
            {
                var mid = view.Position + view.Forward * d;
                var hx = view.Right * (tanX * d);
                var hy = view.Up * (tanY * d);
                corners[k++] = mid + hx + hy;
                corners[k++] = mid + hx - hy;
                corners[k++] = mid - hx + hy;
                corners[k++] = mid - hx - hy;
            }

            foreach (var corner in corners)
            {
                center += corner;
            }

            center /= 8f;
            var radius = 0f;
            foreach (var corner in corners)
            {
                radius = MathF.Max(radius, (corner - center).Length);
            }

            radius = MathF.Ceiling(radius * 16f) / 16f;
            var extension = MathF.Max(radius * 2f, 120f);
            var eye = center - lightDir * (radius + extension);
            var lightView = Matrix4.CreateLookAt(eye, center, up);
            var projection = Matrix4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 0.1f, radius * 2f + extension + 0.1f);

            // Snap the projection to whole texels (no shimmering while the camera moves).
            var shadow = Matrix4.Multiply(projection, lightView);
            var ox = shadow.M14 * resolution * 0.5f;
            var oy = shadow.M24 * resolution * 0.5f;
            var dx = (MathF.Round(ox) - ox) * 2f / resolution;
            var dy = (MathF.Round(oy) - oy) * 2f / resolution;
            projection = new Matrix4(
                projection.M11, projection.M12, projection.M13, projection.M14 + dx,
                projection.M21, projection.M22, projection.M23, projection.M24 + dy,
                projection.M31, projection.M32, projection.M33, projection.M34,
                projection.M41, projection.M42, projection.M43, projection.M44);
            var viewProjection = Matrix4.Multiply(projection, lightView);
            _sunMatrices[c] = Matrix4.Multiply(Bias, viewProjection);
            CascadeFar[c] = split;
            CascadeTexel[c] = radius * 2f / resolution;

            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, _sunArray, 0, c);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            var lv = lightView;
            var depthRange = radius * 2f + extension + 0.2f;
            drawer.Draw(items, viewProjection, item =>
            {
                if (!item.Material.CastShadows || item.Material.Transparent)
                {
                    return false;
                }

                var (min, max) = SceneCollector.TransformBounds(lv, item.WorldMin, item.WorldMax);
                return max.X >= -radius && min.X <= radius && max.Y >= -radius && min.Y <= radius && min.Z <= 0f && max.Z >= -depthRange;
            });
            MapsRendered++;
            previous = split;
        }

        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.ColorMask(true, true, true, true);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        SunActive = true;
    }

    // ---- spot and point lights ----

    /// <summary>
    /// Gives shadow slots to the lights that want them (strongest first, up to the quality's limits) and renders the
    /// maps that changed. Writes each light's ShadowSlot (-1 none, 0-7 spot tile, 100+ point cube).
    /// </summary>
    public void RenderLights(DepthDrawer drawer, List<LightRecord> lights, List<DrawItem> items, int maxSpot, int spotTile, int maxPoint, int pointSize)
    {
        maxSpot = global::System.Math.Clamp(maxSpot, 0, MaxSpotShadows);
        maxPoint = global::System.Math.Clamp(maxPoint, 0, MaxPointShadows);
        var spots = new List<int>();
        var points = new List<int>();
        for (var i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            light.ShadowSlot = -1;
            lights[i] = light;
            if (!light.WantsShadow)
            {
                continue;
            }

            (light.Type == LightType.Spot ? spots : points).Add(i);
        }

        spots = spots.OrderByDescending(i => lights[i].Score).ThenBy(i => i).Take(maxSpot).ToList();
        points = points.OrderByDescending(i => lights[i].Score).ThenBy(i => i).Take(maxPoint).ToList();
        if (spots.Count == 0 && points.Count == 0)
        {
            return;
        }

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        GL.ColorMask(false, false, false, false);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1.2f, 2f);

        if (spots.Count > 0)
        {
            EnsureSpotAtlas(spotTile);
            for (var s = 0; s < spots.Count && _spotAtlas != 0; s++)
            {
                var index = spots[s];
                var light = lights[index];
                light.ShadowSlot = s;
                lights[index] = light;
                RenderSpot(drawer, items, light, s);
            }
        }

        GL.PolygonOffset(1.0f, 1f);
        for (var p = 0; p < points.Count; p++)
        {
            var index = points[p];
            var light = lights[index];
            if (!EnsurePointCube(p, pointSize))
            {
                continue;
            }

            light.ShadowSlot = 100 + p;
            lights[index] = light;
            RenderPoint(drawer, items, light, p);
        }

        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.ColorMask(true, true, true, true);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void RenderSpot(DepthDrawer drawer, List<DrawItem> items, in LightRecord light, int slot)
    {
        var fov = MathF.Min(light.OuterAngleRadians + 0.07f, 2.97f);
        var near = MathF.Max(0.05f, light.Range * 0.005f);
        var projection = Matrix4.CreatePerspective(fov, 1f, near, light.Range);
        var up = MathF.Abs(light.Direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var viewProjection = Matrix4.Multiply(projection, Matrix4.CreateLookAt(light.Position, light.Position + light.Direction, up));
        _spotMatrices[slot] = viewProjection;
        var col = slot % AtlasColumns;
        var row = slot / AtlasColumns;
        _spotRects[slot * 4] = col / (float)AtlasColumns;
        _spotRects[slot * 4 + 1] = row / (float)AtlasRows;
        _spotRects[slot * 4 + 2] = 1f / AtlasColumns;
        _spotRects[slot * 4 + 3] = 1f / AtlasRows;
        _spotTexels[slot * 4] = 1f / (_spotTile * AtlasColumns);

        var signature = Signature(light, items);
        if (signature == _spotSignatures[slot])
        {
            return;
        }

        _spotSignatures[slot] = signature;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _spotFbo);
        GL.Viewport(col * _spotTile, row * _spotTile, _spotTile, _spotTile);
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(col * _spotTile, row * _spotTile, _spotTile, _spotTile);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Disable(EnableCap.ScissorTest);
        var position = light.Position;
        var range = light.Range;
        drawer.Draw(items, viewProjection, item => Casts(item) && Near(item, position, range));
        MapsRendered++;
    }

    private void RenderPoint(DepthDrawer drawer, List<DrawItem> items, in LightRecord light, int slot)
    {
        var signature = Signature(light, items);
        if (signature == _pointSignatures[slot])
        {
            return;
        }

        _pointSignatures[slot] = signature;
        var size = _pointSizes[slot];
        var projection = Matrix4.CreatePerspective(MathF.PI * 0.5f, 1f, 0.05f, light.Range);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pointFbo);
        GL.Viewport(0, 0, size, size);
        var position = light.Position;
        var range = light.Range;
        for (var face = 0; face < 6; face++)
        {
            var (forward, upVector) = CubeFaces[face];
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.TextureCubeMapPositiveX + face, _pointCubes[slot], 0);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            var viewProjection = Matrix4.Multiply(projection, Matrix4.CreateLookAt(position, position + forward, upVector));
            drawer.Draw(items, viewProjection, item => Casts(item) && Near(item, position, range), distance: true, position, range);
            MapsRendered++;
        }
    }

    /// <summary>Cube-map face directions and up vectors (OpenGL's face orientation).</summary>
    public static readonly (Vector3 Forward, Vector3 Up)[] CubeFaces =
    [
        (new Vector3(1, 0, 0), new Vector3(0, -1, 0)),
        (new Vector3(-1, 0, 0), new Vector3(0, -1, 0)),
        (new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
        (new Vector3(0, -1, 0), new Vector3(0, 0, -1)),
        (new Vector3(0, 0, 1), new Vector3(0, -1, 0)),
        (new Vector3(0, 0, -1), new Vector3(0, -1, 0))
    ];

    private static long _animatedSignature;

    private static bool Casts(in DrawItem item) => item.Material.CastShadows && !item.Material.Transparent;

    private static bool Near(in DrawItem item, Vector3 position, float range)
    {
        var closest = new Vector3(
            global::System.Math.Clamp(position.X, item.WorldMin.X, item.WorldMax.X),
            global::System.Math.Clamp(position.Y, item.WorldMin.Y, item.WorldMax.Y),
            global::System.Math.Clamp(position.Z, item.WorldMin.Z, item.WorldMax.Z));
        return (closest - position).Length < range;
    }

    // Changes whenever the light or any shadow caster in its range does; animated casters change it every frame.
    private static long Signature(in LightRecord light, List<DrawItem> items)
    {
        var hash = new HashCode();
        hash.Add(light.Position.X); hash.Add(light.Position.Y); hash.Add(light.Position.Z);
        hash.Add(light.Direction.X); hash.Add(light.Direction.Y); hash.Add(light.Direction.Z);
        hash.Add(light.Range); hash.Add(light.CosOuter);
        var count = 0;
        foreach (var item in items)
        {
            if (!Casts(item) || !Near(item, light.Position, light.Range))
            {
                continue;
            }

            if (item.Animated)
            {
                return ++_animatedSignature << 1; // even, never equal to a computed (odd) one or a previous frame's
            }

            var m = item.Model;
            hash.Add(m.M11); hash.Add(m.M12); hash.Add(m.M13); hash.Add(m.M14);
            hash.Add(m.M21); hash.Add(m.M22); hash.Add(m.M23); hash.Add(m.M24);
            hash.Add(m.M31); hash.Add(m.M32); hash.Add(m.M33); hash.Add(m.M34);
            hash.Add(item.Material.AlphaMode);
            hash.Add(item.Material.BaseTexture?.Id ?? 0);
            hash.Add(item.Mesh.Name);
            count++;
        }

        hash.Add(count);
        return ((long)hash.ToHashCode() << 1) | 1L;
    }

    /// <summary>Forgets cached spot and point maps (scene or settings changed wholesale).</summary>
    public void Invalidate()
    {
        Array.Clear(_spotSignatures);
        Array.Clear(_pointSignatures);
    }

    private static readonly Matrix4 Bias = new(
        0.5f, 0f, 0f, 0.5f,
        0f, 0.5f, 0f, 0.5f,
        0f, 0f, 0.5f, 0.5f,
        0f, 0f, 0f, 1f);

    private void EnsureSunTargets(int size, int layers)
    {
        if (_sunArray != 0 && _sunSize == size && _sunLayers == layers)
        {
            return;
        }

        DisposeSun();
        _sunSize = size;
        _sunLayers = layers;
        _sunArray = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2DArray, _sunArray);
        GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.DepthComponent24, size, size, layers, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
        SetShadowSampling(TextureTarget.Texture2DArray);
        GL.BindTexture(TextureTarget.Texture2DArray, 0);
        _sunFbo = GlResources.Framebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sunFbo);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, _sunArray, 0, 0);
        if (!GlResources.Complete("sun shadows"))
        {
            DisposeSun();
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        UpdateBytes();
    }

    private void EnsureSpotAtlas(int tile)
    {
        if (_spotAtlas != 0 && _spotTile == tile)
        {
            return;
        }

        DisposeSpot();
        _spotTile = tile;
        _spotAtlas = GlResources.DepthTexture2D(tile * AtlasColumns, tile * AtlasRows, stencil: false, compare: true);
        _spotFbo = GlResources.Framebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _spotFbo);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _spotAtlas, 0);
        if (!GlResources.Complete("spot shadow atlas"))
        {
            DisposeSpot();
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        Array.Clear(_spotSignatures);
        UpdateBytes();
    }

    private bool EnsurePointCube(int slot, int size)
    {
        if (_pointCubes[slot] != 0 && _pointSizes[slot] == size)
        {
            return true;
        }

        DeleteCube(slot);
        _pointSizes[slot] = size;
        var id = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, id);
        for (var face = 0; face < 6; face++)
        {
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, PixelInternalFormat.DepthComponent24, size, size, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
        }

        SetShadowSampling(TextureTarget.TextureCubeMap);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
        _pointCubes[slot] = id;
        if (_pointFbo == 0)
        {
            _pointFbo = GlResources.Framebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pointFbo);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pointFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.TextureCubeMapPositiveX, id, 0);
        var ok = GlResources.Complete("point shadow cube");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (!ok)
        {
            DeleteCube(slot);
            return false;
        }

        _pointSignatures[slot] = 0;
        UpdateBytes();
        return true;
    }

    private static void SetShadowSampling(TextureTarget target)
    {
        GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(target, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(target, TextureParameterName.TextureCompareFunc, (int)All.Lequal);
    }

    private void UpdateBytes() =>
        Bytes = (long)_sunSize * _sunSize * _sunLayers * 4 * (_sunArray != 0 ? 1 : 0)
            + (_spotAtlas != 0 ? (long)_spotTile * AtlasColumns * _spotTile * AtlasRows * 4 : 0)
            + Enumerable.Range(0, MaxPointShadows).Sum(i => _pointCubes[i] != 0 ? (long)_pointSizes[i] * _pointSizes[i] * 6 * 4 : 0);

    private void DisposeSun()
    {
        if (_sunArray != 0)
        {
            GL.DeleteTexture(_sunArray);
            _sunArray = 0;
        }

        GlResources.DeleteFramebuffer(ref _sunFbo);
        _sunSize = _sunLayers = 0;
    }

    private void DisposeSpot()
    {
        GlResources.DeleteTexture(ref _spotAtlas);
        GlResources.DeleteFramebuffer(ref _spotFbo);
        _spotTile = 0;
    }

    private void DeleteCube(int slot)
    {
        if (_pointCubes[slot] != 0)
        {
            GL.DeleteTexture(_pointCubes[slot]);
            _pointCubes[slot] = 0;
            _pointSizes[slot] = 0;
        }
    }

    public void Dispose()
    {
        DisposeSun();
        DisposeSpot();
        for (var i = 0; i < MaxPointShadows; i++)
        {
            DeleteCube(i);
        }

        GlResources.DeleteFramebuffer(ref _pointFbo);
        Bytes = 0;
    }
}
