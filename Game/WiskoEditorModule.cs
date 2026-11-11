using Wisko.Engine.Core;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;

namespace Wisko.Editor;

/// <summary>The editor's game module: the environment every world starts with, and the Testing World template's Parts.</summary>
internal sealed class WiskoEditorModule : IGameModule, ISceneProvider, IWorkspaceProvider
{
    public void BuildScene(SceneComposer composer) => DefaultEnvironment.Build(composer);

    public void BuildWorkspace(Workspace workspace) => TestingWorldTemplate.Populate(workspace);

    public void OnInitialize()
    {
    }

    public void OnUpdate(float deltaTime)
    {
    }

    public void OnShutdown()
    {
    }
}
