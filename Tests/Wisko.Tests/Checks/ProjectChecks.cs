using System.Text;
using Wisko.Engine.Assets;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.ScriptEditor;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>Project files on disk: create, save/load, thumbnails, name rules, safe deletion, script assets and files.</summary>
internal static class ProjectChecks
{
    public static void Persistence(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Projects");
        var store = new ProjectStore(root);
        var part = MakePart("Wedgie", new Vector3(1.5f, 2.25f, -3f), new Vector3(4, 2, 6), false, "Assets/Scripts/Spin.lua");
        part.ShapeType = ShapeType.Wedge;
        part.Rotation = new Vector3(0, 45, 10);
        part.Friction = 0.3f;
        part.MassOverride = 12f;
        part.Locked = true;
        part.IsTrigger = false;
        part.CanCollide = false;
        var workspace = new Workspace();
        workspace.AddPart(part);
        workspace.AddPart(new Part("Plain", new Vector3(0, 1, 0), Part.DefaultSize, Part.DefaultColor));

        var created = store.TryCreateProject("Harness Persist", WorldTemplate.Testing, workspace, out var project, out var error);
        report.Check("Project: created through ProjectStore", created, error);
        if (!created)
        {
            return;
        }

        report.Check("Project: folder and metadata on disk",
            Directory.Exists(project.FolderPath) && File.Exists(Path.Combine(project.FolderPath, ProjectStore.MetadataFileName)));
        report.Check("Project: listed with the same Id and template",
            store.ListProjects().Any(p => p.Id == project.Id && p.WorldTemplate == WorldTemplate.Testing));

        var loaded = SceneFile.TryLoad(project.ScenePath, out var parts, out error);
        var back = loaded ? parts.FirstOrDefault(p => p.Name == "Wedgie") : null;
        report.Check("Scene.json: round-trips every Part", loaded && parts.Count == 2, error ?? $"{parts?.Count} parts");
        report.Check("Scene.json: Part properties preserved",
            back is not null && back.ShapeType == ShapeType.Wedge && back.Position.X == 1.5f && back.Position.Y == 2.25f && back.Position.Z == -3f
            && back.Rotation.Y == 45f && back.Rotation.Z == 10f && !back.Anchored && back.Locked && !back.CanCollide
            && MathF.Abs(back.Friction - 0.3f) < 1e-6f && back.MassOverride == 12f
            && back.Scripts.Count == 1 && back.Scripts[0].AssetPath == "Assets/Scripts/Spin.lua");

        var gui = new GuiDocument();
        var frame = gui.Add(new GuiFrame { Name = "Panel" });
        gui.Add(new GuiText { Name = "Title", Text = "Héllo € — 日本" }, frame);
        gui.Add(new GuiButton { Name = "Go", Text = "Go!" }, frame);
        report.Check("UI.json: saved", GuiFile.TrySave(project.UiPath, gui, out error), error);
        var guiBack = GuiFile.Load(project.UiPath, out var guiReport);
        report.Check("UI.json: loaded with hierarchy and Unicode text intact",
            guiReport.Status != GuiLoadStatus.Invalid && guiBack.Count == 3
            && guiBack.Elements.OfType<GuiText>().Single().Text == "Héllo € — 日本"
            && guiBack.Elements.OfType<GuiButton>().Single().Parent is GuiFrame, $"status {guiReport.Status}, {guiBack.Count} elements");

        report.Check("Atmosphere.json: saved", AtmosphereFile.TrySave(project.AtmospherePath, AtmosphereSettings.Default, out error), error);
        AtmosphereFile.Load(project.AtmospherePath, out var atmosphereStatus, out var problem);
        report.Check("Atmosphere.json: loaded back", atmosphereStatus != AtmosphereLoadStatus.Invalid, $"{atmosphereStatus} {problem}");

        var rgba = new byte[ProjectStore.ThumbnailWidth * ProjectStore.ThumbnailHeight * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 2] = 80;
            rgba[i + 3] = 255;
        }

        report.Check("Thumbnail: written", store.TryWriteThumbnail(project, rgba, ProjectStore.ThumbnailWidth, ProjectStore.ThumbnailHeight, out error), error);
        var png = File.Exists(project.ThumbnailPath) ? File.ReadAllBytes(project.ThumbnailPath) : [];
        report.Check("Thumbnail: valid PNG signature", png.Length > 8 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G');

        report.Check("Name validation: rejects path characters",
            !store.TryValidateNewName("a/b", out _, out _) && !store.TryValidateNewName("a\\b", out _, out _) && !store.TryValidateNewName("a:b", out _, out _));
        var conAccepted = store.TryValidateNewName("CON", out var conName, out var conError);
        report.Check("Name validation: rejects Windows reserved device name CON", !conAccepted, conAccepted ? $"accepted as '{conName}'" : conError);

        var outside = new ProjectInfo { Id = project.Id, Name = "Outside", FolderPath = dir };
        report.Check("Delete safety: a folder outside the Projects root is refused", !store.TryValidateDeletion(outside, out _, out _));
        var rootItself = new ProjectInfo { Id = project.Id, Name = "Root", FolderPath = root };
        report.Check("Delete safety: the Projects root itself is refused", !store.TryValidateDeletion(rootItself, out _, out _));
        var climbing = new ProjectInfo { Id = project.Id, Name = "Climb", FolderPath = Path.Combine(root, "..", "Projects", "Harness Persist") };
        report.Check("Delete safety: a path with '..' is refused", !store.TryValidateDeletion(climbing, out _, out _));
        var otherCase = new ProjectInfo { Id = project.Id, Name = "Case", FolderPath = project.FolderPath.ToUpperInvariant() };
        var caseAccepted = store.TryValidateDeletion(otherCase, out _, out var caseError);
        TestReport.Info($"Delete validation of the same folder spelled in upper case: {(caseAccepted ? "accepted" : $"refused ({caseError})")}");
        var wrongId = new ProjectInfo { Id = Guid.NewGuid().ToString("N"), Name = "Wrong", FolderPath = project.FolderPath };
        report.Check("Delete safety: a folder whose project.json has another Id is refused", !store.TryValidateDeletion(wrongId, out _, out _));

        report.Check("Delete: validated", store.TryValidateDeletion(project, out var folder, out error), error);
        report.Check("Delete: project deleted", store.TryDeleteProject(project, out error), error);
        report.Check("Delete: folder gone and not listed", !Directory.Exists(folder) && store.ListProjects().All(p => p.Id != project.Id));
        store.PurgeDeletedProjects();
        var staging = Path.Combine(root, ProjectStore.DeletingFolderName);
        report.Check("Delete: staging folder left empty", !Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    public static void ScriptAssets(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        Directory.CreateDirectory(root);
        var created = ProjectScriptAssets.TryCreateScript(root, "Spin", out var spin, out var error);
        var spinPath = Path.Combine(root, "Assets", "Scripts", "Spin.lua");
        report.Check("New script: created as Assets/Scripts/Spin.lua", created && spin?.AssetPath == "Assets/Scripts/Spin.lua" && File.Exists(spinPath), error ?? spin?.AssetPath);
        report.Check("New script: template content", File.Exists(spinPath) && File.ReadAllText(spinPath) == ProjectScriptAssets.NewScriptTemplate);
        var again = ProjectScriptAssets.TryCreateScript(root, "Spin", out var spin2, out error);
        report.Check("New script: same name never overwrites (unique name)", again && spin2 is not null && spin2.AssetPath != spin!.AssetPath, spin2?.AssetPath ?? error);
        var bad = ProjectScriptAssets.TryValidateScriptName("CON", out _, out var badError);
        report.Check("New script: Windows reserved name rejected", !bad, badError);

        var inPlace = ProjectScriptAssets.TryAddExistingScript(root, spinPath, out var reference, out var copied, out error);
        report.Check("Attach existing: a script already in Assets/Scripts is referenced in place",
            inPlace && !copied && reference?.AssetPath == "Assets/Scripts/Spin.lua", error ?? $"copied={copied} {reference?.AssetPath}");
        var upperOk = ProjectScriptAssets.TryAddExistingScript(root, spinPath.ToUpperInvariant(), out var upperReference, out var upperCopied, out var upperError);
        TestReport.Info($"Attach existing with the same path in other letter case: ok={upperOk} copied={upperCopied} ref={upperReference?.AssetPath} {upperError}");

        var external = Path.Combine(dir, "External Scripts", "Extérieur.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        File.WriteAllText(external, "function Start() end\n");
        var copiedOk = ProjectScriptAssets.TryAddExistingScript(root, external, out var extReference, out var extCopied, out error);
        report.Check("Attach external: copied into Assets/Scripts",
            copiedOk && extCopied && extReference is not null
            && File.Exists(Path.Combine(root, extReference.AssetPath.Replace('/', Path.DirectorySeparatorChar))), error ?? extReference?.AssetPath);

        var crlfPath = Path.Combine(root, "Assets", "Scripts", "Crlf.lua");
        File.WriteAllBytes(crlfPath, Encoding.UTF8.GetBytes("a = 1\r\nb = 2\r\n"));
        var loaded = ScriptFileStore.TryLoad(crlfPath, out var script, out error);
        report.Check("Script file: CRLF detected and normalised to \\n in the editor", loaded && script!.Newline == "\r\n" && script.Text == "a = 1\nb = 2\n", error);
        var saved = ScriptFileStore.TrySave(crlfPath, "a = 1\nb = 2\nc = 3\n", "\r\n", false, out _, out error);
        report.Check("Script file: saved back with CRLF preserved (atomic replace)", saved && File.ReadAllText(crlfPath) == "a = 1\r\nb = 2\r\nc = 3\r\n", error);
        report.Check("Script file: no temporary files left behind", !Directory.EnumerateFiles(Path.GetDirectoryName(crlfPath)!, "*.tmp").Any());
    }
}
