namespace Wisko.BrandingAssets;

/// <summary>What the source image's background is.</summary>
internal enum BackgroundKind
{
    /// <summary>The source has its own transparency (corners transparent): it is used as it is.</summary>
    Transparent,

    /// <summary>A solid colour around the logo (a screenshot or a flattened image): removed by colour.</summary>
    Solid,

    /// <summary>Neither: the border is not one colour, so the logo cannot be separated automatically.</summary>
    Mixed
}

/// <summary>The source's background as found on its border.</summary>
internal sealed record BackgroundInfo(BackgroundKind Kind, byte R, byte G, byte B, double UniformFraction);

/// <summary>
/// Separates the logo from its background without changing the logo. A transparent source is kept as it is. On a solid
/// background, the background is found by a flood fill from the border (pixels within <c>tolerance</c> of the
/// background colour that are connected to the border), so background-coloured parts inside the logo stay. Pixels at
/// the logo's edge are partly background (anti-aliasing); for each, the logo's own colour there is estimated from the
/// nearest fully covered pixels, and the pixel's coverage is solved from that colour and the background colour. The
/// edge then carries the logo's colour at the right transparency: no light or dark halo of the old background. Every
/// pixel inside the logo keeps its exact colour.
/// </summary>
internal static class Matte
{
    public const int DefaultTolerance = 24;

    public static BackgroundInfo Analyze(RgbaImage image, int tolerance = DefaultTolerance)
    {
        var border = BorderPixels(image).ToList();
        var transparent = border.Count(p => image.Pixels[p + 3] < 16);
        if (transparent >= border.Count * 0.98)
        {
            return new BackgroundInfo(BackgroundKind.Transparent, 0, 0, 0, transparent / (double)border.Count);
        }

        // The most common border colour (by quantized buckets), then how much of the border is within tolerance.
        var counts = new Dictionary<int, int>();
        foreach (var p in border)
        {
            var key = (image.Pixels[p] >> 3) << 10 | (image.Pixels[p + 1] >> 3) << 5 | image.Pixels[p + 2] >> 3;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var best = counts.MaxBy(pair => pair.Value).Key;
        var members = border.Where(p => ((image.Pixels[p] >> 3) << 10 | (image.Pixels[p + 1] >> 3) << 5 | image.Pixels[p + 2] >> 3) == best).ToList();
        var r = (byte)Median(members.Select(p => image.Pixels[p]));
        var g = (byte)Median(members.Select(p => image.Pixels[p + 1]));
        var b = (byte)Median(members.Select(p => image.Pixels[p + 2]));
        var within = border.Count(p => Distance(image.Pixels, p, r, g, b) <= tolerance && image.Pixels[p + 3] == 255);
        var fraction = within / (double)border.Count;
        return new BackgroundInfo(fraction >= 0.97 ? BackgroundKind.Solid : BackgroundKind.Mixed, r, g, b, fraction);
    }

    /// <summary>Areas of at least this many pixels count as enclosed background-coloured areas.</summary>
    public const int MinimumEnclosedArea = 16;

    /// <summary>
    /// The sizes of background-coloured areas the logo encloses (not connected to the border): they may be part of the
    /// artwork, or background seen through the logo. They stay opaque unless <c>removeEnclosed</c> is asked for.
    /// </summary>
    public static List<int> EnclosedAreas(RgbaImage source, byte bgR, byte bgG, byte bgB, int tolerance = DefaultTolerance)
    {
        var background = FloodBackground(source, bgR, bgG, bgB, tolerance, removeEnclosed: false, out var enclosed);
        _ = background;
        return enclosed;
    }

    /// <summary>The image with its solid background removed (see the class summary).</summary>
    public static RgbaImage RemoveSolidBackground(RgbaImage source, byte bgR, byte bgG, byte bgB, int tolerance = DefaultTolerance, bool removeEnclosed = false)
    {
        var w = source.Width;
        var h = source.Height;
        var src = source.Pixels;
        var background = FloodBackground(source, bgR, bgG, bgB, tolerance, removeEnclosed, out _);

        // The edge band: logo pixels next to the background (within 2 px). Everything else of the logo is interior.
        const int band = 2;
        var edge = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (background[y * w + x])
                {
                    continue;
                }

                for (var dy = -band; dy <= band && !edge[y * w + x]; dy++)
                {
                    for (var dx = -band; dx <= band; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && background[ny * w + nx])
                        {
                            edge[y * w + x] = true;
                            break;
                        }
                    }
                }
            }
        }

        var result = new RgbaImage(w, h);
        var dst = result.Pixels;
        for (var i = 0; i < w * h; i++)
        {
            var o = i * 4;
            if (background[i])
            {
                continue; // transparent black
            }

            if (!edge[i])
            {
                dst[o] = src[o];
                dst[o + 1] = src[o + 1];
                dst[o + 2] = src[o + 2];
                dst[o + 3] = 255;
                continue;
            }

            var (fr, fg, fb, found) = LocalForeground(src, background, edge, w, h, i % w, i / w);
            if (!found)
            {
                // A thin stroke with no fully covered pixel near: its own colour, coverage by colour distance.
                (fr, fg, fb) = (src[o], src[o + 1], src[o + 2]);
            }

            double cr = src[o], cg = src[o + 1], cb = src[o + 2];
            double vr = fr - bgR, vg = fg - bgG, vb = fb - bgB;
            var length2 = vr * vr + vg * vg + vb * vb;
            double alpha;
            if (length2 < 24 * 24)
            {
                // The logo's colour here is close to the background: no reliable unmixing, so it stays opaque.
                alpha = 1d;
            }
            else
            {
                alpha = Math.Clamp(((cr - bgR) * vr + (cg - bgG) * vg + (cb - bgB) * vb) / length2, 0d, 1d);
            }

            var a = (byte)Math.Round(alpha * 255d);
            if (a == 0)
            {
                continue;
            }

            // The colour without the background in it: unmixed where coverage is high, the local logo colour otherwise.
            byte r, g, b;
            if (alpha >= 0.5)
            {
                r = Clamp(bgR + (cr - bgR) / alpha);
                g = Clamp(bgG + (cg - bgG) / alpha);
                b = Clamp(bgB + (cb - bgB) / alpha);
            }
            else
            {
                (r, g, b) = ((byte)fr, (byte)fg, (byte)fb);
            }

            dst[o] = r;
            dst[o + 1] = g;
            dst[o + 2] = b;
            dst[o + 3] = a;
        }

        return result;
    }

    /// <summary>The bounding box of the pixels with any coverage, or null when there are none.</summary>
    public static (int X, int Y, int Width, int Height)? Bounds(RgbaImage image, byte minimumAlpha = 1)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Alpha(x, y) >= minimumAlpha)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // Background: the pixels within tolerance of the background colour that connect to the border (and, when asked,
    // enclosed areas of that colour too). The enclosed areas found are listed by size either way.
    private static bool[] FloodBackground(RgbaImage source, byte bgR, byte bgG, byte bgB, int tolerance, bool removeEnclosed, out List<int> enclosed)
    {
        var w = source.Width;
        var h = source.Height;
        var src = source.Pixels;
        var background = new bool[w * h];
        var seen = new bool[w * h];
        var queue = new Queue<int>();
        bool Matches(int i) => Distance(src, i * 4, bgR, bgG, bgB) <= tolerance;

        int Fill(int start, bool[] mark)
        {
            var count = 0;
            queue.Enqueue(start);
            mark[start] = true;
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                count++;
                int x = i % w, y = i / w;
                foreach (var j in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
                {
                    if (j >= 0 && !mark[j] && Matches(j))
                    {
                        mark[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }

            return count;
        }

        for (var x = 0; x < w; x++)
        {
            foreach (var i in new[] { x, (h - 1) * w + x })
            {
                if (!background[i] && Matches(i)) Fill(i, background);
            }
        }

        for (var y = 0; y < h; y++)
        {
            foreach (var i in new[] { y * w, y * w + w - 1 })
            {
                if (!background[i] && Matches(i)) Fill(i, background);
            }
        }

        // Enclosed areas: each further connected area of the background colour, filled once (seen marks it), its pixels
        // remembered so it can become background when asked.
        enclosed = [];
        Array.Copy(background, seen, background.Length);
        var members = new List<int>();
        for (var i = 0; i < w * h; i++)
        {
            if (seen[i] || !Matches(i))
            {
                continue;
            }

            members.Clear();
            queue.Enqueue(i);
            seen[i] = true;
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                members.Add(p);
                int x = p % w, y = p / w;
                foreach (var j in new[] { x > 0 ? p - 1 : -1, x < w - 1 ? p + 1 : -1, y > 0 ? p - w : -1, y < h - 1 ? p + w : -1 })
                {
                    if (j >= 0 && !seen[j] && Matches(j))
                    {
                        seen[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }

            if (members.Count >= MinimumEnclosedArea)
            {
                enclosed.Add(members.Count);
                if (removeEnclosed)
                {
                    foreach (var p in members)
                    {
                        background[p] = true;
                    }
                }
            }
        }

        return background;
    }

    private static (double R, double G, double B, bool Found) LocalForeground(byte[] src, bool[] background, bool[] edge, int w, int h, int x, int y)
    {
        foreach (var radius in new[] { 4, 8 })
        {
            double r = 0, g = 0, b = 0;
            var n = 0;
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                    {
                        continue;
                    }

                    var j = ny * w + nx;
                    if (background[j] || edge[j])
                    {
                        continue;
                    }

                    r += src[j * 4];
                    g += src[j * 4 + 1];
                    b += src[j * 4 + 2];
                    n++;
                }
            }

            if (n > 0)
            {
                return (r / n, g / n, b / n, true);
            }
        }

        return (0, 0, 0, false);
    }

    private static IEnumerable<int> BorderPixels(RgbaImage image)
    {
        for (var x = 0; x < image.Width; x++)
        {
            yield return image.Index(x, 0);
            if (image.Height > 1) yield return image.Index(x, image.Height - 1);
        }

        for (var y = 1; y < image.Height - 1; y++)
        {
            yield return image.Index(0, y);
            if (image.Width > 1) yield return image.Index(image.Width - 1, y);
        }
    }

    private static int Distance(byte[] pixels, int offset, byte r, byte g, byte b) =>
        Math.Max(Math.Abs(pixels[offset] - r), Math.Max(Math.Abs(pixels[offset + 1] - g), Math.Abs(pixels[offset + 2] - b)));

    private static int Median(IEnumerable<byte> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
