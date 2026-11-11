using Wisko.Engine.Gui;
using Wisko.Engine.Input;
using Wisko.Engine.Text;
using Wisko.Engine.UI;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The game GUI while playing: the session's <see cref="GuiRuntime"/> (owned by Play Mode) laid out in the game view
/// and drawn over the 3D world, under all editor chrome. Edit mode draws no game GUI. Text uses a font library of its
/// own (its own glyph atlas, created with the backend and kept for the window's lifetime), so game text never fills or
/// clears the editor's atlas; when the game atlas overflows it is cleared before the next frame's GUI is drawn.
/// </summary>
public sealed partial class Renderer
{
    private readonly GuiRenderer _guiRenderer = new();
    private FontLibrary? _guiFonts;
    private IUiRenderer? _guiUi;

    // Game GUI pictures (Image elements): project images by their full path, loaded when the set in use changes.
    private IUiImageRenderer? _guiImages;
    private IUiTextureCache? _guiTextures;
    private readonly HashSet<string> _guiImagePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _guiImagesWanted = new(StringComparer.Ordinal); // reused every frame
    private GameGuiImages? _gameGuiImages;

    /// <summary>The project's authored GUI (Play draws a copy of it).</summary>
    internal GuiDocument Gui { get; private set; } = new();

    /// <summary>What the last frame's game GUI drew (diagnostics).</summary>
    internal GuiDrawStats LastGuiDraw { get; private set; }

    /// <summary>Entering a project (or a new session): the GUI that Play will copy. Not an undo step.</summary>
    internal void LoadGui(GuiDocument document)
    {
        Gui = document;
        _playMode.GuiSource = document;
        _guiDrag = null;
        _guiSelection.SelectedId = null;
        _guiFieldEdit = null;
        _guiSlider = null;
        _guiTreeDrag = null;
    }

    // The game view: where the 3D world is seen (below the tab strip, above Output, between the side columns).
    private GuiRect GameViewRect
    {
        get
        {
            var view = EditorRect;
            return new GuiRect(view.X, view.Y, view.Width, view.Height);
        }
    }

    private void InitializeRuntimeGui(IUiBackend ui)
    {
        _guiFonts = ui.CreateFontLibrary();
        _guiUi = ui.CreateRenderer(_guiFonts);
        _guiImages = ui.CreateImageRenderer();
        _guiTextures = ui.CreateTextureCache();
        _gameGuiImages = new GameGuiImages(this);
        _guiPanel.Initialize(ui);
        _playMode.GuiTextMeasurer = MeasureGameText;
    }

    // UI.MeasureText and the default chat's line wrapping: the game GUI's own font, as it draws.
    private GuiTextSize MeasureGameText(string text, int fontSize)
    {
        if (_guiUi is not { } ui)
        {
            return new GuiTextSize(text.Length * fontSize * 0.5f, fontSize * 1.25f);
        }

        var scale = GuiRenderer.TextScale(fontSize);
        return new GuiTextSize(ui.MeasureWidth(text, scale), ui.MeasureLineHeight(scale));
    }

    private void ShutdownRuntimeGui()
    {
        _guiTextures?.Dispose();
        _guiTextures = null;
        _guiImages?.Dispose();
        _guiImages = null;
        _guiUi?.Dispose();
        _guiUi = null;
        _guiPanel.Dispose();
        _guiFonts?.Dispose(); // after the renderer that uses it
        _guiFonts = null;
    }

    // Play only, and only when the game view is showing (not a script tab).
    private void RenderRuntimeGui(float dpiScale)
    {
        LastGuiDraw = default;
        if (!_playMode.IsPlaying || _playMode.Gui is not { } gui || _guiUi is null || _guiFonts is null)
        {
            return;
        }

        // A safe frame boundary: nothing of this frame uses the game atlas yet. Logged at clears 1, 2, 4, 8, ... (text
        // that needs more than the whole atlas would clear it every frame).
        if (_guiFonts.AtlasOverflowed)
        {
            _guiFonts.ClearAtlas();
            if (int.IsPow2(_guiFonts.AtlasClears))
            {
                Console.WriteLine($"[GUI] Game glyph atlas was full: cleared (clear #{_guiFonts.AtlasClears})");
            }
        }

        var canvas = GameViewRect;
        var entries = gui.Update(canvas);
        RefreshGuiImages(entries);
        LastGuiDraw = _guiRenderer.Draw(_guiUi, entries, canvas, dpiScale, _width, _height, gui, images: _gameGuiImages);
    }

    // The pictures Image elements show, loaded (or let go) when the set in use changes: only files inside the project's
    // Assets folder are ever read.
    private void RefreshGuiImages(IReadOnlyList<GuiLayoutEntry> entries)
    {
        if (_guiTextures is null)
        {
            return;
        }

        var wanted = _guiImagesWanted;
        wanted.Clear();
        foreach (var entry in entries)
        {
            if (entry.Element is GuiImage { Image.Length: > 0 } image && ResolveGuiImage(image.Image) is { } path)
            {
                wanted.Add(path);
            }
        }

        if (!wanted.SetEquals(_guiImagePaths))
        {
            _guiImagePaths.Clear();
            _guiImagePaths.UnionWith(wanted);
            _guiTextures.Refresh(wanted);
        }
    }

    // A project-relative picture path ("Assets/...") as a full path inside this project, or null.
    private string? ResolveGuiImage(string image) =>
        ProjectRoot is { } root && image.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
        && Assets.ProjectAssetPaths.TryResolve(root, image, out var full) ? full : null;

    private sealed class GameGuiImages(Renderer renderer) : IGuiImageSource
    {
        public bool TryDraw(string image, GuiRect rect, GuiRect clip, int screenWidth, int screenHeight)
        {
            // A camera feed, rendered this frame (bottom-up rows: drawn with V flipped).
            if (image.StartsWith(Scripting.GuiScriptBridge.CameraFeedPrefix, StringComparison.Ordinal))
            {
                if (renderer._guiImages is not { } feedImages
                    || !renderer.TryGetCameraFeed(image[Scripting.GuiScriptBridge.CameraFeedPrefix.Length..], out var feed))
                {
                    return false;
                }

                DrawFlipped(feedImages, feed, rect, clip, screenWidth, screenHeight);
                return true;
            }

            if (renderer._guiImages is not { } images || renderer._guiTextures is not { } textures
                || renderer.ResolveGuiImage(image) is not { } path || !textures.TryGet(path, out var texture))
            {
                return false;
            }

            UiImages.DrawClipped(images, texture, rect.X, rect.Y, rect.Width, rect.Height, new UiClip(clip.X, clip.Y, clip.Width, clip.Height),
                screenWidth, screenHeight);
            return true;
        }

        // As UiImages.DrawClipped, for a texture whose first row is the bottom of the picture.
        private static void DrawFlipped(IUiImageRenderer images, UiTexture texture, GuiRect rect, GuiRect clip, int screenWidth, int screenHeight)
        {
            if (rect.Width <= 0f || rect.Height <= 0f)
            {
                return;
            }

            var left = MathF.Max(rect.X, clip.X);
            var top = MathF.Max(rect.Y, clip.Y);
            var right = MathF.Min(rect.X + rect.Width, clip.X + clip.Width);
            var bottom = MathF.Min(rect.Y + rect.Height, clip.Y + clip.Height);
            if (right <= left || bottom <= top)
            {
                return;
            }

            images.DrawRegion(texture, left, top, right - left, bottom - top,
                (left - rect.X) / rect.Width, 1f - (top - rect.Y) / rect.Height, (right - rect.X) / rect.Width, 1f - (bottom - rect.Y) / rect.Height,
                screenWidth, screenHeight);
        }
    }

    // Play: after the editor chrome had the pointer, before the 3D view. A press on a visible button (enabled, or a
    // disabled one that only blocks) is used up here, so it never selects a Part behind it; anything else carries on.
    private void HandleRuntimeGuiInput(InputManager input)
    {
        if (!_playMode.IsPlaying || _playMode.Gui is not { } gui)
        {
            return;
        }

        var x = input.MouseX;
        var y = input.MouseY;
        var canvas = GameViewRect;
        var inView = !_toolbarConsumedClick && !_toolbar.IsMenuOpen && canvas.Contains(x, y)
            && !_toolbar.IsOverToolbarUi(x, y, _width) && !_worldExplorer.BlocksViewportInput(x, y, _width, _height)
            && !_colorPanel.IsOverPanel(x, y, _width, _height) && !IsOverEditorChrome(x, y);
        if (gui.UpdatePointer(canvas, x, y, inView, input.LeftMousePressed, input.IsLeftMouseHeld, input.LeftMouseReleased))
        {
            _toolbarConsumedClick = true;
        }
    }

    // A game TextBox had the keyboard this frame (it typed, or Enter submitted it).
    private bool _runtimeTextBoxKeyboard;

    // Keys reach scripts (Play) without the keyboard: what a TextBox gets while it has focus.
    private readonly Engine.Scripting.ScriptInputState _typingScriptInput = new();

    /// <summary>A game TextBox has the keyboard: the editor's shortcuts and the camera keys, and the scripts' keys, are off.</summary>
    private bool RuntimeTextBoxOwnsKeyboard => _runtimeTextBoxKeyboard || (_playMode.IsPlaying && _playMode.Gui?.FocusedTextBoxId is not null);

    // Play, the game view showing: the focused TextBox (if any) types this frame's characters and editing keys.
    private void HandleRuntimeTextBox(InputManager input)
    {
        if (_playMode.IsPlaying && ActiveDocument is null && _playMode.Gui is { IsDisposed: false } gui)
        {
            _runtimeTextBoxKeyboard = gui.HandleKeyboard(input.TextInputCharacters, input.KeyEvents, ClipboardText);
        }
    }

    // This frame's script input with every key up (the mouse as it is): scripts see no typing as game keys.
    private Engine.Scripting.ScriptInputState TypingScriptInput(InputManager input)
    {
        _typingScriptInput.CopyFrom(input.ScriptInput);
        _typingScriptInput.ClearKeys();
        return _typingScriptInput;
    }

    // A script tab covers the game: its GUI gets no pointer.
    private void CancelRuntimeGuiPointer() => _playMode.Gui?.CancelPointer();

    private void OnGuiButtonClicked(int id) =>
        Console.WriteLine($"[GUI] Clicked '{_playMode.Gui?.Document.Find(id)?.Name}' (#{id})");
}
