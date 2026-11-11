using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Assets.Importing;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// GL sampler objects (core in OpenGL 3.3) for material textures: trilinear and anisotropic like <see cref="GlTexture"/>,
/// one per combination of U and V wrap modes (glTF sampler wrapS / wrapT: repeat, clamp to edge, mirrored repeat), so a
/// texture shared by materials with different modes is still uploaded once. Bound on the material units while surfaces
/// are drawn and unbound afterwards, so every other pass (post-processing, the Classic renderer, the UI) keeps its
/// textures' own parameters.
/// </summary>
internal sealed class TextureSamplers : IDisposable
{
    private readonly int[] _samplers = new int[9];

    public static int LiveCount { get; private set; }

    public void Initialize()
    {
        var anisotropy = MathF.Min(GlTexture.MaxAnisotropy(), 8f);
        for (var i = 0; i < _samplers.Length; i++)
        {
            var id = GL.GenSampler();
            GL.SamplerParameter(id, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.SamplerParameter(id, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.SamplerParameter(id, SamplerParameterName.TextureWrapS, (int)Mode((TextureWrap)(i % 3)));
            GL.SamplerParameter(id, SamplerParameterName.TextureWrapT, (int)Mode((TextureWrap)(i / 3)));
            if (anisotropy > 1f)
            {
                // GL_TEXTURE_MAX_ANISOTROPY (EXT_texture_filter_anisotropic; core in 4.6).
                GL.SamplerParameter(id, (SamplerParameterName)0x84FE, anisotropy);
            }

            _samplers[i] = id;
            LiveCount++;
        }
    }

    /// <summary>Binds the sampler for a slot's packed wrap modes (0-8, see <see cref="GpuMaterial.WrapOf"/>) on a unit.</summary>
    public void Bind(int unit, int wrap) => GL.BindSampler(unit, _samplers[wrap is >= 0 and < 9 ? wrap : 0]);

    /// <summary>Unbinds the samplers of units first .. first + count - 1 (their textures' own parameters apply again).</summary>
    public static void Unbind(int first, int count)
    {
        for (var unit = first; unit < first + count; unit++)
        {
            GL.BindSampler(unit, 0);
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < _samplers.Length; i++)
        {
            if (_samplers[i] != 0)
            {
                GL.DeleteSampler(_samplers[i]);
                _samplers[i] = 0;
                LiveCount--;
            }
        }
    }

    private static TextureWrapMode Mode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.Clamp => TextureWrapMode.ClampToEdge,
        TextureWrap.Mirror => TextureWrapMode.MirroredRepeat,
        _ => TextureWrapMode.Repeat
    };
}
