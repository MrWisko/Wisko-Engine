using Wisko.Engine.Input;
using Wisko.Engine.Platform;
using Wisko.Engine.Projects;

namespace Wisko.Engine.UI;

internal enum ProjectHubRequest
{
    None,
    NewWorld,
    OpenProject
}

/// <summary>
/// Project Hub behaviour shared by every rendering backend: lists the projects (Home and Projects) and History, keeps
/// History in step with the projects that exist, handles the custom-preview picker and reset and Delete Project itself,
/// and hands "new world" / "open project" back to the host, which decides what entering the editor means. The project
/// folder is read when the Hub is shown and after a change made here, never every frame.
/// <para>
/// Delete Project (from any card, menu or page) always comes through <see cref="DeleteProject"/>: the store validates
/// the folder, History keeps the world's preview, the store deletes the folder (if that fails the world stays Hub
/// Active), and only then does History mark it Deleted.
/// </para>
/// </summary>
internal sealed class ProjectHubController(ProjectStore projects, ProjectHistory history, ProjectHubRenderer hub, InputManager input)
{
    // A History problem (e.g. a damaged file kept aside) is mentioned the first time the Hub shows, not every time.
    private bool _historyProblemShown;

    /// <summary>True for a project the editor is using now: it is never deleted.</summary>
    public Func<ProjectInfo, bool>? IsProjectInUse { get; set; }

    /// <summary>Told after a project was deleted (the host lets go of anything it kept about it).</summary>
    public Action<ProjectInfo>? ProjectDeleted { get; set; }

    /// <summary>Shows the hub on Home with the current worlds, most recent first.</summary>
    public void Show()
    {
        hub.ShowStatus(_historyProblemShown ? null : history.Problem);
        _historyProblemShown = true;
        Refresh(showHome: true);
    }

    public void ShowStatus(string? status) => hub.ShowStatus(status);

    public bool TryConsumeEscape() => hub.TryConsumeEscape();

    /// <summary>A project was created or opened: History has it (once), with its latest times.</summary>
    public void NoteProjectActive(ProjectInfo project) => history.NoteActive(project);

    public ProjectHubRequest Update(int screenWidth, int screenHeight, out ProjectInfo? project, out WorldTemplate template)
    {
        switch (hub.Update(input, screenWidth, screenHeight, out project, out template))
        {
            case ProjectHubCommand.NewWorld:
                return ProjectHubRequest.NewWorld;
            case ProjectHubCommand.OpenProject when project is not null:
                return ProjectHubRequest.OpenProject;
            case ProjectHubCommand.ChooseCustomThumbnail when project is not null:
                ChooseCustomThumbnail(project);
                break;
            case ProjectHubCommand.ResetCustomThumbnail when project is not null:
                ResetCustomThumbnail(project);
                break;
            case ProjectHubCommand.DeleteProject when project is not null:
                DeleteProject(project);
                break;
        }

        project = null;
        return ProjectHubRequest.None;
    }

    /// <summary>The one deletion path (after the user confirmed). Never throws; problems go to the Hub's status line.</summary>
    internal void DeleteProject(ProjectInfo project)
    {
        if (IsProjectInUse?.Invoke(project) == true)
        {
            hub.ShowStatus("Return to the Hub before deleting this project.");
            return;
        }

        if (!projects.TryValidateDeletion(project, out _, out var problem))
        {
            // E.g. the folder was removed by hand meanwhile: nothing is deleted; the lists show what is really there.
            Console.WriteLine($"[Project] Delete refused: {problem}");
            hub.ShowStatus(problem);
            Refresh(showHome: false);
            return;
        }

        // History has the world and keeps its last preview before the files go.
        history.NoteActive(project);
        if (!history.TryPreserveThumbnail(project, out var thumbnail, out var thumbnailError))
        {
            Console.WriteLine($"[History] Could not keep the preview of \"{project.Name}\": {thumbnailError}");
        }

        if (!projects.TryDeleteProject(project, out var error))
        {
            Console.WriteLine($"[Project] Delete failed: {error}");
            hub.ShowStatus(error);
            Refresh(showHome: false);
            return;
        }

        if (!history.TryMarkDeleted(project, DateTime.UtcNow, thumbnail, out var historyError))
        {
            hub.ShowStatus($"\"{project.Name}\" was deleted, but History couldn't be saved: {historyError}");
        }
        else
        {
            hub.ShowStatus(null);
        }

        Console.WriteLine($"[Project] Deleted \"{project.Name}\" ({project.Id}); History keeps it as Deleted");
        ProjectDeleted?.Invoke(project);
        Refresh(showHome: false);
    }

    // Reads the projects folder, brings History in step and gives both to the Hub.
    private void Refresh(bool showHome)
    {
        var active = projects.ListProjects();
        history.Sync(active);
        var byId = active.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var items = history.Records
            .Select(record =>
            {
                // Only two statuses: its project is here (Hub Active) or not (Deleted). A record Wisko did not delete
                // whose folder is gone stays as it is on disk, so the same project coming back is Hub Active again.
                var project = record.Status == ProjectHistoryStatus.Active ? byId.GetValueOrDefault(record.Id) : null;
                var state = project is not null ? HubHistoryState.HubActive : HubHistoryState.Deleted;
                return new HubHistoryItem(record, project, state, history.ThumbnailPathOf(record),
                    DeletedOutsideWisko: record.Status == ProjectHistoryStatus.Active && project is null);
            })
            .ToList();
        hub.SetData(active, items, showHome);
    }

    private void ChooseCustomThumbnail(ProjectInfo project)
    {
        var result = ImageFilePicker.TryPick(out var path, out var pickError);
        input.ReleaseMouseButtons();

        if (result == FilePickResult.Cancelled)
        {
            return;
        }

        if (result == FilePickResult.Failed || path is null)
        {
            hub.ShowStatus($"Couldn't open the file picker: {pickError}");
            if (VisualCppRuntime.FileDialogsBlocked)
            {
                VisualCppRuntime.OfferDownload(); // Microsoft's Visual C++ runtime is missing (Windows)
            }
            return;
        }

        if (!projects.TrySetCustomThumbnail(project, path, out var error))
        {
            Console.WriteLine($"[Project] Custom image for \"{project.Name}\" not changed: {error}");
            hub.ShowStatus($"Couldn't use that image: {error}");
            return;
        }

        Console.WriteLine($"[Project] Custom image for \"{project.Name}\" set from {path}");
        hub.ShowStatus(null);
        Refresh(showHome: false);
    }

    private void ResetCustomThumbnail(ProjectInfo project)
    {
        if (!projects.TryResetCustomThumbnail(project, out var error))
        {
            hub.ShowStatus($"Couldn't reset the image: {error}");
            return;
        }

        Console.WriteLine($"[Project] Custom image for \"{project.Name}\" removed; showing Thumbnail.png");
        hub.ShowStatus(null);
        Refresh(showHome: false);
    }
}
