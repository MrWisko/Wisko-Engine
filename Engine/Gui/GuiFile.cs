using System.Globalization;
using System.Text;
using System.Text.Json;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Gui;

/// <summary>How <see cref="GuiFile.Load"/> went.</summary>
public enum GuiLoadStatus
{
    /// <summary>Read as saved.</summary>
    Loaded,

    /// <summary>No UI.json (a project without a GUI yet): an empty Canvas. Not an error.</summary>
    Missing,

    /// <summary>Read, with some values or elements repaired (see <see cref="GuiLoadReport.Repairs"/>).</summary>
    Repaired,

    /// <summary>Not a readable UI.json: an empty Canvas; the file was kept aside (<see cref="GuiLoadReport.PreservedPath"/>).</summary>
    Invalid
}

/// <summary>What <see cref="GuiFile.Load"/> found and did.</summary>
public sealed class GuiLoadReport
{
    public GuiLoadStatus Status { get; init; }

    /// <summary>Why the file could not be read (Invalid only).</summary>
    public string? Problem { get; init; }

    /// <summary>Each repair made while reading (Repaired only), in file order.</summary>
    public IReadOnlyList<string> Repairs { get; init; } = [];

    /// <summary>
    /// Where the original file was kept: renamed aside when Invalid; copied aside when a repair dropped elements (an
    /// unknown type, or deeper than allowed), so saving the repaired GUI never loses what was there.
    /// </summary>
    public string? PreservedPath { get; init; }

    /// <summary>
    /// False only when an unreadable UI.json could not be kept aside: then nothing may write UI.json this session, so
    /// the authored file is never overwritten.
    /// </summary>
    public bool SafeToSave { get; init; } = true;
}

/// <summary>
/// A project's UI.json, next to Scene.json and Atmosphere.json: FormatVersion, NextId and the Canvas with its elements as
/// nested Children arrays (so a cycle or an orphan cannot be written down). Written deterministically (fixed field
/// order, every field written; only an empty Children list is left out) and atomically (<see cref="AtomicFile"/>).
/// Reading never throws: a missing file is an empty Canvas; an unreadable one is kept aside as UI.json.invalid and gives
/// an empty Canvas; damaged values and elements are repaired one by one and reported.
/// </summary>
public static class GuiFile
{
    public const string FileName = "UI.json";
    public const int FormatVersion = 1;
    public const string InvalidSuffix = ".invalid";

    // Each GUI level is two JSON levels (the element object and its Children array) under the root, Canvas and
    // Canvas.Children, plus the arrays inside an element: 64 levels need 131. Room for that, far from unlimited.
    private const int JsonMaxDepth = 160;

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = JsonMaxDepth };

    public static GuiDocument Load(string path, out GuiLoadReport report)
    {
        if (!File.Exists(path))
        {
            report = new GuiLoadReport { Status = GuiLoadStatus.Missing };
            return new GuiDocument();
        }

        string? problem;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path), ReadOptions);
            var repairs = new List<string>();
            var document = FromJson(json.RootElement, repairs, out problem, out var lost);
            if (document is not null)
            {
                if (repairs.Count == 0)
                {
                    report = new GuiLoadReport { Status = GuiLoadStatus.Loaded };
                    return document;
                }

                foreach (var repair in repairs)
                {
                    Console.WriteLine($"[UI] {path}: {repair}");
                }

                var copy = lost ? PreserveAside(path, move: false) : null;
                report = new GuiLoadReport { Status = GuiLoadStatus.Repaired, Repairs = repairs, PreservedPath = copy };
                return document;
            }
        }
        catch (Exception ex)
        {
            // Never into project startup: whatever went wrong, the file is kept aside and the GUI starts empty.
            problem = ex.Message;
        }

        // Unreadable: keep the authored file aside (never overwrite it) and start from an empty Canvas.
        var preserved = PreserveAside(path, move: true) ?? PreserveAside(path, move: false);
        Console.WriteLine(preserved is null
            ? $"[UI] {path} could not be read ({problem}) and could not be kept aside: UI.json will not be saved this session"
            : $"[UI] {path} could not be read ({problem}); kept as {Path.GetFileName(preserved)}, starting with an empty Canvas");
        report = new GuiLoadReport { Status = GuiLoadStatus.Invalid, Problem = problem, PreservedPath = preserved, SafeToSave = preserved is not null };
        return new GuiDocument();
    }

    /// <summary>Writes UI.json atomically (<see cref="AtomicFile"/>); on failure the old file is untouched.</summary>
    public static bool TrySave(string path, GuiDocument document, out string? error) =>
        AtomicFile.TryWriteAllText(path, ToJson(document), out error);

    /// <summary>The exact file text for a document; equal documents give identical text.</summary>
    public static string ToJson(GuiDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, MaxDepth = JsonMaxDepth }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("FormatVersion", FormatVersion);
            writer.WriteNumber("NextId", document.NextId);
            writer.WriteStartObject("Canvas");
            writer.WriteStartArray("Children");
            foreach (var child in document.Canvas.Children)
            {
                WriteElement(writer, child);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// A document from a parsed UI.json, or null (with the reason) when the file as a whole is not one: not an object,
    /// a missing or unsupported FormatVersion, or no Canvas object. Damaged elements and values are repaired and
    /// listed; <paramref name="lostElements"/> tells whether any element had to be left out.
    /// </summary>
    public static GuiDocument? FromJson(JsonElement root, List<string> repairs, out string? problem, out bool lostElements)
    {
        lostElements = false;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = "not a JSON object";
            return null;
        }

        if (!root.TryGetProperty("FormatVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var formatVersion))
        {
            problem = "no FormatVersion";
            return null;
        }

        if (formatVersion != FormatVersion)
        {
            problem = $"FormatVersion {formatVersion} is not supported (this version reads {FormatVersion})";
            return null;
        }

        if (!root.TryGetProperty("Canvas", out var canvas) || canvas.ValueKind != JsonValueKind.Object)
        {
            problem = "no Canvas object";
            return null;
        }

        problem = null;
        var roots = new List<GuiElement>();
        var context = new ReadContext(repairs);
        ReadChildren(canvas, "Canvas", roots, depth: 1, context);
        lostElements = context.Lost;

        int? savedNextId = root.TryGetProperty("NextId", out var next) && next.ValueKind == JsonValueKind.Number && next.TryGetInt32(out var nextId) ? nextId : null;
        var document = new GuiDocument();
        document.Load(roots, savedNextId, out var idsRepaired, out var nextIdRepaired);
        if (idsRepaired > 0)
        {
            repairs.Add($"{idsRepaired} missing, invalid or repeated Id(s) replaced with new ones");
        }

        if (nextIdRepaired)
        {
            repairs.Add($"NextId {(savedNextId?.ToString(CultureInfo.InvariantCulture) ?? "(missing)")} set to {document.NextId}");
        }

        return document;
    }

    private static void WriteElement(Utf8JsonWriter writer, GuiElement element)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Id", element.Id);
        writer.WriteString("Type", element.Type.ToString());
        writer.WriteString("Name", element.Name);
        writer.WriteBoolean("Visible", element.Visible);
        writer.WriteNumber("ZOrder", element.ZOrder);
        WriteDim2(writer, "Position", element.Position);
        WriteDim2(writer, "Size", element.Size);
        writer.WriteStartArray("AnchorPoint");
        writer.WriteNumberValue(element.AnchorPoint.X);
        writer.WriteNumberValue(element.AnchorPoint.Y);
        writer.WriteEndArray();
        switch (element)
        {
            case GuiFrame frame:
                WriteColor(writer, "BackgroundColor", frame.BackgroundColor);
                writer.WriteNumber("Opacity", frame.Opacity);
                break;
            case GuiText text:
                writer.WriteString("Text", text.Text);
                WriteColor(writer, "TextColor", text.TextColor);
                writer.WriteNumber("FontSize", text.FontSize);
                writer.WriteString("HorizontalAlignment", text.HorizontalAlignment.ToString());
                writer.WriteString("VerticalAlignment", text.VerticalAlignment.ToString());
                break;
            case GuiButton button:
                writer.WriteBoolean("Enabled", button.Enabled);
                WriteColor(writer, "BackgroundColor", button.BackgroundColor);
                writer.WriteString("Text", button.Text);
                WriteColor(writer, "TextColor", button.TextColor);
                writer.WriteNumber("FontSize", button.FontSize);
                break;
            case GuiImage image:
                writer.WriteString("Image", image.Image);
                break;
            case GuiTextBox box:
                writer.WriteString("Text", box.Text);
                writer.WriteString("PlaceholderText", box.PlaceholderText);
                WriteColor(writer, "BackgroundColor", box.BackgroundColor);
                WriteColor(writer, "TextColor", box.TextColor);
                writer.WriteNumber("FontSize", box.FontSize);
                writer.WriteNumber("MaxLength", box.MaxLength);
                writer.WriteBoolean("ClearOnSubmit", box.ClearOnSubmit);
                break;
        }

        if (element.Children.Count > 0)
        {
            writer.WriteStartArray("Children");
            foreach (var child in element.Children)
            {
                WriteElement(writer, child);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteDim2(Utf8JsonWriter writer, string name, GuiDim2 value)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(value.X.Scale);
        writer.WriteNumberValue(value.X.Offset);
        writer.WriteNumberValue(value.Y.Scale);
        writer.WriteNumberValue(value.Y.Offset);
        writer.WriteEndArray();
    }

    private static void WriteColor(Utf8JsonWriter writer, string name, GuiColor color)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(color.R);
        writer.WriteNumberValue(color.G);
        writer.WriteNumberValue(color.B);
        writer.WriteEndArray();
    }

    private sealed class ReadContext(List<string> repairs)
    {
        public List<string> Repairs { get; } = repairs;

        public bool Lost { get; set; }
    }

    // The elements of a "Children" array (missing = none). Each is read on its own: a broken one never stops the rest.
    private static void ReadChildren(JsonElement owner, string ownerName, List<GuiElement> into, int depth, ReadContext context)
    {
        if (!owner.TryGetProperty("Children", out var children))
        {
            return;
        }

        if (children.ValueKind != JsonValueKind.Array)
        {
            context.Repairs.Add($"'{ownerName}': Children is not a list; left out");
            context.Lost = true;
            return;
        }

        foreach (var item in children.EnumerateArray())
        {
            if (ReadElement(item, ownerName, depth, context) is { } element)
            {
                into.Add(element);
            }
        }
    }

    private static GuiElement? ReadElement(JsonElement item, string parentName, int depth, ReadContext context)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            context.Repairs.Add($"a child of '{parentName}' is not an object; left out");
            context.Lost = true;
            return null;
        }

        var name = String(item, "Name", null);
        var label = name ?? "(unnamed)";
        if (depth > GuiDocument.MaxDepth)
        {
            context.Repairs.Add($"'{label}' is deeper than {GuiDocument.MaxDepth} levels; it and its children left out");
            context.Lost = true;
            return null;
        }

        var typeName = String(item, "Type", null);
        GuiElement? element = typeName is not null && Enum.TryParse<GuiElementType>(typeName, ignoreCase: false, out var type) && Enum.IsDefined(type)
            ? type switch
            {
                GuiElementType.Frame => new GuiFrame(),
                GuiElementType.Text => new GuiText(),
                GuiElementType.Image => new GuiImage(),
                GuiElementType.TextBox => new GuiTextBox(),
                _ => new GuiButton()
            }
            : null;
        if (element is null)
        {
            context.Repairs.Add($"'{label}' has unknown Type \"{typeName ?? "(missing)"}\"; it and its children left out");
            context.Lost = true;
            return null;
        }

        var repairs = context.Repairs;
        element.Id = item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var savedId) ? savedId : 0;
        element.Name = name ?? Repaired(repairs, label, "Name", element.Name);
        element.Visible = Bool(item, "Visible", element.Visible, label, repairs);
        element.ZOrder = Int(item, "ZOrder", element.ZOrder, label, repairs);
        element.Position = Dim2(item, "Position", GuiDim2.Zero, label, repairs);
        element.Size = Dim2(item, "Size", element.DefaultSize, label, repairs);
        element.AnchorPoint = Anchor(item, label, repairs);
        switch (element)
        {
            case GuiFrame frame:
                frame.BackgroundColor = Color(item, "BackgroundColor", frame.BackgroundColor, label, repairs);
                frame.Opacity = Unit(item, "Opacity", frame.Opacity, label, repairs);
                break;
            case GuiText text:
                text.Text = String(item, "Text", null) ?? Repaired(repairs, label, "Text", text.Text);
                text.TextColor = Color(item, "TextColor", text.TextColor, label, repairs);
                text.FontSize = FontSize(item, text.FontSize, label, repairs);
                text.HorizontalAlignment = EnumValue(item, "HorizontalAlignment", text.HorizontalAlignment, label, repairs);
                text.VerticalAlignment = EnumValue(item, "VerticalAlignment", text.VerticalAlignment, label, repairs);
                break;
            case GuiButton button:
                button.Enabled = Bool(item, "Enabled", button.Enabled, label, repairs);
                button.BackgroundColor = Color(item, "BackgroundColor", button.BackgroundColor, label, repairs);
                button.Text = String(item, "Text", null) ?? Repaired(repairs, label, "Text", button.Text);
                button.TextColor = Color(item, "TextColor", button.TextColor, label, repairs);
                button.FontSize = FontSize(item, button.FontSize, label, repairs);
                break;
            case GuiImage image:
                image.Image = String(item, "Image", null) ?? string.Empty;
                break;
            case GuiTextBox box:
                box.MaxLength = Int(item, "MaxLength", box.MaxLength, label, repairs);
                box.Text = String(item, "Text", null) ?? string.Empty;
                box.PlaceholderText = String(item, "PlaceholderText", null) ?? string.Empty;
                box.BackgroundColor = Color(item, "BackgroundColor", box.BackgroundColor, label, repairs);
                box.TextColor = Color(item, "TextColor", box.TextColor, label, repairs);
                box.FontSize = FontSize(item, box.FontSize, label, repairs);
                box.ClearOnSubmit = Bool(item, "ClearOnSubmit", box.ClearOnSubmit, label, repairs);
                break;
        }

        var children = new List<GuiElement>();
        ReadChildren(item, label, children, depth + 1, context);
        foreach (var child in children)
        {
            element.ChildList.Add(child);
            child.Parent = element;
        }

        return element;
    }

    private static T Repaired<T>(List<string> repairs, string label, string field, T fallback)
    {
        repairs.Add($"'{label}': {field} missing or invalid; default {fallback} used");
        return fallback;
    }

    private static string? String(JsonElement item, string name, string? fallback) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : fallback;

    private static bool Bool(JsonElement item, string name, bool fallback, string label, List<string> repairs) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : Repaired(repairs, label, name, fallback);

    private static int Int(JsonElement item, string name, int fallback, string label, List<string> repairs) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : Repaired(repairs, label, name, fallback);

    // A finite float, or null (also for a number too large for a float).
    private static float? Float(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number) && float.IsFinite(number) ? number : null;

    // Four numbers; each one that is missing or not finite takes that component of the property's default.
    private static GuiDim2 Dim2(JsonElement item, string name, GuiDim2 fallback, string label, List<string> repairs)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
        {
            return Repaired(repairs, label, name, fallback);
        }

        var parts = value.EnumerateArray().Select(Float).ToArray();
        if (parts.Any(part => part is null))
        {
            repairs.Add($"'{label}': {name} has a value that is not a finite number; that value uses its default");
        }

        return new GuiDim2(parts[0] ?? fallback.X.Scale, parts[1] ?? fallback.X.Offset, parts[2] ?? fallback.Y.Scale, parts[3] ?? fallback.Y.Offset);
    }

    private static GuiPoint Anchor(JsonElement item, string label, List<string> repairs)
    {
        if (!item.TryGetProperty("AnchorPoint", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
        {
            return Repaired(repairs, label, "AnchorPoint", default(GuiPoint));
        }

        var parts = value.EnumerateArray().Select(Float).ToArray();
        var anchor = new GuiPoint(parts[0] ?? 0f, parts[1] ?? 0f);
        var clamped = anchor.Clamped();
        if (parts.Any(part => part is null) || clamped != anchor)
        {
            repairs.Add($"'{label}': AnchorPoint ({anchor.X}, {anchor.Y}) made ({clamped.X}, {clamped.Y}) (0 to 1)");
        }

        return clamped;
    }

    private static float Unit(JsonElement item, string name, float fallback, string label, List<string> repairs)
    {
        if (!item.TryGetProperty(name, out var value) || Float(value) is not { } number)
        {
            return Repaired(repairs, label, name, fallback);
        }

        var clamped = global::System.Math.Clamp(number, 0f, 1f);
        if (clamped != number)
        {
            repairs.Add($"'{label}': {name} {number} clamped to {clamped}");
        }

        return clamped;
    }

    private static int FontSize(JsonElement item, int fallback, string label, List<string> repairs)
    {
        if (!item.TryGetProperty("FontSize", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            return Repaired(repairs, label, "FontSize", fallback);
        }

        var size = (int)global::System.Math.Clamp(global::System.Math.Round(number), GuiText.MinFontSize, GuiText.MaxFontSize);
        if (size != number)
        {
            repairs.Add($"'{label}': FontSize {number.ToString(CultureInfo.InvariantCulture)} made {size} ({GuiText.MinFontSize} to {GuiText.MaxFontSize})");
        }

        return size;
    }

    private static GuiColor Color(JsonElement item, string name, GuiColor fallback, string label, List<string> repairs)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
        {
            return Repaired(repairs, label, name, fallback);
        }

        var channels = new byte[3];
        var i = 0;
        var clamped = false;
        foreach (var channel in value.EnumerateArray())
        {
            if (channel.ValueKind != JsonValueKind.Number || !channel.TryGetDouble(out var number) || !double.IsFinite(number))
            {
                return Repaired(repairs, label, name, fallback);
            }

            var rounded = global::System.Math.Clamp(global::System.Math.Round(number), 0d, 255d);
            clamped |= rounded != number;
            channels[i++] = (byte)rounded;
        }

        if (clamped)
        {
            repairs.Add($"'{label}': {name} made 0-255 whole numbers");
        }

        return new GuiColor(channels[0], channels[1], channels[2]);
    }

    private static T EnumValue<T>(JsonElement item, string name, T fallback, string label, List<string> repairs)
        where T : struct, Enum =>
        String(item, name, null) is { } text && Enum.TryParse<T>(text, ignoreCase: false, out var value) && Enum.IsDefined(value) && !char.IsDigit(text[0])
            ? value
            : Repaired(repairs, label, name, fallback);

    // Keeps the original file under a name not in use yet (UI.json.invalid, UI.json.invalid.2, ...): renamed (move) or
    // copied. Null when that failed.
    private static string? PreserveAside(string path, bool move)
    {
        try
        {
            var target = path + InvalidSuffix;
            for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            {
                target = $"{path}{InvalidSuffix}.{n}";
            }

            if (move)
            {
                File.Move(path, target);
            }
            else
            {
                File.Copy(path, target);
            }

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[UI] Could not keep {path} aside: {ex.Message}");
            return null;
        }
    }
}
