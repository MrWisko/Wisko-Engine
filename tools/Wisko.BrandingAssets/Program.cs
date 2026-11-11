using System.Globalization;
using Wisko.BrandingAssets;

// Wisko's icon files from the logo artwork. See branding/README.md.
//   inspect  <source.png> [--crop x,y,w,h] [--background #RRGGBB] [--tolerance n]
//   generate <source.png> --out <branding folder> [--crop ...] [--background ...] [--tolerance n] [--padding f] [--allow-upscale] [--remove-enclosed]
//   validate <branding folder> [--sheet <contact-sheet.png>]
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: inspect <source.png> [options] | generate <source.png> --out <folder> [options] | validate <folder> [--sheet <png>]");
    return 2;
}

string? Value(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

try
{
    var options = new GenerateOptions
    {
        Crop = Value("--crop") is { } crop ? Pipeline.ParseRectangle(crop) : null,
        Background = Value("--background") is { } color ? Pipeline.ParseColor(color) : null,
        Tolerance = Value("--tolerance") is { } tolerance ? int.Parse(tolerance, CultureInfo.InvariantCulture) : Matte.DefaultTolerance,
        Padding = Value("--padding") is { } padding ? double.Parse(padding, CultureInfo.InvariantCulture) : 0.04,
        AllowUpscale = args.Contains("--allow-upscale"),
        RemoveEnclosed = args.Contains("--remove-enclosed")
    };

    switch (args[0])
    {
        case "inspect":
        {
            var report = Pipeline.Inspect(RgbaImage.Load(args[1]), options);
            foreach (var finding in report.Findings)
            {
                Console.WriteLine(finding);
            }

            Console.WriteLine(report.Separable && report.LargeEnough
                ? "ready: every icon size can be made from this source without upscaling"
                : report.Separable ? "usable only with --allow-upscale (larger icons would be blurred)" : "not usable as it is");
            return report.Separable ? 0 : 1;
        }

        case "generate":
        {
            var output = Value("--out") ?? throw new FormatException("generate needs --out <branding folder>");
            foreach (var path in Pipeline.Generate(args[1], options, output))
            {
                Console.WriteLine($"wrote {path}");
            }

            return 0;
        }

        case "validate":
        {
            var findings = Pipeline.Validate(args[1]);
            foreach (var finding in findings)
            {
                Console.WriteLine($"[{(finding.Passed ? "PASS" : "FAIL")}] {finding.Check}{(finding.Detail.Length > 0 ? " -- " + finding.Detail : "")}");
            }

            var sheet = Value("--sheet") ?? Path.Combine(Path.GetTempPath(), "wisko-branding-contact-sheet.png");
            Pipeline.ContactSheet(args[1]).SavePng(sheet);
            Console.WriteLine($"contact sheet (check small sizes by eye): {sheet}");
            return findings.All(f => f.Passed) ? 0 : 1;
        }

        default:
            Console.Error.WriteLine($"unknown command {args[0]}");
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
