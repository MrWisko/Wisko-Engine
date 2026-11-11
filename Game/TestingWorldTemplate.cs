using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Editor;

/// <summary>The Testing World template's Parts: three coloured cubes.</summary>
internal static class TestingWorldTemplate
{
    private static readonly Vector3 Red = new(0.9f, 0.15f, 0.15f);
    private static readonly Vector3 Blue = new(0.15f, 0.15f, 0.9f);
    private static readonly Vector3 Green = new(0.15f, 0.9f, 0.15f);

    public static void Populate(Workspace workspace)
    {
        workspace.AddPart(new Part("BaseCube", new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 4f), Red));
        workspace.AddPart(new Part("BlueCube", new Vector3(10f, 2f, 0f), new Vector3(4f, 4f, 4f), Blue));
        workspace.AddPart(new Part("GreenCube", new Vector3(-10f, 2f, 0f), new Vector3(4f, 4f, 4f), Green));
    }
}
