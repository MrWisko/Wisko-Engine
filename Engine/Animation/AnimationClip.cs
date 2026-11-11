using System.Numerics;
using Wisko.Engine.Assets.Importing;
using Vector3 = Wisko.Engine.Math.Vector3;

namespace Wisko.Engine.Animation;

/// <summary>
/// One playable animation of a model: its name, length and keyframe tracks, each resolved to a node of the model's
/// <see cref="ModelSkeleton"/>. Immutable and shared by every instance of the asset.
/// </summary>
public sealed class AnimationClip
{
    private readonly NodeTrack[] _tracks;
    private readonly int[] _trackOfNode;

    internal AnimationClip(string name, string fileName, int index, float duration, NodeTrack[] tracks, int nodeCount)
    {
        Name = name;
        FileName = fileName;
        Index = index;
        Duration = duration;
        _tracks = tracks;
        _trackOfNode = new int[nodeCount];
        Array.Fill(_trackOfNode, -1);
        for (var i = 0; i < tracks.Length; i++)
        {
            _trackOfNode[tracks[i].Node] = i;
        }
    }

    /// <summary>The name scripts and the editor use: the file's, or "Animation N" (N = <see cref="Index"/> + 1) when it has none.</summary>
    public string Name { get; }

    /// <summary>The name exactly as in the file (empty when the clip has none).</summary>
    public string FileName { get; }

    /// <summary>Position in the file's clip list, from 0.</summary>
    public int Index { get; }

    /// <summary>Length in seconds.</summary>
    public float Duration { get; }

    /// <summary>Nodes this clip moves.</summary>
    public int TrackCount => _tracks.Length;

    /// <summary>Whether the clip has keyframes for this node.</summary>
    public bool Animates(int node) => _trackOfNode[node] >= 0;

    /// <summary>
    /// The node's local transform at <paramref name="time"/> seconds (clamped to the keyframes: before the first key the
    /// first value holds, after the last the last). Channels the clip does not key keep <paramref name="rest"/>'s value.
    /// </summary>
    public NodeTransform Sample(int node, float time, in NodeTransform rest)
    {
        var index = _trackOfNode[node];
        if (index < 0)
        {
            return rest;
        }

        var track = _tracks[index];
        return new NodeTransform(
            track.Translation.Count == 0 ? rest.Translation : SampleVector(track.Translation, time),
            track.Rotation.Count == 0 ? rest.Rotation : SampleRotation(track.Rotation, time),
            track.Scale.Count == 0 ? rest.Scale : SampleVector(track.Scale, time));
    }

    internal static Vector3 SampleVector(ImportedChannel<Vector3> channel, float time)
    {
        if (!Locate(channel.Times, time, out var key, out var fraction) || channel.Step)
        {
            return channel.Values[key];
        }

        return AnimationMath.Lerp(channel.Values[key], channel.Values[key + 1], fraction);
    }

    internal static Quaternion SampleRotation(ImportedChannel<Quaternion> channel, float time)
    {
        if (!Locate(channel.Times, time, out var key, out var fraction) || channel.Step)
        {
            return channel.Values[key];
        }

        return AnimationMath.Slerp(channel.Values[key], channel.Values[key + 1], fraction);
    }

    // The key at or before `time` and how far it is towards the next one (0..1). False when the time is outside the keys
    // (or there is one key): `key` is then the first or last key, held.
    private static bool Locate(float[] times, float time, out int key, out float fraction)
    {
        fraction = 0f;
        var last = times.Length - 1;
        if (last == 0 || !(time > times[0]))
        {
            key = 0;
            return false;
        }

        if (time >= times[last])
        {
            key = last;
            return false;
        }

        // times[low] <= time < times[high]
        int low = 0, high = last;
        while (high - low > 1)
        {
            var middle = (low + high) >> 1;
            if (times[middle] <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        key = low;
        var span = times[high] - times[low];
        fraction = span > 0f ? (time - times[low]) / span : 1f;
        return true;
    }
}

/// <summary>The keyframes of one node in a clip (channels without keys leave that part at rest).</summary>
internal sealed class NodeTrack(int node, ImportedChannel<Vector3> translation, ImportedChannel<Quaternion> rotation, ImportedChannel<Vector3> scale)
{
    public int Node { get; } = node;

    public ImportedChannel<Vector3> Translation { get; } = translation;

    public ImportedChannel<Quaternion> Rotation { get; } = rotation;

    public ImportedChannel<Vector3> Scale { get; } = scale;
}
