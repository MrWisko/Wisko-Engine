using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Silk.NET.Assimp;
using Silk.NET.Core.Contexts;

// Imports every model under a folder with Wisko's import flags and prints, per model, what Assimp produced (meshes,
// vertices, faces, bones, materials, animations, nodes and a checksum of the vertex positions), then one digest over all
// lines. Two Assimp builds that give the same digest import those models identically.
//
//   Wisko.AssimpImportReport <folder> [--assimp <path to the Assimp library>] [--out <report file>]
//
// Without --assimp the library from the Silk.NET.Assimp package (next to this program) is used.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Wisko.AssimpImportReport <folder> [--assimp <library path>] [--out <report file>]");
    return 2;
}

string? Value(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// The same steps as Wisko's importer (Engine/Assets/Importing/AssimpModelImporter.cs).
const PostProcessSteps ImportSteps = PostProcessSteps.Triangulate | PostProcessSteps.SortByPrimitiveType
    | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.GenerateSmoothNormals | PostProcessSteps.FlipUVs
    | PostProcessSteps.ValidateDataStructure;
string[] extensions = [".glb", ".gltf", ".fbx", ".obj", ".dae"];

var library = Value("--assimp");
using var assimp = library is null ? Assimp.GetApi() : new Assimp(new DefaultNativeContext(Path.GetFullPath(library)));
var root = Path.GetFullPath(args[0]);
var lines = new List<string>
{
    $"Assimp {assimp.GetVersionMajor()}.{assimp.GetVersionMinor()}.{assimp.GetVersionPatch()} revision {assimp.GetVersionRevision():x8}"
};
var separator = Path.DirectorySeparatorChar;
var models = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
    .Where(file => extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
    .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal) && !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
    .Select(file => (Full: file, Relative: Path.GetRelativePath(root, file).Replace(separator, '/')))
    .OrderBy(model => model.Relative, StringComparer.Ordinal)
    .ToList();

var results = new List<string>();
foreach (var (full, relative) in models)
{
    results.Add(Describe(assimp, full, relative));
}

lines.AddRange(results);
var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", results))))[..16];
lines.Add($"models {results.Count}; digest {digest}");
foreach (var line in lines)
{
    Console.WriteLine(line);
}

if (Value("--out") is { } output)
{
    System.IO.File.WriteAllLines(output, lines);
}

return results.Any(line => line.Contains(": FAILED", StringComparison.Ordinal)) ? 1 : 0;

static unsafe string Describe(Assimp assimp, string path, string relative)
{
    var scene = assimp.ImportFile(path, (uint)ImportSteps);
    if (scene is null)
    {
        return $"{relative}: FAILED {assimp.GetErrorStringS()}";
    }

    try
    {
        var text = new StringBuilder(FormattableString.Invariant(
            $"{relative}: meshes {scene->MNumMeshes}, materials {scene->MNumMaterials}, animations {scene->MNumAnimations}, nodes {CountNodes(scene->MRootNode)}"));
        for (var m = 0; m < scene->MNumMeshes; m++)
        {
            var mesh = scene->MMeshes[m];
            double sum = 0;
            for (var v = 0; v < mesh->MNumVertices; v++)
            {
                sum += mesh->MVertices[v].X + mesh->MVertices[v].Y * 2 + mesh->MVertices[v].Z * 3;
            }

            text.Append(CultureInfo.InvariantCulture,
                $" | mesh {m}: v {mesh->MNumVertices} f {mesh->MNumFaces} bones {mesh->MNumBones} mat {mesh->MMaterialIndex} normals {(mesh->MNormals != null ? 1 : 0)} uv {(mesh->MTextureCoords[0] != null ? 1 : 0)} sum {sum:F4}");
        }

        for (var a = 0; a < scene->MNumAnimations; a++)
        {
            var animation = scene->MAnimations[a];
            text.Append(CultureInfo.InvariantCulture,
                $" | anim {a}: channels {animation->MNumChannels} duration {animation->MDuration:F3} tps {animation->MTicksPerSecond:F1}");
        }

        return text.ToString();
    }
    finally
    {
        assimp.ReleaseImport(scene);
    }
}

static unsafe int CountNodes(Node* node)
{
    var count = 1;
    for (var i = 0; i < node->MNumChildren; i++)
    {
        count += CountNodes(node->MChildren[i]);
    }

    return count;
}
