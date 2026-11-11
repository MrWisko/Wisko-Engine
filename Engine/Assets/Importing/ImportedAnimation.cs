using System.Numerics;
using Vector3 = Wisko.Engine.Math.Vector3;
using Matrix4 = Wisko.Engine.Math.Matrix4;

namespace Wisko.Engine.Assets.Importing;

/// <summary>
/// How a skinned mesh is bound to the model's bones, as decoded: its bone palette (node names and inverse bind
/// matrices) and up to <see cref="MaxInfluences"/> weighted palette slots per vertex. Names are resolved to nodes later
/// (<see cref="ModelAsset"/>), where a reference that matches no node is reported.
/// </summary>
public sealed class ImportedSkin
{
    /// <summary>Bone influences kept per vertex (the strongest ones; the rest are dropped and the weights renormalized).</summary>
    public const int MaxInfluences = 4;

    /// <summary>
    /// The palette slot a vertex uses when the file gives it no weight at all: one past the last bone, meaning "the mesh's
    /// own node" (the vertex then moves rigidly with that node, as it would in an unskinned mesh).
    /// </summary>
    public int UnweightedSlot => BoneNames.Count;

    /// <summary>Palette slot -> the node that bone follows (by name, as in the file).</summary>
    public required IReadOnlyList<string> BoneNames { get; init; }

    /// <summary>
    /// Palette slot -> mesh space to that bone's space at bind time (Assimp's offset matrix, glTF's inverseBindMatrices),
    /// in the engine <see cref="Matrix4"/> layout.
    /// </summary>
    public required Matrix4[] InverseBindMatrices { get; init; }

    /// <summary><see cref="MaxInfluences"/> palette slots per vertex (unused entries are slot 0 with weight 0).</summary>
    public required ushort[] Joints { get; init; }

    /// <summary><see cref="MaxInfluences"/> weights per vertex, strongest first, summing to 1.</summary>
    public required float[] Weights { get; init; }

    /// <summary>Vertices that had more than <see cref="MaxInfluences"/> influences (the weakest were dropped).</summary>
    public int TruncatedVertices { get; init; }

    /// <summary>Vertices whose weights did not add up to 1 in the file (renormalized).</summary>
    public int RenormalizedVertices { get; init; }

    /// <summary>Vertices with no weight at all (they follow the mesh's own node: <see cref="UnweightedSlot"/>).</summary>
    public int UnweightedVertices { get; init; }

    /// <summary>Weights that were not finite or were negative (ignored).</summary>
    public int InvalidWeights { get; init; }
}

/// <summary>One animation clip as decoded: its name in the file, its length and the keyframe tracks of the nodes it moves.</summary>
public sealed class ImportedAnimation
{
    /// <summary>The clip's name in the file; empty when it has none.</summary>
    public required string Name { get; init; }

    /// <summary>Length in seconds (the file's duration in ticks / ticks per second).</summary>
    public required double DurationSeconds { get; init; }

    public required IReadOnlyList<ImportedNodeTrack> Tracks { get; init; }

    /// <summary>Morph-target (blend shape) and vertex-animation channels, which Wisko does not play (reported, ignored).</summary>
    public int UnsupportedChannelCount { get; init; }
}

/// <summary>
/// Keyframes of one node (by name): translation, rotation and scale, each with its own key times in seconds. A channel
/// without keys leaves that part of the node's transform at rest.
/// </summary>
public sealed class ImportedNodeTrack
{
    public required string NodeName { get; init; }

    public required ImportedChannel<Vector3> Translation { get; init; }

    public required ImportedChannel<Quaternion> Rotation { get; init; }

    public required ImportedChannel<Vector3> Scale { get; init; }
}

/// <summary>Keyframe times (seconds, ascending) and values; <see cref="Step"/>: each value holds until the next key.</summary>
public sealed class ImportedChannel<T>(float[] times, T[] values, bool step)
    where T : struct
{
    public static ImportedChannel<T> Empty { get; } = new([], [], false);

    public float[] Times { get; } = times;

    public T[] Values { get; } = values;

    public bool Step { get; } = step;

    public int Count => Times.Length;
}
