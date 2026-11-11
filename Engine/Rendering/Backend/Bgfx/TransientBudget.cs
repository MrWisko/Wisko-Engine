namespace Wisko.Engine.Rendering.Backend.Bgfx;

/// <summary>
/// How much of a large per-frame draw (Lua particles, debug lines) fits in bgfx's transient vertex space. That space is
/// one fixed amount per frame, shared by every per-frame draw (overlays and the editor's UI come after the particles),
/// so instead of skipping a draw that does not fit, as many whole primitives are drawn as fit while a reserve stays free
/// for the draws that follow. Pure arithmetic, testable without bgfx.
/// </summary>
internal static class TransientBudget
{
    /// <summary>Kept free for the overlays and the editor UI drawn after the particles and debug lines.</summary>
    public const uint ReserveBytes = 1u << 20;

    /// <summary>
    /// The vertices to draw of <paramref name="requested"/>: at most what is <paramref name="available"/> beyond the
    /// <paramref name="reserve"/> (all in vertices), rounded down to whole primitives of <paramref name="perPrimitive"/>.
    /// </summary>
    public static uint Affordable(uint requested, uint available, uint reserve, uint perPrimitive)
    {
        if (perPrimitive == 0)
        {
            perPrimitive = 1;
        }

        var usable = available > reserve ? available - reserve : 0u;
        var count = global::System.Math.Min(requested, usable);
        return count - count % perPrimitive;
    }

    /// <summary>The vertex count to ask bgfx about: what is wanted plus the reserve, without overflowing.</summary>
    public static uint Query(uint requested, uint reserve) =>
        requested > uint.MaxValue - reserve ? uint.MaxValue : requested + reserve;
}
