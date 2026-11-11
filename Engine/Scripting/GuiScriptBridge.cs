using System.Globalization;
using Wisko.Engine.Gui;

namespace Wisko.Engine.Scripting;

/// <summary>
/// The host side of a script's UI: the Play session's <see cref="GuiRuntime"/> (its own copy of the authored GUI; the
/// authored document is never reachable from here). Elements are addressed by Id and looked up again on every call, so
/// nothing here holds an element between calls. Values are checked the way the GUI model checks them (FontSize 8 to 72,
/// finite Scale + Offset); a value that cannot be used is refused with a short message for the script. Changes go
/// through the elements' own setters, so the document's Version moves and the layout is rebuilt on its next use; after a
/// change that can make a button unclickable (Visible, Enabled, Position, Size) a hovered or held one is let go at once.
/// </summary>
internal sealed class GuiScriptBridge(GuiRuntime? gui)
{
    /// <summary>Longest Name a script can give an element it creates (UI.Create).</summary>
    public const int MaxNameLength = 100;

    /// <summary>An Image whose Image is "camera:Name" shows that named camera's feed (Cameras).</summary>
    public const string CameraFeedPrefix = "camera:";

    private bool Usable => gui is { IsDisposed: false };

    /// <summary>Where the GUI was last laid out, in window pixels (the game view); empty before the first layout.</summary>
    public GuiRect CanvasRect => Usable ? gui!.Layout.Canvas : default;

    /// <summary>The Canvas (Id 0) or the element with this Id in this session's GUI; null when there is none.</summary>
    // A picture path as UI.json and the renderer take it: inside Assets/, a PNG or JPEG, normalized ("Assets/..." with /).
    private static bool TryImagePath(string path, out string normalized)
    {
        normalized = string.Empty;

        // "camera:<Name>" shows a named camera's feed (Cameras: camera:CreateFeed); no file is involved.
        if (path.StartsWith(CameraFeedPrefix, StringComparison.Ordinal))
        {
            var name = path[CameraFeedPrefix.Length..];
            if (name.Length is 0 or > 64 || name.Any(char.IsControl))
            {
                return false;
            }

            normalized = path;
            return true;
        }

        if (!Assets.ProjectAssetPaths.TryNormalize(path, out var assetPath) || !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(assetPath);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = assetPath;
        return true;
    }

    public GuiNode? ResolveNode(long id) =>
        !Usable ? null : id == GuiCanvas.CanvasId ? gui!.Document.Canvas : Resolve(id);

    /// <summary>
    /// A new Frame, Text or Button (its type's defaults; named <paramref name="name"/>, default its type) as the last
    /// child of <paramref name="parent"/>, with a new Id (never one used before). Null when done, else why not.
    /// </summary>
    public string? Create(string type, string? name, GuiNode parent, int maxElements, out GuiElement? created)
    {
        created = null;
        if (!Usable)
        {
            return "UI.Create is not available here (no Play GUI)";
        }

        GuiElement element = type switch
        {
            "Frame" => new GuiFrame(),
            "Text" => new GuiText(),
            "Button" => new GuiButton(),
            "Image" => new GuiImage(),
            "TextBox" => new GuiTextBox(),
            _ => null!
        };
        if (element is null)
        {
            return $"UI.Create: unknown type \"{(type.Length > 40 ? type[..40] + "..." : type)}\" (use \"Frame\", \"Text\", \"Button\", \"Image\" or \"TextBox\")";
        }

        if (name is not null && name.Length > MaxNameLength)
        {
            return $"UI.Create: the name must be at most {MaxNameLength} characters";
        }

        if (gui!.Document.Count >= maxElements)
        {
            return $"UI.Create: the GUI already has {maxElements:N0} elements; destroy some first";
        }

        if (GuiDocument.DepthOf(parent) + 1 > GuiDocument.MaxDepth)
        {
            return $"UI.Create: GUI elements can be at most {GuiDocument.MaxDepth} levels deep";
        }

        if (name is not null)
        {
            element.Name = name;
        }

        created = gui.Document.Add(element, parent);
        return null;
    }

    /// <summary>Removes the element and everything inside it (their Ids are never used again); returns the removed Ids.</summary>
    public List<int> Destroy(GuiElement element)
    {
        var removed = GuiDocument.Descendants(element).Select(child => child.Id).Prepend(element.Id).ToList();
        gui!.Document.Remove(element);
        gui.ReleaseUnclickable();
        return removed;
    }

    /// <summary>Moves the element (with its subtree) to the end of <paramref name="parent"/>'s children; null when done, else why not.</summary>
    public string? SetParent(GuiElement element, GuiNode parent)
    {
        if (!gui!.Document.TryReparent(element, parent, out var reason))
        {
            return $"SetParent: {reason}";
        }

        gui.ReleaseUnclickable();
        return null;
    }

    /// <summary>The first element with exactly this name in tree order (parents before children, siblings in saved order).</summary>
    public GuiElement? Find(string name)
    {
        if (!Usable)
        {
            return null;
        }

        foreach (var element in gui!.Document.Elements)
        {
            if (string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>Every element with exactly this name, in tree order.</summary>
    public IEnumerable<GuiElement> FindAll(string name)
    {
        if (!Usable)
        {
            yield break;
        }

        foreach (var element in gui!.Document.Elements)
        {
            if (string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                yield return element;
            }
        }
    }

    /// <summary>
    /// "HUD/Menu/Play": each name a direct child of the one before, from the Canvas (a leading "/" is allowed); among
    /// siblings with that name the first in saved order. Null when any step is missing or empty.
    /// </summary>
    public GuiElement? FindPath(string path)
    {
        if (!Usable)
        {
            return null;
        }

        var names = (path.StartsWith('/') ? path[1..] : path).Split('/');
        GuiNode node = gui!.Document.Canvas;
        foreach (var name in names)
        {
            if (name.Length == 0 || node.Children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal)) is not { } next)
            {
                return null;
            }

            node = next;
        }

        return node as GuiElement;
    }

    /// <summary>The element with this Id; null for anything that is not a whole number naming an element (the Canvas included).</summary>
    public GuiElement? FindById(double id) =>
        Usable && double.IsFinite(id) && id == global::System.Math.Floor(id) && id is > 0 and <= GuiDocument.MaxId ? gui!.Document.Find((int)id) : null;

    public object? Get(GuiElement element, string key, out string? error)
    {
        error = null;
        switch (key)
        {
            case "Id":
                return (long)element.Id;
            case "Name":
                return element.Name;
            case "Type":
                return element.Type.ToString();
            case "Visible":
                return element.Visible;
            case "Text" when element is GuiText text:
                return text.Text;
            case "Text" when element is GuiButton button:
                return button.Text;
            case "Text" when element is GuiTextBox box:
                return box.Text;
            case "PlaceholderText" when element is GuiTextBox box:
                return box.PlaceholderText;
            case "MaxLength" when element is GuiTextBox box:
                return (long)box.MaxLength;
            case "ClearOnSubmit" when element is GuiTextBox box:
                return box.ClearOnSubmit;
            case "Focused" when element is GuiTextBox box:
                return gui!.FocusedTextBoxId == box.Id;
            case "FontSize" when element is GuiTextBox box:
                return (long)box.FontSize;
            case "Color" when element is GuiTextBox box:
                return Hex(box.BackgroundColor);
            case "TextColor" when element is GuiTextBox box:
                return Hex(box.TextColor);
            case "FontSize" when element is GuiText text:
                return (long)text.FontSize;
            case "FontSize" when element is GuiButton button:
                return (long)button.FontSize;
            case "Enabled" when element is GuiButton button:
                return button.Enabled;
            case "ZOrder":
                return (long)element.ZOrder;
            case "Color" when element is GuiFrame frame:
                return Hex(frame.BackgroundColor);
            case "Color" when element is GuiButton button:
                return Hex(button.BackgroundColor);
            case "Opacity" when element is GuiFrame frame:
                return (double)frame.Opacity;
            case "TextColor" when element is GuiText text:
                return Hex(text.TextColor);
            case "TextColor" when element is GuiButton button:
                return Hex(button.TextColor);
            case "HorizontalAlignment" when element is GuiText text:
                return text.HorizontalAlignment.ToString();
            case "VerticalAlignment" when element is GuiText text:
                return text.VerticalAlignment.ToString();
            case "Image" when element is GuiImage image:
                return image.Image;
            default:
                error = NoMember(element, key);
                return null;
        }
    }

    /// <summary>Applies a property write; null when done, else why not (nothing changed).</summary>
    public string? Set(GuiElement element, string key, object? value)
    {
        string? error = null;
        switch (key)
        {
            case "Id" or "Name" or "Type" or "Focused":
                return $"{key} is read-only";
            case "Visible":
                if (value is bool visible)
                {
                    element.Visible = visible;
                }
                else
                {
                    error = "Visible must be true or false";
                }

                break;
            case "Enabled" when element is GuiButton button:
                if (value is bool enabled)
                {
                    button.Enabled = enabled;
                }
                else
                {
                    error = "Enabled must be true or false";
                }

                break;
            case "Text" when element is GuiText or GuiButton or GuiTextBox:
                if (value is string text)
                {
                    SetText(element, text);
                }
                else
                {
                    error = "Text must be a string";
                }

                break;
            case "FontSize" when element is GuiText or GuiButton or GuiTextBox:
                // Whole pixels, 8 to 72 (as UI.json reads a size: rounded, then clamped).
                if (value is double size && double.IsFinite(size))
                {
                    SetFontSize(element, (int)global::System.Math.Clamp(global::System.Math.Round(size), GuiText.MinFontSize, GuiText.MaxFontSize));
                }
                else
                {
                    error = "FontSize must be a finite number";
                }

                break;
            case "ZOrder":
                if (value is double z && double.IsFinite(z) && z == global::System.Math.Floor(z) && z is >= int.MinValue and <= int.MaxValue)
                {
                    element.ZOrder = (int)z;
                }
                else
                {
                    error = "ZOrder must be a whole number";
                }

                break;
            case "Color" when element is GuiFrame or GuiButton or GuiTextBox:
            case "TextColor" when element is GuiText or GuiButton or GuiTextBox:
                if (value is string hex && TryParseHex(hex, out var color))
                {
                    if (key == "TextColor")
                    {
                        if (element is GuiButton labelled)
                        {
                            labelled.TextColor = color;
                        }
                        else if (element is GuiTextBox typed)
                        {
                            typed.TextColor = color;
                        }
                        else
                        {
                            ((GuiText)element).TextColor = color;
                        }
                    }
                    else if (element is GuiFrame frame)
                    {
                        frame.BackgroundColor = color;
                    }
                    else if (element is GuiTextBox field)
                    {
                        field.BackgroundColor = color;
                    }
                    else
                    {
                        ((GuiButton)element).BackgroundColor = color;
                    }
                }
                else
                {
                    error = $"{key} must be \"#RRGGBB\" (six hex digits)";
                }

                break;
            case "Opacity" when element is GuiFrame frame:
                // 0 to 1, as the model keeps it (a finite value outside is clamped).
                if (value is double opacity && double.IsFinite(opacity))
                {
                    frame.Opacity = (float)global::System.Math.Clamp(opacity, 0d, 1d);
                }
                else
                {
                    error = "Opacity must be a finite number (0 to 1)";
                }

                break;
            case "HorizontalAlignment" when element is GuiText label:
                if (value is string h && Enum.TryParse<GuiHorizontalAlignment>(h, ignoreCase: false, out var horizontal) && Enum.IsDefined(horizontal) && h == horizontal.ToString())
                {
                    label.HorizontalAlignment = horizontal;
                }
                else
                {
                    error = "HorizontalAlignment must be \"Left\", \"Center\" or \"Right\"";
                }

                break;
            case "VerticalAlignment" when element is GuiText label:
                if (value is string v && Enum.TryParse<GuiVerticalAlignment>(v, ignoreCase: false, out var vertical) && Enum.IsDefined(vertical) && v == vertical.ToString())
                {
                    label.VerticalAlignment = vertical;
                }
                else
                {
                    error = "VerticalAlignment must be \"Top\", \"Middle\" or \"Bottom\"";
                }

                break;
            case "PlaceholderText" when element is GuiTextBox box:
                if (value is string placeholder)
                {
                    box.PlaceholderText = placeholder;
                }
                else
                {
                    error = "PlaceholderText must be a string";
                }

                break;
            case "MaxLength" when element is GuiTextBox box:
                if (value is double max && double.IsFinite(max) && max == global::System.Math.Floor(max) && max is >= 1 and <= GuiTextBox.MaxMaxLength)
                {
                    box.MaxLength = (int)max;
                }
                else
                {
                    error = $"MaxLength must be a whole number from 1 to {GuiTextBox.MaxMaxLength}";
                }

                break;
            case "ClearOnSubmit" when element is GuiTextBox box:
                if (value is bool clear)
                {
                    box.ClearOnSubmit = clear;
                }
                else
                {
                    error = "ClearOnSubmit must be true or false";
                }

                break;
            case "Image" when element is GuiImage picture:
                // A project picture: "Assets/..." ending in .png, .jpg or .jpeg (normalized), or "" for none.
                if (value is string path && (path.Length == 0 || TryImagePath(path, out path)))
                {
                    picture.Image = path;
                }
                else
                {
                    error = "Image must be a project picture path (\"Assets/Images/Coin.png\": a .png, .jpg or .jpeg inside Assets), a camera feed (\"camera:Security\") or \"\"";
                }

                break;
            default:
                return NoMember(element, key);
        }

        if (error is null && key is "Visible" or "Enabled")
        {
            gui!.ReleaseUnclickable();
        }

        return error;
    }

    /// <summary>SetPosition / SetSize: the element's Position or Size (Scale + Offset per axis), exactly as given.</summary>
    public string? SetRect(GuiElement element, string method, double xScale, double xOffset, double yScale, double yOffset)
    {
        if (!Fits(xScale) || !Fits(xOffset) || !Fits(yScale) || !Fits(yOffset))
        {
            return $"{method} expects four finite numbers (xScale, xOffset, yScale, yOffset)";
        }

        var value = new GuiDim2((float)xScale, (float)xOffset, (float)yScale, (float)yOffset);
        if (method == "SetSize")
        {
            element.Size = value;
        }
        else
        {
            element.Position = value;
        }

        gui!.ReleaseUnclickable();
        return null;
    }

    /// <summary>GetPosition / GetSize: XScale, XOffset, YScale, YOffset of the element's Position or Size.</summary>
    public static GuiDim2 GetRect(GuiElement element, string which) => which == "Size" ? element.Size : element.Position;

    /// <summary>SetAnchor: the anchor point, each finite value kept within 0..1 (as the model keeps it).</summary>
    public string? SetAnchor(GuiElement element, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return "SetAnchor expects two finite numbers (x, y), each 0 to 1";
        }

        element.AnchorPoint = new GuiPoint((float)global::System.Math.Clamp(x, 0d, 1d), (float)global::System.Math.Clamp(y, 0d, 1d));
        gui!.ReleaseUnclickable();
        return null;
    }

    /// <summary>UI.MeasureText: <paramref name="text"/>'s width at <paramref name="fontSize"/> pixels and one line's height; null without a GUI.</summary>
    public GuiTextSize? MeasureText(string text, int fontSize) => Usable ? gui!.MeasureText(text, fontSize) : null;

    /// <summary>Input.IsTyping: a game TextBox has the keyboard.</summary>
    public bool Typing => Usable && gui!.FocusedTextBoxId is not null;

    /// <summary>textBox:Focus(): gives it the keyboard (null when it has it, else why not).</summary>
    public string? Focus(GuiElement element) =>
        element is GuiTextBox ? gui!.Focus(element.Id, byScript: true) : NoMember(element, "Focus");

    /// <summary>textBox:Release(): it lets go of the keyboard (nothing when it does not have it).</summary>
    public string? Release(GuiElement element)
    {
        if (element is not GuiTextBox)
        {
            return NoMember(element, "Release");
        }

        if (gui!.FocusedTextBoxId == element.Id)
        {
            gui.ReleaseFocus(submitted: false);
        }

        return null;
    }

    /// <summary>The element, when <paramref name="id"/> still names one in this session's GUI.</summary>
    public GuiElement? Resolve(long id) => Usable && id is > 0 and <= GuiDocument.MaxId ? gui!.Document.Find((int)id) : null;

    public static string NoMember(GuiElement element, string key) => $"GUI element '{element.Name}' is a {element.Type} and has no {key} property";

    private static bool Fits(double value) => double.IsFinite(value) && global::System.Math.Abs(value) <= float.MaxValue;

    // Canonical colour text: "#RRGGBB", uppercase.
    private static string Hex(GuiColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // "#RRGGBB" or "RRGGBB", exactly six hex digits (either case).
    private static bool TryParseHex(string text, out GuiColor color)
    {
        color = default;
        var hex = text.StartsWith('#') ? text[1..] : text;
        if (hex.Length != 6 || !hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = new GuiColor((byte)(value >> 16), (byte)(value >> 8 & 0xFF), (byte)(value & 0xFF));
        return true;
    }

    private static void SetText(GuiElement element, string text)
    {
        if (element is GuiText label)
        {
            label.Text = text;
        }
        else if (element is GuiButton button)
        {
            button.Text = text;
        }
        else if (element is GuiTextBox box)
        {
            box.Text = text;
        }
    }

    private static void SetFontSize(GuiElement element, int size)
    {
        if (element is GuiText label)
        {
            label.FontSize = size;
        }
        else if (element is GuiButton button)
        {
            button.FontSize = size;
        }
        else if (element is GuiTextBox box)
        {
            box.FontSize = size;
        }
    }
}
