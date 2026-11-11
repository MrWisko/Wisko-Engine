using Wisko.Engine.Rendering;

namespace Wisko.Engine.Core;

public interface ISceneProvider
{
    void BuildScene(SceneComposer composer);
}
