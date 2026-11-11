using Wisko.Engine.Assets.Importing;

namespace Wisko.Engine.Assets;

/// <summary>Result of a successful <see cref="ProjectModelImporter.TryImport"/>.</summary>
public sealed class ProjectModelImport
{
    public required ModelAsset Asset { get; init; }

    /// <summary>Name for the new scene instance ("Tree" for Tree.glb).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Project-relative folder now holding the copied files, e.g. "Assets/Models/Tree-2".</summary>
    public required string AssetFolder { get; init; }

    /// <summary>Copied files, relative to <see cref="AssetFolder"/> (forward slashes).</summary>
    public required IReadOnlyList<string> CopiedFiles { get; init; }

    /// <summary>
    /// Referenced files that do not exist (e.g. a texture); not fatal: the Universal renderer draws that material without
    /// the missing texture (and says so once in the log), the Classic renderer draws no textures.
    /// </summary>
    public required IReadOnlyList<string> MissingFiles { get; init; }
}

/// <summary>
/// Turns an external model file into a portable, project-owned asset. Assimp (via <see cref="AssimpModelImporter"/>)
/// only decodes; this class owns the file transaction:
/// <list type="number">
/// <item>decode the source while recording every file Assimp opens (.bin buffers, .mtl libraries, ...);</item>
/// <item>dependency set = the model + those files + existing external texture files (Assimp does not open those,
/// but they belong to the asset), all of which must live in or just above the model's folder;</item>
/// <item>copy only those files into a temporary folder inside the project, keeping their relative layout;</item>
/// <item>decode the copy with reads confined to that folder, so an absolute reference to anything outside it fails
/// instead of silently becoming a machine dependency;</item>
/// <item>rename the temporary folder to <c>Assets/Models/&lt;unique name&gt;</c> (same volume, so atomic).</item>
/// </list>
/// Every failure removes the temporary folder: a half-imported asset is never left behind, and an existing asset
/// folder is never overwritten.
/// </summary>
public sealed class ProjectModelImporter
{
    // Dependencies may sit in the model's folder tree or up to this many folders above it ("../textures/a.png").
    private const int MaxParentLevels = 2;

    // Under <project>/Assets; only this importer writes there, so leftovers of an interrupted import are removed.
    private const string TempFolderName = ".import-temp";

    private readonly AssimpModelImporter _decoder;

    public ProjectModelImporter(AssimpModelImporter decoder)
    {
        _decoder = decoder;
    }

    /// <summary>The model formats offered by Import (lower case, with leading dot).</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = [".glb", ".gltf", ".obj", ".fbx"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Imports <paramref name="sourcePath"/> into <paramref name="projectRoot"/>. On failure <paramref name="error"/> is a
    /// short user-facing sentence; details go to the console/application log.
    /// </summary>
    public bool TryImport(string sourcePath, string projectRoot, out ProjectModelImport result, out string error)
    {
        result = null!;
        string source;
        string project;
        try
        {
            source = Path.GetFullPath(sourcePath);
            project = Path.GetFullPath(projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log($"Unusable path '{sourcePath}': {ex.Message}");
            error = "That file path can't be used";
            return false;
        }

        var fileName = Path.GetFileName(source);
        if (!IsSupported(source))
        {
            error = $"Unsupported file type \"{Path.GetExtension(source)}\" (use .glb, .gltf, .obj or .fbx)";
            return false;
        }

        if (!File.Exists(source))
        {
            error = $"File not found: {fileName}";
            return false;
        }

        if (!Directory.Exists(project))
        {
            error = "The project folder is missing";
            return false;
        }

        Log($"Importing {source} into {project}");

        // 1. Decode the original, recording what Assimp reads.
        var sourceLog = new ModelFileAccessLog();
        if (!TryDecode(source, sourceLog, out var sourceModel, out error))
        {
            return false;
        }

        if (!HasGeometry(sourceModel))
        {
            error = $"{fileName} contains no 3D geometry";
            return false;
        }

        // 2. The dependency set and the folder it is copied relative to.
        if (!TryCollectFiles(source, sourceLog, sourceModel, out var files, out var sourceRoot, out var missing, out error))
        {
            return false;
        }

        var tempParent = Path.Combine(project, ProjectAssetPaths.AssetsFolder, TempFolderName);
        var temp = Path.Combine(tempParent, Guid.NewGuid().ToString("N"));
        try
        {
            RemoveStaleTemp(tempParent);
            Directory.CreateDirectory(temp);

            // 3. Copy exactly the dependency set.
            var copied = CopyFiles(files, sourceRoot, temp);
            var mainRelative = Path.GetRelativePath(sourceRoot, source);
            var copiedMain = Path.Combine(temp, mainRelative);

            // 4. Decode the copy with every read confined to the copy.
            var copyLog = new ModelFileAccessLog(temp);
            if (!TryDecode(copiedMain, copyLog, out var copiedModel, out error))
            {
                return false;
            }

            if (!TryCheckConfined(fileName, copyLog, copiedModel, temp, out error))
            {
                return false;
            }

            // 5. Publish under a fresh, unique folder name.
            var modelsFolder = Path.Combine(project, ProjectAssetPaths.AssetsFolder, "Models");
            Directory.CreateDirectory(modelsFolder);
            var baseName = ProjectAssetPaths.ToSafeName(Path.GetFileNameWithoutExtension(source));
            var finalFolder = MoveToUniqueFolder(temp, modelsFolder, baseName);
            var finalMain = Path.Combine(finalFolder, mainRelative);
            var asset = new ModelAsset(ProjectAssetPaths.ToAssetPath(project, finalMain), Relocate(copiedModel, temp, finalFolder, finalMain));

            result = new ProjectModelImport
            {
                Asset = asset,
                DisplayName = DisplayNameFor(source),
                AssetFolder = ProjectAssetPaths.ToAssetPath(project, finalFolder),
                CopiedFiles = copied.Select(path => path.Replace(Path.DirectorySeparatorChar, '/')).ToArray(),
                MissingFiles = missing
            };

            Log($"Imported {fileName} -> {asset.AssetPath} ({copied.Count} file(s), {asset.Model.Meshes.Count} mesh(es), {asset.TriangleCount} triangles)");
            foreach (var file in missing)
            {
                Log($"  warning: referenced file not found: {file}");
            }

            foreach (var warning in copiedModel.Warnings)
            {
                Log($"  note: {warning}");
            }

            foreach (var note in asset.Animation.Notes)
            {
                Log($"  animation note: {note}");
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            Log($"Copy into the project failed: {ex}");
            error = "Couldn't copy the model into the project";
            return false;
        }
        finally
        {
            TryDeleteDirectory(temp);
            TryDeleteEmptyDirectory(tempParent);
        }
    }

    private bool TryDecode(string path, ModelFileAccessLog log, out ImportedModel model, out string error)
    {
        model = null!;
        error = string.Empty;
        try
        {
            model = _decoder.Import(path, log);
            return true;
        }
        catch (Exception ex)
        {
            // ModelImportException carries Assimp's message; anything else is a native/IO failure. Neither belongs in
            // the toast verbatim.
            Log($"Decode failed for {path}: {ex.Message}");
            var fileName = Path.GetFileName(path);
            var refused = log.Refused.FirstOrDefault();
            var missing = log.Missing.FirstOrDefault(requested => !SamePath(requested, path));
            error = refused is not null
                ? $"{fileName} refers to {Path.GetFileName(refused)} by an absolute path"
                : missing is not null
                    ? $"Missing file: {Path.GetFileName(missing)}"
                    : IsAnimationProblem(ex.Message)
                        ? $"{fileName} has invalid animation data (keyframes or bones; see the log)"
                        : $"Couldn't read {fileName}";
            return false;
        }
    }

    // Assimp's validation names the animation structures it refuses (keys out of order or past the clip's end, bad bones).
    private static bool IsAnimationProblem(string message) =>
        message.Contains("aiNodeAnim", StringComparison.Ordinal) || message.Contains("aiAnimation", StringComparison.Ordinal)
        || message.Contains("aiBone", StringComparison.Ordinal);

    private static bool HasGeometry(ImportedModel model) =>
        !model.Bounds.IsEmpty && model.Meshes.Any(mesh => mesh.TriangleCount > 0);

    private static bool TryCollectFiles(
        string source,
        ModelFileAccessLog log,
        ImportedModel model,
        out List<string> files,
        out string sourceRoot,
        out List<string> missing,
        out string error)
    {
        files = [source];
        missing = [];
        sourceRoot = string.Empty;
        error = string.Empty;

        foreach (var opened in log.Opened)
        {
            AddDistinct(files, opened);
        }

        // Every texture slot (base colour, metallic-roughness, normal, occlusion, emissive) that lives in its own file.
        foreach (var texture in model.Materials.SelectMany(material => material.Textures).Where(t => t.EmbeddedIndex is null))
        {
            if (texture.ResolvedFileExists && texture.ResolvedPath is not null)
            {
                AddDistinct(files, Path.GetFullPath(texture.ResolvedPath));
            }
            else if (!missing.Contains(texture.SourcePath))
            {
                missing.Add(texture.SourcePath);
            }
        }

        foreach (var requested in log.Missing)
        {
            var name = Path.GetFileName(requested);
            if (!missing.Contains(name))
            {
                missing.Add(name);
            }
        }

        // Everything must sit below the model's folder or at most MaxParentLevels above it; an unrelated location
        // (another drive, a system folder, an absolute path elsewhere) would drag a machine layout into the project.
        var modelFolder = Path.GetDirectoryName(source)!;
        var allowedRoot = modelFolder;
        for (var i = 0; i < MaxParentLevels && Path.GetDirectoryName(allowedRoot) is { } parent; i++)
        {
            allowedRoot = parent;
        }

        var outside = files.FirstOrDefault(file => !ProjectAssetPaths.IsUnder(file, allowedRoot));
        if (outside is not null)
        {
            Log($"Dependency outside the model's folder: {outside}");
            error = $"{Path.GetFileName(outside)} is outside the model's folder";
            return false;
        }

        sourceRoot = files.Select(file => Path.GetDirectoryName(file)!).Aggregate(CommonFolder);
        return true;
    }

    // Copies each file to the same relative location under the temporary folder (never outside it). Returns the
    // relative paths written. Names differing only by case collapse to one copy (case-insensitive volumes).
    private static List<string> CopyFiles(List<string> files, string sourceRoot, string temp)
    {
        var copied = new List<string>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.GetFullPath(Path.Combine(temp, relative));
            if (Path.IsPathRooted(relative) || !ProjectAssetPaths.IsUnder(destination, temp))
            {
                throw new InvalidOperationException($"Refusing to copy {file} outside the import folder ({relative}).");
            }

            if (!destinations.Add(destination))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            copied.Add(relative);
        }

        return copied;
    }

    // The copy may only have read files inside its own folder, and any external texture it names must be one of them.
    private static bool TryCheckConfined(string fileName, ModelFileAccessLog log, ImportedModel model, string temp, out string error)
    {
        error = string.Empty;
        var refused = log.Refused.FirstOrDefault();
        if (refused is not null)
        {
            Log($"Copied model still reads outside its folder: {refused}");
            error = $"{fileName} refers to {Path.GetFileName(refused)} by an absolute path";
            return false;
        }

        foreach (var texture in model.Materials.SelectMany(material => material.Textures))
        {
            if (texture is { EmbeddedIndex: null, ResolvedFileExists: true, ResolvedPath: { } path }
                && !ProjectAssetPaths.IsUnder(Path.GetFullPath(path), temp))
            {
                Log($"Copied model references a texture outside its folder: {path}");
                error = $"{fileName} refers to {Path.GetFileName(path)} by an absolute path";
                return false;
            }
        }

        return true;
    }

    private static string MoveToUniqueFolder(string temp, string modelsFolder, string baseName)
    {
        for (var n = 1; n < 10_000; n++)
        {
            var target = Path.Combine(modelsFolder, n == 1 ? baseName : $"{baseName}-{n}");
            if (Directory.Exists(target) || File.Exists(target))
            {
                continue;
            }

            Directory.Move(temp, target);
            return target;
        }

        throw new IOException($"No free asset folder name for {baseName}.");
    }

    // The validated copy, re-pointed at its final location (its paths were the temporary folder's).
    private static ImportedModel Relocate(ImportedModel model, string temp, string finalFolder, string finalMain) =>
        new()
        {
            SourcePath = finalMain,
            SourceFormat = model.SourceFormat,
            Meshes = model.Meshes,
            Materials = model.Materials.Select(material => material.WithTextures(texture => texture is { ResolvedPath: { } path }
                    && ProjectAssetPaths.IsUnder(Path.GetFullPath(path), temp)
                ? new ImportedTextureReference
                {
                    SourcePath = texture.SourcePath,
                    EmbeddedIndex = texture.EmbeddedIndex,
                    ResolvedPath = Path.Combine(finalFolder, Path.GetRelativePath(temp, Path.GetFullPath(path))),
                    ResolvedFileExists = texture.ResolvedFileExists,
                    UvIndex = texture.UvIndex,
                    WrapU = texture.WrapU,
                    WrapV = texture.WrapV
                }
                : texture)).ToArray(),
            EmbeddedTextures = model.EmbeddedTextures,
            RootNode = model.RootNode,
            Bounds = model.Bounds,
            Warnings = model.Warnings,
            Animations = model.Animations
        };

    private static string DisplayNameFor(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source).Normalize(System.Text.NormalizationForm.FormC).Trim();
        return name.Length == 0 ? "Model" : name;
    }

    private static string CommonFolder(string a, string b)
    {
        var separator = Path.DirectorySeparatorChar;
        var left = a.Split(separator);
        var right = b.Split(separator);
        var count = 0;
        while (count < left.Length && count < right.Length && string.Equals(left[count], right[count], StringComparison.Ordinal))
        {
            count++;
        }

        var common = string.Join(separator, left.Take(count));
        return common.Length == 0 ? separator.ToString() : common;
    }

    private static void AddDistinct(List<string> list, string path)
    {
        if (!list.Any(existing => SamePath(existing, path)))
        {
            list.Add(path);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    private static void RemoveStaleTemp(string tempParent)
    {
        if (Directory.Exists(tempParent))
        {
            Directory.Delete(tempParent, recursive: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log($"Could not remove temporary import folder {path}: {ex.Message}");
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log($"Could not remove {path}: {ex.Message}");
        }
    }

    private static void Log(string message) => Console.WriteLine($"[ModelImport] {message}");
}
