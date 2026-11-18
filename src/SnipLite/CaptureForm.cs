using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SnipLite;

internal enum CaptureMode
{
    Snip,
    Ocr,
    Translation,
    Qr,
    Record,
    Gif
}

internal sealed class CaptureResult
{
    public required Bitmap Image { get; init; }

    /// <summary>Selection in physical screen coordinates.</summary>
    public required Rectangle Bounds { get; init; }

    /// <summary>True when the user asked for a pinned window instead of a copy.</summary>
    public bool Pin { get; init; }
    public CaptureAction? Action { get; init; }
    public bool KeepOpen { get; init; }

    /// <summary>
    /// The overlay already put the image on the clipboard, so the tray must not
    /// copy it a second time (auto-copy hotkey path).
    /// </summary>
    public bool AlreadyCopied { get; init; }

    /// <summary>The overlay already saved this capture to history (explicit save).</summary>
    public bool HistoryRecorded { get; init; }
}

/// <summary>
/// Full virtual-screen overlay: freeze the desktop, pick a region, annotate it,
/// then copy / save / pin. All maths are in physical pixels so mixed-DPI
/// setups behave the same everywhere.
/// </summary>
internal sealed partial class CaptureForm : ToolWindow
{
    private const int MagnifierSize = 168;

    private readonly Rectangle _virtual;
    private readonly Bitmap _screenshot;
    private readonly bool _ocrCapture;
    private readonly bool _translationCapture;
    private readonly bool _qrCaptureOnly;
    private readonly AppSettings _settings;
    private readonly Font _badgeFont;
    private readonly AnnotationBar _bar = new();
    private readonly List<Shape> _shapes = new();
    private readonly Stack<List<Shape>> _undo = new();
    private readonly Stack<List<Shape>> _redo = new();
    private TextShape? _activeText;
    private TextShape? _editingText;
    private bool _selectingEditorText;
    private int _editorSelectionAnchor;
    private Rectangle _textPreviewBounds;
    private TextIBeamCursor? _textCursor;
    private bool _movingText;
    private bool _resizingText;
    private float _resizeFontStart;
    private RectangleF _resizeTextBounds;
    private Point _textDragOrigin;
    private Point _textDragCursor;
    private List<Shape>? _beforeTextDrag;
    private string? _slider;
    private Bitmap? _annotationLayer;
    private Rectangle _annotationCrop;
    private bool _annotationDirty = true;
    private Color _drawingColor = AnnotationBar.Palette[3];
    private Color _markerColor = AnnotationBar.Palette[6];
    private bool _hexColor;
    private int _magnifierZoom;
    private readonly DesktopTargets? _targets;
    private Rectangle _hoverTarget;
    private Rectangle _hoverWindow;
    private Rectangle _pressTarget;
    private bool _targetBusy;
    private readonly System.Windows.Forms.Timer _targetTimer = new() { Interval = 60 };
    private Point? _lastResolvedTargetPoint;
    private long _lastTargetResolvedAt;
    private long _lastTargetQuery;
    private readonly HashSet<Point> _automationPoints = new();
    private long _lastAutomationQuery;
    private Point? _lastAutomationPoint;
    private long _lastAutomationResolvedAt;
    private readonly Rectangle? _preset;
    /// <summary>"截屏并自动复制": finish (copy and close) as soon as a region is framed.</summary>
    private readonly bool _autoCopy;
    private readonly bool _longCapture;
    private readonly bool _recordCapture;
    private readonly bool _gifCapture;
    private LongCaptureForm? _longForm;
    private CaptureKeyboardHook? _keyboardHook;
    private readonly List<ContextMenuStrip> _optionMenus = new();
    private CaptureCrosshair? _crosshair;

    private Shape? _current;
    private Point _anchor;
    private Point _cursor;
    private Rectangle _selection;
    private bool _dragging;
    private bool _hasSelection;
    private bool _showMagnifier;
    private bool _finished;
    private bool _historyRecorded; // an explicit save already added this capture to history
    private int _dragMode; // 0 = idle, 1 = new selection / drawing, 2 = move, 3+ = resize anchor
    private Rectangle _dragStartSelection;
    private Point _dragStartCursor;
    private ImeSink? _ime;
    private Font? _textFont;
    private System.Windows.Forms.Timer? _caretTimer;
    private string _textText = string.Empty;
    private Point _textAnchor;
    private Rectangle _caretRect;
    private Rectangle _composeRect;
    private bool _caretOn;
    private bool _resultPin;
    private bool _barVisible;
    private readonly ToolTip _toolTip = new() { InitialDelay = 400, ReshowDelay = 100 };
    private string _hoverHint = "";
    private OcrDocument? _ocrDocument;
    private bool _ocrMode;
    private bool _recognitionBusy;
    private int _recognitionVersion;
    private int _ocrAnchor = -1;
    private int _ocrEnd = -1;
    private bool _selectingText;
    private PointF _ocrDragStart;
    private Rectangle _ocrCrop;
    private Size _ocrSourceSize;
    private string? _qrText;
    private Rectangle _lastMagnifierBounds;
    private bool MagnifierVisible => _showMagnifier || _dragMode >= 3 || _settings.AutoMagnifier && !_hasSelection && !_dragging;

    public event Action<CaptureResult?>? Done;

    /// <summary>Raised the moment an explicit save succeeds, so the tray can record history.</summary>
    public event Action<Bitmap, Rectangle, string>? Saved;

    public CaptureForm(Rectangle virtualScreen, CaptureMode mode, AppSettings settings, Rectangle? preset = null, bool autoCopy = false, bool longCapture = false)
    {
        _virtual = virtualScreen;
        _autoCopy = autoCopy;
        _longCapture = longCapture;
        _recordCapture = mode == CaptureMode.Record;
        _gifCapture = mode == CaptureMode.Gif;
        _ocrCapture = mode == CaptureMode.Ocr;
        _translationCapture = mode == CaptureMode.Translation;
        _qrCaptureOnly = mode == CaptureMode.Qr;
        _settings = settings;
        _translationProvider = TranslationProviders.Create(settings.TranslationProvider);
        _hexColor = settings.ColorHex;
        _magnifierZoom = settings.MagnifierZoom;
        _preset = preset;
        if (settings.DetectWindows || settings.DetectElements) _targets = new DesktopTargets();

        // PArgb blits about 3x faster than Argb in GDI+; alpha is opaque after CopyFromScreen
        _screenshot = new Bitmap(_virtual.Width, _virtual.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_screenshot))
        {
            g.CopyFromScreen(_virtual.Location, Point.Empty, _virtual.Size, CopyPixelOperation.SourceCopy);
            if (settings.CaptureCursor) Win32.DrawCursor(g, _virtual.Location);
        }

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Cursor = Cursors.Default;
        KeyPreview = true;
        Text = "SnipLite";

        _badgeFont = new Font("Consolas", 10f, FontStyle.Bold);
        _targetTimer.Tick += (_, _) =>
        {
            if (!_finished && !_hasSelection && !_dragging && Visible) UpdateTarget();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Localization.LanguageService.Changed -= RefreshInterfaceLanguage;
        base.Dispose(disposing);
    }

    private void RefreshInterfaceLanguage()
    {
        if (IsDisposed) return;
        _hoverHint = "";
        _toolTip.Hide(this);
        Invalidate();
    }

    private float Ui => DeviceDpi / 96f;

    private int Zoom => Math.Clamp(_magnifierZoom, 2, 16);

    private int AnchorHit => (int)Math.Round(8 * Ui);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Localization.LanguageService.Changed += RefreshInterfaceLanguage;

        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST,
            _virtual.X, _virtual.Y, _virtual.Width, _virtual.Height,
            Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);

        _bar.Scale = Ui;
        _bar.Accent = _settings.Accent;
        _bar.ItemVisible = id => id switch
        {
            "rect" => _settings.ShowToolRect,
            "ellipse" => _settings.ShowToolEllipse,
            "arrow" => _settings.ShowToolArrow,
            "pen" => _settings.ShowToolPen,
            "marker" => _settings.ShowToolMarker,
            "mosaic" => _settings.ShowToolMosaic,
            "text" => _settings.ShowToolText,
            "eraser" => _settings.ShowToolEraser,
            "undo" => _settings.ShowToolUndo,
            "redo" => _settings.ShowToolRedo,
            "ocr" => _settings.ShowToolOcr,
            "translate" => _settings.ShowToolTranslate,
            "qr" => _settings.ShowToolQr,
            "close" => _settings.ShowToolClose,
            "pin" => _settings.ShowToolPin,
            "save" => _settings.ShowToolSave,
            "copy" => _settings.ShowToolCopy,
            "long" => _settings.ShowToolLong,
            "rec" => _settings.ShowToolRec,
            "gif" => _settings.ShowToolGif,
            _ => true
        };
        if (_preset is Rectangle preset)
        {
            preset.Offset(-_virtual.X, -_virtual.Y);
            _selection = Rectangle.Intersect(preset, new Rectangle(Point.Empty, _virtual.Size));
            _hasSelection = _selection.Width > 0 && _selection.Height > 0;
        }
        if (_settings.BlockLeftWindows || _settings.BlockRightWindows || _settings.IgnoreInjectedCopy)
            _keyboardHook = new CaptureKeyboardHook(Handle, _settings);

        Activate();
        _cursor = CursorToLocal();
        if (_settings.ShowCrosshair) _crosshair = new CaptureCrosshair(this, _virtual, _settings.Accent);
        UpdateCursor();
        if (!_hasSelection) UpdateTarget();
        if (_targets is not null) _targetTimer.Start();
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Localization.LanguageService.Changed -= RefreshInterfaceLanguage;
        _recognitionVersion++;
        CancelTranslation();
        ClearTranslationImages();
        _targetTimer.Stop(); _targetTimer.Dispose();
        _crosshair?.Dispose(); _crosshair = null;
        _keyboardHook?.Dispose();
        _toolTip.Dispose();
        var menus = _optionMenus.ToArray(); _optionMenus.Clear();
        // ToolStrip's modal filter is still unwinding during Closed / owner
        // closure. Release menus on the next UI turn, never inside Closed.
        SynchronizationContext.Current?.Post(_ => { foreach (var menu in menus) menu.Dispose(); }, null);
        base.OnFormClosed(e);

        _caretTimer?.Stop();
        _caretTimer?.Dispose();
        _caretTimer = null;
        _textFont?.Dispose();
        _textFont = null;

        _screenshot.Dispose();
        _annotationLayer?.Dispose();
        _badgeFont.Dispose();
        Cursor = Cursors.Default;
        _textCursor?.Dispose(); _textCursor = null;

        // the 4K screenshot and its buffers are gone; hand the pages back
        if (!_settings.KeepResponsive) GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    // ---------------------------------------------------------------- paint

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_screenshot, 0, 0);

        // While the rubber band is being dragged _hasSelection is still false,
        // so use the geometry itself to decide what to paint.
        var region = _selection.Width > 0 && _selection.Height > 0;

        using (var dim = new SolidBrush(Color.FromArgb(Math.Clamp(_settings.MaskOpacity, 0, 200), Color.FromArgb(_settings.CaptureMaskColor))))
        {
            if (region || !_hoverTarget.IsEmpty && !_dragging)
            {
                // four bands instead of a Region: same pixels, a fraction of the cost
                var s = region ? _selection : _hoverTarget;

                g.FillRectangle(dim, 0, 0, _virtual.Width, s.Top);
                g.FillRectangle(dim, 0, s.Bottom, _virtual.Width, _virtual.Height - s.Bottom);
                g.FillRectangle(dim, 0, s.Top, s.Left, s.Height);
                g.FillRectangle(dim, s.Right, s.Top, _virtual.Width - s.Right, s.Height);
            }
            else
            {
                g.FillRectangle(dim, 0, 0, _virtual.Width, _virtual.Height);
            }
        }

        if (region)
        {
            var state = g.Save();
            g.SetClip(_selection);

            DrawTranslationOverlay(g, Point.Empty);
            if (_shapes.Count > 0 || _current is not null)
                g.DrawImageUnscaled(GetAnnotationLayer(Crop), Crop.Location);
            DrawTextPreview(g);
            if (_activeText is not null && !_ocrMode)
            {
                var bounds = _ime is not null && _textFont is not null
                    ? TextEditLayout.Bounds(g, _textFont, _textText, _textAnchor) : _activeText.Bounds(g);
                bounds.Inflate(3 * Ui, 3 * Ui);
                using var outline = new Pen(_bar.Accent, Ui) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                var handle = TextResizeHandle(g, _activeText);
                using var handleFill = new SolidBrush(Color.White); g.FillRectangle(handleFill, handle);
                using var handleEdge = new Pen(_bar.Accent, Ui); g.DrawRectangle(handleEdge, handle);
            }
            DrawOcrSelection(g);

            g.Restore(state);

            DrawSelection(g, _settings.CaptureAnchors && _hasSelection && !_ocrMode && !_recognitionBusy);

            if (_hasSelection)
            {
                _bar.CanUndo = _undo.Count > 0;
                _bar.CanRedo = _redo.Count > 0;
                _bar.AttachedPanelHeight = TranslationVisible ? _translationPanel!.Height : 0;
                _bar.Layout(_selection, _virtual.Size);
                LayoutTranslationPanel();
                _barVisible = true;
                _bar.Render(g);
            }
            else
            {
                _barVisible = false;
            }
            DrawSelectionBadge(g);
        }
        else
        {
            _barVisible = false;

            if (_settings.ShowCrosshair && _crosshair is null)
            {
                using var cross = new Pen(Color.FromArgb(190, 0, 174, 255), 1f);
                g.DrawLine(cross, _cursor.X, 0, _cursor.X, _virtual.Height);
                g.DrawLine(cross, 0, _cursor.Y, _virtual.Width, _cursor.Y);
            }
            if (!_hoverTarget.IsEmpty && !_dragging)
            {
                using var hover = new Pen(_settings.Accent, _settings.CaptureBorderWidth);
                g.DrawRectangle(hover, _hoverTarget);
            }
            if (_settings.CaptureShortcutHints)
            {
                var hint = UiText.T("拖动框选 / 单击窗口 · Esc 退出 · Alt 放大镜");
                var box = new Rectangle((int)(20 * Ui), _virtual.Height - (int)(42 * Ui), (int)(600 * Ui), (int)(28 * Ui));
                using var back = new SolidBrush(AppTheme.Current.Card);
                using var hintPath = FluentDrawing.Rounded(box, 6 * Ui); g.FillPath(back, hintPath);
                TextRenderer.DrawText(g, hint, Font, box, AppTheme.Current.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        if (MagnifierVisible)
            DrawMagnifier(g);
    }

    private void DrawSelection(Graphics g, bool handles)
    {
        var r = _selection;

        // Draw the frame one pixel outside the crop so it can never end up in
        // the captured bitmap (it used to look like a captured guide line).
        using (var pen = new Pen(_settings.Accent, _settings.CaptureBorderWidth))
            g.DrawRectangle(pen, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);

        if (handles)
        {
            var handle = Math.Max(8, (int)Math.Round(8 * Ui));
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(_settings.CaptureAnchorColor | unchecked((int)0xFF000000)));
            using var halo = new Pen(Color.FromArgb(150, Color.Black), Math.Max(3, 3 * Ui));
            using var edge = new Pen(_settings.Accent, Math.Max(1, 1.5f * Ui));
            foreach (var p in Anchors(r))
            {
                var bounds = new Rectangle(Math.Clamp(p.X - handle / 2, 2, Math.Max(2, _virtual.Width - handle - 2)),
                    Math.Clamp(p.Y - handle / 2, 2, Math.Max(2, _virtual.Height - handle - 2)), handle, handle);
                g.DrawRectangle(halo, bounds);
                g.FillRectangle(brush, bounds);
                g.DrawRectangle(edge, bounds);
            }
            g.Restore(state);
        }
    }

    private void DrawSelectionBadge(Graphics g)
    {
        var r = _selection;
        if (_settings.HideOnePixelHint && r.Width <= 1 && r.Height <= 1) return;

        var label = $"{r.Width} × {r.Height}";
        var size = g.MeasureString(label, _badgeFont);
        var padding = (int)Math.Ceiling(6 * Ui);
        var screenSelection = r; screenSelection.Offset(_virtual.Location);
        var viewport = Screen.FromRectangle(screenSelection).Bounds;
        viewport.Offset(-_virtual.X, -_virtual.Y);
        viewport = Rectangle.Intersect(viewport, new Rectangle(Point.Empty, _virtual.Size));
        var obstacle = _barVisible ? _bar.Bounds : Rectangle.Empty;
        if (TranslationVisible) obstacle = Rectangle.Union(obstacle, _translationPanel!.Bounds);
        var box = SelectionBadgeBounds(r, new Size((int)Math.Ceiling(size.Width) + padding * 2,
            (int)Math.Ceiling(size.Height) + padding), viewport, obstacle, (int)Math.Ceiling(10 * Ui));

        using var back = new SolidBrush(AppTheme.Current.Card);
        using var text = new SolidBrush(AppTheme.Current.Text);
        using var badge = FluentDrawing.Rounded(box, 6 * Ui);
        g.FillPath(back, badge);
        using var border = new Pen(AppTheme.Current.Border);
        g.DrawPath(border, badge);
        g.DrawString(label, _badgeFont, text, box.X + padding, box.Y + padding / 2);
    }

    internal static Rectangle SelectionBadgeBounds(Rectangle selection, Size size, Rectangle viewport, Rectangle obstacle, int gap)
    {
        size = new Size(Math.Min(size.Width, viewport.Width), Math.Min(size.Height, viewport.Height));
        Rectangle Place(int x, int y) => new(Math.Clamp(x, viewport.Left, viewport.Right - size.Width),
            Math.Clamp(y, viewport.Top, viewport.Bottom - size.Height), size.Width, size.Height);
        // Prefer outside the upper-left corner. At the screen edge move inside,
        // leaving the corner handle exposed; the toolbar may itself be above the crop.
        var above = Place(selection.Left, selection.Top - size.Height - gap);
        var inside = Place(selection.Left + gap, selection.Top + gap);
        if (selection.Top - size.Height - gap >= viewport.Top && !above.IntersectsWith(obstacle)) return above;
        if (!inside.IntersectsWith(obstacle)) return inside;
        var belowBar = Place(selection.Left + gap, obstacle.Bottom + gap);
        if (!belowBar.IntersectsWith(obstacle)) return belowBar;
        var rightOfBar = Place(obstacle.Right + gap, selection.Top + gap);
        if (!rightOfBar.IntersectsWith(obstacle)) return rightOfBar;
        return above;
    }

    private void DrawMagnifier(Graphics g)
    {
        var size = (int)Math.Round(MagnifierSize * Ui);
        var sourceSize = Math.Max(3, size / Zoom);
        if (sourceSize % 2 == 0) sourceSize--;
        size = sourceSize * Zoom;
        var source = new Rectangle(
            _cursor.X - sourceSize / 2,
            _cursor.Y - sourceSize / 2,
            sourceSize,
            sourceSize);

        var gap = (int)Math.Round(24 * Ui);
        var box = new Rectangle(_cursor.X + gap, _cursor.Y + gap, size, size);
        if (box.Right > _virtual.Width) box.X = _cursor.X - gap - size;
        if (box.Bottom > _virtual.Height) box.Y = _cursor.Y - gap - size;

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        using (var bg = new SolidBrush(Color.FromArgb(235, 20, 20, 20)))
            g.FillRectangle(bg, box);

        g.DrawImage(_screenshot, box, source, GraphicsUnit.Pixel);
        if (_hasSelection)
        {
            var state = g.Save();
            g.SetClip(box);
            g.TranslateTransform(box.X - source.X * Zoom, box.Y - source.Y * Zoom);
            g.ScaleTransform(Zoom, Zoom);
            if (_settings.MagnifierMask)
            {
                using var region = new Region(source); region.Exclude(_selection);
                using var dim = new SolidBrush(Color.FromArgb(_settings.MaskOpacity, Color.FromArgb(_settings.CaptureMaskColor)));
                g.FillRegion(dim, region);
            }
            if (_settings.MagnifierBorder)
            { using var selectionPen = new Pen(_settings.Accent, 1f / Zoom); g.DrawRectangle(selectionPen, _selection); }
            if (_settings.MagnifierAnchors)
            { using var anchor = new SolidBrush(Color.FromArgb(_settings.CaptureAnchorColor)); foreach (var p in Anchors(_selection)) g.FillRectangle(anchor, p.X - 1, p.Y - 1, 2, 2); }
            g.Restore(state);
        }

        using (var border = new Pen(Color.FromArgb(255, 0, 174, 255), 1f))
            g.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);

        // highlight the pixel under the cursor
        var cell = box.Width / (float)sourceSize;
        var focus = new RectangleF(
            box.X + (_cursor.X - source.X) * cell,
            box.Y + (_cursor.Y - source.Y) * cell,
            cell, cell);
        MagnifierReticle.Draw(g, box, focus, Ui, _settings.Accent);

        var pixel = _screenshot.GetPixel(_cursor.X, _cursor.Y);
        var info = $"{CoordinateText()}\n{ColorValue(pixel)}" + (_settings.MagnifierHints ? UiText.T("\nC 复制颜色 · Shift 切换 RGB / HEX") + (_settings.CopyCoordinatesHotkey == Keys.None ? "" : Localization.LanguageService.Format("\n{0} 复制坐标", HotkeyDefinitions.Format(_settings.CopyCoordinatesHotkey))) : "");
        var labelSize = g.MeasureString(info, _badgeFont);
        var label = new Rectangle(Math.Clamp(box.X, 0, Math.Max(0, _virtual.Width - (int)labelSize.Width - 12)), Math.Max(0, box.Y - (int)labelSize.Height - 8), (int)labelSize.Width + 12, (int)labelSize.Height + 6);
        using var labelBack = new SolidBrush(AppTheme.Current.Card);
        using var labelText = new SolidBrush(AppTheme.Current.Text);
        using var labelPath = FluentDrawing.Rounded(label, 6 * Ui);
        g.FillPath(labelBack, labelPath);
        g.DrawString(info, _badgeFont, labelText, label.X + 6, label.Y + 3);
        _lastMagnifierBounds = Rectangle.Inflate(Rectangle.Union(box, label), 3, 3);
    }

    private static IEnumerable<Point> Anchors(Rectangle r)
    {
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;

        yield return new Point(r.X, r.Y);
        yield return new Point(cx, r.Y);
        yield return new Point(r.Right, r.Y);
        yield return new Point(r.X, cy);
        yield return new Point(r.Right, cy);
        yield return new Point(r.X, r.Bottom);
        yield return new Point(cx, r.Bottom);
        yield return new Point(r.Right, r.Bottom);
    }

    // ---------------------------------------------------------------- input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _crosshair?.Hide();
        if (e.Button == MouseButtons.Right)
        {
            HandleRightClick(e.Location);
            return;
        }
        if (e.Button == MouseButtons.Middle && _hasSelection) { ExecuteAction(_settings.MiddleClickAction, _settings.MiddleClickExit); return; }
        if (e.Button != MouseButtons.Left) return;

        var p = Clamp(e.Location);
        _cursor = p;

        if (_ocrMode || _recognitionBusy)
        {
            var command = _bar.HitTest(p);
            if (command == "ocr") { ExitRecognition(); return; }
            if (command == "ocrtextcopy") { CopyOcrText(allIfUnselected: true); return; }
            if (command is not null) { ExitRecognition(); Command(command); return; }
            if (_recognitionBusy) return;
            if (_bar.Bounds.Contains(p)) return;
            _ocrDragStart = OcrToLocal(p);
            _ocrAnchor = _ocrDocument?.HitTest(_ocrDragStart, tolerance: 16 * Ui) ?? -1;
            _ocrEnd = _ocrAnchor;
            _bar.ResultCopied = false;
            // Allow the drag to begin anywhere inside the crop, so a marquee that
            // starts in the empty margin can still sweep the text backwards.
            _selectingText = _ocrDocument is not null;
            Capture = _selectingText;
            Invalidate();
            return;
        }

        if (_hasSelection)
        {
            var id = _bar.HitTest(p);
            if (id != null)
            {
                Command(id);
                return;
            }
            if (_bar.Bounds.Contains(p)) return;

            ClearQrResult();
            var onTextResize = false;
            if (_ime is not null && _activeText is not null)
            {
                using var graphics = CreateGraphics();
                onTextResize = Rectangle.Inflate(TextResizeHandle(graphics, _activeText), AnchorHit / 2, AnchorHit / 2).Contains(p);
            }
            if (_ime is not null && _textPreviewBounds.Contains(p) && !onTextResize)
            {
                var extend = (ModifierKeys & Keys.Shift) != 0;
                var previousAnchor = _ime.SelectionStart;
                PlaceTextCaret(p, extend);
                _editorSelectionAnchor = extend ? previousAnchor : _ime.SelectionStart;
                _selectingEditorText = true; Capture = true;
                return;
            }
            CommitText();
            if (_activeText is not null)
            {
                using var graphics = CreateGraphics();
                if (Rectangle.Inflate(TextResizeHandle(graphics, _activeText), AnchorHit / 2, AnchorHit / 2).Contains(p))
                {
                    _resizingText = true; _resizeFontStart = _activeText.FontSize;
                    _resizeTextBounds = _activeText.Bounds(graphics); _beforeTextDrag = Snapshot();
                    Capture = true; return;
                }
                if (TextMoveFrame(graphics, _activeText).Contains(p) && !_activeText.Bounds(graphics).Contains(p))
                {
                    _movingText = true; _textDragOrigin = _activeText.At; _textDragCursor = p;
                    _beforeTextDrag = Snapshot(); Capture = true; return;
                }
            }
            var text = HitText(p);
            if (text is not null && (_bar.Tool == ToolKind.Text || ReferenceEquals(text, _activeText)))
            {
                EditText(text, p);
                _editorSelectionAnchor = _ime!.SelectionStart;
                _selectingEditorText = true; Capture = true;
                return;
            }

            // Selection border / corners always resize, even while a drawing tool is armed.
            var anchorIndex = HitAnchor(p);
            if (anchorIndex >= 0)
            {
                _dragMode = anchorIndex + 3;
                _dragStartSelection = _selection;
                _dragStartCursor = p;
                Capture = true;
                Invalidate(MagnifierDirtyBounds(_cursor));
                return;
            }

            if (_bar.Tool != ToolKind.None && _selection.Contains(p))
            {
                if (_bar.Tool == ToolKind.Text)
                {
                    BeginText(p);
                    return;
                }

                _activeText = null;
                var width = _bar.Tool == ToolKind.Marker ? _bar.MarkerWidth
                    : _bar.Tool is ToolKind.Eraser or ToolKind.Mosaic ? _bar.BrushWidth : _bar.StrokeWidth;
                _current = Shape.Create(_bar.Tool, p, _bar.Color, width * Ui);
                _current.Fill = _bar.Fill;
                if (_current is ArrowShape arrow) arrow.Style = _bar.ArrowStyle;
                if (_current is MosaicShape mosaic)
                {
                    mosaic.Block = Math.Max(2, (int)Math.Round(_bar.MosaicBlock * Ui));
                    mosaic.Rectangular = _bar.MosaicRectangle;
                }
                if (_current is EraserShape eraser) eraser.Rectangular = _bar.EraserRectangle;
                if (_current is MarkerShape marker)
                {
                    marker.Rectangular = _bar.MarkerRectangle;
                    marker.Opacity = _bar.MarkerOpacity;
                }

                _dragMode = 1;
                Capture = true;
                InvalidateAnnotations();
                return;
            }

            var index = HitAnchor(p);
            if (index >= 0)
            {
                _dragMode = index + 3;
                _dragStartSelection = _selection;
                _dragStartCursor = p;
                Capture = true;
                Invalidate(MagnifierDirtyBounds(_cursor));
                return;
            }

            if (_selection.Contains(p))
            {
                _dragMode = 2;
                _dragStartSelection = _selection;
                _dragStartCursor = p;
                return;
            }
        }

        _anchor = p;
        ClearQrResult();
        // Mouse-down can arrive before the next move/timer message. Resolve the
        // native target at the actual click instead of accepting an old highlight.
        if (!_hasSelection) UpdateTarget();
        _pressTarget = _hoverTarget.Contains(p) ? _hoverTarget : Rectangle.Empty;
        _dragging = true;
        _dragMode = 1;
        _hasSelection = false;
        _historyRecorded = false;
        _selection = Rectangle.Empty;
        _shapes.Clear();
        _undo.Clear();
        _redo.Clear();
        _activeText = null;
        _annotationDirty = true;
        _bar.Tool = ToolKind.None;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var previous = _cursor;
        var previousSelection = _selection;
        _cursor = Clamp(e.Location);

        if (_selectingEditorText && _ime is not null)
        {
            using var graphics = CreateGraphics();
            var index = TextEditLayout.HitTest(graphics, _textFont!, _textText, _textAnchor, _cursor);
            _ime.Select(Math.Min(index, _editorSelectionAnchor), Math.Abs(index - _editorSelectionAnchor));
            RestartCaret(); RefreshText(); UpdateCursor(); return;
        }

        if (_resizingText && _activeText is not null)
        {
            var ratio = Math.Max((_cursor.X - _resizeTextBounds.Left) / Math.Max(1, _resizeTextBounds.Width),
                (_cursor.Y - _resizeTextBounds.Top) / Math.Max(1, _resizeTextBounds.Height));
            _activeText.FontSize = _bar.FontSize = Math.Clamp(_resizeFontStart * ratio, 4, 200);
            InvalidateAnnotations(); return;
        }

        if (_slider is not null)
        {
            _bar.SetSlider(_slider, _cursor);
            Invalidate();
            return;
        }
        if (_movingText && _activeText is not null)
        {
            using var g = CreateGraphics();
            var size = _activeText.Bounds(g).Size;
            _activeText.At = new Point(
                Math.Clamp(_textDragOrigin.X + _cursor.X - _textDragCursor.X, _selection.Left, Math.Max(_selection.Left, _selection.Right - (int)Math.Ceiling(size.Width))),
                Math.Clamp(_textDragOrigin.Y + _cursor.Y - _textDragCursor.Y, _selection.Top, Math.Max(_selection.Top, _selection.Bottom - (int)Math.Ceiling(size.Height))));
            InvalidateAnnotations();
            return;
        }

        if (_ocrMode || _recognitionBusy)
        {
            if (_selectingText && _ocrDocument is not null)
            {
                var current = OcrToLocal(_cursor);
                var moved = Math.Abs(current.X - _ocrDragStart.X) >= 2f || Math.Abs(current.Y - _ocrDragStart.Y) >= 2f;
                if (_ocrAnchor >= 0 || moved)
                {
                    // Always build a normalised rectangle: dragging right-to-left or
                    // bottom-to-top must produce positive width/height, otherwise the
                    // character intersection test silently fails.
                    var dragRect = RectangleF.FromLTRB(
                        Math.Min(_ocrDragStart.X, current.X),
                        Math.Min(_ocrDragStart.Y, current.Y),
                        Math.Max(_ocrDragStart.X, current.X),
                        Math.Max(_ocrDragStart.Y, current.Y));
                    var (start, end) = _ocrDocument.RangeIn(dragRect);
                    if (start < 0)
                    {
                        // No glyph under the marquee (pointer in the margin): fall back
                        // to the nearest character so selection still follows the drag.
                        start = end = _ocrDocument.HitTest(current, nearest: true);
                    }
                    _ocrAnchor = start;
                    _ocrEnd = end;
                    Invalidate(_selection);
                }
            }
            UpdateCursor();
            UpdateHint();
            return;
        }
        UpdateHint();

        if (_current != null && _dragMode == 1)
        {
            _current.DragTo(_cursor);
            _annotationDirty = true;
            Repaint();
        }
        else if (_dragging)
        {
            _selection = Normalize(_anchor, _cursor);
            Repaint(previousSelection);
        }
        else if (_dragMode == 2)
        {
            var dx = _cursor.X - _dragStartCursor.X;
            var dy = _cursor.Y - _dragStartCursor.Y;
            var x = _dragStartSelection.X + dx;
            var y = _dragStartSelection.Y + dy;

            if (_settings.AllowOffscreenSelection)
            {
                // Off-screen dragging only has to keep a 24 px strip reachable, so the
                // box can be pushed past any edge yet still be grabbed again.
                var keep = Math.Max(8, (int)Math.Round(24 * Ui));
                x = Math.Clamp(x, keep - _dragStartSelection.Width, _virtual.Width - keep);
                y = Math.Clamp(y, keep - _dragStartSelection.Height, _virtual.Height - keep);
            }
            else
            {
                x = Math.Clamp(x, 0, Math.Max(0, _virtual.Width - _dragStartSelection.Width));
                y = Math.Clamp(y, 0, Math.Max(0, _virtual.Height - _dragStartSelection.Height));
            }

            _selection = new Rectangle(x, y, _dragStartSelection.Width, _dragStartSelection.Height);

            Repaint(previousSelection);
        }
        else if (_dragMode >= 3)
        {
            ResizeSelection(_dragMode - 3);
            Repaint(previousSelection);
        }
        else
        {
            UpdateCursor();

            if (!_hasSelection)
            {
                UpdateTarget();
                RepaintCrosshair(previous);
            }
            else if (MagnifierVisible)
            {
                Invalidate(_lastMagnifierBounds);
                Invalidate(MagnifierDirtyBounds(_cursor));
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_selectingEditorText)
        {
            _selectingEditorText = false; Capture = false; _ime?.Focus(); return;
        }
        if (_resizingText)
        {
            if (_activeText is not null && Math.Abs(_activeText.FontSize - _resizeFontStart) > .01 && _beforeTextDrag is not null)
            { _undo.Push(_beforeTextDrag); _redo.Clear(); }
            _beforeTextDrag = null; _resizingText = false; Capture = false; InvalidateAnnotations(); return;
        }
        if (_slider is not null) { _slider = null; Capture = false; return; }
        if (_movingText)
        {
            if (_activeText is not null && _activeText.At != _textDragOrigin && _beforeTextDrag is not null)
            {
                _undo.Push(_beforeTextDrag);
                _redo.Clear();
            }
            _beforeTextDrag = null;
            _movingText = false;
            Capture = false;
            InvalidateAnnotations();
            return;
        }
        if (_ocrMode || _recognitionBusy)
        {
            _selectingText = false;
            Capture = false;
            return;
        }
        if (_current != null && _dragMode == 1)
        {
            if (!_current.IsEmpty)
            {
                SaveUndo();
                _shapes.Add(_current);
            }

            _current = null;
            _dragMode = 0;
            Capture = false;
            InvalidateAnnotations();
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            _dragMode = 0;
            _selection = Normalize(_anchor, Clamp(e.Location));

            if (_selection.Width < 3 || _selection.Height < 3)
            {
                _selection = _pressTarget;
                _hasSelection = !_pressTarget.IsEmpty;
            }
            else
            {
                _hasSelection = true;
            }

            Invalidate();

            if (_longCapture && _hasSelection && Crop.Width > 0 && Crop.Height > 0)
            {
                StartLongCapture();
                return;
            }

            if ((_recordCapture || _gifCapture) && _hasSelection && Crop.Width > 0 && Crop.Height > 0)
            {
                StartRecording(_gifCapture);
                return;
            }

            // "截屏并自动复制": a framed region finishes straight away — write the
            // bitmap to the clipboard right here (no history, no auto-save, no tray
            // round-trip) and close the overlay without ever showing the toolbar.
            if (_autoCopy && _hasSelection && Crop.Width > 0 && Crop.Height > 0)
            {
                var copied = AutoCopySelection();
                Confirm(alreadyCopied: copied);
                return;
            }

            if (_hasSelection && _settings.CopyAfterCapture && !_ocrCapture && !_qrCaptureOnly && !_translationCapture)
                CopySelectionToClipboard();
            if (_translationCapture && _hasSelection && !TranslationVisible) _ = TranslateSelectionAsync();
            else if (_ocrCapture && _hasSelection) _ = RecognizeSelectionAsync(ocr: true);
            else if (_qrCaptureOnly && _hasSelection) _ = RecognizeSelectionAsync(ocr: false);
            else RefreshTranslationSelection();
            return;
        }

        if (_dragMode >= 2)
        {
            if (_dragMode >= 3)
            {
                _cursor = Clamp(e.Location);
                ResizeSelection(_dragMode - 3);
            }
            _dragMode = 0;
            Capture = false;
            Invalidate();
            if (_hasSelection && _settings.CopyAfterCapture && !_ocrCapture && !_qrCaptureOnly && !_translationCapture)
                CopySelectionToClipboard();
            if (_translationCapture && _hasSelection && !TranslationVisible) _ = TranslateSelectionAsync();
            else if (_ocrCapture && _hasSelection) _ = RecognizeSelectionAsync(ocr: true);
            else if (_qrCaptureOnly && _hasSelection) _ = RecognizeSelectionAsync(ocr: false);
            else RefreshTranslationSelection();
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _dragMode >= 3)
        {
            _dragMode = 0;
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (_ocrMode || _recognitionBusy) return;
        if (_bar.Tool == ToolKind.Text || _activeText is not null) return;
        if (!_hasSelection || _ime != null) return;

        var p = Clamp(CursorToLocal());

        // A double-click on the toolbar is the user cycling the line width (or
        // picking two colours in a row), not "copy this selection" — committing
        // here used to close the overlay right after the first real click.
        if (_bar.HitTest(p) != null) return;

        // Same for the dimmed desktop outside the selection.
        if (!_selection.Contains(p)) return;

        ExecuteAction(_settings.DoubleClickAction, _settings.DoubleClickExit);
    }

    protected override bool ProcessCmdKey(ref Message m, Keys keyData)
    {
        if (_ime is null && keyData != Keys.None && HotkeyDefinitions.WithWindowsKey(keyData) == _settings.CopyCoordinatesHotkey)
        {
            _cursor = CursorToLocal();
            try { Clipboard.SetText(CoordinateText()); Toast.Show(UiText.T("坐标已复制")); }
            catch (Exception ex) { Toast.Error(UiText.T("复制失败：") + ex.Message); }
            return true;
        }
        // RegisterHotKey normally routes this through TrayContext. Also handle
        // the configured key locally when the global registration is disabled.
        if (keyData != Keys.None && keyData == _settings.PinHotkey)
        {
            TryPinSelection();
            return true;
        }
        if (TranslationVisible && _ime is null && keyData == Keys.Escape) { CloseTranslation(); return true; }
        if (_qrText is not null)
        {
            if (keyData == (Keys.Control | Keys.C)) { CopyInlineQr(); return true; }
            if (keyData == Keys.Escape) { ClearQrResult(); Invalidate(); return true; }
        }
        if (_ocrMode || _recognitionBusy)
        {
            if (keyData == Keys.Escape) { ExitRecognition(); return true; }
            if (!_recognitionBusy && keyData == (Keys.Control | Keys.C)) { CopyOcrText(allIfUnselected: true); return true; }
            if (!_recognitionBusy && keyData == (Keys.Control | Keys.A) && _ocrDocument is not null)
            {
                _ocrAnchor = 0;
                _ocrEnd = _ocrDocument.Characters.Count - 1;
                _bar.ResultCopied = false;
                Invalidate();
            }
            // Recognition must never fall through to screenshot copy / confirm
            // or editing shortcuts while the text overlay is active.
            return true;
        }
        if (_ime != null)
        {
            // The sink is 1x1 px: let it swallow text editing keys, but keep
            // Escape/Enter meaningful for the text tool itself.
            if (keyData == Keys.Escape)
            {
                CancelText();
                return true;
            }

            return base.ProcessCmdKey(ref m, keyData);
        }

        if (keyData == Keys.Escape)
        {
            if (_resizingText && _activeText is not null)
            {
                _activeText.FontSize = _bar.FontSize = _resizeFontStart;
                _resizingText = false; _beforeTextDrag = null; Capture = false;
            }
            else if (_movingText && _activeText is not null)
            {
                _activeText.At = _textDragOrigin;
                _movingText = false;
                _beforeTextDrag = null;
                Capture = false;
            }
            else if (_current != null)
            {
                _current = null;
                _dragMode = 0;
                Capture = false;
            }
            else if (_activeText is not null) _activeText = null;
            else if (_bar.Tool != ToolKind.None)
            {
                _bar.Tool = ToolKind.None;
            }
            else
            {
                if (!_settings.ConfirmEscape || MessageBox.Show(this, UiText.T("退出当前截图？"), UiText.T("取消截图"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Cancel();
                return true;
            }

            InvalidateAnnotations();
            return true;
        }

        if (keyData == Keys.Enter)
        {
            if (_translationCapture) { if (!TranslationVisible) _ = TranslateSelectionAsync(); return true; }
            if (_ocrCapture || _qrCaptureOnly) { _ = RecognizeSelectionAsync(ocr: _ocrCapture); return true; }
            ExecuteAction(_settings.EnterAction, _settings.EnterExit);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { ExecuteAction(CaptureAction.QuickSave, false); return true; }

        // Ctrl+T remains an in-capture alias for the pin action.
        if (keyData == (Keys.Control | Keys.T))
        {
            TryPinSelection();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Z))
        {
            Undo();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z))
        {
            Redo();
            return true;
        }

        if (keyData == (Keys.Control | Keys.C))
        {
            Copy();
            return true;
        }

        if (keyData == (Keys.Control | Keys.S))
        {
            SaveAs();
            return true;
        }

        if (keyData == (Keys.Control | Keys.A))
        {
            ClearQrResult();
            _selection = new Rectangle(0, 0, _virtual.Width, _virtual.Height);
            _hasSelection = true;
            Invalidate();
            return true;
        }

        if (_hasSelection && keyData == Keys.R) { SetTool(ToolKind.Rectangle); return true; }
        if (_hasSelection && keyData == Keys.E) { SetTool(ToolKind.Ellipse); return true; }
        if (_hasSelection && keyData == Keys.A) { SetTool(ToolKind.Arrow); return true; }
        if (_hasSelection && keyData == Keys.P) { SetTool(ToolKind.Pen); return true; }
        if (_hasSelection && keyData == Keys.H) { SetTool(ToolKind.Marker); return true; }
        if (_hasSelection && keyData == Keys.X) { SetTool(ToolKind.Eraser); return true; }
        if (_hasSelection && keyData == Keys.T) { SetTool(ToolKind.Text); return true; }
        if (_hasSelection && keyData == Keys.M) { SetTool(ToolKind.Mosaic); return true; }

        if ((keyData & Keys.KeyCode) == Keys.C && (keyData & Keys.Control) == 0)
        {
            var color = _screenshot.GetPixel(_cursor.X, _cursor.Y);
            try { Clipboard.SetText(ColorValue(color)); Toast.Show(UiText.T("颜色已复制：") + ColorValue(color)); }
            catch { Toast.Error(UiText.T("复制颜色失败，请重试")); }
            return true;
        }

        return base.ProcessCmdKey(ref m, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.ShiftKey && MagnifierVisible)
        {
            _hexColor = !_hexColor;
            e.SuppressKeyPress = true;
            Invalidate();
            return;
        }
        if (e.KeyCode == Keys.Menu && !TranslationVisible)
        {
            _showMagnifier = true;
            Invalidate();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Menu)
        {
            _showMagnifier = false;
            Invalidate();
        }

        base.OnKeyUp(e);
    }

    // --------------------------------------------------------------- toolbar

    private void SetTool(ToolKind tool)
    {
        CommitText();
        _activeText = null;
        if (_bar.Tool == ToolKind.Marker) { _markerColor = _bar.Color; _bar.Color = _drawingColor; }
        if (tool == ToolKind.Marker && _bar.Tool != ToolKind.Marker) { _drawingColor = _bar.Color; _bar.Color = _markerColor; }
        _bar.Tool = _bar.Tool == tool ? ToolKind.None : tool;
        Invalidate();
    }

    private void Command(string id)
    {
        if (id == "colorcur")
        {
            using var picker = new ColorDialog { Color = _bar.Color, FullOpen = true, AnyColor = true };
            if (picker.ShowDialog(this) == DialogResult.OK) { _bar.Color = picker.Color; RestyleText(); }
            RestoreEditorFocus();
            Invalidate();
            return;
        }

        if (id.StartsWith("color", StringComparison.Ordinal) &&
            int.TryParse(id[5..], out var colorIndex) &&
            colorIndex >= 0 && colorIndex < AnnotationBar.Palette.Length)
        {
            _bar.Color = AnnotationBar.Palette[colorIndex];
            RestyleText();
            Invalidate();
            return;
        }

        switch (id)
        {
            case "rect": SetTool(ToolKind.Rectangle); return;
            case "ellipse": SetTool(ToolKind.Ellipse); return;
            case "arrow": SetTool(ToolKind.Arrow); return;
            case "pen": SetTool(ToolKind.Pen); return;
            case "marker": SetTool(ToolKind.Marker); return;
            case "eraser": SetTool(ToolKind.Eraser); return;
            case "text": SetTool(ToolKind.Text); return;
            case "mosaic": SetTool(ToolKind.Mosaic); return;
            case "long": StartLongCapture(); return;
            case "rec": StartRecording(false); return;
            case "gif": StartRecording(true); return;
            case "ocr": CloseTranslation(); _ = RecognizeSelectionAsync(ocr: true); return;
            case "qr": CloseTranslation(); _ = RecognizeSelectionAsync(ocr: false); return;
            case "translate": if (TranslationVisible) CloseTranslation(); else _ = TranslateSelectionAsync(); return;
            case "resultcopy": CopyInlineQr(); return;

            case "width": ShowWidthPicker(); return;
            case "fontsize": ShowSizePicker(); return;
            case "fontfamily": ShowFontPicker(); return;
            case "arrowstyle": ShowArrowPicker(); return;
            case "modebrush": case "modeline": case "moderect":
                if (_bar.Tool == ToolKind.Marker) _bar.MarkerRectangle = id == "moderect";
                if (_bar.Tool == ToolKind.Mosaic) _bar.MosaicRectangle = id == "moderect";
                if (_bar.Tool == ToolKind.Eraser) _bar.EraserRectangle = id == "moderect";
                Invalidate(); return;
            case "opacity": case "strength":
                _slider = id;
                _bar.SetSlider(id, _cursor);
                Capture = true;
                Invalidate(); return;

            case "fill":
                _bar.Fill = !_bar.Fill;
                Invalidate();
                return;

            case "bold":
                _bar.Bold = !_bar.Bold;
                RestyleText();
                Invalidate();
                return;

            case "italic":
                _bar.Italic = !_bar.Italic;
                RestyleText();
                Invalidate();
                return;

            case "undo": Undo(); return;
            case "redo": Redo(); return;
            case "save": SaveAs(); return;
            case "copy": Copy(); return;
            case "close": Cancel(); return;

            case "pin":
                TryPinSelection();
                return;
        }
    }

    private void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        RestoreShapes(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;

        _undo.Push(Snapshot());
        RestoreShapes(_redo.Pop());
    }

    private List<Shape> Snapshot() => _shapes.Select(shape => shape.Clone()).ToList();
    private void SaveUndo() { ClearQrResult(); _undo.Push(Snapshot()); _redo.Clear(); _annotationDirty = true; }
    private void RestoreShapes(List<Shape> shapes)
    {
        ClearQrResult();
        _shapes.Clear(); _shapes.AddRange(shapes); _activeText = null;
        InvalidateAnnotations();
    }
    private void InvalidateAnnotations() { _annotationDirty = true; Invalidate(); }

    private Bitmap GetAnnotationLayer(Rectangle crop)
    {
        if (_annotationLayer is null || _annotationCrop != crop)
        {
            _annotationLayer?.Dispose();
            _annotationLayer = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
            _annotationLayer.SetResolution(DeviceDpi, DeviceDpi);
            _annotationCrop = crop;
            _annotationDirty = true;
        }
        if (_annotationDirty)
        {
            AnnotationRenderer.RenderLayer(_annotationLayer, crop, _screenshot,
                _shapes.Where(shape => !ReferenceEquals(shape, _editingText)), _current);
            _annotationDirty = false;
        }
        return _annotationLayer;
    }

    private TextShape? HitText(Point p)
    {
        using var g = CreateGraphics();
        return _shapes.OfType<TextShape>().LastOrDefault(text => text.Bounds(g).Contains(p));
    }
    private void LoadTextStyle(TextShape text)
    {
        _bar.FontFamily = text.FontFamily; _bar.FontSize = text.FontSize;
        _bar.Bold = text.Bold; _bar.Italic = text.Italic; _bar.Color = text.Color;
    }

    private string CoordinateText()
    {
        var absolute = Localization.LanguageService.Format("屏幕 ({0}, {1})", _cursor.X + _virtual.X, _cursor.Y + _virtual.Y);
        return _hasSelection ? Localization.LanguageService.Format("{0}\n选区 ({1}, {2})", absolute, _cursor.X - _selection.X, _cursor.Y - _selection.Y) : absolute;
    }

    private string ColorValue(Color color) => _settings.FormatColor(color, _hexColor);

    private void UpdateTarget()
    {
        if (_targets is null || _dragging) return;
        var screen = new Point(_cursor.X + _virtual.X, _cursor.Y + _virtual.Y);
        var target = _targets.At(screen, _settings.DetectElements, includeAutomation: false);
        if (!target.IsEmpty) target.Offset(-_virtual.X, -_virtual.Y);
        var next = Rectangle.Intersect(target, new Rectangle(Point.Empty, _virtual.Size));
        if (next == _hoverWindow && _settings.DetectElements && _hoverTarget.Contains(_cursor)) next = _hoverTarget;
        _hoverWindow = Rectangle.Intersect(target, new Rectangle(Point.Empty, _virtual.Size));
        if (next != _hoverTarget)
        {
            var dirty = Rectangle.Union(_hoverTarget, next); dirty.Inflate(4, 4);
            _hoverTarget = next; Invalidate(dirty);
        }
        // A coarse/empty result is not final: accessibility trees often become
        // ready after focus changes. Retry even when the pointer stays still.
        var now = Environment.TickCount64;
        if (!_settings.DetectElements) return;
        QueueAutomationTarget(screen, now);
        if (_targetBusy || now - _lastTargetQuery < 80
            || _lastResolvedTargetPoint == screen && now - _lastTargetResolvedAt < 350) return;
        _targetBusy = true;
        _lastTargetQuery = Environment.TickCount64;
        _ = Task.Run(() => _targets.AccessibleAt(screen)).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() =>
            {
                _targetBusy = false;
                if (task.IsCompletedSuccessfully) ApplyResolvedTarget(screen, task.Result);
                _lastResolvedTargetPoint = screen;
                _lastTargetResolvedAt = Environment.TickCount64;
            }); } catch (InvalidOperationException) { }
        });
    }

    private void QueueAutomationTarget(Point screen, long now)
    {
        // UIA providers can stall while constructing their trees. Keep MSAA
        // retries independent, and allow a new point past one slow UIA request.
        if (_automationPoints.Count >= 2 || _automationPoints.Contains(screen) || now - _lastAutomationQuery < 120
            || _lastAutomationPoint == screen && now - _lastAutomationResolvedAt < 750) return;
        _automationPoints.Add(screen); _lastAutomationQuery = now;
        _ = Task.Run(() => _targets!.AutomationAt(screen)).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() =>
            {
                _automationPoints.Remove(screen);
                _lastAutomationPoint = screen; _lastAutomationResolvedAt = Environment.TickCount64;
                if (task.IsCompletedSuccessfully) ApplyResolvedTarget(screen, task.Result);
            }); } catch (InvalidOperationException) { }
        });
    }

    private void ApplyResolvedTarget(Point screen, Rectangle element)
    {
        if (_finished || _hasSelection || _dragging || element.IsEmpty
            || _cursor.X + _virtual.X != screen.X || _cursor.Y + _virtual.Y != screen.Y) return;
        element.Offset(-_virtual.X, -_virtual.Y);
        var next = Rectangle.Intersect(element, new Rectangle(Point.Empty, _virtual.Size));
        // The desktop is frozen: a late coarse result must not replace an
        // already resolved smaller target at the same pointer position.
        if (!next.Contains(_cursor) || next == _hoverTarget
            || !_hoverTarget.IsEmpty && (long)next.Width * next.Height >= (long)_hoverTarget.Width * _hoverTarget.Height) return;
        var dirty = Rectangle.Union(_hoverTarget, next); dirty.Inflate(4, 4);
        _hoverTarget = next; Invalidate(dirty);
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_settings.DisableCaptureWheel || _ocrMode || _recognitionBusy) return;
        if (MagnifierVisible)
        {
            _magnifierZoom = Math.Clamp(_magnifierZoom + Math.Sign(e.Delta), 2, 16);
            Invalidate(_lastMagnifierBounds);
            Invalidate(MagnifierDirtyBounds(_cursor));
        }
        else if (_bar.Tool == ToolKind.Text && _hasSelection) { _bar.FontSize = Math.Clamp(_bar.FontSize + Math.Sign(e.Delta), 4, 200); RestyleText(); Invalidate(); }
        base.OnMouseWheel(e);
    }
    protected override void OnDeactivate(EventArgs e)
    {
        _crosshair?.Hide();
        base.OnDeactivate(e);
        if (!_settings.ExitCaptureOnDeactivate || _finished) return;
        BeginInvoke(() =>
        {
            if (!IsDisposed && !ContainsFocus && !OwnedForms.Any(form => form.Visible) && !_optionMenus.Any(menu => menu.Visible)) Cancel();
        });
    }
    private void ExecuteAction(CaptureAction action, bool exit)
    {
        if (!_hasSelection || _finished) return;
        CommitText();
        if (action == CaptureAction.None) { if (exit) Cancel(); return; }
        var crop = Crop; if (crop.Width < 1 || crop.Height < 1) return;
        Finish(new CaptureResult
        {
            Image = RenderComposite(crop), Bounds = new Rectangle(crop.X + _virtual.X, crop.Y + _virtual.Y, crop.Width, crop.Height),
            Pin = action == CaptureAction.Pin, Action = action, KeepOpen = !exit,
            HistoryRecorded = _historyRecorded
        });
    }

    private void HandleRightClick(Point local)
    {
        _crosshair?.Hide();
        switch (_settings.RightClickAction)
        {
            case RightClickBehavior.ExitCapture:
                Capture = false;
                Cancel();
                return;

            case RightClickBehavior.ShowMenu:
                ShowCaptureContextMenu(PointToScreen(local));
                return;

            case RightClickBehavior.None:
                // 无操作：已框选时保持截图不动；未框选时仍允许退出，避免误触后无法退出
                if (_hasSelection && !_finished) return;
                Capture = false;
                Cancel();
                return;

            case RightClickBehavior.SaveFile:
                // 保存文件：弹出 Ctrl+S 同款另存为对话框，按开关决定保存成功后是否退出
                if (_hasSelection && !_finished)
                {
                    if (SaveAs() && _settings.RightClickExit) { Capture = false; Cancel(); }
                    return;
                }
                Capture = false;
                Cancel();
                return;

            case RightClickBehavior.QuickSave:
            default:
                // 快速保存：已框选 → 静默保存到快速保存目录；未框选 → 直接取消
                if (_hasSelection && !_finished)
                {
                    ExecuteAction(CaptureAction.QuickSave, exit: _settings.RightClickExit);
                    return;
                }
                Capture = false;
                Cancel();
                return;
        }
    }

    private void ShowCaptureContextMenu(Point screen)
    {
        var menu = SnipMenu.Create();
        _optionMenus.Add(menu);
        if (_hasSelection)
        {
            menu.Items.Add(SnipMenu.Item("复制到剪贴板", () => ExecuteAction(CaptureAction.Copy, exit: true), "Ctrl+C"));
            menu.Items.Add(SnipMenu.Item("贴到屏幕", () => ExecuteAction(CaptureAction.Pin, exit: true), "Ctrl+T"));
            menu.Items.Add(SnipMenu.Item("快速保存", () => ExecuteAction(CaptureAction.QuickSave, exit: true), "Ctrl+Shift+S"));
            menu.Items.Add(SnipMenu.Item("另存为…", () => { if (SaveAs()) { Capture = false; Cancel(); } }, "Ctrl+S"));
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(SnipMenu.Item("退出截图", () => { Capture = false; Cancel(); }, "Esc"));
        menu.Show(screen);
    }

    private void RestoreEditorFocus()
    {
        if (_ime is not null) _ime.Focus();
        else RestoreFocus();
    }

    private void ShowOptionMenu(ContextMenuStrip menu, string id, Point? screenLocation = null)
    {
        _optionMenus.Add(menu);
        menu.Closed += (_, _) =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) RestoreEditorFocus(); });
        };
        var bounds = _bar.ItemBounds(id);
        menu.Show(this, screenLocation.HasValue ? PointToClient(screenLocation.Value) : new Point(bounds.Left, bounds.Bottom));
    }

    private void ShowSizePicker()
    {
        var menu = SnipMenu.Create();
        var number = new NumericUpDown { Minimum = 4, Maximum = 200, DecimalPlaces = 1, Value = (decimal)_bar.FontSize, Width = (int)(120 * Ui) };
        number.ValueChanged += (_, _) => { _bar.FontSize = (float)number.Value; RestyleText(); Invalidate(); };
        menu.Items.Add(new ToolStripControlHost(number));
        foreach (var size in new[] { 8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 72 })
            menu.Items.Add(SnipMenu.Item(size + " pt", () => { _bar.FontSize = size; RestyleText(); Invalidate(); }, check: Math.Abs(_bar.FontSize - size) < .1));
        ShowOptionMenu(menu, "fontsize");
    }

    private void ShowFontPicker()
    {
        var menu = SnipMenu.Create();
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = false;
        var list = new FontFamilyList(Ui, _bar.FontFamily);
        var anchor = RectangleToScreen(_bar.ItemBounds("fontfamily"));
        var area = Rectangle.Intersect(Screen.FromRectangle(anchor).WorkingArea, RectangleToScreen(ClientRectangle));
        var gap = (int)Math.Ceiling(3 * Ui);
        var above = Math.Clamp(PointToScreen(_bar.Bounds.Location).Y - gap, area.Top, area.Bottom);
        var below = Math.Clamp(anchor.Bottom + gap, area.Top, area.Bottom);
        var chrome = 2 + menu.Padding.Vertical;
        var height = list.ItemHeight * Math.Min(12, list.Items.Count) + chrome;
        var openAbove = height > area.Bottom - below && above - area.Top > area.Bottom - below;
        var availableHeight = Math.Max(1, openAbove ? above - area.Top : area.Bottom - below);
        var rows = Math.Clamp((availableHeight - chrome) / list.ItemHeight, 1, Math.Max(1, Math.Min(12, list.Items.Count)));
        height = Math.Min(availableHeight, rows * list.ItemHeight + chrome);
        var width = Math.Min((int)Math.Round(260 * Ui), area.Width);
        // ToolStrip keeps a horizontal inset even with both margins disabled.
        list.Size = new Size(Math.Max(1, width - (int)Math.Ceiling(20 * Ui)), Math.Max(1, height - menu.Padding.Vertical));
        var host = new ToolStripControlHost(list)
        {
            AutoSize = false, Size = list.Size, Margin = Padding.Empty, Padding = Padding.Empty
        };
        menu.Items.Add(host);
        menu.AutoSize = false;
        menu.Size = new Size(width, height);
        menu.PerformLayout();
        host.Width = Math.Max(1, menu.ClientSize.Width - host.Bounds.Left - menu.Padding.Right);
        list.Chosen += family =>
        {
            _bar.FontFamily = family;
            RestyleText(); Invalidate();
            menu.Close();
        };
        list.Dismissed += () => menu.Close();
        menu.Opened += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
                list.TopIndex = Math.Max(0, list.SelectedIndex - list.ClientSize.Height / list.ItemHeight / 2);
            list.Focus();
        };
        var location = new Point(Math.Clamp(anchor.Left, area.Left, area.Right - width), openAbove ? above - height : below);
        ShowOptionMenu(menu, "fontfamily", location);
    }

    private void ShowWidthPicker()
    {
        var menu = SnipMenu.Create();
        var value = _bar.Tool == ToolKind.Marker ? _bar.MarkerWidth
            : _bar.Tool is ToolKind.Mosaic or ToolKind.Eraser ? _bar.BrushWidth : _bar.StrokeWidth;
        var slider = new TrackBar { Minimum = 1, Maximum = 100, Value = (int)value, TickFrequency = 10, Width = (int)(220 * Ui) };
        slider.ValueChanged += (_, _) =>
        {
            if (_bar.Tool == ToolKind.Marker) _bar.MarkerWidth = slider.Value;
            else if (_bar.Tool is ToolKind.Mosaic or ToolKind.Eraser) _bar.BrushWidth = slider.Value;
            else _bar.StrokeWidth = slider.Value;
            Invalidate();
        };
        menu.Items.Add(new ToolStripControlHost(slider));
        ShowOptionMenu(menu, "width");
    }

    private void ShowArrowPicker()
    {
        var menu = SnipMenu.Create();
        menu.ShowCheckMargin = false; menu.ShowImageMargin = false;
        menu.Padding = new Padding(2);
        var gallery = new ArrowStyleGallery(Ui, _bar.ArrowStyle);
        var host = new ToolStripControlHost(gallery) { AutoSize = false, Size = gallery.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        menu.Items.Add(host);
        // ContextMenuStrip keeps an item inset even without image/check
        // margins. Reserve that inset instead of clipping the hosted control.
        menu.AutoSize = false;
        menu.Size = new Size(gallery.Width + (int)Math.Ceiling(20 * Ui), gallery.Height + (int)Math.Ceiling(8 * Ui));
        gallery.Chosen += style => { _bar.ArrowStyle = style; menu.Close(); Invalidate(); };
        ShowOptionMenu(menu, "arrowstyle");
    }

    // --------------------------------------------------------------- results

    private async Task RecognizeSelectionAsync(bool ocr)
    {
        if (!_hasSelection || _recognitionBusy || _ocrMode) return;
        CommitText();
        ClearQrResult();
        var crop = Crop;
        if (crop.Width < 1 || crop.Height < 1) return;
        using var image = RenderComposite(crop);
        var version = ++_recognitionVersion;
        _recognitionBusy = true;
        _ocrMode = ocr;
        _ocrCrop = crop;
        // Remember what was actually handed to the OCR engine: its bounding boxes
        // are in those pixels, and the overlay draws in selection pixels.
        _ocrSourceSize = image.Size;
        _ocrDocument = null;
        _ocrAnchor = _ocrEnd = -1;
        _showMagnifier = false;
        _bar.OcrMode = ocr;
        _bar.OcrCopyVisible = false;
        _bar.ResultCopied = false;
        _bar.SetStatus(() => ocr ? UiText.T("正在识别文字…  Esc 返回标注") : UiText.T("正在识别二维码 / 条码…  Esc 取消"));
        UpdateCursor();
        Invalidate();
        try
        {
            if (ocr)
            {
                var document = await Task.Run(() => Services.RecognizeTextAsync(image, _settings.OcrLanguage, _settings.OcrEngine));
                if (IsDisposed || version != _recognitionVersion) return;
                _recognitionBusy = false;
                _ocrDocument = document;
                _bar.OcrCopyVisible = document.Characters.Count > 0;
                _bar.SetStatus(() => document.Characters.Count == 0
                    ? UiText.T("未识别到文字，按 Esc 返回后调整选区")
                    : UiText.T("拖动选字 · Ctrl+A 全选 · Ctrl+C 复制 · Esc 返回编辑"));
            }
            else
            {
                var text = await Task.Run(() => Services.DecodeQr(image));
                if (IsDisposed || version != _recognitionVersion) return;
                ShowInlineQr(text);
            }
        }
        catch (Exception ex)
        {
            if (IsDisposed || version != _recognitionVersion) return;
            _recognitionBusy = false;
            _bar.SetStatus(() => UiText.T("识别失败：") + ex.Message);
            Toast.Error(_bar.Status);
        }
        finally
        {
            if (!IsDisposed && version == _recognitionVersion)
            {
                UpdateCursor();
                Invalidate();
            }
        }
    }

    private void ExitRecognition()
    {
        _recognitionVersion++;
        _recognitionBusy = _ocrMode = _selectingText = false;
        Capture = false;
        _ocrDocument = null;
        _ocrAnchor = _ocrEnd = -1;
        _bar.OcrMode = false;
        _bar.OcrCopyVisible = false;
        _bar.ResultCopied = false;
        _bar.Status = "";
        UpdateCursor();
        Invalidate();
    }

    private void ShowInlineQr(string? text)
    {
        _recognitionBusy = false;
        _qrText = string.IsNullOrEmpty(text) ? null : text;
        _bar.SetStatus(() => _qrText ?? UiText.T("未检测到二维码 / 条码，请调整选区后重试"));
        _bar.ResultCopyVisible = _qrText is not null;
        _bar.ResultCopied = false;
        UpdateCursor(); Invalidate();
    }
    private void ClearQrResult()
    {
        if (_qrText is null && !_bar.ResultCopyVisible) return;
        _qrText = null; _bar.ResultCopyVisible = false; _bar.ResultCopied = false; _bar.Status = "";
    }
    private void CopyInlineQr()
    {
        if (string.IsNullOrEmpty(_qrText)) return;
        try { Clipboard.SetText(_qrText); _bar.ResultCopied = true; }
        catch { _bar.ResultCopied = false; Toast.Error(UiText.T("复制失败，请重试")); }
        Invalidate();
    }

    private void CopyOcrText(bool allIfUnselected = false)
    {
        var text = allIfUnselected && (_ocrAnchor < 0 || _ocrEnd < 0)
            ? _ocrDocument?.Text ?? "" : _ocrDocument?.GetText(_ocrAnchor, _ocrEnd) ?? "";
        if (text.Length == 0)
        {
            _bar.SetStatus(() => UiText.T("请先拖动选择文字，或按 Ctrl+A 全选"));
        }
        else
        {
            try
            {
                Clipboard.SetText(text);
                _bar.ResultCopied = true;
                _bar.SetStatus(() => UiText.T("已复制选中文字，可继续划词 · Esc 返回标注"));
            }
            catch { _bar.SetStatus(() => UiText.T("复制失败，请重试")); }
        }
        Invalidate();
    }

    private void DrawOcrSelection(Graphics g)
    {
        if (!_ocrMode || _ocrDocument is null) return;
        using var fill = new SolidBrush(Color.FromArgb(90, 0, 103, 192));
        using var line = new Pen(Color.FromArgb(150, _bar.Accent), Math.Max(1, Ui));
        var start = Math.Min(_ocrAnchor, _ocrEnd);
        var end = Math.Max(_ocrAnchor, _ocrEnd);
        // Merge the selected characters into one block per text line; filling every
        // character separately produced a ghosted double outline over the glyphs.
        var blocks = new List<RectangleF>();
        for (var i = 0; i < _ocrDocument.Characters.Count; i++)
        {
            var bounds = OcrToScreen(_ocrDocument.Characters[i].Bounds);
            if (start >= 0 && i >= start && i <= end)
            {
                var center = (bounds.Top + bounds.Bottom) / 2;
                var previousCenter = blocks.Count > 0 ? (blocks[^1].Top + blocks[^1].Bottom) / 2 : float.MaxValue;
                if (blocks.Count > 0 && Math.Abs(center - previousCenter) < bounds.Height * .5f)
                    blocks[^1] = RectangleF.Union(blocks[^1], bounds);
                else blocks.Add(bounds);
                continue;
            }
            g.DrawLine(line, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        }
        foreach (var block in blocks)
        {
            // One seamless block per line, with 4 px of horizontal breathing room on
            // both ends so the first and last glyphs are wrapped completely. The
            // overlay may paint past the crop edge, so only the window bounds clamp.
            var slack = Math.Max(4f, 4f * Ui);
            var left = Math.Max(0f, block.Left - slack);
            var right = block.Right + slack;
            var rect = RectangleF.FromLTRB(left, block.Top, right, block.Bottom);
            g.FillRectangle(fill, rect);
        }
    }

    private void UpdateHint()
    {
        var hint = _barVisible ? AnnotationBar.Hint(_bar.HitTest(_cursor), _ocrMode) : "";
        if (_barVisible && _bar.HitTest(_cursor) == "pin")
        {
            var binding = HotkeyDefinitions.Format(_settings.PinHotkey);
            hint = _settings.PinHotkey is Keys.None or (Keys.Control | Keys.T) ? UiText.T("贴图（Ctrl+T）") : Localization.LanguageService.Format("贴图（{0} / Ctrl+T）", binding);
        }
        if (TranslationVisible && _bar.HitTest(_cursor) == "translate") hint = UiText.T("关闭截图翻译（Esc）");
        if (_qrText is not null && _bar.MessageBounds.Contains(_cursor) && _bar.HitTest(_cursor) != "resultcopy") hint = _qrText;
        if (_hoverHint == hint) return;
        _hoverHint = hint;
        _toolTip.SetToolTip(this, hint);
    }

    private Rectangle Crop =>
        Rectangle.Intersect(_selection, new Rectangle(0, 0, _virtual.Width, _virtual.Height));

    // OCR boxes are expressed in the pixels of the bitmap that was recognised; the
    // overlay paints in selection pixels. A selection can be bigger or smaller than
    // that bitmap (preprocessing, DPI scaling), so always map through the ratio.
    private float OcrScaleX => _ocrSourceSize.Width > 0 ? (float)_ocrCrop.Width / _ocrSourceSize.Width : 1f;
    private float OcrScaleY => _ocrSourceSize.Height > 0 ? (float)_ocrCrop.Height / _ocrSourceSize.Height : 1f;

    private RectangleF OcrToScreen(RectangleF bounds) => new(
        _ocrCrop.Left + bounds.X * OcrScaleX,
        _ocrCrop.Top + bounds.Y * OcrScaleY,
        bounds.Width * OcrScaleX,
        bounds.Height * OcrScaleY);

    private PointF OcrToLocal(Point point) => new(
        (point.X - _ocrCrop.Left) / OcrScaleX,
        (point.Y - _ocrCrop.Top) / OcrScaleY);

    private Bitmap RenderComposite(Rectangle crop)
    {
        var image = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(image);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_screenshot, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
        DrawTranslationOverlay(g, new Point(-crop.X, -crop.Y));

        if (_shapes.Count > 0) g.DrawImageUnscaled(GetAnnotationLayer(crop), 0, 0);

        return image;
    }

    private void Copy()
    {
        if (!_hasSelection) return;
        CommitText();

        var crop = Crop;
        if (crop.Width < 1 || crop.Height < 1) return;

        Finish(new CaptureResult
        {
            Image = RenderComposite(crop),
            Bounds = new Rectangle(crop.X + _virtual.X, crop.Y + _virtual.Y, crop.Width, crop.Height),
            Pin = _resultPin,
            HistoryRecorded = _historyRecorded
        });
    }

    private bool SaveAs()
    {
        if (!_hasSelection) return false;
        CommitText();

        var crop = Crop;
        if (crop.Width < 1 || crop.Height < 1) return false;

        var extension = _settings.RememberImageExtension ? _settings.LastImageExtension : ".png";
        var folder = new[] { _settings.QuickSaveDirectory, _settings.SaveDirectory }.FirstOrDefault(Directory.Exists);
        var fileName = Services.ImageFileName(_settings, extension);
        // 预填时避开默认目录里的同名文件；用户自己在弹窗里确认覆盖的路径不再二次改名。
        if (folder is not null) fileName = Path.GetFileName(Services.UniqueFilePath(folder, fileName, extension));

        using var dialog = new SaveFileDialog
        {
            Filter = UiText.T("PNG 图片|*.png|JPEG 图片|*.jpg"),
            FileName = fileName,
            InitialDirectory = folder ?? "",
            FilterIndex = _settings.RememberImageExtension && _settings.LastImageExtension != ".png" ? 2 : 1,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;

        try
        {
            using var image = RenderComposite(crop);
            var jpeg = dialog.FilterIndex == 2;
            Services.SaveImage(image, dialog.FileName, jpeg, _settings.JpegQuality);
            // Record the saved capture immediately: saving is an independent exit path
            // that never reaches the overlay's Done callback.
            _historyRecorded = true;
            try { Saved?.Invoke(image, new Rectangle(crop.X + _virtual.X, crop.Y + _virtual.Y, crop.Width, crop.Height), dialog.FileName); }
            catch (Exception ex) { AppLog.Write("记录截屏历史失败", ex); }
            if (_settings.SoundOnSave) _ = Services.PlaySoundAsync(_settings.CaptureSound);
            if (_settings.RememberImageExtension) { _settings.LastImageExtension = jpeg ? ".jpg" : ".png"; _settings.Save(); }
            Toast.Show(UiText.T("已保存：") + Path.GetFileName(dialog.FileName));
            return true;
        }
        catch (Exception ex)
        {
            Toast.Show(UiText.T("保存失败：") + ex.Message);
            return false;
        }
    }

    private bool CopySelectionToClipboard(bool notify = false)
    {
        var crop = Crop;
        if (crop.Width < 1 || crop.Height < 1) return false;
        try
        {
            using var image = RenderComposite(crop);
            Services.CopyImage(image, _settings.CaptureBitmapClipboard, _settings);
            if (notify) Toast.Show(Localization.LanguageService.Format("已复制  {0} × {1}", crop.Width, crop.Height));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("自动复制截图失败", ex);
            Toast.Error(UiText.T("自动复制截图失败：") + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Auto-copy hotkey path: put the framed region on the clipboard directly via
    /// <see cref="Clipboard.SetImage(Image)"/> and report success so the caller can
    /// close the overlay. Deliberately bypasses <c>Services.CopyImage</c> and every
    /// history / auto-save branch, so a broken save directory or clipboard format
    /// setting can never swallow the copy.
    /// </summary>
    private bool AutoCopySelection()
    {
        var crop = Crop;
        if (crop.Width < 1 || crop.Height < 1) return false;
        try
        {
            using var image = RenderComposite(crop);
            Clipboard.SetImage(image);
            AppLog.Write($"截屏并自动复制：框选 {crop.Width}×{crop.Height}，已写入剪贴板并关闭");
            Toast.Show(Localization.LanguageService.Format("已复制  {0} × {1}", crop.Width, crop.Height));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("截屏并自动复制：写入剪贴板失败", ex);
            Toast.Error(UiText.T("自动复制截图失败：") + ex.Message);
            return false;
        }
    }

    /// <summary>Starts a screen recording / GIF of the framed region (toolbar or mode).</summary>
    private void StartRecording(bool gif)
    {
        var crop = Crop;
        if (crop.Width < 20 || crop.Height < 20)
        {
            Toast.Error(UiText.T("录制选区过小，请重新框选"));
            return;
        }
        var region = new Rectangle(crop.X + _virtual.X, crop.Y + _virtual.Y, crop.Width, crop.Height);
        CommitText();
        Hide();

        // The floating card is purely user-controlled: hiding it leaves the global
        // record hotkey (or Esc / Enter) as the only way to stop.
        var showControls = gif ? _settings.GifShowTimer : _settings.RecShowTimer;
        var form = new RecordingForm(region, _settings, gif, showControls);
        form.FormClosed += (_, _) =>
        {
            if (IsDisposed) return;
            _finished = true;
            Hide();
            Close();
            Done?.Invoke(null);
        };
        form.Show();
    }

    private void StartLongCapture()
    {
        if (_longForm != null) return;
        if (!_settings.LongCaptureEnabled)
        {
            Toast.Error(UiText.T("长截图功能已关闭"));
            return;
        }
        var crop = Crop;
        if (crop.Width < 40 || crop.Height < 40)
        {
            Toast.Error(UiText.T("长截图选区过小，请重新框选"));
            return;
        }

        var region = new Rectangle(crop.X + _virtual.X, crop.Y + _virtual.Y, crop.Width, crop.Height);
        CommitText();
        Hide();

        var form = new LongCaptureForm(region, _settings);
        _longForm = form;
        // 完成 / 复制 both finish the session: the long image is saved or copied and
        // the whole capture overlay leaves capture mode.
        form.Saved += () =>
        {
            _longForm = null;
            form.Dispose();
            if (IsDisposed) return;
            _finished = true;
            Hide();
            Close();
        };
        form.Copied += () =>
        {
            _longForm = null;
            form.Dispose();
            if (IsDisposed) return;
            _finished = true;
            Hide();
            Close();
        };
        form.Cancelled += () =>
        {
            _longForm = null;
            form.Dispose();
            if (IsDisposed) return;
            // Cancel aborts the whole capture: no falling back to the selection overlay.
            _finished = true;
            Hide();
            Close();
            Done?.Invoke(null);
        };
        // 编辑 hands the stitched image to the annotation editor; the editor owns its own
        // copy / save / pin actions, so the capture overlay just gets out of the way.
        form.Edited += image =>
        {
            _longForm = null;
            form.Dispose();
            if (IsDisposed)
            {
                image.Dispose();
                return;
            }
            _finished = true;
            Hide();
            BeginInvoke(new Action(() =>
            {
                var editor = new LongEditForm(image, _settings) { TopMost = true };
                editor.Shown += (_, _) =>
                {
                    // Force the editor in front of the browser the capture was taken from.
                    editor.Activate();
                    editor.BringToFront();
                    Win32.SetForegroundWindow(editor.Handle);
                };
                editor.ShowDialog();
                if (!IsDisposed) Close();
            }));
        };
        form.Show();
    }

    private void Confirm(bool alreadyCopied = false)
    {
        if (_finished || !_hasSelection) return;

        CommitText();

        var crop = Crop;
        if (crop.Width < 1 || crop.Height < 1) return;

        Finish(new CaptureResult
        {
            Image = RenderComposite(crop),
            Bounds = new Rectangle(crop.X + _virtual.X, crop.Y + _virtual.Y, crop.Width, crop.Height),
            Pin = _resultPin,
            AlreadyCopied = alreadyCopied,
            HistoryRecorded = _historyRecorded
        });
    }

    internal bool TryPinSelection()
    {
        if (_finished || !_hasSelection || _dragging || _current is not null || _movingText || _resizingText || _dragMode != 0)
            return false;
        if (Crop.Width < 1 || Crop.Height < 1) return false;
        _resultPin = true;
        Confirm();
        return true;
    }

    private void Finish(CaptureResult result)
    {
        if (!result.KeepOpen) { _finished = true; Close(); }
        Done?.Invoke(result);
    }

    private void Cancel()
    {
        if (_finished) return;

        CommitText();
        _finished = true;
        Close();
        Done?.Invoke(null);
    }

    // ------------------------------------------------------------ text input

    /// <summary>
    /// Snipaste's text tool has no visible box: the glyphs and the caret are
    /// painted straight onto the frozen screenshot, while a 1x1 edit control
    /// follows the caret to receive keyboard and IME input. Nothing opaque is
    /// ever laid over the image, so the live preview is exactly what the
    /// committed <see cref="TextShape"/> will look like.
    /// </summary>
    private void BeginText(Point p)
    {
        StartTextInput(p, null, null);
    }

    private void EditText(TextShape text, Point caretPoint)
    {
        CommitText();
        LoadTextStyle(text);
        StartTextInput(text.At, text, caretPoint);
    }

    private void StartTextInput(Point p, TextShape? existing, Point? caretPoint)
    {
        CommitText();

        _textAnchor = p;
        _textText = existing?.Text ?? string.Empty;

        _activeText = existing; _editingText = existing;
        _textFont = TextShape.CreateFont(_bar.FontFamily, _bar.FontSize, TextShape.StyleOf(_bar.Bold, _bar.Italic));

        _ime = new ImeSink
        {
            BorderStyle = BorderStyle.None,
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.None,
            TabStop = false,
            ShortcutsEnabled = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(30, 30, 30),
            Font = _textFont,
            Location = p,
            Size = new Size(1, 1)
        };
        _ime.Text = _textText;
        _ime.Select(_textText.Length, 0);

        _ime.TextChanged += (_, _) =>
        {
            _textText = _ime?.Text ?? string.Empty;
            RestartCaret();
            RefreshText();
        };

        _ime.KeyUp += (_, _) =>
        {
            if (_ime is null) return;
            RestartCaret();
            RefreshText();
        };

        _ime.CompositionChanged += () =>
        {
            RestartCaret();
            RefreshText();
        };

        _ime.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift) return;

            e.SuppressKeyPress = true;
            if (CommitText()) Invalidate();
        };

        Controls.Add(_ime);

        RestartCaret();
        _ime.Focus();
        if (caretPoint is Point point) PlaceTextCaret(point, false);
        RefreshText(); InvalidateAnnotations(); UpdateCursor();
    }

    private void RestyleText()
    {
        if (_activeText is not null && _ime is null)
        {
            SaveUndo();
            _activeText.FontFamily = _bar.FontFamily; _activeText.FontSize = _bar.FontSize;
            _activeText.Bold = _bar.Bold; _activeText.Italic = _bar.Italic; _activeText.Color = _bar.Color;
            InvalidateAnnotations();
        }
        if (_ime is null) return;

        var oldFont = _textFont;
        _textFont = TextShape.CreateFont(_bar.FontFamily, _bar.FontSize, TextShape.StyleOf(_bar.Bold, _bar.Italic));
        _ime.Font = _textFont;
        oldFont?.Dispose();

        RefreshText(); UpdateCursor();
        Invalidate(_selection);
    }

    private void RestartCaret()
    {
        _caretOn = true;

        if (_caretTimer is null)
        {
            _caretTimer = new System.Windows.Forms.Timer { Interval = 530 };
            _caretTimer.Tick += (_, _) =>
            {
                _caretOn = !_caretOn;
                InvalidateCaret();
            };
        }
        else
        {
            _caretTimer.Stop();
        }

        _caretTimer.Start();
    }

    /// <summary>Invalidate only the caret / composition strip, never the 4K overlay.</summary>
    private void InvalidateCaret()
    {
        var dirty = Rectangle.Union(_caretRect, _composeRect);
        if (dirty.IsEmpty) return;

        dirty.Inflate(12, 4);
        Invalidate(dirty);
    }

    private void RefreshText()
    {
        if (_ime is null || _textFont is null) return;

        var previous = Rectangle.Union(_textPreviewBounds, Rectangle.Union(_caretRect, _composeRect));
        UpdateCaret();
        using (var graphics = CreateGraphics())
            _textPreviewBounds = TextEditLayout.Bounds(graphics, _textFont, PreviewText(), _textAnchor);

        var dirty = Rectangle.Union(previous, Rectangle.Union(_textPreviewBounds, Rectangle.Union(_caretRect, _composeRect)));
        if (dirty.IsEmpty) return;

        dirty.Inflate(12, 12);
        Invalidate(dirty);
    }

    private void PlaceTextCaret(Point point, bool extend)
    {
        if (_ime is null || _textFont is null) return;
        using var graphics = CreateGraphics();
        var index = TextEditLayout.HitTest(graphics, _textFont, _textText, _textAnchor, point);
        var anchor = extend ? _ime.SelectionStart : index;
        _ime.Select(Math.Min(anchor, index), Math.Abs(index - anchor));
        _ime.Focus(); RestartCaret(); RefreshText();
    }

    private void UpdateCaret()
    {
        _caretRect = Rectangle.Empty;
        _composeRect = Rectangle.Empty;

        if (_ime is null || _textFont is null) return;

        using var g = CreateGraphics();
        var index = Math.Clamp(_ime.SelectionStart, 0, _textText.Length);
        _caretRect = TextEditLayout.Caret(g, _textFont, _textText, _textAnchor, index);

        // keep the hidden sink under the caret so IME windows anchor correctly
        _ime.Bounds = new Rectangle(_caretRect.X, _caretRect.Bottom, 1, 1);

        var compose = _ime.Composition;

        if (compose.Length > 0)
        {
            _composeRect = TextEditLayout.Bounds(g, _textFont, compose, _caretRect.Location);
        }
    }

    private void DrawTextPreview(Graphics g)
    {
        if (_ime is null || _textFont is null) return;

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var compose = _ime.Composition;
        var index = Math.Clamp(_ime.SelectionStart, 0, _textText.Length);
        var selectionLength = _ime.SelectionLength;
        var display = PreviewText();
        if (compose.Length == 0 && selectionLength > 0)
        {
            using var selection = new SolidBrush(Color.FromArgb(105, _settings.Accent));
            foreach (var rect in TextEditLayout.Selection(g, _textFont, _textText, _textAnchor, index, selectionLength))
                g.FillRectangle(selection, rect);
        }
        using (var brush = new SolidBrush(_bar.Color))
            if (display.Length > 0) g.DrawString(display, _textFont, brush, _textAnchor, StringFormat.GenericTypographic);

        if (_caretOn && (selectionLength == 0 || compose.Length > 0))
        {
            var caret = compose.Length == 0 ? _caretRect
                : TextEditLayout.Caret(g, _textFont, display, _textAnchor, index + Math.Clamp(_ime.CompositionCaret, 0, compose.Length));
            TextEditLayout.DrawCaret(g, caret, _settings.Accent);
        }

        if (_composeRect.Width > 0)
        {
            using var underline = new Pen(_bar.Color, 1f);
            g.DrawLine(underline, _composeRect.Left, _composeRect.Bottom - 1, _composeRect.Right, _composeRect.Bottom - 1);
        }
    }

    private string PreviewText()
    {
        if (_ime is null || _ime.Composition.Length == 0) return _textText;
        var index = Math.Clamp(_ime.SelectionStart, 0, _textText.Length);
        return _textText[..index] + _ime.Composition + _textText[(index + _ime.SelectionLength)..];
    }

    private bool CommitText()
    {
        if (_ime is null) return false;

        var text = _textText;
        var existing = _editingText;
        var anchor = _textAnchor;
        var color = _bar.Color;
        var fontSize = _bar.FontSize;
        var family = _bar.FontFamily;
        var bold = _bar.Bold;
        var italic = _bar.Italic;

        EndText();

        if (existing is not null)
        {
            var changed = existing.Text != text || existing.FontFamily != family || existing.FontSize != fontSize
                || existing.Color != color || existing.Bold != bold || existing.Italic != italic;
            if (changed)
            {
                SaveUndo();
                existing.Text = text; existing.FontFamily = family; existing.FontSize = fontSize;
                existing.Color = color; existing.Bold = bold; existing.Italic = italic;
                if (string.IsNullOrWhiteSpace(text)) _shapes.Remove(existing);
            }
            _activeText = _shapes.Contains(existing) ? existing : null;
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            SaveUndo();
            _activeText = new TextShape
            {
                At = anchor,
                Text = text,
                Color = color,
                FontSize = fontSize,
                FontFamily = family,
                Bold = bold,
                Italic = italic
            };
            _shapes.Add(_activeText);
        }

        InvalidateAnnotations(); RestoreFocus(); UpdateCursor();
        return true;
    }

    private void CancelText()
    {
        if (_ime is null) return;

        var existing = _editingText;
        EndText(); _activeText = existing;
        if (existing is not null) LoadTextStyle(existing);
        RestoreFocus();
        InvalidateAnnotations(); UpdateCursor();
    }

    private void EndText()
    {
        _caretTimer?.Stop();
        _caretTimer?.Dispose();
        _caretTimer = null;

        if (_ime is not null)
        {
            Controls.Remove(_ime);
            _ime.Dispose();
            _ime = null;
        }

        _textFont?.Dispose();
        _textFont = null;

        _textText = string.Empty;
        _editingText = null;
        _selectingEditorText = false;
        _textPreviewBounds = Rectangle.Empty;
        _caretRect = Rectangle.Empty;
        _composeRect = Rectangle.Empty;
    }

    private void RestoreFocus()
    {
        if (IsDisposed || !IsHandleCreated || !Visible) return;

        Activate();
    }

    // --------------------------------------------------------------- helpers

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, _virtual.Width - 1),
        Math.Clamp(p.Y, 0, _virtual.Height - 1));

    /// <summary>
    /// Physical cursor position relative to the overlay. Using GetCursorPos keeps
    /// the maths in device pixels regardless of the per-monitor DPI WinForms
    /// reports for this window.
    /// </summary>
    private Point CursorToLocal()
    {
        var p = Win32.CursorPosition();
        return new Point(p.X - _virtual.X, p.Y - _virtual.Y);
    }

    private static Rectangle Normalize(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// Resize handle lookup. Corners win (both axes), then the full length of every edge
    /// becomes draggable — not just the middle handle — with a 6 px tolerance band.
    /// </summary>
    private int HitAnchor(Point p)
    {
        var r = _selection;
        if (r.Width <= 0 || r.Height <= 0) return -1;

        var corner = AnchorHit;
        var edge = Math.Max(3, (int)Math.Round(6 * Ui));
        // Tiny selections keep a movable interior: never let the bands swallow them.
        edge = Math.Min(edge, Math.Max(2, Math.Min(r.Width, r.Height) / 3));

        if (Math.Abs(p.X - r.Left) <= corner && Math.Abs(p.Y - r.Top) <= corner) return 0;
        if (Math.Abs(p.X - r.Right) <= corner && Math.Abs(p.Y - r.Top) <= corner) return 2;
        if (Math.Abs(p.X - r.Left) <= corner && Math.Abs(p.Y - r.Bottom) <= corner) return 5;
        if (Math.Abs(p.X - r.Right) <= corner && Math.Abs(p.Y - r.Bottom) <= corner) return 7;

        var withinX = p.X >= r.Left - edge && p.X <= r.Right + edge;
        var withinY = p.Y >= r.Top - edge && p.Y <= r.Bottom + edge;
        var dTop = Math.Abs(p.Y - r.Top);
        var dBottom = Math.Abs(p.Y - r.Bottom);
        var dLeft = Math.Abs(p.X - r.Left);
        var dRight = Math.Abs(p.X - r.Right);

        var best = -1;
        var bestDistance = edge + 1;
        if (withinX && dTop <= edge && dTop < bestDistance) { best = 1; bestDistance = dTop; }
        if (withinX && dBottom <= edge && dBottom < bestDistance) { best = 6; bestDistance = dBottom; }
        if (withinY && dLeft <= edge && dLeft < bestDistance) { best = 3; bestDistance = dLeft; }
        if (withinY && dRight <= edge && dRight < bestDistance) { best = 4; bestDistance = dRight; }
        return best;
    }

    private void ResizeSelection(int anchor)
    {
        var dx = _cursor.X - _dragStartCursor.X;
        var dy = _cursor.Y - _dragStartCursor.Y;
        var r = _dragStartSelection;

        var left = (float)r.Left;
        var top = (float)r.Top;
        var right = (float)r.Right;
        var bottom = (float)r.Bottom;

        switch (anchor)
        {
            case 0: left += dx; top += dy; break;
            case 1: top += dy; break;
            case 2: right += dx; top += dy; break;
            case 3: left += dx; break;
            case 4: right += dx; break;
            case 5: left += dx; bottom += dy; break;
            case 6: bottom += dy; break;
            case 7: right += dx; bottom += dy; break;
        }

        left = Math.Clamp(left, 0, _virtual.Width - 1);
        top = Math.Clamp(top, 0, _virtual.Height - 1);
        right = Math.Clamp(right, left + 1, _virtual.Width);
        bottom = Math.Clamp(bottom, top + 1, _virtual.Height);

        _selection = Rectangle.FromLTRB((int)left, (int)top, (int)right, (int)bottom);
    }

    /// <summary>
    /// Repaints only what a selection change can touch (selection, badge, toolbar)
    /// instead of the whole 4K overlay.
    /// </summary>
    private void Repaint(Rectangle? previous = null)
    {
        if (!_hasSelection && previous is not { Width: > 0 })
        {
            Invalidate();
            return;
        }

        var region = _selection;

        if (previous is { Width: > 0 } old)
            region = Rectangle.Union(region, old);

        if (_barVisible)
            region = Rectangle.Union(region, _bar.Bounds);

        var margin = (int)Math.Round(120 * Ui);
        region.Inflate(margin, margin);

        if (MagnifierVisible)
        {
            // The lens can extend beyond the selection's normal repaint margin.
            // Erase its previous position as well as painting the next one.
            if (!_lastMagnifierBounds.IsEmpty) region = Rectangle.Union(region, _lastMagnifierBounds);
            region = Rectangle.Union(region, MagnifierDirtyBounds(_cursor));
        }

        Invalidate(Rectangle.Intersect(region, new Rectangle(Point.Empty, _virtual.Size)));
    }

    /// <summary>Before a selection exists only the two crosshair lines move.</summary>
    private void RepaintCrosshair(Point previous)
    {
        if (MagnifierVisible)
        {
            Invalidate(_lastMagnifierBounds);
            Invalidate(MagnifierDirtyBounds(_cursor));
        }

        if (_crosshair is not null) return;

        var w = Math.Max(2, (int)Math.Round(2 * Ui));

        Invalidate(new Rectangle(previous.X - w, 0, w * 2, _virtual.Height));
        Invalidate(new Rectangle(_cursor.X - w, 0, w * 2, _virtual.Height));
        Invalidate(new Rectangle(0, previous.Y - w, _virtual.Width, w * 2));
        Invalidate(new Rectangle(0, _cursor.Y - w, _virtual.Width, w * 2));
    }

    private void UpdateCursor()
    {
        var fullCrosshair = _crosshair is not null && !_hasSelection && !_dragging && !_finished && ContainsFocus;
        // Keep the familiar native small cross as a visible centre marker.
        if (fullCrosshair) _crosshair!.Move(_cursor); else _crosshair?.Hide();
        if (_recognitionBusy) { Cursor = _bar.HitTest(_cursor) is not null ? Cursors.Hand : Cursors.AppStarting; return; }
        if (_ocrMode)
        {
            Cursor = _bar.Bounds.Contains(_cursor) ? Cursors.Hand
                : _ocrDocument?.HitTest(OcrToLocal(_cursor), tolerance: 16 * Ui) >= 0
                    ? Cursors.IBeam : Cursors.Default;
            return;
        }
        if (_hasSelection && _barVisible && _bar.Bounds.Contains(_cursor))
        {
            Cursor = Cursors.Hand;
            return;
        }

        if (_hasSelection)
        {
            if (_activeText is not null)
            {
                using var graphics = CreateGraphics();
                if (Rectangle.Inflate(TextResizeHandle(graphics, _activeText), AnchorHit / 2, AnchorHit / 2).Contains(_cursor))
                { Cursor = Cursors.SizeNWSE; return; }
                if (TextMoveFrame(graphics, _activeText).Contains(_cursor)
                    && !(_ime is not null ? _textPreviewBounds : Rectangle.Ceiling(_activeText.Bounds(graphics))).Contains(_cursor))
                { Cursor = Cursors.SizeAll; return; }
            }
            var hitText = HitText(_cursor);
            if (_ime is not null || (_bar.Tool == ToolKind.Text || _activeText is not null) && hitText is not null
                || _bar.Tool == ToolKind.Text && _selection.Contains(_cursor))
            {
                SetTextCursor(hitText);
                return;
            }
            if (_bar.Tool != ToolKind.None && _selection.Contains(_cursor))
            {
                var edgeIndex = HitAnchor(_cursor);
                if (edgeIndex < 0)
                {
                    Cursor = Cursors.Default;
                    return;
                }
                Cursor = edgeIndex switch
                {
                    0 or 7 => Cursors.SizeNWSE,
                    2 or 5 => Cursors.SizeNESW,
                    1 or 6 => Cursors.SizeNS,
                    _ => Cursors.SizeWE
                };
                return;
            }

            var index = HitAnchor(_cursor);

            if (index >= 0)
            {
                Cursor = index switch
                {
                    0 or 7 => Cursors.SizeNWSE,
                    1 or 6 => Cursors.SizeNS,
                    2 or 5 => Cursors.SizeNESW,
                    3 or 4 => Cursors.SizeWE,
                    _ => Cursors.Default
                };
                return;
            }

            if (_selection.Contains(_cursor))
            {
                Cursor = Cursors.SizeAll;
                return;
            }
        }

        Cursor = Cursors.Default;
    }

    private Rectangle TextResizeHandle(Graphics graphics, TextShape text)
    {
        var bounds = ReferenceEquals(text, _editingText) && _textFont is not null
            ? TextEditLayout.Bounds(graphics, _textFont, _textText, _textAnchor) : text.Bounds(graphics);
        var size = Math.Max(7, (int)(7 * Ui));
        return new Rectangle((int)bounds.Right - size / 2, (int)bounds.Bottom - size / 2, size, size);
    }

    private Rectangle TextMoveFrame(Graphics graphics, TextShape text) =>
        Rectangle.Inflate(Rectangle.Ceiling(text.Bounds(graphics)), Math.Max(5, (int)(5 * Ui)), Math.Max(5, (int)(5 * Ui)));

    private void SetTextCursor(TextShape? text)
    {
        using var graphics = CreateGraphics();
        using var font = TextShape.CreateFont(text?.FontFamily ?? _bar.FontFamily, text?.FontSize ?? _bar.FontSize,
            TextShape.StyleOf(text?.Bold ?? _bar.Bold, text?.Italic ?? _bar.Italic));
        var height = Math.Clamp((int)Math.Ceiling((_textFont ?? font).GetHeight(graphics)), 8, 1024);
        if (_textCursor?.Height == height) { Cursor = _textCursor.Cursor; return; }
        try
        {
            var cursor = new TextIBeamCursor(height); var previous = _textCursor;
            _textCursor = cursor; Cursor = cursor.Cursor; previous?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { Cursor = Cursors.IBeam; }
        catch (ArgumentException) { Cursor = Cursors.IBeam; }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _crosshair?.Hide(); base.OnMouseLeave(e);
    }
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); if (_crosshair is not null) UpdateCursor(); }
    private Rectangle MagnifierDirtyBounds(Point point)
    {
        var source = Math.Max(3, (int)Math.Round(MagnifierSize * Ui) / Zoom);
        if (source % 2 == 0) source--;
        var size = source * Zoom; var gap = (int)Math.Round(24 * Ui);
        var x = point.X + gap; var y = point.Y + gap;
        if (x + size > _virtual.Width) x = point.X - gap - size;
        if (y + size > _virtual.Height) y = point.Y - gap - size;
        var labelHeight = (int)Math.Ceiling(_badgeFont.Height * 3 + 16 * Ui);
        var labelWidth = (int)Math.Round(380 * Ui);
        var labelX = Math.Clamp(x, 0, Math.Max(0, _virtual.Width - labelWidth));
        return Rectangle.Inflate(Rectangle.Union(new Rectangle(x, y, size, size),
            new Rectangle(labelX, Math.Max(0, y - labelHeight - 8), labelWidth, labelHeight)), 4, 4);
    }
}
