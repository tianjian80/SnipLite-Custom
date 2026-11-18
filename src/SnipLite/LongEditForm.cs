using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SnipLite;

/// <summary>
/// Annotation editor for a finished long screenshot. The stitched image is shown
/// fit-to-width with vertical scrolling and the regular capture tool bar sits at the
/// bottom, so every annotation tool works on the full-length image.
/// </summary>
internal sealed class LongEditForm : Form
{
    private readonly Bitmap _image;
    private readonly AppSettings _settings;
    private readonly float _ui;
    private readonly AnnotationBar _bar = new();
    private readonly List<Shape> _shapes = new();
    private readonly Stack<List<Shape>> _undo = new();
    private readonly Stack<List<Shape>> _redo = new();
    private readonly EditorCanvas _canvas = new() { Dock = DockStyle.Fill };
    private EditorToolbarForm? _toolbar;
    private bool _rightPending;

    private Shape? _current;
    private TextBox? _editor;
    private TextShape? _editing;
    private Point _anchor;
    private Point _cursor;
    private bool _dragging;
    private string? _slider;
    private Rectangle _canvasArea;
    private Rectangle _display;
    private float _zoom = 1f;
    private int _scrollY;
    private int _maxScroll;

    public LongEditForm(Bitmap image, AppSettings settings)
    {
        _image = image;
        _settings = settings;
        _ui = Math.Max(1f, settings.CreateInterfaceFont().Size / 9f);

        Text = UiText.T("编辑长截图");
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        BackColor = Color.White;
        ForeColor = AppTheme.Current.Text;
        Font = settings.CreateInterfaceFont();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        KeyPreview = true;
        MinimumSize = new Size((int)(560 * _ui), (int)(360 * _ui));

        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        // 预留屏幕底部空间：主窗口高度必须给悬浮工具栏留出位置，否则工具栏无处可放。
        var reserved = (int)Math.Round(120 * _ui);
        var maxHeight = Math.Max((int)(320 * _ui), work.Height - reserved - (int)(24 * _ui));
        ClientSize = new Size(Math.Min((int)(1000 * _ui), work.Width - 60),
            Math.Min(Math.Min((int)(780 * _ui), work.Height - 60), maxHeight));

        _bar.Scale = _ui;
        _bar.Accent = settings.Accent;
        _bar.StrokeWidth = 3f;
        _bar.ItemVisible = id => id is not ("long" or "ocr" or "translate" or "qr");

        // 主窗口内部只保留画布：工具栏是完全独立的悬浮窗体，位于窗口外侧下方。
        _canvas.BackColor = AppTheme.Current.Field;
        _canvas.Paint += (_, e) => DrawImage(e.Graphics);
        _canvas.MouseDown += (_, e) => OnMouseDown(e);
        _canvas.MouseMove += (_, e) => OnMouseMove(e);
        _canvas.MouseUp += (_, e) => OnMouseUp(e);
        _canvas.MouseDoubleClick += (_, e) => OnMouseDoubleClick(e);
        _canvas.MouseWheel += (_, e) => OnMouseWheel(e);
        Controls.Add(_canvas);
        // No system context menu inside the editor.
        ContextMenuStrip = new ContextMenuStrip();
    }

    internal void AttachToolbar()
    {
        var toolbar = new EditorToolbarForm(this, _bar, _ui) { Owner = this };
        toolbar.Prepare();
        _toolbar = toolbar;
        // 安全最小宽度：单排图标的实测总宽（不低于 520px），主窗口到此自动停住。
        var minWidth = Math.Max((int)Math.Round(520 * _ui), toolbar.MinimumSize.Width + (int)Math.Round(18 * _ui));
        MinimumSize = new Size(Math.Max(MinimumSize.Width, minWidth), MinimumSize.Height);
        if (Width < minWidth) Width = minWidth;
        toolbar.Show(this);
        toolbar.SyncToOwner();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AttachToolbar();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        _toolbar?.SyncToOwner();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 单向联动：主窗口移动/缩放时，悬浮工具栏实时跟随并等比缩放。
        _toolbar?.SyncToOwner();
        Repaint();
    }

    internal void BarInvalidate() => _toolbar?.RefreshBar();

    internal string? SliderId => _slider;

    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;

    internal void BarSlider(string id, Point point)
    {
        _slider = id;
        _bar.SetSlider(id, point);
        _toolbar?.RefreshBar();
        Repaint();
    }

    internal void BarSliderEnd() => _slider = null;

    internal void BarTextUpdate() => Repaint();

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        Repaint();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Every Invalidate() on the window funnels here; the image itself is painted by
        // the canvas control, which fills the whole client area.
        e.Graphics.Clear(AppTheme.Current.Field);
        _bar.CanUndo = _undo.Count > 0;
        _bar.CanRedo = _redo.Count > 0;
        Repaint();
    }

    private void Repaint() => _canvas.Invalidate();

    /// <summary>Double-buffered image pane.</summary>
    private sealed class EditorCanvas : Panel
    {
        public EditorCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.StandardClick
                | ControlStyles.StandardDoubleClick, true);
        }

        /// <summary>
        /// Edge zones fall through to the window frame so dragging the border resizes the
        /// window instead of starting a new annotation rectangle on the canvas.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) // WM_NCHITTEST
            {
                var p = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, (m.LParam.ToInt32() >> 16) & 0xFFFF));
                if (p.X >= 0 && p.Y >= 0 && p.X < ClientSize.Width && p.Y < ClientSize.Height)
                {
                    const int side = 8, corner = 14;
                    var nearSide = p.X < side || p.X >= ClientSize.Width - side
                        || p.Y < side || p.Y >= ClientSize.Height - side;
                    var nearCorner = (p.X < corner || p.X >= ClientSize.Width - corner)
                        && (p.Y < corner || p.Y >= ClientSize.Height - corner);
                    if (nearSide || nearCorner)
                    {
                        m.Result = new IntPtr(-1); // HTTRANSPARENT → parent frame resizes
                        return;
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>The canvas occupies everything above the dedicated tool strip.</summary>
    private void DrawImage(Graphics g)
    {
        _canvasArea = _canvas.ClientRectangle;
        var area = _canvasArea;
        if (area.Width < 8 || area.Height < 8) return;

        _zoom = Math.Max(0.0001f, (area.Width - (int)(8 * _ui)) / (float)_image.Width);
        var content = Math.Max(1, (int)Math.Round(_image.Height * _zoom));
        _maxScroll = Math.Max(0, content - area.Height);
        _scrollY = Math.Clamp(_scrollY, 0, _maxScroll);

        var width = Math.Max(1, (int)Math.Round(_image.Width * _zoom));
        var left = area.X + (area.Width - width) / 2;
        _display = new Rectangle(left, area.Y - _scrollY, width, content);

        var state = g.Save();
        g.SetClip(area);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_image, _display);

        var shapes = g.Save();
        g.TranslateTransform(_display.X, _display.Y);
        g.ScaleTransform(_zoom, _zoom);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var shape in _shapes) shape.Render(g, _image);
        _current?.Render(g, _image);
        g.Restore(shapes);
        g.Restore(state);
    }

    private Point ToImage(Point p) => new(
        (int)Math.Round((p.X - _display.X) / _zoom),
        (int)Math.Round((p.Y - _display.Y) / _zoom));

    private float WidthOf() => _bar.Tool switch
    {
        ToolKind.Marker => _bar.MarkerWidth,
        ToolKind.Eraser or ToolKind.Mosaic => _bar.BrushWidth,
        _ => _bar.StrokeWidth
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Middle)
        {
            RunAction(_settings.MiddleClickAction, _settings.MiddleClickExit);
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            // An unfinished shape wins over the configured right-click action.
            if (_dragging || _current is not null)
            {
                _dragging = false;
                _current = null;
                Repaint();
                return;
            }
            _rightPending = true;
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (!_display.Contains(e.Location) || _bar.Tool == ToolKind.None) return;
        CommitEditor();
        _cursor = e.Location;
        var point = ToImage(e.Location);
        var zoom = Math.Max(0.01f, _zoom);

        if (_bar.Tool == ToolKind.Text)
        {
            BeginEditor(point, e.Location);
            return;
        }

        _anchor = point;
        _current = Shape.Create(_bar.Tool, point, _bar.Color, WidthOf() * _ui / zoom);
        if (_current is MosaicShape mosaic) mosaic.Block = Math.Max(2, (int)Math.Round(_bar.MosaicBlock * _ui / zoom));
        _dragging = true;
        Repaint();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _cursor = e.Location;

        if (_slider is not null)
        {
            _bar.SetSlider(_slider, e.Location);
            Repaint();
            return;
        }

        if (_dragging && _current is not null)
        {
            _current.DragTo(ToImage(e.Location));
            Repaint();
            return;
        }

        Cursor = _display.Contains(e.Location) && _bar.Tool != ToolKind.None ? Cursors.Cross : Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // 左键双击: ignored while a text box is open so typing never triggers it.
        if (e.Button != MouseButtons.Left || _editor is not null) return;
        if (!_display.Contains(e.Location)) return;
        RunAction(_settings.DoubleClickAction, _settings.DoubleClickExit);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            if (_rightPending)
            {
                _rightPending = false;
                HandleRightClick();
            }
            return;
        }
        if (_slider is not null)
        {
            _slider = null;
            return;
        }
        if (!_dragging || _current is null) return;
        _dragging = false;
        _current.DragTo(ToImage(e.Location));
        if (!_current.IsEmpty)
        {
            _shapes.Add(_current);
            SaveUndo();
        }
        _current = null;
        Repaint();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_maxScroll <= 0) return;
        _scrollY = Math.Clamp(_scrollY - e.Delta, 0, _maxScroll);
        Repaint();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape when _editor is not null: CancelEditor(); return true;
            case Keys.Escape: Close(); return true;
            case Keys.Enter when _editor is null:
                RunAction(_settings.EnterAction, _settings.EnterExit);
                return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y:
            case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Control | Keys.C: Copy(); return true;
            case Keys.Control | Keys.S: Save(); return true;
            case Keys.Enter when _editor is not null: CommitEditor(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------ text

    private void BeginEditor(Point imagePoint, Point location)
    {
        CommitEditor();
        var shape = (TextShape)Shape.Create(ToolKind.Text, imagePoint, _bar.Color, 1f);
        shape.FontSize = _bar.FontSize * _ui / Math.Max(0.01f, _zoom);
        shape.FontFamily = _bar.FontFamily;
        shape.Bold = _bar.Bold;
        shape.Italic = _bar.Italic;
        _editing = shape;

        var box = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Multiline = true,
            Width = (int)(240 * _ui),
            Height = (int)(Math.Max(28, _bar.FontSize * _ui * 1.9)),
            Font = new Font(_bar.FontFamily, _bar.FontSize * _ui * Math.Min(1f, _zoom * 1.5f)),
            Location = new Point(Math.Min(location.X, ClientSize.Width - (int)(250 * _ui)),
                Math.Min(location.Y, _canvasArea.Bottom - (int)(40 * _ui)))
        };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; CommitEditor(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelEditor(); }
        };
        Controls.Add(box);
        box.BringToFront();
        box.Focus();
        _editor = box;
    }

    private void CommitEditor()
    {
        if (_editor is null) return;
        var text = _editor.Text;
        var shape = _editing;
        var box = _editor;
        _editor = null;
        _editing = null;
        Controls.Remove(box);
        box.Dispose();

        if (shape is not null && !string.IsNullOrWhiteSpace(text))
        {
            shape.Text = text;
            _shapes.Add(shape);
            SaveUndo();
        }
        Repaint();
    }

    private void CancelEditor()
    {
        var box = _editor;
        _editor = null;
        _editing = null;
        if (box is null) return;
        Controls.Remove(box);
        box.Dispose();
        Repaint();
    }

    // ------------------------------------------------------------ commands

    internal void Command(string id, Point point)
    {
        switch (id)
        {
            case "rect": _bar.Tool = ToolKind.Rectangle; break;
            case "ellipse": _bar.Tool = ToolKind.Ellipse; break;
            case "arrow": _bar.Tool = ToolKind.Arrow; break;
            case "pen": _bar.Tool = ToolKind.Pen; break;
            case "marker": _bar.Tool = ToolKind.Marker; break;
            case "mosaic": _bar.Tool = ToolKind.Mosaic; break;
            case "text": _bar.Tool = ToolKind.Text; break;
            case "eraser": _bar.Tool = ToolKind.Eraser; break;
            case "fill": _bar.Fill = !_bar.Fill; break;
            case "bold": _bar.Bold = !_bar.Bold; break;
            case "italic": _bar.Italic = !_bar.Italic; break;
            case "undo": Undo(); return;
            case "redo": Redo(); return;
            case "close": Close(); return;
            case "copy": Copy(); return;
            case "save": Save(); return;
            case "pin": Pin(); return;
            case "width":
            case "fontsize":
            case "opacity":
            case "strength":
                _slider = id;
                _bar.SetSlider(id, point);
                break;
            default:
                if (id.StartsWith("color", StringComparison.Ordinal)) SetColor(id);
                break;
        }
        Repaint();
        _toolbar?.RefreshBar();
    }

    // ------------------------------------------------------------ 首选项 → 截屏 → 按键

    /// <summary>Runs a configured key/mouse action against the composed image.</summary>
    private void RunAction(CaptureAction action, bool exit)
    {
        switch (action)
        {
            case CaptureAction.Copy: Copy(exit); return;
            case CaptureAction.Pin: Pin(exit); return;
            case CaptureAction.QuickSave: SaveQuick(exit); return;
            default:
                if (exit) Close();
                return;
        }
    }

    /// <summary>Mouse right click: 退出 / 无操作 / 保存 / 快速保存 / 菜单.</summary>
    private void HandleRightClick()
    {
        switch (_settings.RightClickAction)
        {
            case RightClickBehavior.ExitCapture:
                Close();
                return;
            case RightClickBehavior.None:
                return;
            case RightClickBehavior.SaveFile:
                Save(_settings.RightClickExit);
                return;
            case RightClickBehavior.QuickSave:
                SaveQuick(_settings.RightClickExit);
                return;
            default:
                ShowMenu();
                return;
        }
    }

    private void ShowMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(UiText.T("复制到剪贴板"), null, (_, _) => Copy());
        menu.Items.Add(UiText.T("贴到屏幕"), null, (_, _) => Pin());
        menu.Items.Add(UiText.T("另存为…"), null, (_, _) => Save());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(UiText.T("关闭编辑"), null, (_, _) => Close());
        menu.Show(Cursor.Position);
    }

    private void SetColor(string id)
    {
        if (id == "colorcur")
        {
            using var dialog = new ColorDialog { Color = _bar.Color, FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) _bar.Color = dialog.Color;
            return;
        }
        if (int.TryParse(id.AsSpan(5), out var index) && index >= 0 && index < AnnotationBar.Palette.Length)
            _bar.Color = AnnotationBar.Palette[index];
    }

    // ------------------------------------------------------------ undo

    private void SaveUndo()
    {
        _undo.Push(_shapes.Select(shape => shape.Clone()).ToList());
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_shapes.Select(shape => shape.Clone()).ToList());
        var previous = _undo.Pop();
        _shapes.Clear();
        _shapes.AddRange(previous);
        Repaint();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_shapes.Select(shape => shape.Clone()).ToList());
        var next = _redo.Pop();
        _shapes.Clear();
        _shapes.AddRange(next);
        Repaint();
    }

    // ------------------------------------------------------------ output

    private Bitmap Compose()
    {
        var result = new Bitmap(_image.Width, _image.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.DrawImageUnscaled(_image, 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var shape in _shapes) shape.Render(g, _image);
        return result;
    }

    private void Copy(bool exit = true)
    {
        CommitEditor();
        try
        {
            using var image = Compose();
            Services.CopyImage(image, _settings.CaptureBitmapClipboard, _settings);
            Toast.Show(UiText.T("图像已复制"));
        }
        catch (Exception ex)
        {
            Toast.Error(UiText.T("复制失败：") + ex.Message);
            return;
        }
        if (exit) Close();
    }

    private void Save(bool exit = true)
    {
        CommitEditor();
        var folder = new[] { _settings.QuickSaveDirectory, _settings.SaveDirectory }.FirstOrDefault(Directory.Exists);
        using var dialog = new SaveFileDialog
        {
            Filter = UiText.T("PNG 图片|*.png|JPEG 图片|*.jpg"),
            FileName = Services.ImageFileName(_settings, ".png"),
            InitialDirectory = folder ?? "",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var image = Compose();
            var jpeg = Path.GetExtension(dialog.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase);
            Services.SaveImage(image, dialog.FileName, jpeg, _settings.JpegQuality);
            Toast.Show(UiText.T("已保存：") + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            Toast.Error(UiText.T("保存失败：") + ex.Message);
            return;
        }
        if (exit) Close();
    }

    /// <summary>Silent save into the configured quick-save folder.</summary>
    private void SaveQuick(bool exit = true)
    {
        CommitEditor();
        var folder = Directory.Exists(_settings.QuickSaveDirectory) ? _settings.QuickSaveDirectory : _settings.SaveDirectory;
        if (!Directory.Exists(folder))
        {
            Save(exit);
            return;
        }
        try
        {
            using var image = Compose();
            var file = Path.Combine(folder, Services.ImageFileName(_settings, ".png"));
            Services.SaveImage(image, file, false, _settings.JpegQuality);
            Toast.Show(UiText.T("已保存：") + Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            Toast.Error(UiText.T("保存失败：") + ex.Message);
            return;
        }
        if (exit) Close();
    }

    private void Pin(bool exit = true)
    {
        CommitEditor();
        var image = Compose();
        new PinForm(image, null, 100, _settings).Show();
        if (exit) Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image.Dispose();
        base.Dispose(disposing);
    }
}
