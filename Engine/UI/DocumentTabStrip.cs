using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>One script tab as the strip shows it.</summary>
internal readonly record struct DocumentTabView(string Label, bool IsDirty);

/// <summary>What a click on the tab strip hit.</summary>
internal readonly record struct DocumentTabHit(int Tab, bool OnClose)
{
    /// <summary>The pinned 3D View tab.</summary>
    public const int SceneView = -1;
}

/// <summary>
/// The tabs above the central content: the pinned "3D View" tab (never closable) followed by one tab per open script
/// (file name, "●" when unsaved, × to close). Script tabs scroll horizontally when they do not fit (mouse wheel over
/// the strip, and the active tab is kept in view).
/// </summary>
internal sealed class DocumentTabStrip : IDisposable
{
    public const float Height = 30f;
    private const float SceneTabWidth = 96f;
    private const float TabPadding = 14f;
    private const float CloseSize = 16f;
    private const float TextScale = 1.65f;

    private static readonly Vector3 StripFill = new(0.10f, 0.11f, 0.13f);
    private static readonly Vector3 TabFill = new(0.15f, 0.16f, 0.19f);
    private static readonly Vector3 ActiveFill = new(0.08f, 0.085f, 0.10f);
    private static readonly Vector3 Accent = new(0.36f, 0.62f, 1.0f);
    private static readonly Vector3 Text = new(0.86f, 0.88f, 0.92f);
    private static readonly Vector3 MutedText = new(0.58f, 0.61f, 0.66f);
    private static readonly Vector3 Separator = new(0.22f, 0.24f, 0.28f);

    private IUiRenderer _ui = null!;
    private float _scroll;

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    public void Scroll(float delta, float stripWidth, IReadOnlyList<DocumentTabView> tabs) =>
        _scroll = global::System.Math.Clamp(_scroll + delta, 0f, MaxScroll(stripWidth, tabs));

    /// <summary>Scrolls just enough to show the given script tab.</summary>
    public void Reveal(int tab, float stripWidth, IReadOnlyList<DocumentTabView> tabs)
    {
        if (tab < 0 || tab >= tabs.Count)
        {
            return;
        }

        var left = 0f;
        for (var i = 0; i < tab; i++)
        {
            left += TabWidth(tabs[i]);
        }

        var visible = stripWidth - SceneTabWidth;
        var right = left + TabWidth(tabs[tab]);
        if (left < _scroll)
        {
            _scroll = left;
        }
        else if (right > _scroll + visible)
        {
            _scroll = right - visible;
        }

        _scroll = global::System.Math.Clamp(_scroll, 0f, MaxScroll(stripWidth, tabs));
    }

    public bool TryHit(float mouseX, float mouseY, float x, float y, float width, IReadOnlyList<DocumentTabView> tabs, out DocumentTabHit hit)
    {
        hit = default;
        if (mouseY < y || mouseY > y + Height || mouseX < x || mouseX > x + width)
        {
            return false;
        }

        if (mouseX < x + SceneTabWidth)
        {
            hit = new DocumentTabHit(DocumentTabHit.SceneView, false);
            return true;
        }

        var tabX = x + SceneTabWidth - _scroll;
        for (var i = 0; i < tabs.Count; i++)
        {
            var tabWidth = TabWidth(tabs[i]);
            if (mouseX >= tabX && mouseX < tabX + tabWidth && mouseX >= x + SceneTabWidth)
            {
                var closeX = tabX + tabWidth - TabPadding * 0.5f - CloseSize;
                hit = new DocumentTabHit(i, mouseX >= closeX - 2f);
                return true;
            }

            tabX += tabWidth;
        }

        hit = new DocumentTabHit(int.MinValue, false); // empty strip area
        return true;
    }

    public void Render(float x, float y, float width, int activeTab, IReadOnlyList<DocumentTabView> tabs, float mouseX, float mouseY,
        int screenWidth, int screenHeight, float dpiScale)
    {
        _scroll = global::System.Math.Clamp(_scroll, 0f, MaxScroll(width, tabs));
        _ui.BeginBatch(dpiScale);
        _ui.DrawRect(x, y, width, Height, StripFill);
        _ui.DrawRect(x, y + Height - 1f, width, 1f, Separator);

        DrawTab(x, y, SceneTabWidth, "3D View", activeTab == DocumentTabHit.SceneView, dirty: false, closable: false, mouseX, mouseY);
        _ui.DrawRect(x + SceneTabWidth, y + 6f, 1f, Height - 12f, Separator);

        _ui.SetClip(new UiClip(x + SceneTabWidth + 1f, y, MathF.Max(0f, width - SceneTabWidth - 1f), Height));
        var tabX = x + SceneTabWidth + 1f - _scroll;
        for (var i = 0; i < tabs.Count; i++)
        {
            var tabWidth = TabWidth(tabs[i]);
            if (tabX + tabWidth > x + SceneTabWidth && tabX < x + width)
            {
                DrawTab(tabX, y, tabWidth, tabs[i].Label, activeTab == i, tabs[i].IsDirty, closable: true, mouseX, mouseY);
            }

            tabX += tabWidth;
        }

        _ui.SetClip(null);

        // Overflow hints: a fade line where tabs continue out of view.
        if (_scroll > 0f)
        {
            _ui.DrawRect(x + SceneTabWidth + 1f, y + 4f, 2f, Height - 8f, Accent, 0.6f);
        }

        if (_scroll < MaxScroll(width, tabs))
        {
            _ui.DrawRect(x + width - 2f, y + 4f, 2f, Height - 8f, Accent, 0.6f);
        }

        _ui.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _ui.Dispose();

    private void DrawTab(float x, float y, float width, string label, bool active, bool dirty, bool closable, float mouseX, float mouseY)
    {
        var hovered = mouseX >= x && mouseX < x + width && mouseY >= y && mouseY < y + Height;
        _ui.DrawRect(x, y, width - 1f, Height - 1f, active ? ActiveFill : TabFill);
        if (active)
        {
            _ui.DrawRect(x, y, width - 1f, 2f, Accent);
        }

        var textHeight = _ui.MeasureLineHeight(TextScale);
        var textY = y + (Height - textHeight) * 0.5f;
        var labelX = closable ? x + TabPadding : x + (width - _ui.MeasureWidth(label, TextScale)) * 0.5f;
        _ui.DrawString(labelX, textY, label, active ? Text : MutedText, TextScale, active ? FontWeight.SemiBold : FontWeight.Regular);
        if (!closable)
        {
            return;
        }

        var cx = x + width - TabPadding * 0.5f - CloseSize * 0.5f - 2f;
        var cy = y + Height * 0.5f;
        var overClose = mouseX >= cx - CloseSize * 0.5f && mouseX <= cx + CloseSize * 0.5f && hovered;
        if (dirty && !hovered)
        {
            UiIcons.FillCircle(_ui, cx, cy, 3.5f, Text);
        }
        else if (hovered || active || dirty)
        {
            if (overClose)
            {
                _ui.DrawRect(cx - CloseSize * 0.5f, cy - CloseSize * 0.5f, CloseSize, CloseSize, new Vector3(0.28f, 0.30f, 0.35f));
            }

            UiIcons.DrawCross(_ui, cx, cy, 7f, active ? Text : MutedText);
        }
    }

    private float TabWidth(DocumentTabView tab) => _ui.MeasureWidth(tab.Label, TextScale) + TabPadding * 2f + CloseSize + 4f;

    private float MaxScroll(float stripWidth, IReadOnlyList<DocumentTabView> tabs)
    {
        var total = 0f;
        foreach (var tab in tabs)
        {
            total += TabWidth(tab);
        }

        return MathF.Max(0f, total - (stripWidth - SceneTabWidth - 1f));
    }
}
