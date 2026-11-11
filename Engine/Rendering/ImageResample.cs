namespace Wisko.Engine.Rendering;

internal static class ImageResample
{
    /// <summary>
    /// Center-crops a bottom-up RGBA framebuffer readback to the target aspect ratio and box-filters it
    /// down to <paramref name="width"/> x <paramref name="height"/>, returning top-down opaque RGBA rows.
    /// </summary>
    public static byte[] CropAndDownscaleFromFramebuffer(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var targetAspect = width / (float)height;
        var cropWidth = (float)sourceWidth;
        var cropHeight = (float)sourceHeight;
        if (cropWidth / cropHeight > targetAspect)
        {
            cropWidth = cropHeight * targetAspect;
        }
        else
        {
            cropHeight = cropWidth / targetAspect;
        }

        var cropX = (sourceWidth - cropWidth) * 0.5f;
        var cropY = (sourceHeight - cropHeight) * 0.5f;
        var stepX = cropWidth / width;
        var stepY = cropHeight / height;
        var result = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            // Output row 0 is the top of the image = the highest framebuffer row.
            var srcTop = cropY + cropHeight - (y + 1) * stepY;
            var y0 = global::System.Math.Clamp((int)srcTop, 0, sourceHeight - 1);
            var y1 = global::System.Math.Clamp((int)MathF.Ceiling(srcTop + stepY), y0 + 1, sourceHeight);

            for (var x = 0; x < width; x++)
            {
                var srcLeft = cropX + x * stepX;
                var x0 = global::System.Math.Clamp((int)srcLeft, 0, sourceWidth - 1);
                var x1 = global::System.Math.Clamp((int)MathF.Ceiling(srcLeft + stepX), x0 + 1, sourceWidth);

                long r = 0, g = 0, b = 0;
                var count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * sourceWidth * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = row + sx * 4;
                        r += source[i];
                        g += source[i + 1];
                        b += source[i + 2];
                        count++;
                    }
                }

                var o = (y * width + x) * 4;
                result[o] = (byte)(r / count);
                result[o + 1] = (byte)(g / count);
                result[o + 2] = (byte)(b / count);
                result[o + 3] = 255;
            }
        }

        return result;
    }

    /// <summary>
    /// Center-crops top-down RGBA to the <paramref name="width"/>:<paramref name="height"/> aspect ratio
    /// (never stretches) and resamples it to exactly that size: exact area averaging when shrinking,
    /// bilinear when enlarging. Averaging is alpha-weighted so transparent pixels don't darken edges.
    /// </summary>
    public static byte[] CropToAspectAndResize(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var targetAspect = width / (float)height;
        var cropWidth = sourceWidth;
        var cropHeight = sourceHeight;
        if (sourceWidth / (float)sourceHeight > targetAspect)
        {
            cropWidth = global::System.Math.Clamp((int)MathF.Round(sourceHeight * targetAspect), 1, sourceWidth);
        }
        else
        {
            cropHeight = global::System.Math.Clamp((int)MathF.Round(sourceWidth / targetAspect), 1, sourceHeight);
        }

        var cropX = (sourceWidth - cropWidth) / 2;
        var cropY = (sourceHeight - cropHeight) / 2;
        var cropMaxX = cropX + cropWidth - 1;
        var cropMaxY = cropY + cropHeight - 1;
        var scaleX = cropWidth / (float)width;
        var scaleY = cropHeight / (float)height;
        var result = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0, weight = 0;
                if (scaleX >= 1f && scaleY >= 1f)
                {
                    var x0 = cropX + x * scaleX;
                    var y0 = cropY + y * scaleY;
                    var x1 = x0 + scaleX;
                    var y1 = y0 + scaleY;
                    for (var sy = (int)y0; sy < (int)MathF.Ceiling(y1) && sy <= cropMaxY; sy++)
                    {
                        var wy = MathF.Min(sy + 1, y1) - MathF.Max(sy, y0);
                        for (var sx = (int)x0; sx < (int)MathF.Ceiling(x1) && sx <= cropMaxX; sx++)
                        {
                            var w = wy * (MathF.Min(sx + 1, x1) - MathF.Max(sx, x0));
                            Accumulate(source, (sy * sourceWidth + sx) * 4, w, ref r, ref g, ref b, ref a, ref weight);
                        }
                    }
                }
                else
                {
                    // Samples stay inside the crop so the cut-away edges never bleed in.
                    var fx = global::System.Math.Clamp(cropX + (x + 0.5f) * scaleX - 0.5f, cropX, cropMaxX);
                    var fy = global::System.Math.Clamp(cropY + (y + 0.5f) * scaleY - 0.5f, cropY, cropMaxY);
                    var ix = (int)fx;
                    var iy = (int)fy;
                    var tx = fx - ix;
                    var ty = fy - iy;
                    var ix1 = global::System.Math.Min(ix + 1, cropMaxX);
                    var iy1 = global::System.Math.Min(iy + 1, cropMaxY);
                    Accumulate(source, (iy * sourceWidth + ix) * 4, (1 - tx) * (1 - ty), ref r, ref g, ref b, ref a, ref weight);
                    Accumulate(source, (iy * sourceWidth + ix1) * 4, tx * (1 - ty), ref r, ref g, ref b, ref a, ref weight);
                    Accumulate(source, (iy1 * sourceWidth + ix) * 4, (1 - tx) * ty, ref r, ref g, ref b, ref a, ref weight);
                    Accumulate(source, (iy1 * sourceWidth + ix1) * 4, tx * ty, ref r, ref g, ref b, ref a, ref weight);
                }

                var o = (y * width + x) * 4;
                if (weight <= 0 || a <= 0)
                {
                    continue; // fully transparent
                }

                result[o] = ToByte(r / a);
                result[o + 1] = ToByte(g / a);
                result[o + 2] = ToByte(b / a);
                result[o + 3] = ToByte(a / weight);
            }
        }

        return result;
    }

    // Alpha-weighted accumulation: color sums are premultiplied by alpha, alpha is averaged by coverage.
    private static void Accumulate(
        byte[] source,
        int index,
        float coverage,
        ref double r,
        ref double g,
        ref double b,
        ref double a,
        ref double weight)
    {
        if (coverage <= 0f)
        {
            return;
        }

        var alpha = source[index + 3] * (double)coverage;
        r += source[index] * alpha;
        g += source[index + 1] * alpha;
        b += source[index + 2] * alpha;
        a += alpha;
        weight += coverage;
    }

    private static byte ToByte(double value) => (byte)global::System.Math.Clamp((int)global::System.Math.Round(value), 0, 255);
}
