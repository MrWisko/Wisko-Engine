using Wisko.Engine.World;

namespace Wisko.Engine.Core;

/// <summary>
/// Supplies the parts of the "Testing World" template. Not used at startup: new worlds and opened
/// projects get their parts from the template choice or the project's Scene.json.
/// </summary>
public interface IWorkspaceProvider
{
    void BuildWorkspace(Workspace workspace);
}
