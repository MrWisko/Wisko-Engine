using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Assimp;
using AssimpMesh = Silk.NET.Assimp.Mesh;
using AssimpNode = Silk.NET.Assimp.Node;
using AssimpScene = Silk.NET.Assimp.Scene;
using Matrix4 = Wisko.Engine.Math.Matrix4;
using Vector3 = Wisko.Engine.Math.Vector3;

namespace Wisko.Engine.Assets.Importing;

/// <summary>
/// Decodes a model file with Assimp and converts it into an <see cref="ImportedModel"/>. Assimp is only the decoding
/// layer: every value is copied out of the native scene, which is released before <see cref="Import"/> returns.
/// </summary>
public sealed unsafe class AssimpModelImporter : IDisposable
{
    // Assimp's output convention already matches the engine (right-handed, Y up, counter-clockwise front faces),
    // so no handedness / winding conversion flags are used. Chosen steps:
    //   Triangulate            - the engine renders triangle lists only.
    //   SortByPrimitiveType    - splits leftover point/line primitives into their own meshes, which are skipped.
    //   JoinIdenticalVertices  - indexed meshes (OBJ and friends arrive unindexed).
    //   GenerateSmoothNormals  - only fills in normals for meshes that have none; existing normals are kept.
    //   FlipUVs                - UV origin at the top-left of the image (glTF/Metal/D3D convention), so images
    //                            uploaded top row first sample identically on OpenGL and bgfx later.
    //   ValidateDataStructure  - rejects malformed scenes instead of converting garbage.
    private const PostProcessSteps ImportSteps =
        PostProcessSteps.Triangulate
        | PostProcessSteps.SortByPrimitiveType
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.GenerateSmoothNormals
        | PostProcessSteps.FlipUVs
        | PostProcessSteps.ValidateDataStructure;

    private const uint TrianglePrimitive = (uint)PrimitiveType.Triangle;

    // Set by Triangulate on meshes that came from polygons; it describes the face encoding, not a primitive type.
    private const uint NgonEncodingFlag = (uint)PrimitiveType.NgonEncodingFlag;

    // AI_MATKEY_BASE_COLOR (glTF baseColorFactor); Silk.NET 2.23 has no constant for it.
    private const string MaterialBaseColorKey = "$clr.base";

    // The packaged native library is built single-threaded (aiGetCompileFlags: ASSIMP_CFLAGS_SINGLETHREADED), so
    // imports are serialized process-wide.
    private static readonly object NativeImportLock = new();

    // Silk.NET.Assimp 2.23 asks for libassimp.5.dylib on macOS. In Ultz.Native.Assimp 6.0.2 that file is a leftover
    // Assimp 5.4.1 build; libassimp.6.dylib in the same package is Assimp 6.0.2, the version Windows loads
    // (Assimp64.dll) and the binding matches. Same lookup as GetApi (app folder, runtimes/<rid>/native), other name.
    internal const string MacOSLibraryName = "libassimp.6.dylib";

    private readonly Assimp _assimp;

    public AssimpModelImporter()
    {
        _assimp = OperatingSystem.IsMacOS()
            ? new Assimp(Assimp.CreateDefaultContext([MacOSLibraryName]))
            : Assimp.GetApi();
    }

    /// <summary>Native Assimp library version, e.g. "6.0.2".</summary>
    public string NativeVersion => $"{_assimp.GetVersionMajor()}.{_assimp.GetVersionMinor()}.{_assimp.GetVersionPatch()}";

    /// <summary>Native Assimp library revision (the git commit it was built from), e.g. "fb375dd8".</summary>
    public string NativeRevision => _assimp.GetVersionRevision().ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>File extensions (lower case, with leading dot) the native library can import.</summary>
    public IReadOnlyList<string> SupportedExtensions
    {
        get
        {
            AssimpString list;
            _assimp.GetExtensionList(&list);
            return list.AsString
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(extension => extension.TrimStart('*').ToLowerInvariant())
                .ToArray();
        }
    }

    public bool CanImport(string path) =>
        _assimp.IsExtensionSupported(Path.GetExtension(path)) != 0;

    public ImportedModel Import(string path) => Import(path, null);

    /// <summary>
    /// Import with an optional per-call <see cref="ModelFileAccessLog"/>: records which files Assimp opened (and which
    /// it could not find) and, when the log has an allowed root, refuses files outside it.
    /// </summary>
    internal ImportedModel Import(string path, ModelFileAccessLog? accessLog)
    {
        var fullPath = Path.GetFullPath(path);
        if (!System.IO.File.Exists(fullPath))
        {
            throw new ModelImportException($"Model file not found: {fullPath}");
        }

        // File access goes through .NET (AssimpFileSystem); the path crosses the boundary as UTF-8 and comes back
        // to the same callbacks, so no platform code page or C runtime path handling is involved.
        var utf8Path = Encoding.UTF8.GetBytes(fullPath + '\0');
        lock (NativeImportLock)
        {
            return ImportLocked(fullPath, utf8Path, accessLog);
        }
    }

    private ImportedModel ImportLocked(string fullPath, byte[] utf8Path, ModelFileAccessLog? accessLog)
    {
        var fileSystem = AssimpFileSystem.Create(accessLog);
        AssimpScene* scene;
        try
        {
            fixed (byte* pathPointer = utf8Path)
            {
                scene = _assimp.ImportFileEx(pathPointer, (uint)ImportSteps, fileSystem);
            }
        }
        finally
        {
            AssimpFileSystem.Free(fileSystem); // only used while importing; the scene does not reference it
        }

        if (scene is null)
        {
            throw new ModelImportException($"Assimp could not import {fullPath}: {_assimp.GetErrorStringS()}");
        }

        try
        {
            if ((scene->MFlags & (uint)SceneFlags.Incomplete) != 0 || scene->MRootNode is null)
            {
                throw new ModelImportException($"Assimp returned an incomplete scene for {fullPath}");
            }

            return Convert(scene, fullPath);
        }
        finally
        {
            _assimp.ReleaseImport(scene);
        }
    }

    public void Dispose() => _assimp.Dispose();

    private ImportedModel Convert(AssimpScene* scene, string fullPath)
    {
        var warnings = new List<string>();
        var embedded = ConvertEmbeddedTextures(scene);

        // Meshes that are not pure triangles (points/lines after SortByPrimitiveType) are dropped; the remaining
        // ones are renumbered so node references stay valid.
        var meshes = new List<ImportedMesh>();
        var meshRemap = new int[scene->MNumMeshes];
        for (var i = 0; i < scene->MNumMeshes; i++)
        {
            var mesh = scene->MMeshes[i];
            if ((mesh->MPrimitiveTypes & ~NgonEncodingFlag) != TrianglePrimitive)
            {
                meshRemap[i] = -1;
                warnings.Add($"Skipped mesh '{mesh->MName.AsString}' with non-triangle primitives ({(PrimitiveType)mesh->MPrimitiveTypes}).");
                continue;
            }

            meshRemap[i] = meshes.Count;
            meshes.Add(ConvertMesh(mesh));
            if (mesh->MNumAnimMeshes > 0)
            {
                warnings.Add($"Mesh '{mesh->MName.AsString}' has {mesh->MNumAnimMeshes} morph target(s) (blend shapes); Wisko shows its base shape only.");
            }
        }

        var materials = new List<ImportedMaterial>();
        var modelDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        for (var i = 0; i < scene->MNumMaterials; i++)
        {
            materials.Add(ConvertMaterial(scene, scene->MMaterials[i], i, modelDirectory));
        }

        // Diagnostics: what the file uses that Wisko cannot show is reported, never silently dropped.
        foreach (var material in materials.Where(m => m.UsesSecondUvSet))
        {
            warnings.Add($"Material '{material.Name}' reads a texture with a second UV set; Wisko imports the first only, so that texture may look misplaced.");
        }

        var decodable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "png", "jpg", "jpeg", "bmp", "tga", "gif", "psd", "hdr", "pic", "pnm", "ppm", "pgm" };
        foreach (var image in embedded.Where(t => t.IsEncoded && !decodable.Contains(t.FormatHint)))
        {
            warnings.Add($"Embedded image '{image.Name}' is in the '{image.FormatHint}' format, which Wisko cannot decode; it is not shown.");
        }

        foreach (var extension in GltfExtensions.ReadUsed(fullPath).Where(e => !GltfExtensions.Supported.Contains(e)))
        {
            warnings.Add($"glTF extension {extension}: {GltfExtensions.Effect(extension)}.");
        }

        var root = ConvertNode(scene->MRootNode, meshRemap);
        var bounds = ImportedBounds.Empty;
        AccumulateBounds(root, Matrix4.Identity, meshes, ref bounds);

        return new ImportedModel
        {
            SourcePath = fullPath,
            SourceFormat = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant(),
            Meshes = meshes,
            Materials = materials,
            EmbeddedTextures = embedded,
            RootNode = root,
            Bounds = bounds,
            Warnings = warnings,
            Animations = ConvertAnimations(scene, warnings)
        };
    }

    // Assimp's documented fallback when a file gives no tick rate.
    private const double DefaultTicksPerSecond = 25d;

    private static List<ImportedAnimation> ConvertAnimations(AssimpScene* scene, List<string> warnings)
    {
        var animations = new List<ImportedAnimation>();
        for (var a = 0; a < scene->MNumAnimations; a++)
        {
            var animation = scene->MAnimations[a];
            var name = animation->MName.AsString;
            var shown = name.Length == 0 ? $"#{a + 1}" : $"'{name}'";
            var ticksPerSecond = animation->MTicksPerSecond;
            if (!(ticksPerSecond > 0d) || !double.IsFinite(ticksPerSecond))
            {
                ticksPerSecond = DefaultTicksPerSecond;
            }

            var problems = new KeyProblems();
            var tracks = new List<ImportedNodeTrack>();
            for (var c = 0; c < animation->MNumChannels; c++)
            {
                var channel = animation->MChannels[c];
                tracks.Add(new ImportedNodeTrack
                {
                    NodeName = channel->MNodeName.AsString,
                    Translation = ConvertVectorKeys(channel->MPositionKeys, channel->MNumPositionKeys, ticksPerSecond, ref problems),
                    Rotation = ConvertRotationKeys(channel->MRotationKeys, channel->MNumRotationKeys, ticksPerSecond, ref problems),
                    Scale = ConvertVectorKeys(channel->MScalingKeys, channel->MNumScalingKeys, ticksPerSecond, ref problems)
                });
            }

            // The clip lasts as long as the file says, or until its last key when that is later.
            var duration = animation->MDuration / ticksPerSecond;
            if (!double.IsFinite(duration) || duration < 0d)
            {
                warnings.Add($"Animation {shown}: its duration ({animation->MDuration}) is not usable; its keyframes decide its length.");
                duration = 0d;
            }

            var lastKey = tracks.Count == 0 ? 0f : tracks.Max(track => MathF.Max(LastTime(track.Translation), MathF.Max(LastTime(track.Rotation), LastTime(track.Scale))));
            duration = global::System.Math.Max(duration, lastKey);

            if (problems.Dropped > 0)
            {
                warnings.Add($"Animation {shown}: {problems.Dropped} keyframe(s) with a time or value that is not a finite number were ignored.");
            }

            if (problems.Sorted > 0)
            {
                warnings.Add($"Animation {shown}: {problems.Sorted} channel(s) had keyframes out of time order; they were sorted by time.");
            }

            if (problems.Cubic)
            {
                warnings.Add($"Animation {shown}: cubic-spline keys are played as straight (linear) interpolation between their keyframes.");
            }

            var unsupported = (int)(animation->MNumMeshChannels + animation->MNumMorphMeshChannels);
            if (unsupported > 0)
            {
                warnings.Add($"Animation {shown}: {unsupported} morph-target or vertex-animation channel(s) are not supported and are ignored.");
            }

            animations.Add(new ImportedAnimation { Name = name, DurationSeconds = duration, Tracks = tracks, UnsupportedChannelCount = unsupported });
        }

        return animations;
    }

    private static float LastTime<T>(ImportedChannel<T> channel)
        where T : struct => channel.Count == 0 ? 0f : channel.Times[^1];

    private struct KeyProblems
    {
        public int Dropped;
        public int Sorted;
        public bool Cubic;
    }

    private static ImportedChannel<Vector3> ConvertVectorKeys(VectorKey* keys, uint count, double ticksPerSecond, ref KeyProblems problems)
    {
        var times = new List<float>((int)count);
        var values = new List<Vector3>((int)count);
        var step = count > 0;
        for (var k = 0; k < count; k++)
        {
            var key = keys[k];
            var time = (float)(key.MTime / ticksPerSecond);
            if (!float.IsFinite(time) || !float.IsFinite(key.MValue.X) || !float.IsFinite(key.MValue.Y) || !float.IsFinite(key.MValue.Z))
            {
                problems.Dropped++;
                continue;
            }

            step &= key.MInterpolation == AnimInterpolation.AnimInterpolationStep;
            problems.Cubic |= key.MInterpolation == AnimInterpolation.AnimInterpolationCubicSpline;
            times.Add(time);
            values.Add(new Vector3(key.MValue.X, key.MValue.Y, key.MValue.Z));
        }

        return Channel(times, values, step && times.Count > 0, ref problems);
    }

    private static ImportedChannel<System.Numerics.Quaternion> ConvertRotationKeys(QuatKey* keys, uint count, double ticksPerSecond, ref KeyProblems problems)
    {
        var times = new List<float>((int)count);
        var values = new List<System.Numerics.Quaternion>((int)count);
        var step = count > 0;
        for (var k = 0; k < count; k++)
        {
            var key = keys[k];
            var time = (float)(key.MTime / ticksPerSecond);
            var q = new System.Numerics.Quaternion(key.MValue.X, key.MValue.Y, key.MValue.Z, key.MValue.W);
            var length = q.Length();
            if (!float.IsFinite(time) || !float.IsFinite(length) || length < 1e-6f)
            {
                problems.Dropped++;
                continue;
            }

            step &= key.MInterpolation == AnimInterpolation.AnimInterpolationStep;
            problems.Cubic |= key.MInterpolation == AnimInterpolation.AnimInterpolationCubicSpline;
            times.Add(time);
            values.Add(System.Numerics.Quaternion.Normalize(q));
        }

        return Channel(times, values, step && times.Count > 0, ref problems);
    }

    // Keys in ascending time order (a stable sort keeps keys that share a time in file order).
    private static ImportedChannel<T> Channel<T>(List<float> times, List<T> values, bool step, ref KeyProblems problems)
        where T : struct
    {
        var ordered = true;
        for (var i = 1; i < times.Count && ordered; i++)
        {
            ordered = times[i] >= times[i - 1];
        }

        if (!ordered)
        {
            problems.Sorted++;
            var order = Enumerable.Range(0, times.Count).OrderBy(i => times[i]).ThenBy(i => i).ToArray();
            return new ImportedChannel<T>(order.Select(i => times[i]).ToArray(), order.Select(i => values[i]).ToArray(), step);
        }

        return new ImportedChannel<T>(times.ToArray(), values.ToArray(), step);
    }

    // Bone weights per vertex: every valid (finite, positive) weight the bones give it, then the MaxInfluences strongest
    // (ties: the lower palette slot), renormalized to sum to 1. A vertex with none follows the mesh's own node.
    private static ImportedSkin? ConvertSkin(AssimpMesh* mesh)
    {
        var boneCount = (int)mesh->MNumBones;
        if (boneCount == 0)
        {
            return null;
        }

        var vertexCount = (int)mesh->MNumVertices;
        var names = new string[boneCount];
        var inverseBind = new Matrix4[boneCount];
        var counts = new int[vertexCount];
        var invalid = 0;
        for (var b = 0; b < boneCount; b++)
        {
            var bone = mesh->MBones[b];
            names[b] = bone->MName.AsString;
            inverseBind[b] = ToMatrix4(bone->MOffsetMatrix);
            for (var w = 0; w < bone->MNumWeights; w++)
            {
                var weight = bone->MWeights[w];
                if (weight.MVertexId >= vertexCount || !float.IsFinite(weight.MWeight) || weight.MWeight < 0f)
                {
                    invalid++;
                }
                else if (weight.MWeight > 0f)
                {
                    counts[weight.MVertexId]++;
                }
            }
        }

        var starts = new int[vertexCount + 1];
        for (var v = 0; v < vertexCount; v++)
        {
            starts[v + 1] = starts[v] + counts[v];
        }

        var slots = new ushort[starts[vertexCount]];
        var weights = new float[starts[vertexCount]];
        var fill = (int[])starts.Clone();
        for (var b = 0; b < boneCount; b++)
        {
            var bone = mesh->MBones[b];
            for (var w = 0; w < bone->MNumWeights; w++)
            {
                var weight = bone->MWeights[w];
                if (weight.MVertexId < vertexCount && float.IsFinite(weight.MWeight) && weight.MWeight > 0f)
                {
                    var at = fill[weight.MVertexId]++;
                    slots[at] = (ushort)b;
                    weights[at] = weight.MWeight;
                }
            }
        }

        const int max = ImportedSkin.MaxInfluences;
        var joints = new ushort[vertexCount * max];
        var kept = new float[vertexCount * max];
        int truncated = 0, renormalized = 0, unweighted = 0;
        var order = new List<int>();
        for (var v = 0; v < vertexCount; v++)
        {
            var start = starts[v];
            var count = starts[v + 1] - start;
            if (count == 0)
            {
                unweighted++;
                joints[v * max] = (ushort)boneCount; // ImportedSkin.UnweightedSlot
                kept[v * max] = 1f;
                continue;
            }

            order.Clear();
            for (var i = 0; i < count; i++)
            {
                order.Add(start + i);
            }

            order.Sort((x, y) => weights[y].CompareTo(weights[x]) is var byWeight and not 0 ? byWeight : slots[x].CompareTo(slots[y]));
            var total = 0f;
            for (var i = 0; i < count; i++)
            {
                total += weights[order[i]];
            }

            var use = global::System.Math.Min(count, max);
            var sum = 0f;
            for (var i = 0; i < use; i++)
            {
                sum += weights[order[i]];
            }

            if (count > max)
            {
                truncated++;
            }
            else if (MathF.Abs(total - 1f) > 1e-3f)
            {
                renormalized++;
            }

            for (var i = 0; i < use; i++)
            {
                joints[v * max + i] = slots[order[i]];
                kept[v * max + i] = weights[order[i]] / sum;
            }
        }

        return new ImportedSkin
        {
            BoneNames = names,
            InverseBindMatrices = inverseBind,
            Joints = joints,
            Weights = kept,
            TruncatedVertices = truncated,
            RenormalizedVertices = renormalized,
            UnweightedVertices = unweighted,
            InvalidWeights = invalid
        };
    }

    // aiMatrix4x4 is row-major with the translation in a4/b4/c4; Silk exposes that memory as M11..M44 in the same order,
    // which is exactly the engine Matrix4 layout (translation in M14/M24/M34). Copied as-is, no transpose.
    private static Matrix4 ToMatrix4(System.Numerics.Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);

    private static ImportedMesh ConvertMesh(AssimpMesh* mesh)
    {
        var vertexCount = (int)mesh->MNumVertices;
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var bounds = ImportedBounds.Empty;
        for (var v = 0; v < vertexCount; v++)
        {
            var p = mesh->MVertices[v];
            positions[v] = new Vector3(p.X, p.Y, p.Z);
            bounds = bounds.Include(positions[v]);
            if (mesh->MNormals is not null)
            {
                var n = mesh->MNormals[v];
                normals[v] = new Vector3(n.X, n.Y, n.Z).Normalized();
            }
        }

        TexCoord[]? uv0 = null;
        var uvSource = mesh->MTextureCoords[0];
        if (uvSource is not null)
        {
            uv0 = new TexCoord[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                uv0[v] = new TexCoord(uvSource[v].X, uvSource[v].Y);
            }
        }

        // Tangents only when the file has them (glTF TANGENT, FBX tangents); handedness from the file's bitangent, so
        // bitangent = w * cross(normal, tangent) points "up" in the image as glTF defines. Otherwise the renderer
        // computes them from the UVs when a normal map needs them.
        float[]? tangents = null;
        if (mesh->MTangents is not null && mesh->MBitangents is not null && mesh->MNormals is not null)
        {
            tangents = new float[vertexCount * 4];
            for (var v = 0; v < vertexCount; v++)
            {
                var t = mesh->MTangents[v];
                var b = mesh->MBitangents[v];
                var tangent = new Vector3(t.X, t.Y, t.Z).Normalized();
                var handedness = Vector3.Dot(Vector3.Cross(normals[v], tangent), new Vector3(b.X, b.Y, b.Z)) < 0f ? -1f : 1f;
                tangents[v * 4] = tangent.X;
                tangents[v * 4 + 1] = tangent.Y;
                tangents[v * 4 + 2] = tangent.Z;
                tangents[v * 4 + 3] = handedness;
            }
        }

        ColorRgba[]? colors = null;
        var colorSource = mesh->MColors[0];
        if (colorSource is not null)
        {
            colors = new ColorRgba[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                colors[v] = new ColorRgba(colorSource[v].X, colorSource[v].Y, colorSource[v].Z, colorSource[v].W);
            }
        }

        // Triangulate + SortByPrimitiveType guarantee three indices per face for a triangle mesh.
        var indices = new uint[mesh->MNumFaces * 3];
        for (var f = 0; f < mesh->MNumFaces; f++)
        {
            var face = mesh->MFaces[f];
            indices[f * 3] = face.MIndices[0];
            indices[f * 3 + 1] = face.MIndices[1];
            indices[f * 3 + 2] = face.MIndices[2];
        }

        return new ImportedMesh
        {
            Name = mesh->MName.AsString,
            Positions = positions,
            Normals = normals,
            Uv0 = uv0,
            Tangents = tangents,
            Colors = colors,
            Indices = indices,
            MaterialIndex = (int)mesh->MMaterialIndex,
            Bounds = bounds,
            Skin = ConvertSkin(mesh),
            MorphTargetCount = (int)mesh->MNumAnimMeshes
        };
    }

    private ImportedMaterial ConvertMaterial(AssimpScene* scene, Material* material, int index, string modelDirectory)
    {
        AssimpString name;
        var materialName = _assimp.GetMaterialString(material, Assimp.MaterialNameBase, 0, 0, &name) == Return.Success
            ? name.AsString
            : $"Material{index}";

        // glTF metallic-roughness base colour first, then the classic diffuse colour (OBJ Kd, FBX DiffuseColor).
        System.Numerics.Vector4 color;
        var baseColor = ColorRgba.White;
        if (_assimp.GetMaterialColor(material, MaterialBaseColorKey, 0, 0, &color) == Return.Success
            || _assimp.GetMaterialColor(material, Assimp.MaterialColorDiffuseBase, 0, 0, &color) == Return.Success)
        {
            baseColor = new ColorRgba(color.X, color.Y, color.Z, color.W);
        }

        var texture = TryGetTexture(scene, material, TextureType.BaseColor, modelDirectory)
            ?? TryGetTexture(scene, material, TextureType.Diffuse, modelDirectory);

        // glTF metallic-roughness (Assimp: "$mat.metallicFactor" / "$mat.roughnessFactor"; the packed texture under
        // UNKNOWN, or METALNESS / DIFFUSE_ROUGHNESS in newer Assimp builds). Other formats leave these null.
        var metallic = GetFloat(material, "$mat.metallicFactor");
        var roughness = GetFloat(material, "$mat.roughnessFactor");
        var metallicRoughness = TryGetTexture(scene, material, TextureType.Unknown, modelDirectory)
            ?? TryGetTexture(scene, material, TextureType.Metalness, modelDirectory)
            ?? TryGetTexture(scene, material, TextureType.DiffuseRoughness, modelDirectory);
        var normal = TryGetTexture(scene, material, TextureType.Normals, modelDirectory)
            ?? TryGetTexture(scene, material, TextureType.NormalCamera, modelDirectory);
        var occlusion = TryGetTexture(scene, material, TextureType.Lightmap, modelDirectory)
            ?? TryGetTexture(scene, material, TextureType.AmbientOcclusion, modelDirectory);
        var emissiveTexture = TryGetTexture(scene, material, TextureType.Emissive, modelDirectory)
            ?? TryGetTexture(scene, material, TextureType.EmissionColor, modelDirectory);

        var emissive = new ColorRgba(0f, 0f, 0f, 1f);
        if (_assimp.GetMaterialColor(material, "$clr.emissive", 0, 0, &color) == Return.Success)
        {
            emissive = new ColorRgba(color.X, color.Y, color.Z, 1f);
        }

        var alphaMode = ImportedAlphaMode.Opaque;
        AssimpString mode;
        if (_assimp.GetMaterialString(material, "$mat.gltf.alphaMode", 0, 0, &mode) == Return.Success)
        {
            alphaMode = mode.AsString.ToUpperInvariant() switch
            {
                "MASK" => ImportedAlphaMode.Mask,
                "BLEND" => ImportedAlphaMode.Blend,
                _ => ImportedAlphaMode.Opaque
            };
        }

        // OBJ "d" / FBX opacity below 1 means see-through (glTF uses the base colour's alpha with its alpha mode).
        var opacity = GetFloat(material, "$mat.opacity") ?? 1f;
        if (!float.IsFinite(opacity))
        {
            opacity = 1f;
        }

        if (alphaMode == ImportedAlphaMode.Opaque && opacity < 0.999f && opacity > 0f)
        {
            alphaMode = ImportedAlphaMode.Blend;
        }

        // aiShadingMode_NoShading (= Unlit, KHR_materials_unlit).
        const int NoShading = 9;
        var unlit = GetInt(material, "$mat.shadingm") == NoShading;

        var usesSecondUv = new[] { texture, metallicRoughness, normal, occlusion, emissiveTexture }.Any(t => t is { UvIndex: > 0 });
        return new ImportedMaterial
        {
            Name = materialName,
            BaseColor = baseColor,
            BaseColorTexture = texture,
            Metallic = Finite01(metallic),
            Roughness = Finite01(roughness),
            Shininess = GetFloat(material, "$mat.shininess") is { } shininess && float.IsFinite(shininess) && shininess > 0f ? shininess : null,
            MetallicRoughnessTexture = metallicRoughness,
            NormalTexture = normal,
            NormalScale = GetFloat(material, "$tex.scale", TextureType.Normals) is { } scale && float.IsFinite(scale) ? scale : 1f,
            OcclusionTexture = occlusion,
            OcclusionStrength = Finite01(GetFloat(material, "$tex.strength", TextureType.Lightmap)) ?? 1f,
            EmissiveColor = emissive,
            EmissiveTexture = emissiveTexture,
            EmissiveStrength = GetFloat(material, "$mat.emissiveIntensity") is { } strength && float.IsFinite(strength) && strength >= 0f ? strength : 1f,
            AlphaMode = alphaMode,
            AlphaCutoff = Finite01(GetFloat(material, "$mat.gltf.alphaCutoff")) ?? 0.5f,
            Opacity = global::System.Math.Clamp(opacity, 0f, 1f),
            DoubleSided = GetInt(material, "$mat.twosided") is { } twoSided && twoSided != 0,
            Unlit = unlit,
            UsesSecondUvSet = usesSecondUv
        };
    }

    private static float? Finite01(float? value) =>
        value is { } v && float.IsFinite(v) ? global::System.Math.Clamp(v, 0f, 1f) : null;

    private float? GetFloat(Material* material, string key, TextureType type = TextureType.None, uint index = 0)
    {
        float value;
        uint count = 1;
        return _assimp.GetMaterialFloatArray(material, key, (uint)type, index, &value, &count) == Return.Success && count >= 1 ? value : null;
    }

    private int? GetInt(Material* material, string key)
    {
        int value;
        uint count = 1;
        return _assimp.GetMaterialIntegerArray(material, key, 0, 0, &value, &count) == Return.Success && count >= 1 ? value : null;
    }

    private ImportedTextureReference? TryGetTexture(AssimpScene* scene, Material* material, TextureType type, string modelDirectory)
    {
        if (_assimp.GetMaterialTextureCount(material, type) == 0)
        {
            return null;
        }

        AssimpString path;
        uint uvIndex = 0;
        // One map mode per axis (U, V, W); Assimp fills them from the glTF sampler's wrapS / wrapT.
        var mapModes = stackalloc TextureMapMode[3];
        mapModes[0] = mapModes[1] = mapModes[2] = TextureMapMode.Wrap;
        if (_assimp.GetMaterialTexture(material, type, 0, &path, null, &uvIndex, null, null, mapModes, null) != Return.Success)
        {
            return null;
        }

        var sourcePath = path.AsString;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var wrapU = WrapOf(mapModes[0]);
        var wrapV = WrapOf(mapModes[1]);
        var embeddedIndex = FindEmbeddedTexture(scene, sourcePath);
        if (embeddedIndex is not null)
        {
            return new ImportedTextureReference
            {
                SourcePath = sourcePath, EmbeddedIndex = embeddedIndex, UvIndex = (int)uvIndex, WrapU = wrapU, WrapV = wrapV
            };
        }

        var resolved = ResolveExternalPath(modelDirectory, sourcePath);
        var existing = resolved is null ? null : AssimpFileSystem.ResolveExisting(resolved);
        return new ImportedTextureReference
        {
            SourcePath = sourcePath,
            ResolvedPath = existing ?? resolved,
            ResolvedFileExists = existing is not null,
            UvIndex = (int)uvIndex,
            WrapU = wrapU,
            WrapV = wrapV
        };
    }

    // Decal (border colour outside 0..1) has no glTF equivalent; it is drawn clamped.
    private static TextureWrap WrapOf(TextureMapMode mode) => mode switch
    {
        TextureMapMode.Clamp or TextureMapMode.Decal => TextureWrap.Clamp,
        TextureMapMode.Mirror => TextureWrap.Mirror,
        _ => TextureWrap.Repeat
    };

    // "*N" addresses scene texture N (GLB/glTF data URIs); some formats (FBX) instead reuse the original file name.
    private static int? FindEmbeddedTexture(AssimpScene* scene, string sourcePath)
    {
        if (sourcePath.StartsWith('*') && int.TryParse(sourcePath.AsSpan(1), out var index))
        {
            return index >= 0 && index < scene->MNumTextures ? index : null;
        }

        for (var i = 0; i < scene->MNumTextures; i++)
        {
            var fileName = scene->MTextures[i]->MFilename.AsString;
            if (fileName.Length > 0 && (fileName == sourcePath || Path.GetFileName(fileName) == Path.GetFileName(sourcePath)))
            {
                return i;
            }
        }

        return null;
    }

    // Relative references resolve against the model's folder. Backslashes from Windows-authored files are treated
    // as separators. Absolute paths are kept (they will be re-homed by the future project-copy step). A
    // percent-encoded reference (glTF URI) resolves to its decoded file name when only that exists.
    private static string? ResolveExternalPath(string modelDirectory, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var normalized = sourcePath.Replace('\\', '/');
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(modelDirectory, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static List<ImportedEmbeddedTexture> ConvertEmbeddedTextures(AssimpScene* scene)
    {
        var textures = new List<ImportedEmbeddedTexture>();
        for (var i = 0; i < scene->MNumTextures; i++)
        {
            var texture = scene->MTextures[i];
            var formatHint = Marshal.PtrToStringAnsi((IntPtr)texture->AchFormatHint) ?? string.Empty;
            var name = texture->MFilename.AsString;
            if (texture->MHeight == 0)
            {
                // Encoded image file (PNG/JPEG...): MWidth is its byte size.
                var bytes = new ReadOnlySpan<byte>(texture->PcData, (int)texture->MWidth).ToArray();
                textures.Add(new ImportedEmbeddedTexture
                {
                    Name = name, IsEncoded = true, FormatHint = formatHint, Width = 0, Height = 0, Data = bytes
                });
                continue;
            }

            // Raw texels, stored BGRA8 by Assimp; converted to RGBA8.
            var width = (int)texture->MWidth;
            var height = (int)texture->MHeight;
            var rgba = new byte[width * height * 4];
            for (var t = 0; t < width * height; t++)
            {
                var texel = texture->PcData[t];
                rgba[t * 4] = texel.R;
                rgba[t * 4 + 1] = texel.G;
                rgba[t * 4 + 2] = texel.B;
                rgba[t * 4 + 3] = texel.A;
            }

            textures.Add(new ImportedEmbeddedTexture
            {
                Name = name, IsEncoded = false, FormatHint = formatHint, Width = width, Height = height, Data = rgba
            });
        }

        return textures;
    }

    private static ImportedNode ConvertNode(AssimpNode* node, int[] meshRemap)
    {
        var meshIndices = new List<int>();
        for (var i = 0; i < node->MNumMeshes; i++)
        {
            var mapped = meshRemap[node->MMeshes[i]];
            if (mapped >= 0)
            {
                meshIndices.Add(mapped);
            }
        }

        var children = new List<ImportedNode>();
        for (var i = 0; i < node->MNumChildren; i++)
        {
            children.Add(ConvertNode(node->MChildren[i], meshRemap));
        }

        return new ImportedNode
        {
            Name = node->MName.AsString,
            LocalTransform = ToMatrix4(node->MTransformation),
            MeshIndices = meshIndices,
            Children = children
        };
    }

    private static void AccumulateBounds(ImportedNode node, Matrix4 parent, List<ImportedMesh> meshes, ref ImportedBounds bounds)
    {
        var world = Matrix4.Multiply(parent, node.LocalTransform);
        foreach (var meshIndex in node.MeshIndices)
        {
            foreach (var p in meshes[meshIndex].Positions)
            {
                bounds = bounds.Include(new Vector3(
                    world.M11 * p.X + world.M12 * p.Y + world.M13 * p.Z + world.M14,
                    world.M21 * p.X + world.M22 * p.Y + world.M23 * p.Z + world.M24,
                    world.M31 * p.X + world.M32 * p.Y + world.M33 * p.Z + world.M34));
            }
        }

        foreach (var child in node.Children)
        {
            AccumulateBounds(child, world, meshes, ref bounds);
        }
    }
}

public sealed class ModelImportException(string message) : Exception(message);
