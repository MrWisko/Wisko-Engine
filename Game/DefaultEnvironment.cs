using Wisko.Engine.Math;
using Wisko.Engine.Rendering;

namespace Wisko.Editor;

/// <summary>The environment every world starts with: the 512 x 512 baseplate (it can be deleted per world).</summary>
internal static class DefaultEnvironment
{
    private static readonly Vector3 SurfaceGrey = new(0.62f, 0.62f, 0.62f);
    private static readonly Vector3 SideGrey = new(0.48f, 0.48f, 0.48f);

    private const float BaseplateSize = 512f;
    private const float BaseplateHeight = 1f;

    public static void Build(SceneComposer composer)
    {
        var baseplateCenter = new Vector3(0f, -BaseplateHeight * 0.5f, 0f);

        composer.AddStudioBaseplate(
            "Baseplate",
            baseplateCenter,
            BaseplateSize,
            BaseplateHeight,
            BaseplateSize,
            SurfaceGrey,
            SideGrey);
    }
}
