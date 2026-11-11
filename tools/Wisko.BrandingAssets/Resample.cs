namespace Wisko.BrandingAssets;

/// <summary>
/// Scaling in premultiplied alpha, so transparent pixels never bleed their colour into the logo's edge. Downscaling
/// averages exactly the source area each target pixel covers (no ringing, no sharpening); upscaling, which a too small
/// source needs and which is reported, interpolates bilinearly.
/// </summary>
internal static class Resample
{
    public static RgbaImage Resize(RgbaImage source, int width, int height)
    {
        if (width == source.Width && height == source.Height)
        {
            return new RgbaImage(width, height, (byte[])source.Pixels.Clone());
        }

        var premultiplied = Premultiply(source);
        var result = width <= source.Width && height <= source.Height
            ? AreaAverage(premultiplied, source.Width, source.Height, width, height)
            : Bilinear(premultiplied, source.Width, source.Height, width, height);
        return Unpremultiply(result, width, height);
    }

    /// <summary>
    /// The logo centred on a transparent square of <paramref name="size"/> pixels, uniformly scaled (never stretched) so
    /// its larger side fills <c>1 - 2 * padding</c> of the square.
    /// </summary>
    public static RgbaImage OnSquare(RgbaImage logo, int size, double padding) => OnCanvas(logo, size, size, padding, null);

    /// <summary>
    /// The logo centred on a <paramref name="width"/> x <paramref name="height"/> canvas, uniformly scaled (never
    /// stretched) to fit within <c>1 - 2 * padding</c> of each side, on <paramref name="background"/> (opaque; the logo's
    /// edge blended onto it) or on transparency when null.
    /// </summary>
    public static RgbaImage OnCanvas(RgbaImage logo, int width, int height, double padding, (byte R, byte G, byte B)? background)
    {
        var innerWidth = Math.Max(1, (int)Math.Round(width * (1d - 2d * padding)));
        var innerHeight = Math.Max(1, (int)Math.Round(height * (1d - 2d * padding)));
        var scale = Math.Min(innerWidth / (double)logo.Width, innerHeight / (double)logo.Height);
        var w = Math.Max(1, (int)Math.Round(logo.Width * scale));
        var h = Math.Max(1, (int)Math.Round(logo.Height * scale));
        var scaled = Resize(logo, w, h);
        var canvas = new RgbaImage(width, height);
        var left = (width - w) / 2;
        var top = (height - h) / 2;
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(scaled.Pixels, y * w * 4, canvas.Pixels, ((top + y) * width + left) * 4, w * 4);
        }

        if (background is { } b)
        {
            var p = canvas.Pixels;
            for (var i = 0; i < p.Length; i += 4)
            {
                var a = p[i + 3] / 255d;
                p[i] = (byte)Math.Round(p[i] * a + b.R * (1 - a));
                p[i + 1] = (byte)Math.Round(p[i + 1] * a + b.G * (1 - a));
                p[i + 2] = (byte)Math.Round(p[i + 2] * a + b.B * (1 - a));
                p[i + 3] = 255;
            }
        }

        return canvas;
    }

    public static RgbaImage Crop(RgbaImage image, int x, int y, int width, int height)
    {
        var result = new RgbaImage(width, height);
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(image.Pixels, ((y + row) * image.Width + x) * 4, result.Pixels, row * width * 4, width * 4);
        }

        return result;
    }

    private static double[] Premultiply(RgbaImage image)
    {
        var p = new double[image.Width * image.Height * 4];
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = image.Pixels[i + 3] / 255d;
            p[i] = image.Pixels[i] * a;
            p[i + 1] = image.Pixels[i + 1] * a;
            p[i + 2] = image.Pixels[i + 2] * a;
            p[i + 3] = a;
        }

        return p;
    }

    private static RgbaImage Unpremultiply(double[] p, int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            var alpha = (byte)Math.Clamp(Math.Round(a * 255d), 0, 255);
            if (alpha == 0)
            {
                continue;
            }

            image.Pixels[i] = (byte)Math.Clamp(Math.Round(p[i] / a), 0, 255);
            image.Pixels[i + 1] = (byte)Math.Clamp(Math.Round(p[i + 1] / a), 0, 255);
            image.Pixels[i + 2] = (byte)Math.Clamp(Math.Round(p[i + 2] / a), 0, 255);
            image.Pixels[i + 3] = alpha;
        }

        return image;
    }

    // Each target pixel is the coverage-weighted average of the source pixels under it.
    private static double[] AreaAverage(double[] src, int sw, int sh, int tw, int th)
    {
        var dst = new double[tw * th * 4];
        var sx = sw / (double)tw;
        var sy = sh / (double)th;
        for (var ty = 0; ty < th; ty++)
        {
            var y0 = ty * sy;
            var y1 = y0 + sy;
            for (var tx = 0; tx < tw; tx++)
            {
                var x0 = tx * sx;
                var x1 = x0 + sx;
                double r = 0, g = 0, b = 0, a = 0, total = 0;
                for (var y = (int)Math.Floor(y0); y < Math.Min(sh, (int)Math.Ceiling(y1)); y++)
                {
                    var wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                    for (var x = (int)Math.Floor(x0); x < Math.Min(sw, (int)Math.Ceiling(x1)); x++)
                    {
                        var weight = wy * (Math.Min(x + 1, x1) - Math.Max(x, x0));
                        var i = (y * sw + x) * 4;
                        r += src[i] * weight;
                        g += src[i + 1] * weight;
                        b += src[i + 2] * weight;
                        a += src[i + 3] * weight;
                        total += weight;
                    }
                }

                var o = (ty * tw + tx) * 4;
                dst[o] = r / total;
                dst[o + 1] = g / total;
                dst[o + 2] = b / total;
                dst[o + 3] = a / total;
            }
        }

        return dst;
    }

    private static double[] Bilinear(double[] src, int sw, int sh, int tw, int th)
    {
        var dst = new double[tw * th * 4];
        for (var ty = 0; ty < th; ty++)
        {
            var fy = Math.Clamp((ty + 0.5) * sh / th - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            var wy = fy - y0;
            for (var tx = 0; tx < tw; tx++)
            {
                var fx = Math.Clamp((tx + 0.5) * sw / tw - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                var wx = fx - x0;
                for (var c = 0; c < 4; c++)
                {
                    var top = src[(y0 * sw + x0) * 4 + c] * (1 - wx) + src[(y0 * sw + x1) * 4 + c] * wx;
                    var bottom = src[(y1 * sw + x0) * 4 + c] * (1 - wx) + src[(y1 * sw + x1) * 4 + c] * wx;
                    dst[(ty * tw + tx) * 4 + c] = top * (1 - wy) + bottom * wy;
                }
            }
        }

        return dst;
    }
}
