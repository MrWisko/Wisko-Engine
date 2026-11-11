namespace Wisko.Engine.Projects;

/// <summary>
/// A named, on-disk project. The folder name is the project name; metadata lives in project.json.
/// </summary>
public sealed class ProjectInfo
{
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// The project's stable identity (project.json "Id"): never its name or folder, so two projects with the same name
    /// stay two (e.g. in History). Letters, digits and '-' only (see <see cref="ProjectStore.IsValidId"/>).
    /// </summary>
    public string Id { get; init; } = string.Empty;

    public required string Name { get; init; }

    public required string FolderPath { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime LastOpenedUtc { get; set; }

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public WorldTemplate WorldTemplate { get; init; } = WorldTemplate.Classic;

    public string ScenePath => Path.Combine(FolderPath, ProjectStore.SceneFileName);

    /// <summary>The project's Atmosphere.json (absent in projects from before Atmosphere: defaults are used).</summary>
    public string AtmospherePath => Path.Combine(FolderPath, Engine.Atmosphere.AtmosphereFile.FileName);

    /// <summary>The project's Graphics.json (absent in older projects: the Classic renderer is used).</summary>
    public string GraphicsPath => Path.Combine(FolderPath, Engine.Graphics.GraphicsFile.FileName);

    /// <summary>The project's UI.json, its game GUI (absent in projects without one: an empty Canvas is used).</summary>
    public string UiPath => Path.Combine(FolderPath, Engine.Gui.GuiFile.FileName);

    public string ThumbnailPath => Path.Combine(FolderPath, ProjectStore.ThumbnailFileName);

    // User-chosen card image shown instead of Thumbnail.png while the file exists (nothing in project.json).
    public string CustomThumbnailPath => Path.Combine(FolderPath, ProjectStore.CustomThumbnailFileName);
}
