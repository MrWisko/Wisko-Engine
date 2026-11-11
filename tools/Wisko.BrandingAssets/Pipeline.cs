using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wisko.BrandingAssets;

/// <summary>How to separate the logo from the source.</summary>
internal sealed record GenerateOptions
{
    /// <summary>A rectangle of the source to use (x, y, width, height); null: the whole image.</summary>
    public (int X, int Y, int Width, int Height)? Crop { get; init; }

    /// <summary>The background colour to remove; null: found on the source's border (or the source's own transparency).</summary>
    public (byte R, byte G, byte B)? Background { get; init; }

    public int Tolerance { get; init; } = Matte.DefaultTolerance;

    /// <summary>Space around the logo in every icon, as a fraction of the icon's side on each edge.</summary>
    public double Padding { get; init; } = 0.04;

    /// <summary>Whether icons larger than the logo itself may be made (upscaled, so blurred).</summary>
    public bool AllowUpscale { get; init; }

    /// <summary>Whether background-coloured areas the logo encloses become transparent too (otherwise they stay as they are).</summary>
    public bool RemoveEnclosed { get; init; }
}

/// <summary>What the source is, before anything is made from it.</summary>
internal sealed record SourceReport(
    int Width, int Height, bool HasTransparency, BackgroundInfo Background, (int X, int Y, int Width, int Height)? Logo,
    int LogoExtent, IReadOnlyList<string> Findings, bool LargeEnough, bool Separable);

/// <summary>One validation result.</summary>
internal sealed record Finding(string Check, bool Passed, string Detail);

/// <summary>
/// Makes every Wisko icon file from the logo artwork, without changing the logo: it is only separated from its
/// background, cropped to its own bounds and uniformly scaled onto transparent squares (geometry, colours and shading
/// as in the source). Files (relative to the branding folder): Master/Wisko-Master-Transparent.png (the separated logo
/// at the source's own resolution), AppIcon/Wisko-AppIcon-1024.png, AppIcon/Wisko-AppIcon.ico (Windows),
/// AppIcon/Wisko-AppIcon.icns (macOS), Tiny/Wisko-TinyMark-16/32/64/512.png (window icons),
/// Installer/Wisko-WizardImage-WxH.png and Installer/Wisko-WizardSmallImage-N.png (the Windows installer's wizard
/// images), and manifest.json (which source, settings and outputs, with checksums).
/// </summary>
internal static class Pipeline
{
    public static readonly int[] TinySizes = [16, 32, 64, 512];

    /// <summary>
    /// The installer's large wizard image (left of its Welcome and Finished pages): the image area of Inno Setup 6.6 and
    /// later at 100% to 250% display scaling, so Setup picks an exact fit instead of stretching.
    /// </summary>
    public static readonly (int Width, int Height)[] WizardImageSizes =
        [(202, 386), (269, 515), (336, 643), (403, 772), (430, 824), (498, 953), (534, 1022)];

    /// <summary>The installer's small wizard image (top right of its other pages), square, at the same scalings.</summary>
    public static readonly int[] WizardSmallImageSizes = [58, 77, 97, 116, 124, 143, 159];

    /// <summary>Space around the logo in the wizard images, as a fraction of each side (the narrow side limits the logo).</summary>
    public const double WizardImagePadding = 0.18;

    public const double WizardSmallImagePadding = 0.12;

    public static SourceReport Inspect(RgbaImage source, GenerateOptions options)
    {
        var image = options.Crop is { } c ? Resample.Crop(source, c.X, c.Y, c.Width, c.Height) : source;
        var findings = new List<string>();
        var hasTransparency = false;
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] < 255)
            {
                hasTransparency = true;
                break;
            }
        }

        var background = options.Background is { } forced
            ? new BackgroundInfo(BackgroundKind.Solid, forced.R, forced.G, forced.B, 1d)
            : Matte.Analyze(image, options.Tolerance);
        var separated = Separate(image, background, options);
        var logo = separated is null ? null : Matte.Bounds(separated, 8);
        var extent = logo is { } b ? Math.Max(b.Width, b.Height) : 0;

        findings.Add($"source {image.Width} x {image.Height} px{(options.Crop is null ? "" : " (cropped)")}, " +
            (hasTransparency ? "with transparency" : "no transparency (a screenshot or a flattened image)"));
        findings.Add(background.Kind switch
        {
            BackgroundKind.Transparent => "background: the source's own transparency (used as it is)",
            BackgroundKind.Solid => FormattableString.Invariant(
                $"background: solid #{background.R:X2}{background.G:X2}{background.B:X2} ({background.UniformFraction:P0} of the border), removed by colour"),
            _ => FormattableString.Invariant(
                $"background: not one colour ({background.UniformFraction:P0} of the border matches the most common colour); the logo cannot be separated automatically: crop to the logo (--crop) or name the background (--background)")
        });
        if (background.Kind == BackgroundKind.Solid)
        {
            var enclosed = Matte.EnclosedAreas(image, background.R, background.G, background.B, options.Tolerance);
            if (enclosed.Count > 0)
            {
                findings.Add($"{enclosed.Count} background-coloured area(s) inside the logo ({string.Join(", ", enclosed.OrderDescending().Take(5))} px): " +
                    (options.RemoveEnclosed ? "made transparent (--remove-enclosed)" : "kept as part of the logo; if they are background showing through, pass --remove-enclosed"));
            }
        }

        if (logo is { } bounds)
        {
            findings.Add($"logo: {bounds.Width} x {bounds.Height} px at ({bounds.X}, {bounds.Y})");
            if (bounds.X == 0 || bounds.Y == 0 || bounds.X + bounds.Width == image.Width || bounds.Y + bounds.Height == image.Height)
            {
                findings.Add("the logo touches the image's edge: it may be cut off (a screenshot cropped too tightly?)");
            }
        }

        var largeEnough = extent >= 1024;
        if (extent > 0 && !largeEnough)
        {
            findings.Add(FormattableString.Invariant(
                $"the logo is only {extent} px across: the 1024 px icon{(extent < 512 ? " and the 512 px icons" : "")} would be upscaled (blurred). This looks like a screenshot or a thumbnail, not the full-resolution artwork."));
        }

        return new SourceReport(image.Width, image.Height, hasTransparency, background, logo, extent, findings, largeEnough,
            separated is not null && logo is not null);
    }

    /// <summary>Writes every file into <paramref name="brandingFolder"/>; the paths written, or the reason it refused.</summary>
    public static IReadOnlyList<string> Generate(string sourcePath, GenerateOptions options, string brandingFolder)
    {
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var source = RgbaImage.Decode(sourceBytes);
        var report = Inspect(source, options);
        if (!report.Separable)
        {
            throw new InvalidOperationException("the logo could not be separated from its background: " + string.Join("; ", report.Findings));
        }

        if (!report.LargeEnough && !options.AllowUpscale)
        {
            throw new InvalidOperationException("the source is too small for every icon size without upscaling (pass --allow-upscale to make them anyway): " +
                string.Join("; ", report.Findings));
        }

        var image = options.Crop is { } c ? Resample.Crop(source, c.X, c.Y, c.Width, c.Height) : source;
        var separated = Separate(image, report.Background, options)!;
        var b = report.Logo!.Value;
        var logo = Resample.Crop(separated, b.X, b.Y, b.Width, b.Height);

        var outputs = new Dictionary<string, RgbaImage>
        {
            ["Master/Wisko-Master-Transparent.png"] = Resample.OnSquare(logo, Math.Max(logo.Width, logo.Height), 0d),
            ["AppIcon/Wisko-AppIcon-1024.png"] = Resample.OnSquare(logo, 1024, options.Padding)
        };
        foreach (var size in TinySizes)
        {
            outputs[$"Tiny/Wisko-TinyMark-{size}.png"] = Resample.OnSquare(logo, size, options.Padding);
        }

        // The wizard images show the logo as its artwork does, on the artwork's own background (the installer's pages are
        // white, which a light logo would vanish into); a transparent source gives transparent wizard images.
        (byte R, byte G, byte B)? backdrop = report.Background.Kind == BackgroundKind.Solid
            ? (report.Background.R, report.Background.G, report.Background.B)
            : null;
        foreach (var (width, height) in WizardImageSizes)
        {
            outputs[$"Installer/Wisko-WizardImage-{width}x{height}.png"] = Resample.OnCanvas(logo, width, height, WizardImagePadding, backdrop);
        }

        foreach (var size in WizardSmallImageSizes)
        {
            outputs[$"Installer/Wisko-WizardSmallImage-{size}.png"] = Resample.OnCanvas(logo, size, size, WizardSmallImagePadding, backdrop);
        }

        var written = new List<string>();
        foreach (var (relative, picture) in outputs)
        {
            picture.SavePng(Path.Combine(brandingFolder, relative));
            written.Add(relative);
        }

        // Every size straight from the logo (not from a larger icon), for the sharpest small sizes.
        var sizes = IconFiles.IcoSizes.Concat(IconFiles.IcnsEntries.Select(e => e.Size)).Distinct().Order()
            .ToDictionary(size => size, size => Resample.OnSquare(logo, size, options.Padding));
        File.WriteAllBytes(Path.Combine(brandingFolder, "AppIcon/Wisko-AppIcon.ico"), IconFiles.WriteIco(IconFiles.IcoSizes.Select(s => sizes[s]).ToList()));
        File.WriteAllBytes(Path.Combine(brandingFolder, "AppIcon/Wisko-AppIcon.icns"), IconFiles.WriteIcns(sizes));
        written.Add("AppIcon/Wisko-AppIcon.ico");
        written.Add("AppIcon/Wisko-AppIcon.icns");

        var manifest = new
        {
            Source = new { File = Path.GetFileName(sourcePath), Sha256 = Sha256(sourceBytes), report.Width, report.Height },
            Crop = options.Crop is { } crop ? new[] { crop.X, crop.Y, crop.Width, crop.Height } : null,
            Background = report.Background.Kind == BackgroundKind.Solid
                ? FormattableString.Invariant($"#{report.Background.R:X2}{report.Background.G:X2}{report.Background.B:X2}")
                : report.Background.Kind.ToString().ToLowerInvariant(),
            options.Tolerance,
            options.Padding,
            options.RemoveEnclosed,
            Logo = new[] { b.X, b.Y, b.Width, b.Height },
            Upscaled = !report.LargeEnough,
            Outputs = written.Order(StringComparer.Ordinal).ToDictionary(path => path, path => Sha256(File.ReadAllBytes(Path.Combine(brandingFolder, path))))
        };
        File.WriteAllText(Path.Combine(brandingFolder, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        written.Add("manifest.json");
        return written;
    }

    /// <summary>Checks the icon files in <paramref name="brandingFolder"/> (sizes, transparency, halos).</summary>
    public static List<Finding> Validate(string brandingFolder)
    {
        var findings = new List<Finding>();
        void Check(string check, bool passed, string detail = "") => findings.Add(new Finding(check, passed, detail));

        RgbaImage? Png(string relative, int? size)
        {
            var path = Path.Combine(brandingFolder, relative);
            if (!File.Exists(path))
            {
                Check(relative, false, "missing");
                return null;
            }

            var image = RgbaImage.Load(path);
            var square = image.Width == image.Height && (size is null || image.Width == size);
            Check($"{relative}: {(size is null ? "square" : $"{size} x {size}")}", square, $"{image.Width} x {image.Height}");
            Check($"{relative}: transparent corners", CornersTransparent(image));
            CheckHalo(relative, image, Check);
            return image;
        }

        Png("Master/Wisko-Master-Transparent.png", null);
        Png("AppIcon/Wisko-AppIcon-1024.png", 1024);
        foreach (var size in TinySizes)
        {
            Png($"Tiny/Wisko-TinyMark-{size}.png", size);
        }

        var ico = Path.Combine(brandingFolder, "AppIcon/Wisko-AppIcon.ico");
        if (File.Exists(ico))
        {
            var entries = IconFiles.ReadIco(File.ReadAllBytes(ico));
            var sizes = entries.Select(e => e.StatedSize).ToHashSet();
            var missing = IconFiles.IcoSizes.Where(s => !sizes.Contains(s)).ToList();
            Check("Wisko-AppIcon.ico: every size Windows uses", missing.Count == 0,
                missing.Count == 0 ? string.Join(", ", sizes.Order()) : "missing " + string.Join(", ", missing));
            Check("Wisko-AppIcon.ico: each image matches its stated size",
                entries.All(e => e.Image.Width == e.StatedSize && e.Image.Height == e.StatedSize));
            foreach (var entry in entries.Where(e => e.StatedSize >= 32))
            {
                CheckHalo($"Wisko-AppIcon.ico {entry.StatedSize}", entry.Image, Check);
            }
        }
        else
        {
            Check("AppIcon/Wisko-AppIcon.ico", false, "missing");
        }

        var icns = Path.Combine(brandingFolder, "AppIcon/Wisko-AppIcon.icns");
        if (File.Exists(icns))
        {
            var entries = IconFiles.ReadIcns(File.ReadAllBytes(icns)).Where(e => e.Image is not null).ToList();
            var sizes = entries.Select(e => e.Image!.Width).ToHashSet();
            var needed = new[] { 16, 32, 64, 128, 256, 512, 1024 };
            Check("Wisko-AppIcon.icns: every size macOS uses (16 to 1024)", needed.All(sizes.Contains),
                string.Join(", ", sizes.Order()));
        }
        else
        {
            Check("AppIcon/Wisko-AppIcon.icns", false, "missing");
        }

        void Wizard(string relative, int width, int height)
        {
            var path = Path.Combine(brandingFolder, relative);
            if (!File.Exists(path))
            {
                Check(relative, false, "missing");
                return;
            }

            var image = RgbaImage.Load(path);
            Check($"{relative}: {width} x {height}", image.Width == width && image.Height == height, $"{image.Width} x {image.Height}");
            Check($"{relative}: the logo clear of the edge (a plain border)", PlainBorder(image));
        }

        foreach (var (width, height) in WizardImageSizes)
        {
            Wizard($"Installer/Wisko-WizardImage-{width}x{height}.png", width, height);
        }

        foreach (var size in WizardSmallImageSizes)
        {
            Wizard($"Installer/Wisko-WizardSmallImage-{size}.png", size, size);
        }

        return findings;
    }

    /// <summary>
    /// A picture for checking the icons by eye: every icon size at its real size and enlarged 4x (pixels kept sharp), on
    /// light, mid-grey and dark backgrounds.
    /// </summary>
    public static RgbaImage ContactSheet(string brandingFolder)
    {
        var icons = new List<RgbaImage>();
        var ico = Path.Combine(brandingFolder, "AppIcon/Wisko-AppIcon.ico");
        if (File.Exists(ico))
        {
            icons.AddRange(IconFiles.ReadIco(File.ReadAllBytes(ico)).Where(e => e.StatedSize <= 64).Select(e => e.Image));
        }

        foreach (var size in TinySizes.Where(s => s <= 64))
        {
            var path = Path.Combine(brandingFolder, $"Tiny/Wisko-TinyMark-{size}.png");
            if (File.Exists(path))
            {
                icons.Add(RgbaImage.Load(path));
            }
        }

        var backgrounds = new (byte, byte, byte)[] { (245, 245, 245), (128, 128, 128), (24, 24, 24) };
        const int gap = 16;
        var rowHeight = icons.Count == 0 ? 64 : icons.Max(i => i.Height) * 4 + gap * 2;
        var width = gap + icons.Sum(i => i.Width + i.Width * 4 + gap * 2);
        var sheet = new RgbaImage(Math.Max(width, 64), rowHeight * backgrounds.Length);
        for (var row = 0; row < backgrounds.Length; row++)
        {
            var (r, g, b) = backgrounds[row];
            for (var y = row * rowHeight; y < (row + 1) * rowHeight; y++)
            {
                for (var x = 0; x < sheet.Width; x++)
                {
                    var o = sheet.Index(x, y);
                    (sheet.Pixels[o], sheet.Pixels[o + 1], sheet.Pixels[o + 2], sheet.Pixels[o + 3]) = (r, g, b, 255);
                }
            }

            var left = gap;
            foreach (var icon in icons)
            {
                Blend(sheet, icon, left, row * rowHeight + gap, 1);
                left += icon.Width + gap;
                Blend(sheet, icon, left, row * rowHeight + gap, 4);
                left += icon.Width * 4 + gap;
            }
        }

        return sheet;
    }

    private static RgbaImage? Separate(RgbaImage image, BackgroundInfo background, GenerateOptions options) => background.Kind switch
    {
        BackgroundKind.Transparent => image,
        BackgroundKind.Solid => Matte.RemoveSolidBackground(image, background.R, background.G, background.B, options.Tolerance, options.RemoveEnclosed),
        _ => null
    };

    private static bool CornersTransparent(RgbaImage image) =>
        image.Alpha(0, 0) == 0 && image.Alpha(image.Width - 1, 0) == 0
        && image.Alpha(0, image.Height - 1) == 0 && image.Alpha(image.Width - 1, image.Height - 1) == 0;

    // Every pixel on the image's edge is the same as its top-left corner (the backdrop, or transparency).
    private static bool PlainBorder(RgbaImage image)
    {
        var corner = image.Pixels.AsSpan(0, 4);
        for (var y = 0; y < image.Height; y++)
        {
            var step = y == 0 || y == image.Height - 1 ? 1 : Math.Max(1, image.Width - 1);
            for (var x = 0; x < image.Width; x += step)
            {
                if (!image.Pixels.AsSpan(image.Index(x, y), 4).SequenceEqual(corner))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // A halo is a fringe of the old background (or of black from premultiplied scaling) around the logo: partly
    // transparent edge pixels much darker than the darkest, or much lighter than the lightest, fully covered logo pixels
    // right next to them. Against their range, not their mean: a dark area the logo encloses (kept as part of the artwork,
    // like the golden W's black gap) would otherwise make the logo's own light edge beside it read as a light halo.
    private static void CheckHalo(string name, RgbaImage image, Action<string, bool, string> check)
    {
        int edges = 0, dark = 0, light = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var a = image.Alpha(x, y);
                if (a < 40 || a > 215)
                {
                    continue;
                }

                double darkest = double.MaxValue, lightest = double.MinValue;
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < image.Width && ny < image.Height && image.Alpha(nx, ny) >= 250)
                        {
                            var inside = Luminance(image, nx, ny);
                            darkest = Math.Min(darkest, inside);
                            lightest = Math.Max(lightest, inside);
                        }
                    }
                }

                if (darkest == double.MaxValue)
                {
                    continue;
                }

                edges++;
                var edge = Luminance(image, x, y);
                if (edge < darkest - 76 && edge < darkest * 0.6)
                {
                    dark++;
                }
                else if (edge > lightest + 76)
                {
                    light++;
                }
            }
        }

        var limit = Math.Max(2, edges / 20);
        check($"{name}: no dark or light halo", edges < 20 || (dark <= limit && light <= limit),
            FormattableString.Invariant($"{edges} edge pixels, {dark} much darker and {light} much lighter than the logo next to them"));
    }

    private static double Luminance(RgbaImage image, int x, int y)
    {
        var o = image.Index(x, y);
        return 0.2126 * image.Pixels[o] + 0.7152 * image.Pixels[o + 1] + 0.0722 * image.Pixels[o + 2];
    }

    private static void Blend(RgbaImage target, RgbaImage icon, int left, int top, int scale)
    {
        for (var y = 0; y < icon.Height * scale; y++)
        {
            for (var x = 0; x < icon.Width * scale; x++)
            {
                if (left + x >= target.Width || top + y >= target.Height)
                {
                    continue;
                }

                var s = icon.Index(x / scale, y / scale);
                var d = target.Index(left + x, top + y);
                var a = icon.Pixels[s + 3] / 255d;
                for (var c = 0; c < 3; c++)
                {
                    target.Pixels[d + c] = (byte)Math.Round(icon.Pixels[s + c] * a + target.Pixels[d + c] * (1 - a));
                }
            }
        }
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>"x,y,w,h" as a rectangle.</summary>
    public static (int X, int Y, int Width, int Height) ParseRectangle(string text)
    {
        var parts = text.Split(',').Select(p => int.Parse(p.Trim(), NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length != 4 || parts[2] <= 0 || parts[3] <= 0)
        {
            throw new FormatException("--crop needs x,y,width,height");
        }

        return (parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>"#RRGGBB" as a colour.</summary>
    public static (byte R, byte G, byte B) ParseColor(string text)
    {
        var hex = text.TrimStart('#');
        if (hex.Length != 6)
        {
            throw new FormatException("--background needs a colour like #FFFFFF");
        }

        return (byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
