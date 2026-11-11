using System.Globalization;
using Wisko.Engine.Math;
using Wisko.Engine.ScriptEditor;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>Everything the code editor view shows besides the document itself.</summary>
internal sealed class CodeEditorViewState
{
    public bool CaretVisible = true;
    public bool HasFocus = true;
    public bool FindOpen;
    public bool FindFocused;
    public string FindQuery = string.Empty;
    public bool FindCaseSensitive;
    public IReadOnlyList<TextRange> FindMatches = [];
    public int FindCurrent = -1;
    public bool FindCapped;
    public CompletionResult? Completion;
    public int CompletionSelected;
    public int CompletionScroll;

    /// <summary>A line to highlight briefly after jumping to it (e.g. from Output), or -1.</summary>
    public int FlashLine = -1;
    public float FlashAlpha;
    public string? StatusMessage;
    public bool StatusIsError;
    public bool Playing;
}

/// <summary>What a click on the editor's own controls hit.</summary>
internal enum CodeEditorControl
{
    None,
    Text,
    Gutter,
    VerticalScrollbar,
    HorizontalScrollbar,
    FindField,
    FindPrevious,
    FindNext,
    FindCase,
    FindClose,
    Completion
}

/// <summary>
/// Draws a <see cref="ScriptDocument"/> as a code editor inside a rectangle with the shared UI renderer (identical on
/// OpenGL and bgfx): line-number gutter with syntax-error markers, current-line band, selection, find matches,
/// syntax-coloured monospace text, caret, both scrollbars, a status line, the completion popup and the find bar.
/// Only the visible lines are visited; everything is clipped to the editor. Also maps points to text positions.
/// </summary>
internal sealed class CodeEditorView : IDisposable
{
    public const float TextScale = 1.75f;
    public const float StatusHeight = 22f;
    public const float FindBarHeight = 32f;
    private const float ScrollbarSize = 9f;
    private const float GutterPadding = 12f;
    private const float CodePadding = 10f;
    private const float UiScale = 1.6f;
    private const int CompletionRows = 8;
    private const float CompletionRowHeight = 22f;
    private const float CompletionWidth = 380f;
    private const float FindFieldWidth = 220f;

    private static readonly Vector3 Background = new(0.085f, 0.09f, 0.11f);
    private static readonly Vector3 GutterFill = new(0.075f, 0.08f, 0.095f);
    private static readonly Vector3 LineNumber = new(0.38f, 0.41f, 0.46f);
    private static readonly Vector3 CurrentLineNumber = new(0.82f, 0.84f, 0.88f);
    private static readonly Vector3 CurrentLineFill = new(0.12f, 0.13f, 0.155f);
    private static readonly Vector3 SelectionFill = new(0.20f, 0.36f, 0.62f);
    private static readonly Vector3 CaretColor = new(0.92f, 0.94f, 0.98f);
    private static readonly Vector3 ErrorColor = new(0.96f, 0.40f, 0.40f);
    private static readonly Vector3 MatchFill = new(0.90f, 0.68f, 0.20f);
    private static readonly Vector3 FlashFill = new(0.36f, 0.62f, 1.0f);
    private static readonly Vector3 ScrollThumb = new(0.30f, 0.32f, 0.37f);
    private static readonly Vector3 StatusFill = new(0.10f, 0.11f, 0.13f);
    private static readonly Vector3 StatusText = new(0.62f, 0.65f, 0.70f);
    private static readonly Vector3 PlayAccent = new(0.36f, 0.80f, 0.52f);
    private static readonly Vector3 PopupFill = new(0.14f, 0.15f, 0.18f);
    private static readonly Vector3 PopupBorder = new(0.28f, 0.30f, 0.35f);
    private static readonly Vector3 PopupSelected = new(0.20f, 0.36f, 0.62f);
    private static readonly Vector3 FieldFill = new(0.07f, 0.075f, 0.09f);
    private static readonly Vector3 FieldBorder = new(0.36f, 0.62f, 1.0f);
    private static readonly Vector3 ButtonFill = new(0.20f, 0.22f, 0.26f);
    private static readonly Vector3 ButtonActive = new(0.20f, 0.42f, 0.78f);

    private IUiRenderer _ui = null!;
    private float _charWidth = 8f;
    private float _lineHeight = 18f;
    private float _textHeight = 14f;
    private float _dpiScale = 1f;

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    public float LineHeight => _lineHeight;

    public float CharWidth => _charWidth;

    // ---- Layout ----

    /// <summary>Where everything goes for a given editor rectangle and document.</summary>
    internal readonly record struct Layout(UiBox Bounds, UiBox Gutter, UiBox Code, UiBox Status, UiBox VerticalBar, UiBox HorizontalBar, UiBox FindBar)
    {
        public int VisibleLines(float lineHeight) => global::System.Math.Max(1, (int)(Code.Height / lineHeight));
    }

    /// <summary>Measures the font (call once per frame before layout; the dpi scale affects measurement buckets).</summary>
    public void UpdateMetrics(float dpiScale)
    {
        _dpiScale = dpiScale;
        _ui.BeginBatch(dpiScale);
        _charWidth = _ui.MeasureWidth("M", TextScale, FontWeight.Monospace);
        _textHeight = _ui.MeasureLineHeight(TextScale, FontWeight.Monospace);
        _lineHeight = MathF.Round(_textHeight + 5f);
    }

    public Layout GetLayout(UiBox bounds, ScriptDocument document, bool findOpen)
    {
        var digits = global::System.Math.Max(3, document.Buffer.LineCount.ToString(CultureInfo.InvariantCulture).Length);
        var gutterWidth = MathF.Round(digits * _charWidth + GutterPadding * 2f + 6f);
        var top = bounds.Y + (findOpen ? FindBarHeight : 0f);
        var status = new UiBox(bounds.X, bounds.Y + bounds.Height - StatusHeight, bounds.Width, StatusHeight);
        var bodyHeight = MathF.Max(0f, status.Y - top);
        var gutter = new UiBox(bounds.X, top, gutterWidth, bodyHeight);
        var code = new UiBox(bounds.X + gutterWidth, top, MathF.Max(0f, bounds.Width - gutterWidth - ScrollbarSize), MathF.Max(0f, bodyHeight - ScrollbarSize));
        var vertical = new UiBox(bounds.X + bounds.Width - ScrollbarSize, top, ScrollbarSize, MathF.Max(0f, bodyHeight - ScrollbarSize));
        var horizontal = new UiBox(code.X, code.Y + code.Height, code.Width, ScrollbarSize);
        var find = new UiBox(bounds.X, bounds.Y, bounds.Width, findOpen ? FindBarHeight : 0f);
        return new Layout(bounds, gutter, code, status, vertical, horizontal, find);
    }

    public float ContentHeight(ScriptDocument document) => document.Buffer.LineCount * _lineHeight;

    /// <summary>Width of the longest visible-ish line (sampled cheaply: lines in view plus the caret line).</summary>
    public float ContentWidth(ScriptDocument document, Layout layout)
    {
        var first = (int)(document.ScrollY / _lineHeight);
        var last = global::System.Math.Min(document.Buffer.LineCount - 1, first + layout.VisibleLines(_lineHeight) + 1);
        var widest = 0;
        for (var line = first; line <= last; line++)
        {
            var text = document.Buffer.GetLine(line);
            widest = global::System.Math.Max(widest, TextColumns.VisualColumn(text, text.Length));
        }

        var caretLine = document.Buffer.GetLine(document.Caret.Line);
        widest = global::System.Math.Max(widest, TextColumns.VisualColumn(caretLine, caretLine.Length));
        return widest * _charWidth + CodePadding * 2f + _charWidth * 4f;
    }

    public void ClampScroll(ScriptDocument document, Layout layout)
    {
        var maxY = MathF.Max(0f, ContentHeight(document) - layout.Code.Height + _lineHeight * 3f);
        document.ScrollY = global::System.Math.Clamp(document.ScrollY, 0f, maxY);
        var maxX = MathF.Max(0f, ContentWidth(document, layout) - layout.Code.Width);
        document.ScrollX = global::System.Math.Clamp(document.ScrollX, 0f, maxX);
    }

    /// <summary>Scrolls the least needed to show the caret (with a small margin).</summary>
    public void RevealCaret(ScriptDocument document, Layout layout, bool center = false)
    {
        var caret = document.Caret;
        var caretTop = caret.Line * _lineHeight;
        if (center)
        {
            document.ScrollY = MathF.Max(0f, caretTop - layout.Code.Height * 0.4f);
        }
        else if (caretTop < document.ScrollY)
        {
            document.ScrollY = caretTop;
        }
        else if (caretTop + _lineHeight > document.ScrollY + layout.Code.Height)
        {
            document.ScrollY = caretTop + _lineHeight - layout.Code.Height;
        }

        var caretX = CodePadding + TextColumns.VisualColumn(document.Buffer.GetLine(caret.Line), caret.Column) * _charWidth;
        var margin = _charWidth * 4f;
        if (caretX - margin < document.ScrollX)
        {
            document.ScrollX = MathF.Max(0f, caretX - margin);
        }
        else if (caretX + margin > document.ScrollX + layout.Code.Width)
        {
            document.ScrollX = caretX + margin - layout.Code.Width;
        }
    }

    /// <summary>The text position under a point (clamped into the document).</summary>
    public TextPosition PositionAt(ScriptDocument document, Layout layout, float x, float y)
    {
        var line = (int)MathF.Floor((y - layout.Code.Y + document.ScrollY) / _lineHeight);
        line = global::System.Math.Clamp(line, 0, document.Buffer.LineCount - 1);
        var visual = (x - layout.Code.X + document.ScrollX - CodePadding) / _charWidth;
        var text = document.Buffer.GetLine(line);
        return document.Buffer.Clamp(new TextPosition(line, TextColumns.ColumnAtVisual(text, MathF.Max(0f, visual))));
    }

    /// <summary>Top-left of a text position on screen.</summary>
    public (float X, float Y) PointOf(ScriptDocument document, Layout layout, TextPosition position)
    {
        var text = document.Buffer.GetLine(position.Line);
        var x = layout.Code.X + CodePadding + TextColumns.VisualColumn(text, position.Column) * _charWidth - document.ScrollX;
        var y = layout.Code.Y + position.Line * _lineHeight - document.ScrollY;
        return (x, y);
    }

    // ---- Hit testing of the editor's own controls ----

    public CodeEditorControl HitTest(ScriptDocument document, Layout layout, CodeEditorViewState state, float x, float y, out int completionIndex)
    {
        completionIndex = -1;
        if (state.Completion is { } completion && CompletionRect(document, layout, completion) is { } popup && popup.List.Contains(x, y))
        {
            var row = (int)((y - popup.List.Y) / CompletionRowHeight) + state.CompletionScroll;
            if (row >= 0 && row < completion.Items.Count)
            {
                completionIndex = row;
            }

            return CodeEditorControl.Completion;
        }

        if (state.FindOpen && layout.FindBar.Contains(x, y))
        {
            var controls = FindControls(layout);
            return controls.Field.Contains(x, y) ? CodeEditorControl.FindField
                : controls.Previous.Contains(x, y) ? CodeEditorControl.FindPrevious
                : controls.Next.Contains(x, y) ? CodeEditorControl.FindNext
                : controls.Case.Contains(x, y) ? CodeEditorControl.FindCase
                : controls.Close.Contains(x, y) ? CodeEditorControl.FindClose
                : CodeEditorControl.FindField;
        }

        if (layout.VerticalBar.Contains(x, y))
        {
            return CodeEditorControl.VerticalScrollbar;
        }

        if (layout.HorizontalBar.Contains(x, y))
        {
            return CodeEditorControl.HorizontalScrollbar;
        }

        if (layout.Gutter.Contains(x, y))
        {
            return CodeEditorControl.Gutter;
        }

        return layout.Code.Contains(x, y) ? CodeEditorControl.Text : CodeEditorControl.None;
    }

    /// <summary>Scroll position for a scrollbar drag at the given point.</summary>
    public void DragScrollbar(ScriptDocument document, Layout layout, bool vertical, float x, float y)
    {
        if (vertical)
        {
            var content = ContentHeight(document) + _lineHeight * 3f;
            var fraction = (y - layout.VerticalBar.Y) / MathF.Max(1f, layout.VerticalBar.Height);
            document.ScrollY = fraction * content - layout.Code.Height * 0.5f;
        }
        else
        {
            var content = ContentWidth(document, layout);
            var fraction = (x - layout.HorizontalBar.X) / MathF.Max(1f, layout.HorizontalBar.Width);
            document.ScrollX = fraction * content - layout.Code.Width * 0.5f;
        }

        ClampScroll(document, layout);
    }

    // ---- Rendering ----

    public void Render(ScriptDocument document, Layout layout, CodeEditorViewState state, int screenWidth, int screenHeight)
    {
        var ui = _ui;
        ui.BeginBatch(_dpiScale);
        var bounds = layout.Bounds;
        ui.DrawRect(bounds.X, bounds.Y, bounds.Width, bounds.Height, Background);
        ui.DrawRect(layout.Gutter.X, layout.Gutter.Y, layout.Gutter.Width, layout.Gutter.Height, GutterFill);

        var buffer = document.Buffer;
        var first = global::System.Math.Max(0, (int)(document.ScrollY / _lineHeight) - 1);
        var last = global::System.Math.Min(buffer.LineCount - 1, first + layout.VisibleLines(_lineHeight) + 2);
        var codeClip = new UiClip(layout.Code.X, layout.Code.Y, layout.Code.Width, layout.Code.Height);
        var gutterClip = new UiClip(layout.Gutter.X, layout.Gutter.Y, layout.Gutter.Width, layout.Code.Height);
        var selection = document.Selection;
        var errorLines = new Dictionary<int, SyntaxDiagnostic>();
        foreach (var diagnostic in document.Diagnostics)
        {
            errorLines.TryAdd(diagnostic.Line, diagnostic);
        }

        for (var line = first; line <= last; line++)
        {
            var y = layout.Code.Y + line * _lineHeight - document.ScrollY;
            var text = buffer.GetLine(line);

            // Gutter: line number (+ error marker).
            ui.SetClip(gutterClip);
            var number = (line + 1).ToString(CultureInfo.InvariantCulture);
            var numberWidth = number.Length * _charWidth;
            var isCaretLine = line == document.Caret.Line;
            ui.DrawString(layout.Gutter.X + layout.Gutter.Width - GutterPadding - numberWidth, y + 2.5f, number,
                isCaretLine ? CurrentLineNumber : LineNumber, TextScale, FontWeight.Monospace);
            if (errorLines.ContainsKey(line))
            {
                UiIcons.FillCircle(ui, layout.Gutter.X + 8f, y + _lineHeight * 0.5f, 3.5f, ErrorColor);
            }

            ui.SetClip(codeClip);
            if (isCaretLine && !document.HasSelection)
            {
                ui.DrawRect(layout.Code.X, y, layout.Code.Width, _lineHeight, CurrentLineFill);
            }

            if (line == state.FlashLine && state.FlashAlpha > 0f)
            {
                ui.DrawRect(layout.Code.X, y, layout.Code.Width, _lineHeight, FlashFill, 0.35f * state.FlashAlpha);
            }

            // Selection on this line.
            if (document.HasSelection && line >= selection.Start.Line && line <= selection.End.Line)
            {
                var startColumn = line == selection.Start.Line ? selection.Start.Column : 0;
                var endColumn = line == selection.End.Line ? selection.End.Column : text.Length;
                var x0 = ColumnX(layout, document, text, startColumn);
                var x1 = ColumnX(layout, document, text, endColumn) + (line < selection.End.Line ? _charWidth * 0.6f : 0f);
                ui.DrawRect(x0, y, MathF.Max(1f, x1 - x0), _lineHeight, SelectionFill, 0.75f);
            }

            // Find matches on this line.
            foreach (var match in MatchesOnLine(state.FindMatches, line))
            {
                var index = match.Index;
                var x0 = ColumnX(layout, document, text, match.Range.Start.Column);
                var x1 = ColumnX(layout, document, text, match.Range.End.Column);
                ui.DrawRect(x0, y + 1f, MathF.Max(2f, x1 - x0), _lineHeight - 2f, MatchFill, index == state.FindCurrent ? 0.55f : 0.25f);
                if (index == state.FindCurrent)
                {
                    ui.DrawBorderRect(x0, y + 1f, MathF.Max(2f, x1 - x0), _lineHeight - 2f, MatchFill, 1f);
                }
            }

            DrawTokens(layout, document, line, text, y);

            if (errorLines.ContainsKey(line))
            {
                DrawSquiggle(layout, document, text, y + _lineHeight - 3f);
            }
        }

        // Caret.
        if (state.HasFocus && state.CaretVisible && !state.FindFocused)
        {
            var (caretX, caretY) = PointOf(document, layout, document.Caret);
            ui.SetClip(codeClip);
            ui.DrawRect(MathF.Round(caretX) - 1f, caretY + 1f, 2f, _lineHeight - 2f, CaretColor);
        }

        ui.SetClip(null);
        DrawScrollbars(document, layout);
        DrawStatus(document, layout, state);
        if (state.FindOpen)
        {
            DrawFindBar(layout, state);
        }

        if (state.Completion is { } completion)
        {
            DrawCompletion(document, layout, state, completion);
        }

        ui.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _ui.Dispose();

    private float ColumnX(Layout layout, ScriptDocument document, string text, int column) =>
        layout.Code.X + CodePadding + TextColumns.VisualColumn(text, column) * _charWidth - document.ScrollX;

    private static IEnumerable<(TextRange Range, int Index)> MatchesOnLine(IReadOnlyList<TextRange> matches, int line)
    {
        if (matches.Count == 0)
        {
            yield break;
        }

        // Binary search the first match on the line (matches are in document order).
        var low = 0;
        var high = matches.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (matches[mid].Start.Line < line)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        for (var i = low; i < matches.Count && matches[i].Start.Line == line; i++)
        {
            yield return (matches[i], i);
        }
    }

    private void DrawTokens(Layout layout, ScriptDocument document, int line, string text, float y)
    {
        var tokens = document.Highlighter.GetTokens(line);
        var textY = y + (_lineHeight - _textHeight) * 0.5f;
        var left = document.ScrollX - _charWidth;
        var right = document.ScrollX + layout.Code.Width + _charWidth;
        foreach (var token in tokens)
        {
            if (token.Kind == LuaTokenKind.Whitespace)
            {
                continue;
            }

            var startVisual = TextColumns.VisualColumn(text, token.Start);
            var startX = CodePadding + startVisual * _charWidth;
            if (startX > right)
            {
                break;
            }

            var endX = CodePadding + TextColumns.VisualColumn(text, token.End) * _charWidth;
            if (endX < left)
            {
                continue;
            }

            DrawRun(layout.Code.X + startX - document.ScrollX, textY, text, token.Start, token.End, startVisual, ColorOf(token.Kind));
        }
    }

    // Draws text[start..end) cell by cell where needed: ASCII runs as one string; tabs expand to tab stops; other
    // characters (possibly not monospaced in the font) each get their own cell, so columns always line up.
    private void DrawRun(float x, float y, string text, int start, int end, int startVisual, Vector3 color)
    {
        var visual = startVisual;
        var runStart = start;
        var runVisual = visual;
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch is >= ' ' and <= '~')
            {
                visual++;
                continue;
            }

            if (i > runStart)
            {
                _ui.DrawString(x + (runVisual - startVisual) * _charWidth, y, text[runStart..i], color, TextScale, FontWeight.Monospace);
            }

            if (ch == '\t')
            {
                visual += TextColumns.TabSize - visual % TextColumns.TabSize;
            }
            else if (!char.IsLowSurrogate(ch))
            {
                var length = char.IsHighSurrogate(ch) && i + 1 < end && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                _ui.DrawString(x + (visual - startVisual) * _charWidth, y, text.Substring(i, length), color, TextScale, FontWeight.Monospace);
                visual++;
            }

            runStart = i + 1;
            runVisual = visual;
        }

        if (end > runStart)
        {
            _ui.DrawString(x + (runVisual - startVisual) * _charWidth, y, text[runStart..end], color, TextScale, FontWeight.Monospace);
        }
    }

    private static Vector3 ColorOf(LuaTokenKind kind) => kind switch
    {
        LuaTokenKind.Keyword => new Vector3(0.78f, 0.55f, 0.96f),
        LuaTokenKind.EngineGlobal => new Vector3(0.36f, 0.84f, 0.90f),
        LuaTokenKind.Builtin => new Vector3(0.47f, 0.70f, 1.0f),
        LuaTokenKind.Number => new Vector3(0.96f, 0.70f, 0.44f),
        LuaTokenKind.String => new Vector3(0.62f, 0.84f, 0.52f),
        LuaTokenKind.Comment => new Vector3(0.46f, 0.51f, 0.57f),
        LuaTokenKind.Operator => new Vector3(0.70f, 0.74f, 0.80f),
        _ => new Vector3(0.87f, 0.89f, 0.93f)
    };

    // A zig-zag under the line's text marking a syntax error.
    private void DrawSquiggle(Layout layout, ScriptDocument document, string text, float y)
    {
        var startColumn = TextColumns.LeadingWhitespaceLength(text);
        var x0 = ColumnX(layout, document, text, startColumn);
        var x1 = MathF.Max(x0 + _charWidth * 3f, ColumnX(layout, document, text, text.Length));
        for (var x = x0; x < x1; x += 4f)
        {
            var up = ((int)((x - x0) / 4f) & 1) == 0;
            _ui.DrawRect(x, up ? y : y + 1.5f, 2.5f, 1.5f, ErrorColor);
        }
    }

    private void DrawScrollbars(ScriptDocument document, Layout layout)
    {
        var content = ContentHeight(document) + _lineHeight * 3f;
        if (content > layout.Code.Height)
        {
            var bar = layout.VerticalBar;
            var thumb = MathF.Max(24f, bar.Height * layout.Code.Height / content);
            var top = bar.Y + (bar.Height - thumb) * global::System.Math.Clamp(document.ScrollY / MathF.Max(1f, content - layout.Code.Height), 0f, 1f);
            _ui.DrawRect(bar.X + 2f, top, bar.Width - 4f, thumb, ScrollThumb);
            foreach (var diagnostic in document.Diagnostics)
            {
                var markY = bar.Y + bar.Height * diagnostic.Line / MathF.Max(1f, document.Buffer.LineCount);
                _ui.DrawRect(bar.X, markY, bar.Width, 2f, ErrorColor);
            }
        }

        var width = ContentWidth(document, layout);
        if (width > layout.Code.Width || document.ScrollX > 0f)
        {
            var bar = layout.HorizontalBar;
            var total = MathF.Max(width, document.ScrollX + layout.Code.Width);
            var thumb = MathF.Max(24f, bar.Width * layout.Code.Width / total);
            var left = bar.X + (bar.Width - thumb) * global::System.Math.Clamp(document.ScrollX / MathF.Max(1f, total - layout.Code.Width), 0f, 1f);
            _ui.DrawRect(left, bar.Y + 2f, thumb, bar.Height - 4f, ScrollThumb);
        }
    }

    private void DrawStatus(ScriptDocument document, Layout layout, CodeEditorViewState state)
    {
        var status = layout.Status;
        _ui.DrawRect(status.X, status.Y, status.Width, status.Height, StatusFill);
        _ui.DrawRect(status.X, status.Y, status.Width, 1f, new Vector3(0.20f, 0.22f, 0.26f));
        var textY = status.Y + (status.Height - _ui.MeasureLineHeight(UiScale)) * 0.5f;
        var caret = document.Caret;
        var line = document.Buffer.GetLine(caret.Line);
        var position = $"Ln {caret.Line + 1}, Col {TextColumns.VisualColumn(line, caret.Column) + 1}";
        _ui.DrawString(status.X + 10f, textY, position, StatusText, UiScale);

        var right = $"Lua · Spaces: 4 · UTF-8 · {(document.Newline == "\r\n" ? "CRLF" : "LF")}";
        var rightWidth = _ui.MeasureWidth(right, UiScale);
        if (status.Width > 520f)
        {
            _ui.DrawString(status.X + status.Width - rightWidth - 12f, textY, right, StatusText, UiScale);
        }

        // Middle: the syntax error (caret line first) or an editor message.
        var messageX = status.X + 10f + _ui.MeasureWidth(position, UiScale) + 24f;
        var messageRight = status.Width > 520f ? status.X + status.Width - rightWidth - 24f : status.X + status.Width - 10f;
        string? message = null;
        var messageColor = StatusText;
        var diagnostic = document.Diagnostics.FirstOrDefault(d => d.Line == caret.Line) ?? document.Diagnostics.FirstOrDefault();
        if (diagnostic is not null)
        {
            message = $"Line {diagnostic.Line + 1}: {diagnostic.Message}";
            messageColor = ErrorColor;
        }
        else if (state.StatusMessage is not null)
        {
            message = state.StatusMessage;
            messageColor = state.StatusIsError ? ErrorColor : StatusText;
        }

        if (state.Playing && document.SavedDuringPlay)
        {
            message = message is null ? "Changes apply next Play" : message + "  ·  Changes apply next Play";
            if (diagnostic is null)
            {
                messageColor = PlayAccent;
            }
        }

        if (message is not null && messageRight > messageX + 40f)
        {
            _ui.DrawString(messageX, textY, UiText.Fit(_ui, message, messageRight - messageX, UiScale), messageColor, UiScale);
        }
    }

    // ---- Find bar ----

    private readonly record struct FindControlRects(UiBox Field, UiBox Count, UiBox Case, UiBox Previous, UiBox Next, UiBox Close);

    private FindControlRects FindControls(Layout layout)
    {
        var bar = layout.FindBar;
        var y = bar.Y + 4f;
        var h = bar.Height - 8f;
        var close = new UiBox(bar.X + bar.Width - 8f - h, y, h, h);
        var next = new UiBox(close.X - 6f - h, y, h, h);
        var previous = new UiBox(next.X - 4f - h, y, h, h);
        var caseToggle = new UiBox(previous.X - 8f - 30f, y, 30f, h);
        var count = new UiBox(caseToggle.X - 8f - 96f, y, 96f, h);
        var fieldWidth = MathF.Min(FindFieldWidth, MathF.Max(80f, count.X - bar.X - 16f));
        var field = new UiBox(count.X - 8f - fieldWidth, y, fieldWidth, h);
        return new FindControlRects(field, count, caseToggle, previous, next, close);
    }

    private void DrawFindBar(Layout layout, CodeEditorViewState state)
    {
        var bar = layout.FindBar;
        _ui.DrawRect(bar.X, bar.Y, bar.Width, bar.Height, StatusFill);
        _ui.DrawRect(bar.X, bar.Y + bar.Height - 1f, bar.Width, 1f, new Vector3(0.20f, 0.22f, 0.26f));
        var controls = FindControls(layout);
        var field = controls.Field;
        _ui.DrawRect(field.X - 1f, field.Y - 1f, field.Width + 2f, field.Height + 2f, state.FindFocused ? FieldBorder : PopupBorder);
        _ui.DrawRect(field.X, field.Y, field.Width, field.Height, FieldFill);
        var textY = field.Y + (field.Height - _ui.MeasureLineHeight(UiScale)) * 0.5f;
        _ui.SetClip(new UiClip(field.X + 6f, field.Y, field.Width - 12f, field.Height));
        var query = state.FindQuery;
        var queryWidth = _ui.MeasureWidth(query, UiScale);
        var queryX = field.X + 8f - MathF.Max(0f, queryWidth - (field.Width - 20f));
        if (query.Length == 0)
        {
            _ui.DrawString(field.X + 8f, textY, "Find", StatusText, UiScale);
        }
        else
        {
            _ui.DrawString(queryX, textY, query, CurrentLineNumber, UiScale);
        }

        if (state.FindFocused && state.CaretVisible)
        {
            _ui.DrawRect(queryX + queryWidth + 1f, field.Y + 5f, 1.5f, field.Height - 10f, CaretColor);
        }

        _ui.SetClip(null);
        var count = state.FindQuery.Length == 0 ? string.Empty
            : state.FindMatches.Count == 0 ? "No results"
            : $"{state.FindCurrent + 1} of {state.FindMatches.Count}{(state.FindCapped ? "+" : string.Empty)}";
        _ui.DrawString(controls.Count.X, textY, count, state.FindMatches.Count == 0 && state.FindQuery.Length > 0 ? ErrorColor : StatusText, UiScale);
        DrawButton(controls.Case, "Aa", state.FindCaseSensitive);
        DrawButton(controls.Previous, "↑", false);
        DrawButton(controls.Next, "↓", false);
        _ui.DrawRect(controls.Close.X, controls.Close.Y, controls.Close.Width, controls.Close.Height, ButtonFill);
        UiIcons.DrawCross(_ui, controls.Close.X + controls.Close.Width * 0.5f, controls.Close.Y + controls.Close.Height * 0.5f, 7f, CurrentLineNumber);
    }

    private void DrawButton(UiBox rect, string label, bool active)
    {
        _ui.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, active ? ButtonActive : ButtonFill);
        var width = _ui.MeasureWidth(label, UiScale);
        _ui.DrawString(rect.X + (rect.Width - width) * 0.5f, rect.Y + (rect.Height - _ui.MeasureLineHeight(UiScale)) * 0.5f, label, CurrentLineNumber, UiScale);
    }

    // ---- Completion popup ----

    private readonly record struct CompletionRects(UiBox Popup, UiBox List, UiBox Detail);

    private CompletionRects? CompletionRect(ScriptDocument document, Layout layout, CompletionResult completion)
    {
        var (x, y) = PointOf(document, layout, completion.ReplaceStart);
        var rows = global::System.Math.Min(CompletionRows, completion.Items.Count);
        var listHeight = rows * CompletionRowHeight;
        const float detailHeight = 46f;
        var height = listHeight + detailHeight + 2f;
        var left = global::System.Math.Clamp(x - 4f, layout.Bounds.X + 2f, MathF.Max(layout.Bounds.X + 2f, layout.Bounds.X + layout.Bounds.Width - CompletionWidth - 2f));
        var below = y + _lineHeight + 2f;
        var top = below + height <= layout.Status.Y ? below : MathF.Max(layout.Code.Y, y - height - 2f);
        var popup = new UiBox(left, top, CompletionWidth, height);
        return new CompletionRects(popup, new UiBox(left + 1f, top + 1f, CompletionWidth - 2f, listHeight), new UiBox(left + 1f, top + 1f + listHeight, CompletionWidth - 2f, detailHeight));
    }

    private void DrawCompletion(ScriptDocument document, Layout layout, CodeEditorViewState state, CompletionResult completion)
    {
        if (CompletionRect(document, layout, completion) is not { } rects)
        {
            return;
        }

        var popup = rects.Popup;
        _ui.SetClip(null);
        _ui.DrawRect(popup.X - 1f, popup.Y - 1f, popup.Width + 2f, popup.Height + 2f, PopupBorder);
        _ui.DrawRect(popup.X, popup.Y, popup.Width, popup.Height, PopupFill);
        var rows = global::System.Math.Min(CompletionRows, completion.Items.Count);
        var labelY = (CompletionRowHeight - _textHeight) * 0.5f;
        var uiTextY = (CompletionRowHeight - _ui.MeasureLineHeight(UiScale)) * 0.5f;
        _ui.SetClip(new UiClip(rects.List.X, rects.List.Y, rects.List.Width, rects.List.Height));
        for (var row = 0; row < rows; row++)
        {
            var index = row + state.CompletionScroll;
            if (index >= completion.Items.Count)
            {
                break;
            }

            var item = completion.Items[index];
            var y = rects.List.Y + row * CompletionRowHeight;
            if (index == state.CompletionSelected)
            {
                _ui.DrawRect(rects.List.X, y, rects.List.Width, CompletionRowHeight, PopupSelected);
            }

            var (tag, tagColor) = KindTag(item.Kind);
            _ui.DrawString(rects.List.X + 8f, y + uiTextY, tag, tagColor, 1.45f, FontWeight.SemiBold);
            _ui.DrawString(rects.List.X + 44f, y + labelY, item.Label, CaretColor, TextScale, FontWeight.Monospace);
            if (item.Kind != CompletionItemKind.Snippet && item.Signature != item.Label)
            {
                var signature = UiText.Fit(_ui, item.Signature, 150f, 1.5f);
                _ui.DrawString(rects.List.X + rects.List.Width - 8f - _ui.MeasureWidth(signature, 1.5f), y + uiTextY, signature, StatusText, 1.5f);
            }
        }

        _ui.SetClip(null);
        if (completion.Items.Count > CompletionRows)
        {
            var track = rects.List;
            var thumb = MathF.Max(12f, track.Height * CompletionRows / completion.Items.Count);
            var top = track.Y + (track.Height - thumb) * state.CompletionScroll / MathF.Max(1f, completion.Items.Count - CompletionRows);
            _ui.DrawRect(track.X + track.Width - 4f, top, 3f, thumb, ScrollThumb);
        }

        var selected = completion.Items[global::System.Math.Clamp(state.CompletionSelected, 0, completion.Items.Count - 1)];
        var detail = rects.Detail;
        _ui.DrawRect(detail.X, detail.Y, detail.Width, 1f, PopupBorder);
        _ui.DrawString(detail.X + 8f, detail.Y + 5f, UiText.Fit(_ui, selected.Signature.Replace('\n', ' '), detail.Width - 16f, 1.55f), CurrentLineNumber, 1.55f, FontWeight.SemiBold);
        _ui.DrawString(detail.X + 8f, detail.Y + 24f, UiText.Fit(_ui, selected.Summary, detail.Width - 16f, 1.5f), StatusText, 1.5f);
    }

    private static (string Tag, Vector3 Color) KindTag(CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Function => ("fn", new Vector3(0.47f, 0.70f, 1.0f)),
        CompletionItemKind.Property => ("prop", new Vector3(0.36f, 0.84f, 0.90f)),
        CompletionItemKind.Object => ("obj", new Vector3(0.36f, 0.84f, 0.90f)),
        CompletionItemKind.Constant => ("const", new Vector3(0.96f, 0.70f, 0.44f)),
        CompletionItemKind.Snippet => ("snip", new Vector3(0.62f, 0.84f, 0.52f)),
        CompletionItemKind.KeyName => ("key", new Vector3(0.62f, 0.84f, 0.52f)),
        CompletionItemKind.MouseButton => ("btn", new Vector3(0.62f, 0.84f, 0.52f)),
        _ => ("kw", new Vector3(0.78f, 0.55f, 0.96f))
    };


    /// <summary>The completion row count shown at once.</summary>
    public static int CompletionPageSize => CompletionRows;
}
