using Wisko.BrandingAssets;

namespace Wisko.Tests.Checks;

/// <summary>
/// The icon pipeline (tools/Wisko.BrandingAssets) on synthetic test shapes (discs, rings and ellipses drawn here; no
/// logo): separating a shape from a light or dark solid background without a halo and without changing its colours,
/// keeping a transparent source as it is, refusing a thumbnail-sized or mixed-background source, enclosed areas,
/// uniform scaling, the .ico and .icns files, the installer's wizard images, that the halo check catches a bad edge (and
/// not a dark area the logo encloses), and that the files in branding/ pass validation.
/// </summary>
internal static class BrandingPipelineChecks
{
    private static readonly (byte R, byte G, byte B) Outer = (150, 110, 20);
    private static readonly (byte R, byte G, byte B) Inner = (205, 165, 45);

    public static void Run(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, background) in new[] { ("light", ((byte)255, (byte)255, (byte)255)), ("dark", ((byte)24, (byte)24, (byte)24)) })
        {
            var (image, shape) = Disc(1400, 1200, 700, 600, 560, background);
            var source = Path.Combine(dir, $"disc-{name}.png");
            image.SavePng(source);
            var inspect = Pipeline.Inspect(image, new GenerateOptions());
            report.Check($"Branding: a shape on a {name} solid background is found and large enough",
                inspect.Background.Kind == BackgroundKind.Solid && inspect.Separable && inspect.LargeEnough && !inspect.HasTransparency,
                string.Join("; ", inspect.Findings));

            var output = Path.Combine(dir, $"out-{name}");
            var written = Pipeline.Generate(source, new GenerateOptions(), output);
            var findings = Pipeline.Validate(output);
            report.Check($"Branding: every icon file and wizard image made from the {name}-background source passes validation (sizes, transparency, no halo)",
                findings.All(f => f.Passed) && written.Count == 9 + Pipeline.WizardImageSizes.Length + Pipeline.WizardSmallImageSizes.Length,
                string.Join("; ", findings.Where(f => !f.Passed).Select(f => $"{f.Check}: {f.Detail}")));

            // The installer's wizard images: the shape on its source's own background, centred, nothing transparent.
            var wizard = RgbaImage.Load(Path.Combine(output, "Installer/Wisko-WizardImage-202x386.png"));
            var smallWizard = RgbaImage.Load(Path.Combine(output, "Installer/Wisko-WizardSmallImage-58.png"));
            report.Check($"Branding: the wizard images show the shape centred on the {name} source's own background, opaque",
                new[] { wizard, smallWizard }.All(image => PixelIs(image, 0, 0, background.Item1, background.Item2, background.Item3, 255)
                    && PixelIs(image, image.Width / 2, image.Height / 2, Inner.R, Inner.G, Inner.B, 255)
                    && Enumerable.Range(0, image.Width * image.Height).All(i => image.Pixels[i * 4 + 3] == 255)));

            var master = RgbaImage.Load(Path.Combine(output, "Master/Wisko-Master-Transparent.png"));
            var bounds = inspect.Logo!.Value;
            var (alphaError, colorError, edges) = EdgeAccuracy(master, bounds.X, bounds.Y, shape);
            report.Check($"Branding: {name} background removed with the shape's own colour at its edge (no halo of the old background)",
                edges > 500 && alphaError < 0.06 && colorError < 14,
                FormattableString.Invariant($"{edges} edge pixels; mean coverage error {alphaError:F3}, mean colour error {colorError:F1}"));

            var centre = master.Index(master.Width / 2, master.Height / 2);
            report.Check($"Branding: the shape's inside keeps its exact colours ({name})",
                master.Pixels[centre] == Inner.R && master.Pixels[centre + 1] == Inner.G && master.Pixels[centre + 2] == Inner.B && master.Pixels[centre + 3] == 255);
        }

        // A source with its own transparency is used exactly as it is (cropped to the shape only).
        var (transparent, _) = Disc(1200, 1200, 600, 600, 540, null);
        var transparentSource = Path.Combine(dir, "disc-transparent.png");
        transparent.SavePng(transparentSource);
        var transparentOut = Path.Combine(dir, "out-transparent");
        Pipeline.Generate(transparentSource, new GenerateOptions(), transparentOut);
        var kept = RgbaImage.Load(Path.Combine(transparentOut, "Master/Wisko-Master-Transparent.png"));
        var keptBounds = Matte.Bounds(transparent, 8)!.Value;
        report.Check("Branding: a transparent source is kept pixel for pixel",
            SamePixels(kept, Resample.Crop(transparent, keptBounds.X, keptBounds.Y, keptBounds.Width, keptBounds.Height), keptBounds.Width - kept.Width));
        var transparentWizard = RgbaImage.Load(Path.Combine(transparentOut, "Installer/Wisko-WizardImage-202x386.png"));
        report.Check("Branding: a transparent source gives transparent wizard images (the shape centred)",
            transparentWizard.Alpha(0, 0) == 0 && transparentWizard.Alpha(transparentWizard.Width / 2, transparentWizard.Height / 2) == 255);

        // A thumbnail-sized source is reported and refused unless upscaling is asked for.
        var (small, _) = Disc(300, 300, 150, 150, 120, (255, 255, 255));
        var smallSource = Path.Combine(dir, "small.png");
        small.SavePng(smallSource);
        var smallReport = Pipeline.Inspect(small, new GenerateOptions());
        var refused = Throws(() => Pipeline.Generate(smallSource, new GenerateOptions(), Path.Combine(dir, "out-small")));
        var allowed = !Throws(() => Pipeline.Generate(smallSource, new GenerateOptions { AllowUpscale = true }, Path.Combine(dir, "out-small-allowed")));
        report.Check("Branding: a thumbnail-sized source is reported as one and refused without --allow-upscale",
            !smallReport.LargeEnough && smallReport.Findings.Any(f => f.Contains("screenshot or a thumbnail", StringComparison.Ordinal)) && refused && allowed,
            string.Join("; ", smallReport.Findings));

        // A border that is not one colour cannot be separated automatically.
        var mixed = new RgbaImage(1200, 1200);
        for (var y = 0; y < mixed.Height; y++)
        {
            for (var x = 0; x < mixed.Width; x++)
            {
                var o = mixed.Index(x, y);
                (mixed.Pixels[o], mixed.Pixels[o + 1], mixed.Pixels[o + 2], mixed.Pixels[o + 3]) = ((byte)(x * 255 / 1199), (byte)(y * 255 / 1199), 90, 255);
            }
        }

        var mixedSource = Path.Combine(dir, "mixed.png");
        mixed.SavePng(mixedSource);
        report.Check("Branding: a source whose border is not one colour is refused (crop or name the background)",
            !Pipeline.Inspect(mixed, new GenerateOptions()).Separable && Throws(() => Pipeline.Generate(mixedSource, new GenerateOptions(), Path.Combine(dir, "out-mixed"))));

        // A ring: the background-coloured hole is reported, kept by default, made transparent only when asked.
        var ring = Ring(1300, 1300, 650, 650, 600, 250, (255, 255, 255));
        var ringSource = Path.Combine(dir, "ring.png");
        ring.SavePng(ringSource);
        var ringReport = Pipeline.Inspect(ring, new GenerateOptions());
        Pipeline.Generate(ringSource, new GenerateOptions(), Path.Combine(dir, "out-ring"));
        Pipeline.Generate(ringSource, new GenerateOptions { RemoveEnclosed = true }, Path.Combine(dir, "out-ring-open"));
        var ringKept = RgbaImage.Load(Path.Combine(dir, "out-ring", "Master/Wisko-Master-Transparent.png"));
        var ringOpen = RgbaImage.Load(Path.Combine(dir, "out-ring-open", "Master/Wisko-Master-Transparent.png"));
        report.Check("Branding: an enclosed background-coloured area is reported, kept by default and removed only with --remove-enclosed",
            ringReport.Findings.Any(f => f.Contains("inside the logo", StringComparison.Ordinal))
            && ringKept.Alpha(ringKept.Width / 2, ringKept.Height / 2) == 255 && ringOpen.Alpha(ringOpen.Width / 2, ringOpen.Height / 2) == 0,
            string.Join("; ", ringReport.Findings));

        // Geometry: a wide shape stays as wide (uniform scaling onto the square icons).
        var (ellipse, _) = Ellipse(1600, 1000, 800, 500, 600, 350, (255, 255, 255));
        var ellipseSource = Path.Combine(dir, "ellipse.png");
        ellipse.SavePng(ellipseSource);
        var ellipseOut = Path.Combine(dir, "out-ellipse");
        Pipeline.Generate(ellipseSource, new GenerateOptions(), ellipseOut);
        var icon = RgbaImage.Load(Path.Combine(ellipseOut, "AppIcon/Wisko-AppIcon-1024.png"));
        var iconBounds = Matte.Bounds(icon, 128)!.Value;
        var ratio = iconBounds.Width / (double)iconBounds.Height;
        report.Check("Branding: the shape is scaled uniformly (its proportions are kept) and centred",
            Math.Abs(ratio - 1200d / 700d) < 0.03 && Math.Abs(iconBounds.X + iconBounds.Width / 2d - 512) <= 2 && Math.Abs(iconBounds.Y + iconBounds.Height / 2d - 512) <= 2,
            FormattableString.Invariant($"{iconBounds.Width} x {iconBounds.Height} (ratio {ratio:F3}, source 1.714) at ({iconBounds.X}, {iconBounds.Y})"));

        // The .ico and .icns hold every size, with the images their entries state.
        var ico = IconFiles.ReadIco(File.ReadAllBytes(Path.Combine(ellipseOut, "AppIcon/Wisko-AppIcon.ico")));
        var icns = IconFiles.ReadIcns(File.ReadAllBytes(Path.Combine(ellipseOut, "AppIcon/Wisko-AppIcon.icns")));
        report.Check("Branding: the .ico holds 16 to 256 px (PNG entries, sizes as stated)",
            ico.Select(e => e.StatedSize).SequenceEqual(IconFiles.IcoSizes) && ico.All(e => e.Png && e.Image.Width == e.StatedSize));
        report.Check("Branding: the .icns holds every macOS icon type, 16 to 1024 px",
            icns.Select(e => e.Type).SequenceEqual(IconFiles.IcnsEntries.Select(e => e.Type))
            && icns.Zip(IconFiles.IcnsEntries).All(pair => pair.First.Image?.Width == pair.Second.Size));

        // The same source and options always give the same files.
        var again = Path.Combine(dir, "out-ellipse-again");
        Pipeline.Generate(ellipseSource, new GenerateOptions(), again);
        report.Check("Branding: generating twice gives byte-identical files",
            Directory.EnumerateFiles(ellipseOut, "*", SearchOption.AllDirectories).All(path =>
                File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(again, Path.GetRelativePath(ellipseOut, path))))));

        // The halo check catches an edge darkened by a bad matte.
        var haloed = Path.Combine(dir, "haloed");
        Directory.CreateDirectory(Path.Combine(haloed, "Master"));
        var dark = RgbaImage.Load(Path.Combine(dir, "out-light", "Master/Wisko-Master-Transparent.png"));
        for (var i = 0; i < dark.Pixels.Length; i += 4)
        {
            if (dark.Pixels[i + 3] is > 0 and < 255)
            {
                (dark.Pixels[i], dark.Pixels[i + 1], dark.Pixels[i + 2]) = (10, 8, 4);
            }
        }

        dark.SavePng(Path.Combine(haloed, "Master/Wisko-Master-Transparent.png"));
        var haloFinding = Pipeline.Validate(haloed).Single(f => f.Check.StartsWith("Master/Wisko-Master-Transparent.png: no dark", StringComparison.Ordinal));
        report.Check("Branding: validation catches a dark halo", !haloFinding.Passed, haloFinding.Detail);

        // And a light one: the disc separated from the dark background, its edge whitened.
        var lightHaloed = Path.Combine(dir, "light-haloed");
        Directory.CreateDirectory(Path.Combine(lightHaloed, "Master"));
        var light = RgbaImage.Load(Path.Combine(dir, "out-dark", "Master/Wisko-Master-Transparent.png"));
        for (var i = 0; i < light.Pixels.Length; i += 4)
        {
            if (light.Pixels[i + 3] is > 0 and < 255)
            {
                (light.Pixels[i], light.Pixels[i + 1], light.Pixels[i + 2]) = (250, 250, 250);
            }
        }

        light.SavePng(Path.Combine(lightHaloed, "Master/Wisko-Master-Transparent.png"));
        var lightFinding = Pipeline.Validate(lightHaloed).Single(f => f.Check.StartsWith("Master/Wisko-Master-Transparent.png: no dark", StringComparison.Ordinal));
        report.Check("Branding: validation catches a light halo", !lightFinding.Passed, lightFinding.Detail);

        // A dark area the logo encloses, kept as part of the artwork, right beside the logo's light edge (as the golden W's
        // black gap is in its small icons): the edge has the logo's own colour, so it is not a halo.
        var enclosing = Path.Combine(dir, "dark-inside");
        Directory.CreateDirectory(Path.Combine(enclosing, "Master"));
        var square = new RgbaImage(64, 64);
        for (var y = 8; y < 56; y++)
        {
            for (var x = 7; x < 56; x++)
            {
                var o = square.Index(x, y);
                (square.Pixels[o], square.Pixels[o + 1], square.Pixels[o + 2], square.Pixels[o + 3]) =
                    x == 9 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)80, x == 7 ? (byte)128 : (byte)255);
            }
        }

        square.SavePng(Path.Combine(enclosing, "Master/Wisko-Master-Transparent.png"));
        var enclosedFinding = Pipeline.Validate(enclosing).Single(f => f.Check.StartsWith("Master/Wisko-Master-Transparent.png: no dark", StringComparison.Ordinal));
        report.Check("Branding: a dark area inside the logo beside its edge is not taken for a halo", enclosedFinding.Passed, enclosedFinding.Detail);

        // The files in branding/ (made from the golden W artwork) pass validation.
        if (FindBrandingFolder() is { } branding)
        {
            var current = Pipeline.Validate(branding);
            report.Check("Branding: every file in branding/ passes validation (sizes, transparency, no halo, wizard images)",
                current.All(f => f.Passed),
                $"{current.Count(f => f.Passed)} of {current.Count} pass" +
                (current.Any(f => !f.Passed) ? "; failing: " + string.Join("; ", current.Where(f => !f.Passed).Select(f => $"{f.Check}: {f.Detail}")) : ""));
        }
        else
        {
            TestReport.Info("branding/ not found next to the test runner: its files were not validated");
        }
    }

    private static bool PixelIs(RgbaImage image, int x, int y, byte r, byte g, byte b, byte a)
    {
        var o = image.Index(x, y);
        return image.Pixels[o] == r && image.Pixels[o + 1] == g && image.Pixels[o + 2] == b && image.Pixels[o + 3] == a;
    }

    // A disc: Inner colour inside, an Outer-coloured ring (the outer 12%), 4 x 4 supersampled edges; on a background
    // colour, or transparent when background is null. The shape function returns coverage and colour at a pixel.
    private static (RgbaImage Image, Func<int, int, (double Coverage, (byte, byte, byte) Color)> Shape) Disc(
        int width, int height, double cx, double cy, double radius, (byte R, byte G, byte B)? background)
    {
        (double, (byte, byte, byte)) Shape(int x, int y)
        {
            var covered = 0;
            for (var sy = 0; sy < 4; sy++)
            {
                for (var sx = 0; sx < 4; sx++)
                {
                    double dx = x + (sx + 0.5) / 4 - cx, dy = y + (sy + 0.5) / 4 - cy;
                    if (dx * dx + dy * dy <= radius * radius) covered++;
                }
            }

            double ex = x + 0.5 - cx, ey = y + 0.5 - cy;
            return (covered / 16d, Math.Sqrt(ex * ex + ey * ey) > radius * 0.88 ? Outer : Inner);
        }

        return (Draw(width, height, Shape, background), Shape);
    }

    private static (RgbaImage Image, Func<int, int, (double, (byte, byte, byte))> Shape) Ellipse(
        int width, int height, double cx, double cy, double rx, double ry, (byte R, byte G, byte B) background)
    {
        (double, (byte, byte, byte)) Shape(int x, int y)
        {
            var covered = 0;
            for (var sy = 0; sy < 4; sy++)
            {
                for (var sx = 0; sx < 4; sx++)
                {
                    double dx = (x + (sx + 0.5) / 4 - cx) / rx, dy = (y + (sy + 0.5) / 4 - cy) / ry;
                    if (dx * dx + dy * dy <= 1) covered++;
                }
            }

            return (covered / 16d, Inner);
        }

        return (Draw(width, height, Shape, background), Shape);
    }

    private static RgbaImage Ring(int width, int height, double cx, double cy, double outer, double inner, (byte R, byte G, byte B) background) =>
        Draw(width, height, (x, y) =>
        {
            var covered = 0;
            for (var sy = 0; sy < 4; sy++)
            {
                for (var sx = 0; sx < 4; sx++)
                {
                    double dx = x + (sx + 0.5) / 4 - cx, dy = y + (sy + 0.5) / 4 - cy;
                    var d2 = dx * dx + dy * dy;
                    if (d2 <= outer * outer && d2 >= inner * inner) covered++;
                }
            }

            return (covered / 16d, Inner);
        }, background);

    private static RgbaImage Draw(int width, int height, Func<int, int, (double Coverage, (byte R, byte G, byte B) Color)> shape, (byte R, byte G, byte B)? background)
    {
        var image = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (coverage, color) = shape(x, y);
                var o = image.Index(x, y);
                if (background is { } b)
                {
                    image.Pixels[o] = (byte)Math.Round(color.R * coverage + b.R * (1 - coverage));
                    image.Pixels[o + 1] = (byte)Math.Round(color.G * coverage + b.G * (1 - coverage));
                    image.Pixels[o + 2] = (byte)Math.Round(color.B * coverage + b.B * (1 - coverage));
                    image.Pixels[o + 3] = 255;
                }
                else if (coverage > 0)
                {
                    (image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2]) = color;
                    image.Pixels[o + 3] = (byte)Math.Round(coverage * 255);
                }
            }
        }

        return image;
    }

    // Mean errors over the partly covered pixels: separated coverage against the true one, and separated colour against
    // the shape's true colour (where the true coverage is at least a quarter, so the colour is meaningful).
    private static (double Alpha, double Color, int Edges) EdgeAccuracy(RgbaImage master, int offsetX, int offsetY,
        Func<int, int, (double Coverage, (byte R, byte G, byte B) Color)> shape)
    {
        double alphaError = 0, colorError = 0;
        int edges = 0, colored = 0;
        for (var y = 0; y < master.Height; y++)
        {
            for (var x = 0; x < master.Width; x++)
            {
                var (coverage, color) = shape(x + offsetX, y + offsetY);
                if (coverage is <= 0 or >= 1)
                {
                    continue;
                }

                var o = master.Index(x, y);
                edges++;
                alphaError += Math.Abs(master.Pixels[o + 3] / 255d - coverage);
                if (coverage >= 0.25 && master.Pixels[o + 3] > 0)
                {
                    colored++;
                    colorError += (Math.Abs(master.Pixels[o] - color.R) + Math.Abs(master.Pixels[o + 1] - color.G) + Math.Abs(master.Pixels[o + 2] - color.B)) / 3d;
                }
            }
        }

        return (alphaError / Math.Max(1, edges), colorError / Math.Max(1, colored), edges);
    }

    private static bool SamePixels(RgbaImage square, RgbaImage logo, int _)
    {
        // The master is the logo centred on a square of its larger side: compare the logo's pixels where it sits.
        var left = (square.Width - logo.Width) / 2;
        var top = (square.Height - logo.Height) / 2;
        for (var y = 0; y < logo.Height; y++)
        {
            for (var x = 0; x < logo.Width; x++)
            {
                var a = square.Index(left + x, top + y);
                var b = logo.Index(x, y);
                if (logo.Pixels[b + 3] == 0 && square.Pixels[a + 3] == 0)
                {
                    continue;
                }

                for (var c = 0; c < 4; c++)
                {
                    if (square.Pixels[a + c] != logo.Pixels[b + c]) return false;
                }
            }
        }

        return true;
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string? FindBrandingFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "branding");
            if (Directory.Exists(Path.Combine(candidate, "AppIcon")))
            {
                return candidate;
            }
        }

        return null;
    }
}
