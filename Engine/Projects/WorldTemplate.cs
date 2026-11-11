namespace Wisko.Engine.Projects;

/// <summary>Starting content a world was created from. Stored in project.json; missing means Classic.</summary>
public enum WorldTemplate
{
    /// <summary>Editor environment only (ground/sky), zero user parts.</summary>
    Classic,

    /// <summary>Editor environment plus the host's test parts (see IWorkspaceProvider).</summary>
    Testing
}

public static class WorldTemplateExtensions
{
    public static string DisplayName(this WorldTemplate template) => template switch
    {
        WorldTemplate.Testing => "Testing World",
        _ => "Classic"
    };
}
