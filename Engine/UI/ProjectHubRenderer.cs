using System.Globalization;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

internal enum ProjectHubView
{
    Home,
    Projects,
    History,
    Templates
}

internal enum CardHitRegion
{
    None,
    Open,
    ChooseCustomThumbnail,
    ResetCustomThumbnail,
    More,
    Delete
}

internal enum ProjectHubCommand
{
    None,
    NewWorld,
    OpenProject,
    ChooseCustomThumbnail,
    ResetCustomThumbnail,

    /// <summary>Delete Project was confirmed in the dialog (never sent on the first click).</summary>
    DeleteProject
}

/// <summary>
/// How a History world stands, the only two statuses the Hub shows: its project exists (Hub Active), or it does not
/// (Deleted: deleted through Wisko, or no longer in the Projects folder, e.g. removed by hand).
/// </summary>
internal enum HubHistoryState
{
    HubActive,
    Deleted
}

/// <summary>
/// One History world as the Hub shows it: its record, its project while it exists, and its kept preview.
/// <see cref="DeletedOutsideWisko"/>: Deleted only because its folder is gone from the Projects folder (its record is not
/// marked Deleted, so it is Hub Active again if the same project comes back).
/// </summary>
internal sealed record HubHistoryItem(ProjectHistoryRecord Record, ProjectInfo? Project, HubHistoryState State, string? KeptThumbnailPath,
    bool DeletedOutsideWisko = false);

/// <summary>
/// The Project Hub: an icon-only left rail (Home, Projects, History) and a main area.
/// <list type="bullet">
/// <item>Home: New World (which leads to the Classic / Testing World template choice) and the 9 most recently opened
/// worlds, 3 x 3.</item>
/// <item>Projects: every world in the Projects folder, most recently opened first, 3 columns, scrolling.</item>
/// <item>History: every world the user has had (History records), each with its status: Hub Active (opens like a
/// project) or Deleted (shown muted, never opened).</item>
/// </list>
/// Cards share one size on every page (three rows fit the window on Home when they can), with the large 16:9 preview
/// (CustomThumbnail.png when set, else Thumbnail.png, else the world-type icon; a deleted world's kept preview), the
/// name and the world-type row. An active card opens its project; its "..." menu has Choose Custom Image..., Reset Image
/// and Delete Project...; a hover trash icon and the hover x on a custom preview are shortcuts. Deleting always asks first
/// (<see cref="ConfirmDialog"/>, which takes all input while open). Draws nothing it was not given.
/// </summary>
internal sealed class ProjectHubRenderer : IDisposable
{
    /// <summary>Most worlds Home shows (3 x 3); Projects has them all.</summary>
    public const int HomeLimit = 9;

    private const int Columns = 3;
    private const float RailWidth = 64f;
    private const float RailButtonSize = 40f;
    private const float RailButtonX = 12f;
    private const float RailHomeY = 20f;
    private const float RailProjectsY = 68f;
    private const float RailHistoryY = 116f;
    private const float ContentTop = 44f;
    private const float HeadingScale = 2.8f;
    private const float SubtitleScale = 1.8f;
    private const float SectionScale = 1.75f;
    private const float NewWorldWidth = 196f;
    private const float NewWorldHeight = 40f;
    private const float HomeGridTop = ContentTop + 88f;
    private const float PageGridTop = ContentTop + 76f;
    private const float StatusBottomMargin = 36f;
    private const float GridBottomMargin = StatusBottomMargin + 12f;
    private const float ScrollStep = 48f;
    private const float MinCardWidth = 140f;
    private const float MaxCardWidth = 300f;
    private const float CompactBelow = 200f;
    private const float CardPadding = 8f;
    private const float TextArea = 58f;
    private const float CompactTextArea = 47f;
    private const float MoreButtonWidth = 26f;
    private const float TrashButtonWidth = 22f;
    private const float ButtonHeight = 20f;
    private const float ResetButtonRadius = 8f;
    private const float NameScale = 1.95f;
    private const float CompactNameScale = 1.75f;
    private const float MetaScale = 1.6f;
    private const float CompactMetaScale = 1.45f;
    private const float BadgeScale = 1.3f;
    private const float TemplateCardPadding = 12f;
    private const float TemplateTextHeight = 74f;
    private const float TemplateCardWidth = TemplatePreviews.Width + TemplateCardPadding * 2f;
    private const float TemplateCardGap = 20f;
    private const float TemplateGridTop = ContentTop + 76f;
    private const float MinTemplateScale = 0.45f;
    private const float BackButtonSize = 32f;
    private const float MenuWidth = 204f;
    private const float MenuItemHeight = 28f;
    private const float MenuSeparatorHeight = 9f;
    private const float MenuPadding = 4f;

    private static readonly Vector3 Background = new(0.10f, 0.105f, 0.115f);
    private static readonly Vector3 Rail = new(0.075f, 0.08f, 0.09f);
    private static readonly Vector3 RailSeparator = new(0.15f, 0.16f, 0.18f);
    private static readonly Vector3 RailActive = new(0.17f, 0.18f, 0.21f);
    private static readonly Vector3 RailHover = new(0.13f, 0.14f, 0.16f);
    private static readonly Vector3 IconIdle = new(0.55f, 0.58f, 0.63f);
    private static readonly Vector3 IconActive = new(0.92f, 0.94f, 0.97f);
    private static readonly Vector3 Accent = new(0.20f, 0.42f, 0.78f);
    private static readonly Vector3 AccentHover = new(0.25f, 0.49f, 0.88f);
    private static readonly Vector3 Heading = new(0.94f, 0.95f, 0.97f);
    private static readonly Vector3 Card = new(0.135f, 0.145f, 0.165f);
    private static readonly Vector3 CardHover = new(0.175f, 0.185f, 0.21f);
    private static readonly Vector3 CardInactive = new(0.118f, 0.124f, 0.14f);
    private static readonly Vector3 ThumbnailPlaceholder = new(0.09f, 0.095f, 0.105f);
    private static readonly Vector3 NameText = new(0.92f, 0.94f, 0.97f);
    private static readonly Vector3 InactiveName = new(0.68f, 0.70f, 0.74f);
    private static readonly Vector3 Muted = new(0.55f, 0.58f, 0.63f);
    private static readonly Vector3 Error = new(0.95f, 0.45f, 0.42f);
    private static readonly Vector3 ButtonHover = new(0.22f, 0.23f, 0.27f);
    private static readonly Vector3 TrashHover = new(0.42f, 0.16f, 0.16f);
    private static readonly Vector3 ResetFill = new(0.24f, 0.25f, 0.29f);
    private static readonly Vector3 ResetHover = new(0.36f, 0.37f, 0.42f);
    private static readonly Vector3 ScrollThumb = new(0.30f, 0.32f, 0.36f);
    private static readonly Vector3 MenuPanel = new(0.15f, 0.16f, 0.185f);
    private static readonly Vector3 MenuBorder = new(0.25f, 0.27f, 0.31f);
    private static readonly Vector3 MenuHover = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 DestructiveText = new(0.96f, 0.50f, 0.47f);
    private static readonly Vector3 ActiveBadgeFill = new(0.12f, 0.27f, 0.18f);
    private static readonly Vector3 ActiveBadgeText = new(0.58f, 0.88f, 0.66f);
    private static readonly Vector3 DeletedBadgeFill = new(0.21f, 0.215f, 0.235f);
    private static readonly Vector3 DeletedBadgeText = new(0.72f, 0.74f, 0.78f);

    private static readonly WorldTemplate[] Templates = [WorldTemplate.Classic, WorldTemplate.Testing];
    private static readonly string[] MenuLabels = ["Choose Custom Image…", "Reset Image", "", "Delete Project…"];

    private readonly ConfirmDialog _confirm = new();
    private readonly Dictionary<ProjectHubView, float> _scroll = new();

    // The template pictures loaded so far, by template and density (null: missing or unreadable, the icon is drawn).
    private readonly Dictionary<(WorldTemplate Template, int Density), UiTexture?> _templatePreviews = new();
    private IUiBackend _backend = null!;

    // The template card the keyboard is on (arrows / Tab move it, Enter creates it), and whether to draw its ring (only
    // once the keyboard was used: a mouse user sees the hover).
    private int _templateFocus;
    private bool _templateFocusVisible;
    private IUiRenderer _screenUi = null!;
    private IUiImageRenderer _images = null!;
    private IUiTextureCache _thumbnails = null!;
    private IUiTextureCache _customThumbnails = null!;
    private IUiTextureCache _keptThumbnails = null!;
    private IReadOnlyList<ProjectInfo> _projects = Array.Empty<ProjectInfo>();
    private IReadOnlyList<HubHistoryItem> _history = Array.Empty<HubHistoryItem>();
    private ProjectHubView _view = ProjectHubView.Home;
    private string? _status;
    private float _mouseX = -1f;
    private float _mouseY = -1f;
    private int _screenWidth = 1280;
    private int _screenHeight = 720;

    // The open "..." menu: its project and the button it opened from (null: closed).
    private ProjectInfo? _menuProject;
    private UiBox _menuAnchor;

    // Set by the delete confirmation's choice, handed out by the Update that ran it.
    private ProjectInfo? _confirmedDelete;

    /// <summary>The page shown (diagnostics and checks).</summary>
    internal ProjectHubView View => _view;

    internal bool IsMenuOpen => _menuProject is not null;

    internal bool IsConfirmOpen => _confirm.IsOpen;

    /// <summary>Creates the drawing objects from the active backend (OpenGL or bgfx).</summary>
    public void Initialize(IUiBackend ui)
    {
        _backend = ui;
        _screenUi = ui.CreateRenderer();
        _images = ui.CreateImageRenderer();
        _thumbnails = ui.CreateTextureCache();
        _customThumbnails = ui.CreateTextureCache();
        _keptThumbnails = ui.CreateTextureCache();
        _confirm.Initialize(ui);
    }

    /// <summary>
    /// Shows these worlds (projects most recent first, History records) and reloads only images that changed. With
    /// <paramref name="showHome"/> the Hub opens on Home with every page scrolled to the top; otherwise the page stays.
    /// Needs the GL context (texture loads).
    /// </summary>
    public void SetData(IReadOnlyList<ProjectInfo> projects, IReadOnlyList<HubHistoryItem> history, bool showHome)
    {
        _projects = projects;
        _history = history;
        _menuProject = null;
        if (showHome)
        {
            _view = ProjectHubView.Home;
            _scroll.Clear();
        }

        _thumbnails.Refresh(projects.Select(project => project.ThumbnailPath));
        _customThumbnails.Refresh(projects.Select(project => project.CustomThumbnailPath));
        _keptThumbnails.Refresh(history.Select(item => item.KeptThumbnailPath).OfType<string>());
    }

    public void ShowStatus(string? status) => _status = status;

    /// <summary>Escape: closes the delete question (as Cancel) or the "..." menu, or leaves the template choice.</summary>
    public bool TryConsumeEscape()
    {
        if (_confirm.IsOpen)
        {
            _confirm.Cancel();
            return true;
        }

        if (_menuProject is not null)
        {
            _menuProject = null;
            return true;
        }

        if (_view != ProjectHubView.Templates)
        {
            return false;
        }

        _view = ProjectHubView.Home;
        return true;
    }

    public ProjectHubCommand Update(
        InputManager input,
        int screenWidth,
        int screenHeight,
        out ProjectInfo? project,
        out WorldTemplate template)
    {
        project = null;
        template = WorldTemplate.Classic;
        _mouseX = input.MouseX;
        _mouseY = input.MouseY;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;

        // The delete question takes every click and key (nothing reaches the rail or the cards behind it).
        if (_confirm.IsOpen)
        {
            _confirm.Update(input, screenWidth, screenHeight);
            if (_confirmedDelete is { } confirmed)
            {
                _confirmedDelete = null;
                project = confirmed;
                return ProjectHubCommand.DeleteProject;
            }

            return ProjectHubCommand.None;
        }

        if (_menuProject is { } menuProject)
        {
            return UpdateMenu(input, menuProject, out project);
        }

        if (input.ScrollDelta != 0f && _view != ProjectHubView.Templates && GridViewport().Contains(_mouseX, _mouseY))
        {
            ScrollBy(-input.ScrollDelta * ScrollStep);
        }

        if (_view == ProjectHubView.Templates && UpdateTemplateKeys(input, out var chosen))
        {
            template = chosen;
            _view = ProjectHubView.Home;
            return ProjectHubCommand.NewWorld;
        }

        if (!input.LeftMousePressed)
        {
            return ProjectHubCommand.None;
        }

        foreach (var (view, button) in RailButtons)
        {
            if (button.Contains(_mouseX, _mouseY))
            {
                _view = view;
                return ProjectHubCommand.None;
            }
        }

        if (_view == ProjectHubView.Templates)
        {
            if (BackButton.Contains(_mouseX, _mouseY))
            {
                _view = ProjectHubView.Home;
                return ProjectHubCommand.None;
            }

            for (var i = 0; i < Templates.Length; i++)
            {
                if (GetTemplateCard(i).Contains(_mouseX, _mouseY))
                {
                    template = Templates[i];
                    _view = ProjectHubView.Home;
                    return ProjectHubCommand.NewWorld;
                }
            }

            return ProjectHubCommand.None;
        }

        if (_view == ProjectHubView.Home && NewWorldButton().Contains(_mouseX, _mouseY))
        {
            _view = ProjectHubView.Templates;
            _status = null;
            _templateFocus = 0;
            _templateFocusVisible = false;
            return ProjectHubCommand.None;
        }

        var index = HitCard();
        if (index < 0 || CardProject(index) is not { } target)
        {
            return ProjectHubCommand.None;
        }

        var card = GetCard(index);
        switch (HitCardRegion(card, Metrics(), _mouseX, _mouseY, interactive: true, HasCustomThumbnail(target)))
        {
            case CardHitRegion.ResetCustomThumbnail:
                project = target;
                return ProjectHubCommand.ResetCustomThumbnail;
            case CardHitRegion.More:
                _menuProject = target;
                _menuAnchor = GetMoreButton(card, Metrics());
                return ProjectHubCommand.None;
            case CardHitRegion.Delete:
                AskDelete(target);
                return ProjectHubCommand.None;
            case CardHitRegion.Open:
                project = target;
                return ProjectHubCommand.OpenProject;
            default:
                return ProjectHubCommand.None;
        }
    }

    public void Render(int screenWidth, int screenHeight, float dpiScale)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _screenUi.BeginBatch(dpiScale);
        _screenUi.DrawRect(0f, 0f, screenWidth, screenHeight, Background);
        DrawRail(screenHeight);

        if (_view == ProjectHubView.Templates)
        {
            DrawTemplateChoice(dpiScale);
            DrawStatus();
            _screenUi.Flush(screenWidth, screenHeight);
            DrawTemplatePictures(dpiScale);
            return;
        }

        DrawPageHeader();
        DrawStatus();
        var viewport = GridViewport();
        var clip = new UiClip(viewport.X, viewport.Y, viewport.Width, viewport.Height);
        var (first, last) = VisibleRange();
        _screenUi.SetClip(clip);
        for (var i = first; i <= last; i++)
        {
            DrawCard(i);
        }

        _screenUi.SetClip(null);
        _screenUi.Flush(screenWidth, screenHeight);

        // Previews go on top of their placeholder rects (the batch above has been flushed), cut to the list's area.
        for (var i = first; i <= last; i++)
        {
            if (TryGetPreview(i, out var preview))
            {
                var rect = GetThumbnailRect(GetCard(i), Metrics());
                UiImages.DrawClipped(_images, preview, rect.X, rect.Y, rect.Width, rect.Height, clip, screenWidth, screenHeight);
            }
        }

        // Over the previews: inactive worlds' previews muted, the hover x on a custom preview, the scroll bar, the menu.
        _screenUi.BeginBatch(dpiScale);
        _screenUi.SetClip(clip);
        for (var i = first; i <= last; i++)
        {
            DrawPreviewOverlay(i);
        }

        _screenUi.SetClip(null);
        DrawScrollBar();
        DrawMenu();
        _screenUi.Flush(screenWidth, screenHeight);

        _confirm.Render(screenWidth, screenHeight, dpiScale);
    }

    public void Dispose()
    {
        foreach (var texture in _templatePreviews.Values)
        {
            if (texture is { } loaded)
            {
                _backend.DestroyTexture(loaded);
            }
        }

        _templatePreviews.Clear();
        _confirm.Dispose();
        _thumbnails.Dispose();
        _customThumbnails.Dispose();
        _keptThumbnails.Dispose();
        _images.Dispose();
        _screenUi.Dispose();
    }

    // ---------------------------------------------------------------------------------------------- layout

    /// <summary>One card size for every page; on Home three rows fit the window when the cards can stay usable.</summary>
    internal readonly record struct GridMetrics(float Left, float CardWidth, float CardHeight, float Gap, float ThumbnailHeight, bool Compact)
    {
        public float GridWidth => CardWidth * Columns + Gap * (Columns - 1);
    }

    internal static float ContentLeft(int screenWidth) => RailWidth + (screenWidth < 800 ? 24f : 48f);

    internal static GridMetrics MetricsFor(int screenWidth, int screenHeight)
    {
        var left = ContentLeft(screenWidth);
        var available = MathF.Max(0f, screenWidth - left - (screenWidth < 800 ? 20f : 40f));
        var gap = available < 620f ? 10f : 16f;
        var byWidth = MathF.Max(1f, (available - gap * (Columns - 1)) / Columns);
        var rowHeight = (screenHeight - HomeGridTop - GridBottomMargin - gap * 2f) / 3f;

        float Fit(float textArea) =>
            MathF.Min(MathF.Min(MathF.Max(MathF.Min(byWidth, (rowHeight - CardPadding - textArea) * 16f / 9f + CardPadding * 2f), MinCardWidth), MaxCardWidth), byWidth);

        var width = Fit(TextArea);
        var compact = width < CompactBelow;
        if (compact)
        {
            width = Fit(CompactTextArea);
        }

        width = MathF.Floor(width);
        var thumbnailHeight = MathF.Round((width - CardPadding * 2f) * 9f / 16f);
        var height = CardPadding + thumbnailHeight + (compact ? CompactTextArea : TextArea);
        return new GridMetrics(left, width, height, gap, thumbnailHeight, compact);
    }

    private GridMetrics Metrics() => MetricsFor(_screenWidth, _screenHeight);

    private float GridTop => _view == ProjectHubView.Home ? HomeGridTop : PageGridTop;

    /// <summary>The area the current page's cards scroll in (cards outside it are cut away and cannot be clicked).</summary>
    internal UiBox GridViewport()
    {
        var metrics = Metrics();
        var top = GridTop;
        return new UiBox(metrics.Left - 2f, top, metrics.GridWidth + 4f, MathF.Max(0f, _screenHeight - GridBottomMargin - top));
    }

    private int ItemCount => _view switch
    {
        ProjectHubView.Home => global::System.Math.Min(_projects.Count, HomeLimit),
        ProjectHubView.Projects => _projects.Count,
        ProjectHubView.History => _history.Count,
        _ => 0
    };

    private float ContentHeight
    {
        get
        {
            var rows = (ItemCount + Columns - 1) / Columns;
            var metrics = Metrics();
            return rows == 0 ? 0f : rows * metrics.CardHeight + (rows - 1) * metrics.Gap;
        }
    }

    private float MaxScroll => MathF.Max(0f, ContentHeight - GridViewport().Height);

    internal float ScrollOffset => MathF.Min(_scroll.GetValueOrDefault(_view), MaxScroll);

    private void ScrollBy(float delta) => _scroll[_view] = global::System.Math.Clamp(ScrollOffset + delta, 0f, MaxScroll);

    internal UiBox GetCard(int index)
    {
        var metrics = Metrics();
        var column = index % Columns;
        var row = index / Columns;
        return new UiBox(
            metrics.Left + column * (metrics.CardWidth + metrics.Gap),
            GridTop + row * (metrics.CardHeight + metrics.Gap) - ScrollOffset,
            metrics.CardWidth,
            metrics.CardHeight);
    }

    // The cards at least partly inside the viewport.
    private (int First, int Last) VisibleRange()
    {
        var count = ItemCount;
        var metrics = Metrics();
        var viewport = GridViewport();
        var rowStride = metrics.CardHeight + metrics.Gap;
        var firstRow = (int)MathF.Max(0f, MathF.Floor(ScrollOffset / rowStride));
        var lastRow = (int)MathF.Floor((ScrollOffset + viewport.Height) / rowStride);
        return (global::System.Math.Min(firstRow * Columns, count), global::System.Math.Min((lastRow + 1) * Columns, count) - 1);
    }

    private static UiBox GetThumbnailRect(UiBox card, GridMetrics metrics) =>
        new(card.X + CardPadding, card.Y + CardPadding, card.Width - CardPadding * 2f, metrics.ThumbnailHeight);

    private static float NameY(UiBox card, GridMetrics metrics) =>
        card.Y + CardPadding + metrics.ThumbnailHeight + (metrics.Compact ? 6f : 8f);

    private static float MetaY(UiBox card, GridMetrics metrics) => NameY(card, metrics) + (metrics.Compact ? 21f : 26f);

    // "..." at the right of the name row: the card's menu.
    private static UiBox GetMoreButton(UiBox card, GridMetrics metrics) =>
        new(card.X + card.Width - CardPadding - MoreButtonWidth, NameY(card, metrics) - 1f, MoreButtonWidth, ButtonHeight);

    // Trash left of "...", shown while the card is hovered: Delete Project (asks first).
    private static UiBox GetTrashButton(UiBox card, GridMetrics metrics)
    {
        var more = GetMoreButton(card, metrics);
        return new UiBox(more.X - 4f - TrashButtonWidth, more.Y, TrashButtonWidth, ButtonHeight);
    }

    // Inside the preview's top-right corner, so the x clearly belongs to the image it removes.
    private static (float X, float Y) ResetButtonCenter(UiBox preview) => (preview.X + preview.Width - 12f, preview.Y + 12f);

    /// <summary>
    /// Maps a point inside a card to exactly one action. An active card: the x on a custom preview resets it, "..." opens
    /// the menu, the trash asks to delete, anything else opens the project. A card with no project (a Deleted History
    /// world) does nothing.
    /// </summary>
    internal static CardHitRegion HitCardRegion(UiBox card, GridMetrics metrics, float x, float y, bool interactive, bool hasCustomThumbnail)
    {
        if (!card.Contains(x, y) || !interactive)
        {
            return CardHitRegion.None;
        }

        if (hasCustomThumbnail && IsOverResetButton(GetThumbnailRect(card, metrics), x, y))
        {
            return CardHitRegion.ResetCustomThumbnail;
        }

        if (GetMoreButton(card, metrics).Contains(x, y))
        {
            return CardHitRegion.More;
        }

        return GetTrashButton(card, metrics).Contains(x, y) ? CardHitRegion.Delete : CardHitRegion.Open;
    }

    private static bool IsOverResetButton(UiBox preview, float x, float y)
    {
        var (cx, cy) = ResetButtonCenter(preview);
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy <= (ResetButtonRadius + 2f) * (ResetButtonRadius + 2f);
    }

    private int HitCard()
    {
        if (!GridViewport().Contains(_mouseX, _mouseY))
        {
            return -1;
        }

        var (first, last) = VisibleRange();
        for (var i = first; i <= last; i++)
        {
            if (GetCard(i).Contains(_mouseX, _mouseY))
            {
                return i;
            }
        }

        return -1;
    }

    // The project behind card i: Home and Projects always have one; a History card only while its world is Hub Active.
    private ProjectInfo? CardProject(int index) => _view switch
    {
        ProjectHubView.Home or ProjectHubView.Projects => _projects[index],
        ProjectHubView.History => _history[index].State == HubHistoryState.HubActive ? _history[index].Project : null,
        _ => null
    };

    private HubHistoryItem? CardHistory(int index) => _view == ProjectHubView.History ? _history[index] : null;

    // "Has a custom image" = CustomThumbnail.png exists and decoded; a corrupt file counts as none.
    private bool HasCustomThumbnail(ProjectInfo project) => _customThumbnails.TryGet(project.CustomThumbnailPath, out _);

    private bool TryGetPreview(int index, out UiTexture preview)
    {
        if (CardProject(index) is { } project)
        {
            return _customThumbnails.TryGet(project.CustomThumbnailPath, out preview) || _thumbnails.TryGet(project.ThumbnailPath, out preview);
        }

        preview = default;
        return CardHistory(index)?.KeptThumbnailPath is { } kept && _keptThumbnails.TryGet(kept, out preview);
    }

    private static IEnumerable<(ProjectHubView View, UiBox Button)> RailButtons =>
    [
        (ProjectHubView.Home, new UiBox(RailButtonX, RailHomeY, RailButtonSize, RailButtonSize)),
        (ProjectHubView.Projects, new UiBox(RailButtonX, RailProjectsY, RailButtonSize, RailButtonSize)),
        (ProjectHubView.History, new UiBox(RailButtonX, RailHistoryY, RailButtonSize, RailButtonSize))
    ];

    internal static UiBox RailButton(ProjectHubView view) => RailButtons.First(entry => entry.View == view).Button;

    // On Home's heading row, at the right edge of the grid.
    internal UiBox NewWorldButton()
    {
        var metrics = Metrics();
        var right = MathF.Max(metrics.Left + metrics.GridWidth, metrics.Left + NewWorldWidth + 140f);
        return new UiBox(right - NewWorldWidth, ContentTop - 2f, NewWorldWidth, NewWorldHeight);
    }

    private UiBox BackButton => new(ContentLeft(_screenWidth) - 6f, ContentTop - 2f, BackButtonSize, BackButtonSize);

    private UiBox GetTemplateCard(int index) => TemplateCardFor(index, TemplateLayoutFor(_screenWidth, _screenHeight));

    /// <summary>
    /// The template cards: side by side when that fits, else stacked, whichever lets them be larger; scaled down (never
    /// up) to fit the window, the picture keeping its 16:9 shape.
    /// </summary>
    internal readonly record struct TemplateLayout(float Left, float Top, int Columns, float Scale)
    {
        public float CardWidth => TemplateCardWidth * Scale;

        public float CardHeight => TemplateCardPadding * Scale + TemplatePreviews.Height * Scale + TemplateTextHeight;

        public float Gap => TemplateCardGap;
    }

    /// <summary>
    /// A note for the New World screen when this editor cannot draw what the template pictures show (set by the
    /// application from the renderer's capabilities; null or returning null: no note).
    /// </summary>
    internal Func<string?>? TemplatePreviewNote { get; set; }

    internal static TemplateLayout TemplateLayoutFor(int screenWidth, int screenHeight)
    {
        var left = ContentLeft(screenWidth);
        var availableWidth = MathF.Max(1f, screenWidth - left - 24f);
        var availableHeight = MathF.Max(1f, screenHeight - TemplateGridTop - GridBottomMargin);
        var count = Templates.Length;
        float ScaleFor(int columns)
        {
            var rows = (count + columns - 1) / columns;
            var widthScale = (availableWidth - TemplateCardGap * (columns - 1)) / (TemplateCardWidth * columns);
            var picture = TemplateCardPadding + TemplatePreviews.Height;
            var heightScale = (availableHeight - TemplateCardGap * (rows - 1) - TemplateTextHeight * rows) / (picture * rows);
            return global::System.Math.Clamp(MathF.Min(widthScale, heightScale), MinTemplateScale, 1f);
        }

        var side = ScaleFor(count);
        var stacked = ScaleFor(1);
        return stacked > side + 0.01f ? new TemplateLayout(left, TemplateGridTop, 1, stacked) : new TemplateLayout(left, TemplateGridTop, count, side);
    }

    private static UiBox TemplateCardFor(int index, TemplateLayout layout)
    {
        var column = index % layout.Columns;
        var row = index / layout.Columns;
        return new UiBox(layout.Left + column * (layout.CardWidth + layout.Gap), layout.Top + row * (layout.CardHeight + layout.Gap),
            layout.CardWidth, layout.CardHeight);
    }

    // The picture's place in its card (16:9, the card's padding around it).
    private static UiBox TemplatePictureRect(UiBox card, float scale) =>
        new(card.X + TemplateCardPadding * scale, card.Y + TemplateCardPadding * scale, TemplatePreviews.Width * scale, TemplatePreviews.Height * scale);

    // Arrows and Tab move between the cards, Enter creates the one the keyboard is on; the mouse over a card moves the
    // keyboard there too (so Enter means the card under the pointer). True with the template to create.
    private bool UpdateTemplateKeys(InputManager input, out WorldTemplate chosen)
    {
        chosen = WorldTemplate.Classic;
        for (var i = 0; i < Templates.Length; i++)
        {
            if (GetTemplateCard(i).Contains(_mouseX, _mouseY) && _templateFocus != i)
            {
                _templateFocus = i;
            }
        }

        var step = input.ArrowRightPressed || input.ArrowDownPressed || (input.TabPressed && !input.ShiftHeld) ? 1
            : input.ArrowLeftPressed || input.ArrowUpPressed || (input.TabPressed && input.ShiftHeld) ? -1
            : 0;
        if (step != 0)
        {
            _templateFocus = (_templateFocus + step + Templates.Length) % Templates.Length;
            _templateFocusVisible = true;
        }

        if (!input.EnterPressed)
        {
            return false;
        }

        chosen = Templates[global::System.Math.Clamp(_templateFocus, 0, Templates.Length - 1)];
        Console.WriteLine($"[Hub] New World: {chosen.DisplayName()} (keyboard)");
        return true;
    }

    // The picture for this template at the density the display needs, loaded once (null: missing, the icon is drawn).
    private UiTexture? TemplatePicture(WorldTemplate template, float dpiScale)
    {
        var density = TemplatePreviews.DensityFor(dpiScale);
        if (_templatePreviews.TryGetValue((template, density), out var cached))
        {
            return cached;
        }

        UiTexture? texture = TemplatePreviews.TryLoad(template, density, out var rgba, out var width, out var height)
            ? _backend.CreateTexture(rgba, width, height)
            : null;
        _templatePreviews[(template, density)] = texture;
        Console.WriteLine(texture is null
            ? $"[Hub] Template preview {TemplatePreviews.FileName(template, density)} missing: drawing the icon"
            : $"[Hub] Template preview {TemplatePreviews.FileName(template, density)} loaded ({width} x {height})");
        return texture;
    }

    // ---------------------------------------------------------------------------------------------- menu and delete

    internal UiBox MenuRect()
    {
        var height = MenuPadding * 2f + MenuItemHeight * 3f + MenuSeparatorHeight;
        var x = global::System.Math.Clamp(_menuAnchor.X + _menuAnchor.Width - MenuWidth, 8f, MathF.Max(8f, _screenWidth - MenuWidth - 8f));
        var y = _menuAnchor.Y + _menuAnchor.Height + 4f;
        if (y + height > _screenHeight - 8f)
        {
            y = MathF.Max(8f, _menuAnchor.Y - 4f - height);
        }

        return new UiBox(x, y, MenuWidth, height);
    }

    // Item i of the menu (index 2 is the separator).
    internal UiBox MenuItemRect(int item)
    {
        var menu = MenuRect();
        var y = menu.Y + MenuPadding + item * MenuItemHeight - (item > 2 ? MenuItemHeight - MenuSeparatorHeight : 0f);
        return new UiBox(menu.X + MenuPadding, y, menu.Width - MenuPadding * 2f, item == 2 ? MenuSeparatorHeight : MenuItemHeight);
    }

    // Any click closes the menu; on an item it also does that item (Reset Image only when there is a custom image).
    private ProjectHubCommand UpdateMenu(InputManager input, ProjectInfo menuProject, out ProjectInfo? project)
    {
        project = null;
        if (input.ScrollDelta != 0f)
        {
            _menuProject = null;
            return ProjectHubCommand.None;
        }

        if (!input.LeftMousePressed && !input.RightMousePressed)
        {
            return ProjectHubCommand.None;
        }

        _menuProject = null;
        if (!input.LeftMousePressed)
        {
            return ProjectHubCommand.None;
        }

        if (MenuItemRect(0).Contains(_mouseX, _mouseY))
        {
            project = menuProject;
            return ProjectHubCommand.ChooseCustomThumbnail;
        }

        if (MenuItemRect(1).Contains(_mouseX, _mouseY) && HasCustomThumbnail(menuProject))
        {
            project = menuProject;
            return ProjectHubCommand.ResetCustomThumbnail;
        }

        if (MenuItemRect(3).Contains(_mouseX, _mouseY))
        {
            AskDelete(menuProject);
        }

        return ProjectHubCommand.None;
    }

    // The one way to Delete Project: always asks first (Enter and Escape cancel).
    private void AskDelete(ProjectInfo project)
    {
        _menuProject = null;
        _confirm.Open(DeleteProjectRequest(project.Name, () => _confirmedDelete = project));
    }

    /// <summary>
    /// The Delete Project question, asked the same way in the Hub and in the editor's project menu: Delete Project (red)
    /// or Cancel; Enter and Escape both cancel. <paramref name="confirmed"/> runs only for Delete Project.
    /// </summary>
    internal static ConfirmRequest DeleteProjectRequest(string projectName, Action confirmed) => new(
        $"Delete \"{projectName}\"?",
        "This removes the active project and its files.\nIt will remain visible in History as Deleted.",
        ["Delete Project", "Cancel"],
        choice =>
        {
            if (choice == 0)
            {
                confirmed();
            }
        },
        DefaultButton: 1,
        DestructiveButton: 0);

    // ---------------------------------------------------------------------------------------------- drawing

    private void DrawRail(int screenHeight)
    {
        _screenUi.DrawRect(0f, 0f, RailWidth, screenHeight, Rail);
        _screenUi.DrawRect(RailWidth - 1f, 0f, 1f, screenHeight, RailSeparator);

        foreach (var (view, button) in RailButtons)
        {
            // Templates is a step inside Home, so Home stays highlighted there.
            var active = _view == view || (view == ProjectHubView.Home && _view == ProjectHubView.Templates);
            var background = DrawRailButton(button, active);
            var (cx, cy) = (button.X + RailButtonSize * 0.5f, button.Y + RailButtonSize * 0.5f);
            var color = active ? IconActive : IconIdle;
            switch (view)
            {
                case ProjectHubView.Home:
                    UiIcons.DrawHome(_screenUi, cx, cy, color, background);
                    break;
                case ProjectHubView.Projects:
                    UiIcons.DrawProjects(_screenUi, cx, cy, color);
                    break;
                default:
                    UiIcons.DrawHistory(_screenUi, cx, cy, color);
                    break;
            }
        }
    }

    // Returns the color now behind the icon (needed by the ring-shaped home icon).
    private Vector3 DrawRailButton(UiBox button, bool active)
    {
        if (active)
        {
            _screenUi.DrawRect(button.X, button.Y, button.Width, button.Height, RailActive);
            _screenUi.DrawRect(0f, button.Y + 8f, 3f, button.Height - 16f, Accent);
            return RailActive;
        }

        if (!_confirm.IsOpen && _menuProject is null && button.Contains(_mouseX, _mouseY))
        {
            _screenUi.DrawRect(button.X, button.Y, button.Width, button.Height, RailHover);
            return RailHover;
        }

        return Rail;
    }

    private void DrawPageHeader()
    {
        var left = ContentLeft(_screenWidth);
        switch (_view)
        {
            case ProjectHubView.Home:
                _screenUi.DrawString(left, ContentTop, "Home", Heading, HeadingScale, FontWeight.SemiBold);
                var button = NewWorldButton();
                _screenUi.DrawRect(button.X, button.Y, button.Width, button.Height,
                    IsHoverable(button) ? AccentHover : Accent);
                DrawCentered(button, "+  New World", IconActive, NameScale, FontWeight.SemiBold);
                _screenUi.DrawString(left, HomeGridTop - 30f, _projects.Count > 0 ? "Recent Worlds" : "No worlds yet. Create one with New World.",
                    Muted, SectionScale, _projects.Count > 0 ? FontWeight.SemiBold : FontWeight.Regular);
                break;
            case ProjectHubView.Projects:
                _screenUi.DrawString(left, ContentTop, "Projects", Heading, HeadingScale, FontWeight.SemiBold);
                _screenUi.DrawString(left, ContentTop + 38f, _projects.Count switch
                {
                    0 => "No worlds yet",
                    1 => "1 world, most recently opened first",
                    var count => $"{count} worlds, most recently opened first"
                }, Muted, SubtitleScale);
                break;
            case ProjectHubView.History:
                _screenUi.DrawString(left, ContentTop, "History", Heading, HeadingScale, FontWeight.SemiBold);
                _screenUi.DrawString(left, ContentTop + 38f, _history.Count > 0
                    ? "Every world you have had, including deleted ones"
                    : "Worlds you create will appear here", Muted, SubtitleScale);
                break;
        }
    }

    private void DrawStatus()
    {
        if (_status is not null)
        {
            _screenUi.DrawString(ContentLeft(_screenWidth), _screenHeight - StatusBottomMargin, Fit(_status, _screenWidth - ContentLeft(_screenWidth) - 20f, MetaScale),
                Error, MetaScale);
        }
    }

    // Hover only when nothing modal is open and the point is not cut away.
    private bool IsHoverable(UiBox box) => !_confirm.IsOpen && _menuProject is null && box.Contains(_mouseX, _mouseY);

    private bool IsCardHovered(UiBox card) => IsHoverable(card) && GridViewport().Contains(_mouseX, _mouseY);

    private void DrawCard(int index)
    {
        var metrics = Metrics();
        var card = GetCard(index);
        var project = CardProject(index);
        var history = CardHistory(index);
        var interactive = project is not null;
        var hovered = interactive && IsCardHovered(card);
        _screenUi.DrawRect(card.X, card.Y, card.Width, card.Height, !interactive ? CardInactive : hovered ? CardHover : Card);

        var thumbnail = GetThumbnailRect(card, metrics);
        _screenUi.DrawRect(thumbnail.X, thumbnail.Y, thumbnail.Width, thumbnail.Height, ThumbnailPlaceholder);
        var template = project?.WorldTemplate ?? history?.Record.WorldTemplate ?? WorldTemplate.Classic;
        if (!TryGetPreview(index, out _))
        {
            // No preview yet (or none kept): the world-type picture, never a broken image.
            var iconWidth = MathF.Round(thumbnail.Width * 0.34f);
            var iconHeight = MathF.Round(iconWidth * 0.55f);
            UiIcons.DrawTemplate(_screenUi, template, thumbnail.X + (thumbnail.Width - iconWidth) * 0.5f, thumbnail.Y + (thumbnail.Height - iconHeight) * 0.5f,
                iconWidth, iconHeight, interactive ? ResetFill : DeletedBadgeFill);
        }

        var nameScale = metrics.Compact ? CompactNameScale : NameScale;
        var metaScale = metrics.Compact ? CompactMetaScale : MetaScale;
        var nameY = NameY(card, metrics);
        var name = project?.Name ?? history?.Record.Name ?? string.Empty;
        var nameRoom = card.Width - CardPadding * 2f - (interactive ? MoreButtonWidth + TrashButtonWidth + 8f : 0f);
        _screenUi.DrawString(card.X + CardPadding + 2f, nameY, Fit(name, nameRoom, nameScale, FontWeight.SemiBold),
            interactive ? NameText : InactiveName, nameScale, FontWeight.SemiBold);

        if (interactive)
        {
            DrawCardButtons(card, metrics, hovered);
        }

        var metaY = MetaY(card, metrics);
        if (history is not null)
        {
            DrawHistoryRow(card, history, metaY, metaScale);
            return;
        }

        // World-type indicator: always the Classic/Testing icon, never the custom image.
        var iconW = metrics.Compact ? 18f : 22f;
        var iconH = metrics.Compact ? 10f : 12f;
        UiIcons.DrawTemplate(_screenUi, template, card.X + CardPadding + 2f, metaY + 3f, iconW, iconH, Muted);
        _screenUi.DrawString(card.X + CardPadding + iconW + 10f, metaY, Fit(template.DisplayName(), card.Width - iconW - 28f, metaScale), Muted, metaScale);
    }

    private void DrawCardButtons(UiBox card, GridMetrics metrics, bool hovered)
    {
        var more = GetMoreButton(card, metrics);
        var moreHovered = hovered && more.Contains(_mouseX, _mouseY);
        if (moreHovered)
        {
            _screenUi.DrawRect(more.X, more.Y, more.Width, more.Height, ButtonHover);
        }

        var dotsY = more.Y + more.Height * 0.5f;
        for (var dot = -1; dot <= 1; dot++)
        {
            UiIcons.FillCircle(_screenUi, more.X + more.Width * 0.5f + dot * 6f, dotsY, 1.75f, moreHovered ? IconActive : Muted);
        }

        if (!hovered)
        {
            return;
        }

        var trash = GetTrashButton(card, metrics);
        var trashHovered = trash.Contains(_mouseX, _mouseY);
        if (trashHovered)
        {
            _screenUi.DrawRect(trash.X, trash.Y, trash.Width, trash.Height, TrashHover);
        }

        UiIcons.DrawTrash(_screenUi, trash.X + trash.Width * 0.5f, trash.Y + trash.Height * 0.5f, 14f, trashHovered ? DestructiveText : Muted);
    }

    // A status badge, then (when it fits) the date of what happened last: opened, or deleted through Wisko (a world
    // whose folder went missing outside Wisko has no known deletion date, so none is shown).
    private void DrawHistoryRow(UiBox card, HubHistoryItem history, float metaY, float metaScale)
    {
        var (label, fill, text) = history.State == HubHistoryState.HubActive
            ? ("HUB ACTIVE", ActiveBadgeFill, ActiveBadgeText)
            : ("DELETED", DeletedBadgeFill, DeletedBadgeText);
        var labelWidth = _screenUi.MeasureWidth(label, BadgeScale, FontWeight.SemiBold);
        var labelHeight = _screenUi.MeasureLineHeight(BadgeScale, FontWeight.SemiBold);
        var badge = new UiBox(card.X + CardPadding + 2f, metaY, labelWidth + 12f, MathF.Max(labelHeight + 4f, 16f));
        _screenUi.DrawRect(badge.X, badge.Y, badge.Width, badge.Height, fill);
        _screenUi.DrawString(badge.X + 6f, badge.Y + (badge.Height - labelHeight) * 0.5f, label, text, BadgeScale, FontWeight.SemiBold);

        var record = history.Record;
        var detail = history.State == HubHistoryState.HubActive ? record.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : record.DeletedUtc is { } deleted && !history.DeletedOutsideWisko ? deleted.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
        var room = card.X + card.Width - CardPadding - (badge.X + badge.Width + 8f);
        var width = _screenUi.MeasureWidth(detail, metaScale);
        if (width <= room)
        {
            _screenUi.DrawString(card.X + card.Width - CardPadding - 2f - width, metaY + (badge.Height - _screenUi.MeasureLineHeight(metaScale)) * 0.5f,
                detail, Muted, metaScale);
        }
    }

    private void DrawPreviewOverlay(int index)
    {
        var metrics = Metrics();
        var card = GetCard(index);
        var thumbnail = GetThumbnailRect(card, metrics);
        var project = CardProject(index);
        if (project is null)
        {
            // A Deleted world: its preview stays recognizable, but clearly inactive.
            if (TryGetPreview(index, out _))
            {
                _screenUi.DrawRect(thumbnail.X, thumbnail.Y, thumbnail.Width, thumbnail.Height, Background, 0.45f);
            }

            return;
        }

        if (!HasCustomThumbnail(project) || !IsCardHovered(card))
        {
            return;
        }

        var (cx, cy) = ResetButtonCenter(thumbnail);
        var resetHovered = HitCardRegion(card, metrics, _mouseX, _mouseY, interactive: true, hasCustomThumbnail: true) == CardHitRegion.ResetCustomThumbnail;
        UiIcons.FillCircle(_screenUi, cx, cy, ResetButtonRadius, resetHovered ? ResetHover : ResetFill);
        UiIcons.DrawCross(_screenUi, cx, cy, 6f, IconActive);
    }

    private void DrawScrollBar()
    {
        var maxScroll = MaxScroll;
        if (maxScroll <= 0f)
        {
            return;
        }

        var viewport = GridViewport();
        var thumbHeight = MathF.Max(24f, viewport.Height * viewport.Height / ContentHeight);
        var y = viewport.Y + (viewport.Height - thumbHeight) * (ScrollOffset / maxScroll);
        _screenUi.DrawRect(viewport.X + viewport.Width + 6f, y, 4f, thumbHeight, ScrollThumb);
    }

    private void DrawMenu()
    {
        if (_menuProject is not { } project)
        {
            return;
        }

        var menu = MenuRect();
        _screenUi.DrawRect(menu.X - 1f, menu.Y - 1f, menu.Width + 2f, menu.Height + 2f, MenuBorder);
        _screenUi.DrawRect(menu.X, menu.Y, menu.Width, menu.Height, MenuPanel);
        var hasCustom = HasCustomThumbnail(project);
        for (var item = 0; item < MenuLabels.Length; item++)
        {
            var rect = MenuItemRect(item);
            if (item == 2)
            {
                _screenUi.DrawRect(rect.X + 6f, rect.Y + rect.Height * 0.5f, rect.Width - 12f, 1f, MenuBorder);
                continue;
            }

            var enabled = item != 1 || hasCustom;
            if (enabled && rect.Contains(_mouseX, _mouseY))
            {
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, MenuHover);
            }

            var color = !enabled ? Muted : item == 3 ? DestructiveText : NameText;
            _screenUi.DrawString(rect.X + 10f, rect.Y + (rect.Height - _screenUi.MeasureLineHeight(MetaScale)) * 0.5f, MenuLabels[item], color, MetaScale);
        }
    }

    private void DrawTemplateChoice(float dpiScale)
    {
        var back = BackButton;
        if (back.Contains(_mouseX, _mouseY))
        {
            _screenUi.DrawRect(back.X, back.Y, back.Width, back.Height, CardHover);
        }

        UiIcons.DrawBackArrow(_screenUi, back.X + back.Width * 0.5f, back.Y + back.Height * 0.5f, 16f, Heading);
        var left = ContentLeft(_screenWidth) + BackButtonSize + 8f;
        _screenUi.DrawString(left, ContentTop, "New World", Heading, HeadingScale, FontWeight.SemiBold);
        _screenUi.DrawString(left, ContentTop + 38f, "Choose a template", Muted, SubtitleScale);
        if (TemplatePreviewNote?.Invoke() is { } note)
        {
            // Where the renderer cannot draw what the pictures show (the macOS app: no shadows), say so on the same line.
            var noteX = left + _screenUi.MeasureWidth("Choose a template", SubtitleScale) + 18f;
            var noteWidth = _screenWidth - 24f - noteX;
            if (noteWidth > 60f)
            {
                _screenUi.DrawString(noteX, ContentTop + 41f, Fit(note, noteWidth, MetaScale), Muted, MetaScale);
            }
        }

        var layout = TemplateLayoutFor(_screenWidth, _screenHeight);
        for (var i = 0; i < Templates.Length; i++)
        {
            var template = Templates[i];
            var card = TemplateCardFor(i, layout);
            var hovered = card.Contains(_mouseX, _mouseY);
            _screenUi.DrawRect(card.X, card.Y, card.Width, card.Height, hovered ? CardHover : Card);

            // The picture goes on top of this placeholder (after the flush); the icon shows if it is missing.
            var picture = TemplatePictureRect(card, layout.Scale);
            _screenUi.DrawRect(picture.X, picture.Y, picture.Width, picture.Height, ThumbnailPlaceholder);
            if (TemplatePicture(template, dpiScale) is null)
            {
                UiIcons.DrawTemplate(_screenUi, template, picture.X + picture.Width * 0.5f - 40f, picture.Y + picture.Height * 0.5f - 22f, 80f, 44f,
                    hovered ? IconActive : IconIdle);
            }

            var textX = card.X + TemplateCardPadding * layout.Scale + 2f;
            var textWidth = card.Width - TemplateCardPadding * layout.Scale * 2f - 4f;
            var nameY = picture.Y + picture.Height + 14f;
            _screenUi.DrawString(textX, nameY, Fit(template.DisplayName(), textWidth, 2.1f, FontWeight.SemiBold), NameText, 2.1f, FontWeight.SemiBold);
            _screenUi.DrawString(textX, nameY + 28f, Fit(TemplateDescription(template), textWidth, 1.7f), Muted, 1.7f);
        }
    }

    private static string TemplateDescription(WorldTemplate template) =>
        template == WorldTemplate.Testing ? "Starts with three test cubes" : "Empty world, ground and sky";

    // The pictures over their placeholders, then each card's outline: the accent on the hovered card and on the one the
    // keyboard is on (once the keyboard was used), a quiet line on the others; a thin frame around every picture.
    private void DrawTemplatePictures(float dpiScale)
    {
        var layout = TemplateLayoutFor(_screenWidth, _screenHeight);
        for (var i = 0; i < Templates.Length; i++)
        {
            if (TemplatePicture(Templates[i], dpiScale) is { } texture)
            {
                var picture = TemplatePictureRect(TemplateCardFor(i, layout), layout.Scale);
                _images.Draw(texture, picture.X, picture.Y, picture.Width, picture.Height, _screenWidth, _screenHeight);
            }
        }

        _screenUi.BeginBatch(dpiScale);
        for (var i = 0; i < Templates.Length; i++)
        {
            var card = TemplateCardFor(i, layout);
            var picture = TemplatePictureRect(card, layout.Scale);
            _screenUi.DrawBorderRect(picture.X, picture.Y, picture.Width, picture.Height, MenuBorder, 1f);
            var hovered = card.Contains(_mouseX, _mouseY);
            var focused = _templateFocusVisible && i == _templateFocus;
            if (hovered || focused)
            {
                _screenUi.DrawBorderRect(card.X - 2f, card.Y - 2f, card.Width + 4f, card.Height + 4f, focused ? AccentHover : Accent, 2f);
            }
        }

        _screenUi.Flush(_screenWidth, _screenHeight);
    }

    private void DrawCentered(UiBox rect, string label, Vector3 color, float scale, FontWeight weight)
    {
        var textWidth = _screenUi.MeasureWidth(label, scale, weight);
        var textHeight = _screenUi.MeasureLineHeight(scale, weight);
        _screenUi.DrawString(
            rect.X + (rect.Width - textWidth) * 0.5f,
            rect.Y + (rect.Height - textHeight) * 0.5f,
            label,
            color,
            scale,
            weight);
    }

    // Text cut with an ellipsis to fit the width (names, statuses).
    private string Fit(string text, float width, float scale, FontWeight weight = FontWeight.Regular)
    {
        if (width <= 0f)
        {
            return string.Empty;
        }

        if (_screenUi.MeasureWidth(text, scale, weight) <= width)
        {
            return text;
        }

        var length = text.Length;
        while (length > 0 && _screenUi.MeasureWidth(text[..length] + "…", scale, weight) > width)
        {
            length--;
        }

        return length == 0 ? string.Empty : text[..length] + "…";
    }
}
