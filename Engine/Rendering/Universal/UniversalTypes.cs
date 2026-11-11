using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>A 3x3 matrix (normal matrices), column-major for upload.</summary>
internal readonly struct Mat3
{
    private readonly float _m00, _m10, _m20, _m01, _m11, _m21, _m02, _m12, _m22;

    private Mat3(float m00, float m10, float m20, float m01, float m11, float m21, float m02, float m12, float m22)
    {
        _m00 = m00; _m10 = m10; _m20 = m20;
        _m01 = m01; _m11 = m11; _m21 = m21;
        _m02 = m02; _m12 = m12; _m22 = m22;
    }

    /// <summary>The inverse transpose of the model matrix's 3x3 part (identity when singular).</summary>
    public static Mat3 NormalMatrix(in Matrix4 m)
    {
        float a = m.M11, b = m.M12, c = m.M13;
        float d = m.M21, e = m.M22, f = m.M23;
        float g = m.M31, h = m.M32, i = m.M33;
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (MathF.Abs(det) < 1e-20f)
        {
            return new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);
        }

        var s = 1f / det;
        // inverse (row-major) then transposed: element (r, c) of the result is inverse(c, r).
        float i00 = (e * i - f * h) * s, i01 = (c * h - b * i) * s, i02 = (b * f - c * e) * s;
        float i10 = (f * g - d * i) * s, i11 = (a * i - c * g) * s, i12 = (c * d - a * f) * s;
        float i20 = (d * h - e * g) * s, i21 = (b * g - a * h) * s, i22 = (a * e - b * d) * s;
        // Result R = transpose(inverse): R(r,c) = I(c,r). Column-major storage: column 0 = R(0..2, 0) = I(0, 0..2).
        return new Mat3(i00, i01, i02, i10, i11, i12, i20, i21, i22);
    }

    public void CopyTo(Span<float> columnMajor)
    {
        columnMajor[0] = _m00; columnMajor[1] = _m10; columnMajor[2] = _m20;
        columnMajor[3] = _m01; columnMajor[4] = _m11; columnMajor[5] = _m21;
        columnMajor[6] = _m02; columnMajor[7] = _m12; columnMajor[8] = _m22;
    }
}

/// <summary>A material resolved for the GPU: linear colours, textures (null: none), switches.</summary>
internal struct GpuMaterial
{
    public float BaseR, BaseG, BaseB, Opacity;
    public Vector3 Emissive;
    public float Metallic, Roughness, NormalStrength, OcclusionStrength, AlphaCutoff;
    public ShadingMode Mode;
    public AlphaMode AlphaMode;
    public int ToonBands;
    public bool FlatShading, ReceiveShadows, CastShadows, DoubleSided, VertexColorsSrgb, Cull;
    public bool Outline;
    public Vector3 OutlineColor;
    public float OutlineThickness;
    public UvMode UvMode;
    public float UvScaleX, UvScaleY;
    public GlTexture? BaseTexture, MetalRoughTexture, NormalTexture, OcclusionTexture, EmissiveTexture;

    /// <summary>
    /// Wrap modes per texture slot, 4 bits each in slot order (base, metal-rough, normal, occlusion, emissive); 0 repeats.
    /// </summary>
    public int Wrap;

    /// <summary>One slot's wrap modes in its 4 bits (U + 3 x V; 0 = repeat both), at the slot's place.</summary>
    public static int PackWrap(int slot, TextureWrap u, TextureWrap v) =>
        ((int)u + 3 * (int)v) << (slot * 4);

    /// <summary>The sampler index (0-8, U + 3 x V) of a slot.</summary>
    public readonly int WrapOf(int slot) => (Wrap >> (slot * 4)) & 15;

    public readonly bool Transparent => AlphaMode == AlphaMode.Blend;

    public readonly int Flags =>
        (BaseTexture is not null ? 1 : 0) | (MetalRoughTexture is not null ? 2 : 0) | (NormalTexture is not null ? 4 : 0)
        | (OcclusionTexture is not null ? 8 : 0) | (EmissiveTexture is not null ? 16 : 0) | (FlatShading ? 32 : 0)
        | (ReceiveShadows ? 64 : 0) | (DoubleSided ? 128 : 0) | (VertexColorsSrgb ? 256 : 0);
}

/// <summary>One mesh to draw this frame, with everything every pass needs.</summary>
internal struct DrawItem
{
    public UniversalMesh Mesh;
    public Matrix4 Model;
    public Mat3 Normal;
    public bool Mirrored;
    public Part? Part;
    public float[]? Palette;
    public Vector3 WorldMin, WorldMax;
    public Vector3 ObjectSize;
    public GpuMaterial Material;
    public int LightStart, LightCount;
    public bool Selected;
    public bool Animated;
    public float SortKey;
}

/// <summary>A light gathered for this frame (sun excluded), in GPU terms.</summary>
internal struct LightRecord
{
    public LightType Type;
    public Vector3 Position;
    public Vector3 Direction;   // the way the light travels
    public Vector3 Radiance;    // linear colour x intensity
    public float Range;
    public float CosOuter, CosInner;
    public float OuterAngleRadians;
    public bool WantsShadow;
    public int ShadowSlot;      // -1 none, 0-7 spot atlas tile, 100+ point cube
    public Part Part;
    public float Score;
}

/// <summary>A camera to render: matrices, basis and the pixel viewport of the target.</summary>
internal readonly record struct ViewSetup(
    Matrix4 View,
    Matrix4 Projection,
    Vector3 Position,
    Vector3 Forward,
    Vector3 Right,
    Vector3 Up,
    float VerticalFovRadians,
    float Aspect,
    float Near,
    float Far,
    int PixelWidth,
    int PixelHeight)
{
    public Matrix4 ViewProjection => Matrix4.Multiply(Projection, View);

    public static ViewSetup FromCamera(EditorCamera camera, float aspect, int pixelWidth, int pixelHeight) => new(
        camera.ViewMatrix,
        camera.GetProjectionMatrix(aspect),
        camera.Position,
        camera.Forward,
        camera.Right,
        camera.Up,
        camera.FieldOfViewDegrees * MathF.PI / 180f,
        aspect,
        camera.NearPlane,
        camera.FarPlane,
        pixelWidth,
        pixelHeight);
}
