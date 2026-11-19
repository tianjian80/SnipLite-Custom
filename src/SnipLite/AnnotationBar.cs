using System.Drawing.Drawing2D;

namespace SnipLite;

/// <summary>
/// The floating toolbar under the selection, drawn and hit-tested by hand.
/// Layout follows Snipaste: one row of tools / actions, plus a second row with
/// the active tool's options (line width, current colour, palette).
/// </summary>
internal sealed class AnnotationBar
{
    /// <summary>Snipaste's palette: 11 columns x 2 rows, saturated then pastel.</summary>
    public static readonly Color[] Palette =
    {
        Color.FromArgb(0, 0, 0),
        Color.FromArgb(74, 74, 74),
        Color.FromArgb(120, 40, 40),
        Color.FromArgb(229, 57, 53),
        Color.FromArgb(240, 74, 16),
        Color.FromArgb(247, 148, 29),
        Color.FromArgb(253, 216, 53),
        Color.FromArgb(76, 175, 80),
        Color.FromArgb(0, 172, 193),
        Color.FromArgb(33, 120, 220),
        Color.FromArgb(140, 60, 200),

        Color.FromArgb(255, 255, 255),
        Color.FromArgb(205, 205, 205),
        Color.FromArgb(216, 146, 110),
        Color.FromArgb(248, 187, 208),
        Color.FromArgb(255, 204, 128),
        Color.FromArgb(255, 241, 118),
        Color.FromArgb(197, 225, 165),
        Color.FromArgb(128, 222, 190),
        Color.FromArgb(129, 212, 250),
        Color.FromArgb(179, 157, 219),
        Color.FromArgb(225, 190, 231)
    };

    public const int Columns = 11;

    private const int Button = 34;
    private const int Gap = 2;
    private const int Pad = 5;
    // Two colour rows fit the same 34 px content height as every editor.
    private const int Swatch = 17;
    private const int Group = 9;
    private const int OcrEngineChooserWidth = 140;

    private static Color BarBack => AppTheme.Current.Card;
    private static Color IconColor => AppTheme.Current.Text;
    private static Color Divider => AppTheme.Current.Border;
    private static Color SwatchBorder => AppTheme.Current.Border;

    /// <summary>Snipaste's default theme blue, also the fallback accent colour.</summary>
    public static readonly Color DefaultAccent = Color.FromArgb(0, 145, 234);

    private readonly List<(Rectangle Rect, string Id)> _items = new();
    private readonly List<Rectangle> _dividers = new();

    private Rectangle _main;
    private Rectangle _options;
    private Rectangle _message;

    public bool OcrMode { get; set; }
    public bool TranslationMode { get; set; }
    public int AttachedPanelHeight { get; set; }
    public bool OcrCopyVisible { get; set; }
    /// <summary>True while the smart-redaction pass is running: the shield shows its loading look.</summary>
    public bool RedactBusy { get; set; }
    public string OcrEngineLabel { get; set; } = OcrEngines.Label(OcrEngineKind.SimdPaddle);
    public bool CanUndo { get; set; }
    public bool CanRedo { get; set; }
    private string _rawStatus = "";
    private Func<string>? _localizedStatus;
    public string Status { get => _localizedStatus?.Invoke() ?? _rawStatus; set { _localizedStatus = null; _rawStatus = value; } }
    public void SetStatus(Func<string> status) => _localizedStatus = status;
    private int ResultButtonWidth()
    {
        using var font = new Font("Microsoft YaHei UI", 11 * Scale, GraphicsUnit.Pixel);
        var label = OcrMode ? UiText.T("复制文字") : UiText.T("复制");
        var width = Math.Max(TextRenderer.MeasureText(label, font).Width, TextRenderer.MeasureText(UiText.T("已复制"), font).Width);
        return Math.Clamp(width + Px(18), Px(OcrMode ? 80 : 56), Px(150));
    }
    public bool ResultCopyVisible { get; set; }
    public bool ResultCopied { get; set; }

    public ToolKind Tool { get; set; } = ToolKind.None;
    public Color Color { get; set; } = Palette[3];
    public float StrokeWidth { get; set; } = 3f;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public float FontSize { get; set; } = 16f;
    public ArrowStyle ArrowStyle { get; set; }
    public float MarkerWidth { get; set; } = 24;
    public float BrushWidth { get; set; } = 28;
    public int MarkerOpacity { get; set; } = 50;
    public int MosaicBlock { get; set; } = 12;
    public bool MarkerRectangle { get; set; }
    public bool MosaicRectangle { get; set; } = true;
    public bool EraserRectangle { get; set; }
    public bool RectangleMode => Tool switch
    {
        ToolKind.Marker => MarkerRectangle,
        ToolKind.Mosaic => MosaicRectangle,
        ToolKind.Eraser => EraserRectangle,
        _ => false
    };

    /// <summary>Theme colour: the active-tool band and the bar border.</summary>
    public Color Accent { get; set; } = DefaultAccent;

    /// <summary>Shapes are outlines by default; the second cell of row 2 fills them.</summary>
    public bool Fill { get; set; }

    public bool Bold { get; set; }
    public bool Italic { get; set; }

    /// <summary>UI scale (physical pixels per 96 dpi).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Draw only the buttons and dividers; the host window paints the card itself.</summary>
    public bool Flat { get; set; }

    /// <summary>Stack the buttons into one or more vertical columns (long capture).</summary>
    public bool Vertical { get; set; }

    /// <summary>Height budget for the vertical columns; extra columns are added when needed.</summary>
    public int VerticalMaxHeight { get; set; }

    /// <summary>Wrap width of the tool-options block shown next to the vertical column.</summary>
    public int VerticalOptionsWidth { get; set; }

    /// <summary>Place the options block left of the icon column (toolbar on the left side).</summary>
    public bool VerticalOptionsFirst { get; set; }

    public Rectangle Bounds { get; private set; }
    public Rectangle MainBounds => _main;
    public Rectangle OptionsBounds => _options;
    public Rectangle MessageBounds => _message;

    public bool Visible => _items.Count > 0;

    /// <summary>Which toolbar buttons are configured to show; null shows every one.</summary>
    public Func<string, bool>? ItemVisible { get; set; }

    /// <summary>
    /// Wrap the rows at this width instead of keeping one long row (embedded hosts
    /// such as the long-capture preview). Zero keeps the normal single-row layout.
    /// </summary>
    public int WrapWidth { get; set; }

    private bool Shows(string id) => ItemVisible?.Invoke(id) ?? true;

    private int Px(int value) => (int)Math.Round(value * Scale);

    private bool ShowsFill => Tool is ToolKind.Rectangle or ToolKind.Ellipse;

    private bool ShowsTextStyle => Tool == ToolKind.Text;

    public void Clear()
    {
        _items.Clear();
        _dividers.Clear();
        Bounds = Rectangle.Empty;
        _main = Rectangle.Empty;
        _options = Rectangle.Empty;
    }

    public void Layout(Rectangle selection, Size screen)
    {
        if (Vertical)
        {
            LayoutVertical();
            return;
        }

        _items.Clear();
        _dividers.Clear();

        var button = Px(Button);
        var gap = Px(Gap);
        var pad = Px(Pad);
        var swatch = Math.Min(Px(Swatch), button / 2);
        var group = Px(Group);

        var step = swatch;
        var rows = (Palette.Length + Columns - 1) / Columns;
        var gridHeight = rows * step;

        var showOptions = !OcrMode && Tool != ToolKind.None;

        // ---------------------------------------------------------- row 1
        var wrap = WrapWidth > 0;
        var x = pad;
        var y = pad;
        var mainRight = pad;

        void Add(int left, int top, int width, int height, string id)
        {
            _items.Add((new Rectangle(left, top, width, height), id));
            mainRight = Math.Max(mainRight, left + width);
        }

        void NewLine()
        {
            x = pad;
            y += button + gap;
        }

        void Cell(string id)
        {
            if (!Shows(id)) return;
            if (wrap && x > pad && x + button > WrapWidth) NewLine();
            Add(x, y, button, button, id);
            x += button + gap;
        }

        void Divider()
        {
            // A separator that would fall past the row edge simply starts a new row,
            // and it always keeps at least one button of its own group on that row.
            if (wrap && x > pad && x + group * 2 + button > WrapWidth) { NewLine(); return; }
            x += group - gap;
            _dividers.Add(new Rectangle(x, y + pad, 1, button - pad * 2));
            x += group;
        }

        // Groups stay together, empty groups and their dividers disappear, and the
        // remaining buttons pack left with no gap in between.
        var groups = new[]
        {
            new[] { "rect", "ellipse", "arrow", "pen", "marker", "mosaic", "text", "eraser" },
            new[] { "undo", "redo" },
            new[] { "ocr", "translate", "qr", "redact", "long", "rec", "gif" },
            new[] { "close", "pin", "save", "copy" }
        };

        var drewGroup = false;
        foreach (var items in groups)
        {
            var visible = items.Where(Shows).ToArray();
            if (visible.Length == 0) continue;
            if (drewGroup) Divider();
            drewGroup = true;
            foreach (var id in visible) Cell(id);
        }

        var mainWidth = mainRight + pad;
        var mainHeight = y + button + pad;

        _main = new Rectangle(0, 0, mainWidth, mainHeight);

        var totalWidth = mainWidth;
        var totalHeight = mainHeight;

        // ---------------------------------------------------------- row 2
        if (showOptions)
        {
            var oy = mainHeight + gap;
            var ox = pad;
            var rowTop = oy + pad;
            var contentHeight = Math.Max(button, gridHeight);
            var rowStep = contentHeight + gap;
            var optionsRight = pad;

            void Place(int left, int top, int width, int height, string id)
            {
                _items.Add((new Rectangle(left, top, width, height), id));
                optionsRight = Math.Max(optionsRight, left + width);
            }

            void WrapRow(int width)
            {
                if (!wrap || ox <= pad || ox + width <= WrapWidth) return;
                ox = pad;
                rowTop += rowStep;
            }

            void Divider2()
            {
                if (wrap && ox > pad && ox + group * 2 + button > WrapWidth) { ox = pad; rowTop += rowStep; return; }
                ox += group - gap;
                _dividers.Add(new Rectangle(ox, rowTop + pad, 1, contentHeight - pad * 2));
                ox += group;
            }

            void Option(string id, int w = Button)
            {
                var width = Px(w);
                WrapRow(width);
                Place(ox, rowTop, width, button, id);
                ox += width + gap;
            }

            if (!ShowsTextStyle) Option("width");
            if (Tool is ToolKind.Marker or ToolKind.Mosaic or ToolKind.Eraser)
            {
                Option("modebrush");
                Option("moderect");
            }
            if (Tool == ToolKind.Marker) Option("opacity", 125);
            if (Tool == ToolKind.Mosaic) Option("strength", 125);
            if (Tool == ToolKind.Arrow) Option("arrowstyle", 70);

            if (ShowsFill)
            {
                Divider2();
                WrapRow(button);
                Place(ox, rowTop, button, button, "fill");
                ox += button + gap;
            }

            if (ShowsTextStyle)
            {
                Divider2();
                WrapRow(button);
                Place(ox, rowTop, button, button, "bold");
                ox += button + gap;
                WrapRow(button);
                Place(ox, rowTop, button, button, "italic");
                ox += button + gap;
                Option("fontfamily", 156);
                Option("fontsize", 58);
            }

            if (Tool is not ToolKind.Eraser and not ToolKind.Mosaic)
            {
                Divider2();
                WrapRow(gridHeight);
                Place(ox, rowTop, gridHeight, gridHeight, "colorcur");
                ox += gridHeight + gap * 2;

                WrapRow(Columns * step);
                var gridX = ox;

                for (var i = 0; i < Palette.Length; i++)
                {
                    var col = i % Columns;
                    var row = i / Columns;
                    Place(gridX + col * step, rowTop + row * step, swatch, swatch, "color" + i);
                }

                ox = gridX + Columns * step;
            }

            var optionsHeight = rowTop + contentHeight + pad - oy;
            _options = new Rectangle(0, oy, Math.Max(optionsRight + pad, mainWidth), optionsHeight);
            totalWidth = Math.Max(totalWidth, _options.Width);
            totalHeight = oy + optionsHeight;
        }
        else
        {
            _options = Rectangle.Empty;
        }

        _message = Rectangle.Empty;
        if (Status.Length > 0)
        {
            var copyWidth = ResultButtonWidth();
            var engineWidth = Px(OcrEngineChooserWidth);
            var messageWidth = mainWidth;
            if (OcrMode)
            {
                // Hint text stays on the left; the engine chooser sits on the right
                // with its centre aligned under the "识" tool button above it.
                var ocr = ItemBounds("ocr");
                var copyReserve = OcrCopyVisible ? copyWidth + Px(8) : 0;
                var rightPad = Px(8);
                var hintMin = Px(280);
                if (!ocr.IsEmpty)
                {
                    var center = ocr.Left + ocr.Width / 2;
                    messageWidth = Math.Max(messageWidth, center + engineWidth / 2 + copyReserve + rightPad);
                }
                messageWidth = Math.Max(messageWidth, hintMin + Px(20) + engineWidth + copyReserve + rightPad);
            }
            _message = new Rectangle(0, totalHeight - 1, messageWidth, Px(OcrMode ? 44 : 32));
            if (OcrMode)
            {
                var ocr = ItemBounds("ocr");
                var copyReserve = OcrCopyVisible ? copyWidth + Px(8) : 0;
                var rightPad = Px(8);
                var minLeft = _message.Left + Px(280) + Px(10);
                var maxLeft = _message.Right - engineWidth - copyReserve - rightPad;
                var ex = ocr.IsEmpty
                    ? maxLeft
                    : Math.Clamp(ocr.Left + ocr.Width / 2 - engineWidth / 2, minLeft, maxLeft);
                Add(ex, _message.Top + Px(5), engineWidth, Px(34), "ocrengine");
            }
            if (ResultCopyVisible)
                Add(_message.Right - copyWidth - Px(6), _message.Top + Px(4), copyWidth, _message.Height - Px(8), "resultcopy");
            if (OcrMode && OcrCopyVisible)
                Add(_message.Right - copyWidth - Px(6), _message.Top + Px(5), copyWidth, Px(34), "ocrtextcopy");
            totalWidth = Math.Max(totalWidth, _message.Width);
            totalHeight = _message.Bottom;
        }

        var left = selection.Left + (selection.Width - totalWidth) / 2;
        var topPos = selection.Bottom + (int)Math.Round(10 * Scale);
        var placementHeight = totalHeight + (AttachedPanelHeight > 0 ? Px(4) + AttachedPanelHeight : 0);

        if (topPos + placementHeight > screen.Height - 2)
            topPos = selection.Top - placementHeight - (int)Math.Round(10 * Scale);

        if (topPos < 2)
            topPos = Math.Min(selection.Bottom - placementHeight - 10, screen.Height - placementHeight - 2);

        left = Math.Clamp(left, 2, Math.Max(2, screen.Width - totalWidth - 2));
        topPos = Math.Clamp(topPos, 2, Math.Max(2, screen.Height - placementHeight - 2));

        Bounds = new Rectangle(left, topPos, totalWidth, totalHeight);

        Offset(_items, left, topPos);
        Offset(_dividers, left, topPos);
        _main.Offset(left, topPos);
        _options.Offset(left, topPos);
        _message.Offset(left, topPos);
    }

    /// <summary>
    /// Vertical layout used by the long-capture preview: one or more compact icon
    /// columns, with the active tool's options wrapped in a block beside them.
    /// </summary>
    private void LayoutVertical()
    {
        _items.Clear();
        _dividers.Clear();
        _main = Rectangle.Empty;
        _options = Rectangle.Empty;
        _message = Rectangle.Empty;

        var pad = Px(Pad);
        var button = Px(Button);
        var gap = Px(Gap);
        var group = Px(Group);
        var swatch = Math.Min(Px(Swatch), button / 2);
        var step = swatch;
        var rows = (Palette.Length + Columns - 1) / Columns;
        var gridHeight = rows * step;
        var showOptions = !OcrMode && Tool != ToolKind.None;

        // ------------------------------------------------------ options block
        var options = new List<(Rectangle Rect, string Id)>();
        var optionDividers = new List<Rectangle>();
        var optionsWidth = 0;
        var optionsHeight = 0;

        if (showOptions)
        {
            var limit = VerticalOptionsWidth > 0 ? VerticalOptionsWidth : Px(228);
            var contentHeight = Math.Max(button, gridHeight);
            var rowStep = contentHeight + gap;
            var ox = 0;
            var rowTop = 0;

            void Row(int width)
            {
                if (ox > 0 && ox + width > limit) { ox = 0; rowTop += rowStep; }
            }

            void Sep()
            {
                if (ox > 0 && ox + group * 2 + button > limit) { ox = 0; rowTop += rowStep; return; }
                ox += group - gap;
                optionDividers.Add(new Rectangle(ox, rowTop, 1, contentHeight));
                ox += group;
            }

            void Option(string id, int width)
            {
                Row(width);
                options.Add((new Rectangle(ox, rowTop, width, button), id));
                optionsWidth = Math.Max(optionsWidth, ox + width);
                ox += width + gap;
            }

            if (!ShowsTextStyle) Option("width", button);
            if (Tool is ToolKind.Marker or ToolKind.Mosaic or ToolKind.Eraser)
            {
                Option("modebrush", button);
                Option("moderect", button);
            }
            if (Tool == ToolKind.Marker) Option("opacity", Px(125));
            if (Tool == ToolKind.Mosaic) Option("strength", Px(125));
            if (Tool == ToolKind.Arrow) Option("arrowstyle", Px(70));

            if (ShowsFill)
            {
                Sep();
                Option("fill", button);
            }
            if (ShowsTextStyle)
            {
                Sep();
                Option("bold", button);
                Option("italic", button);
                Option("fontfamily", Px(156));
                Option("fontsize", Px(58));
            }
            if (Tool is not ToolKind.Eraser and not ToolKind.Mosaic)
            {
                Sep();
                Row(gridHeight);
                options.Add((new Rectangle(ox, rowTop, gridHeight, gridHeight), "colorcur"));
                optionsWidth = Math.Max(optionsWidth, ox + gridHeight);
                ox += gridHeight + gap * 2;

                Row(Columns * step);
                var gridX = ox;
                for (var i = 0; i < Palette.Length; i++)
                {
                    var col = i % Columns;
                    var row = i / Columns;
                    options.Add((new Rectangle(gridX + col * step, rowTop + row * step, swatch, swatch), "color" + i));
                }
                optionsWidth = Math.Max(optionsWidth, gridX + Columns * step);
            }
            optionsHeight = rowTop + contentHeight;
        }

        // ------------------------------------------------------ icon columns
        var maxContent = Math.Max(button * 2, VerticalMaxHeight - pad * 2);
        // Vertical order follows the documented toolbar order: tools, recognition,
        // undo/redo, then the finish actions.
        var groups = new[]
        {
            new[] { "rect", "ellipse", "arrow", "pen", "marker", "mosaic", "text", "eraser" },
            new[] { "ocr", "translate", "qr", "redact", "long", "rec", "gif" },
            new[] { "undo", "redo" },
            new[] { "close", "pin", "save", "copy" }
        };

        var column = 0;
        var y = 0;
        var columnBottom = 0;
        var drewGroup = false;

        void NewColumn()
        {
            column++;
            y = 0;
        }

        void AddButton(string id)
        {
            if (y > 0 && y + button > maxContent) NewColumn();
            _items.Add((new Rectangle(column * (button + gap), y, button, button), id));
            y += button + gap;
            columnBottom = Math.Max(columnBottom, y - gap);
        }

        foreach (var items in groups)
        {
            var visible = items.Where(Shows).ToArray();
            if (visible.Length == 0) continue;
            if (drewGroup && y > 0)
            {
                if (y + group + button > maxContent) NewColumn();
                else
                {
                    _dividers.Add(new Rectangle(column * (button + gap) + pad / 2, y + (group - gap) / 2, button - pad, 1));
                    y += group;
                }
            }
            drewGroup = true;
            foreach (var id in visible) AddButton(id);
        }

        var mainWidth = (column + 1) * button + column * gap;
        var mainHeight = columnBottom;
        var mainLeft = pad + (showOptions && VerticalOptionsFirst ? optionsWidth + gap : 0);
        var optionsLeft = pad + (showOptions && !VerticalOptionsFirst ? mainWidth + gap : 0);

        Offset(_items, mainLeft, pad);
        Offset(_dividers, mainLeft, pad);
        foreach (var (rect, id) in options)
            _items.Add((new Rectangle(rect.X + optionsLeft, rect.Y + pad, rect.Width, rect.Height), id));
        foreach (var rect in optionDividers)
            _dividers.Add(new Rectangle(rect.X + optionsLeft, rect.Y + pad, rect.Width, rect.Height));

        var totalWidth = pad * 2 + mainWidth + (showOptions ? gap + optionsWidth : 0);
        var totalHeight = pad * 2 + Math.Max(mainHeight, optionsHeight);
        Bounds = new Rectangle(0, 0, Math.Max(totalWidth, pad * 2), Math.Max(totalHeight, pad * 2));
        _main = new Rectangle(0, 0, Bounds.Width, Bounds.Height);
    }

    private static void Offset(List<(Rectangle Rect, string Id)> items, int dx, int dy)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            items[i] = (new Rectangle(item.Rect.X + dx, item.Rect.Y + dy, item.Rect.Width, item.Rect.Height), item.Id);
        }
    }

    private static void Offset(List<Rectangle> rects, int dx, int dy)
    {
        for (var i = 0; i < rects.Count; i++)
            rects[i] = new Rectangle(rects[i].X + dx, rects[i].Y + dy, rects[i].Width, rects[i].Height);
    }

    public string? HitTest(Point point)
    {
        foreach (var item in _items)
            if (item.Rect.Contains(point))
                return item.Id;

        return null;
    }

    public Rectangle ItemBounds(string id) => _items.FirstOrDefault(item => item.Id == id).Rect;

    public void SetSlider(string id, Point point)
    {
        var rect = ItemBounds(id);
        var fraction = Math.Clamp((point.X - rect.Left - Px(8)) / (float)Math.Max(1, rect.Width - Px(48)), 0, 1);
        if (id == "opacity") MarkerOpacity = (int)Math.Round(5 + fraction * 95);
        if (id == "strength") MosaicBlock = (int)Math.Round(2 + fraction * 38);
    }

    public static string Hint(string? id, bool ocrMode) => id switch
    {
        "ocr" => ocrMode ? UiText.T("退出文字选择（Esc）") : UiText.T("识别文字（OCR）"),
        "qr" => UiText.T("识别二维码 / 条码"),
        "long" => UiText.T("长截图：滚动并拼接整个页面"),
        "rec" => UiText.T("录屏：录制选区为视频"),
        "gif" => UiText.T("录制 GIF 动画"),
        "translate" => UiText.T("截图翻译（默认英语 → 中文）"),
        "redact" => UiText.T("智能打码：自动识别并遮挡手机号、邮箱、证件号、银行卡与密钥"),
        "copy" => ocrMode ? UiText.T("复制截图") : UiText.T("复制截图（Ctrl+C）"),
        "close" => ocrMode ? UiText.T("取消截图") : UiText.T("取消截图（Esc）"),
        "pin" => UiText.T("贴图（Ctrl+T）"), "save" => UiText.T("另存为（Ctrl+S）"),
        "rect" => UiText.T("矩形（R）"), "ellipse" => UiText.T("椭圆（E）"), "arrow" => UiText.T("箭头（A）"),
        "pen" => UiText.T("铅笔（P）"), "marker" => UiText.T("马克笔（H）"), "text" => UiText.T("文字（T）"),
        "mosaic" => UiText.T("马赛克（M）"), "eraser" => UiText.T("橡皮擦（X），仅擦除标注"),
        "fontfamily" => UiText.T("选择字体"), "fontsize" => UiText.T("设置字号（可输入数值）"),
        "colorcur" => UiText.T("自定义 RGB 颜色"), "width" => UiText.T("设置线宽 / 笔刷大小"),
        "modeline" => UiText.T("线段模式"), "modebrush" => UiText.T("涂抹模式"), "moderect" => UiText.T("框选模式"),
        "opacity" => UiText.T("拖动调整马克笔不透明度"), "strength" => UiText.T("拖动调整马赛克程度"),
        "arrowstyle" => UiText.T("箭头样式"), "bold" => UiText.T("粗体"), "italic" => UiText.T("斜体"),
        "undo" => UiText.T("撤销（Ctrl+Z）"), "redo" => UiText.T("重做（Ctrl+Y）"),
        "resultcopy" => UiText.T("复制二维码 / 条码完整内容"),
        "ocrtextcopy" => UiText.T("复制选中文字，未选字时复制全部识别结果"),
        "ocrengine" => UiText.T("切换识别引擎"),
        _ => ""
    };

    public void Render(Graphics g)
    {
        if (_items.Count == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        DrawBar(g, _main);

        if (_options.Height > 0)
            DrawBar(g, _options);

        if (_message.Height > 0)
        {
            DrawBar(g, _message);
            using var font = new Font("Microsoft YaHei UI", 12f * Scale, GraphicsUnit.Pixel);
            var text = Rectangle.Inflate(_message, -Px(10), 0);
            if (OcrMode)
            {
                // Hint text fills the left side and stops just before the chooser.
                var engine = ItemBounds("ocrengine");
                if (!engine.IsEmpty)
                    text.Width = engine.Left - Px(10) - text.X;
            }
            else if (ResultCopyVisible)
            {
                text.Width -= ResultButtonWidth() + Px(10);
            }
            TextRenderer.DrawText(g, Status.Replace("\r", " ").Replace("\n", " "), font, text, IconColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | (OcrMode ? TextFormatFlags.HorizontalCenter : 0));
        }
        foreach (var (rect, id) in _items)
        {
            if (id is "resultcopy" or "ocrtextcopy")
            {
                using var back = new SolidBrush(AppTheme.AdaptAccent(Accent));
                using var path = FluentDrawing.Rounded(Rectangle.Inflate(rect, -Px(2), -Px(2)), Px(5));
                g.FillPath(back, path);
                using var font = new Font("Microsoft YaHei UI", 11 * Scale, GraphicsUnit.Pixel);
                TextRenderer.DrawText(g, ResultCopied ? UiText.T("已复制") : id == "ocrtextcopy" ? UiText.T("复制文字") : UiText.T("复制"), font, rect, AppTheme.Current.OnAccent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                continue;
            }
            if (id.StartsWith("color", StringComparison.Ordinal))
            {
                if (id == "colorcur")
                {
                    DrawCurrentColor(g, rect);
                    continue;
                }

                var index = int.Parse(id[5..]);
                var swatch = Palette[index];
                DrawSwatch(g, rect, swatch, swatch.ToArgb() == Color.ToArgb());
                continue;
            }

            if (id == "width")
            {
                DrawWidth(g, rect);
                continue;
            }
            if (id is "fontfamily" or "fontsize" or "arrowstyle" or "ocrengine")
            {
                var label = id switch
                {
                    "fontfamily" => FontFamily,
                    "fontsize" => FontSize.ToString("0.#"),
                    "ocrengine" => OcrEngineLabel,
                    _ => ArrowStyle switch { ArrowStyle.Line => "―", ArrowStyle.Double => "↔", ArrowStyle.Open => "➝", ArrowStyle.Outline => "⇨", _ => "➜" }
                };
                using var border = new Pen(Divider);
                using var field = FluentDrawing.Rounded(Rectangle.Inflate(rect, -1, -1), Px(4));
                g.DrawPath(border, field);
                using var font = new Font("Microsoft YaHei UI", 12 * Scale, GraphicsUnit.Pixel);
                var textRect = Rectangle.Inflate(rect, -Px(6), 0);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                if (id == "ocrengine")
                {
                    // Engine captions read centred inside the field; the other small
                    // editors stay left-aligned next to their value.
                    flags |= TextFormatFlags.HorizontalCenter;
                }
                else textRect.Width -= Px(12);
                if (id == "arrowstyle")
                {
                    var arrow = new ArrowShape { Start = new Point(textRect.Left + Px(2), textRect.Top + textRect.Height / 2), End = new Point(textRect.Right - Px(2), textRect.Top + textRect.Height / 2), Width = 1.6f * Scale, Color = IconColor, Style = ArrowStyle };
                    using var dummy = new Bitmap(1, 1); arrow.Render(g, dummy);
                }
                else TextRenderer.DrawText(g, label, font, textRect, IconColor, flags);
                FluentIcons.Draw(g, "chevron", new Rectangle(rect.Right - Px(17), rect.Top + (rect.Height - Px(14)) / 2, Px(14), Px(14)), IconColor);
                continue;
            }
            if (id is "opacity" or "strength") { DrawSlider(g, rect, id); continue; }

            if (id == "fill")
            {
                DrawFill(g, rect, active: Fill);
                continue;
            }

            var active = id switch
            {
                "rect" => Tool == ToolKind.Rectangle,
                "ellipse" => Tool == ToolKind.Ellipse,
                "arrow" => Tool == ToolKind.Arrow,
                "pen" => Tool == ToolKind.Pen,
                "marker" => Tool == ToolKind.Marker,
                "eraser" => Tool == ToolKind.Eraser,
                "text" => Tool == ToolKind.Text,
                "mosaic" => Tool == ToolKind.Mosaic,
                "ocr" => OcrMode,
                "redact" => RedactBusy,
                "translate" => TranslationMode,
                "bold" => Bold,
                "italic" => Italic,
                "moderect" => RectangleMode,
                "modebrush" or "modeline" => !RectangleMode,
                _ => false
            };
            if (OcrMode && id != "ocr") active = false;

            if (active)
            {
                var selected = Rectangle.Inflate(rect, -Px(2), -Px(2));
                using var fill = new SolidBrush(Color.FromArgb(0, 103, 192));
                using var path = FluentDrawing.Rounded(
                    new RectangleF(selected.X + .5f, selected.Y + .5f, selected.Width - 1, selected.Height - 1), Px(5));
                g.FillPath(fill, path);
            }

            DrawIcon(g, id, rect, active);
        }

        // palette grid lines: Snipaste draws the swatches as one solid block
        using (var grid = new Pen(SwatchBorder, 1f))
        {
            foreach (var (rect, id) in _items)
            {
                if (id == "colorcur" || !id.StartsWith("color", StringComparison.Ordinal)) continue;
                g.DrawRectangle(grid, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
        }

        using var divider = new Pen(Divider, 1f);
        foreach (var d in _dividers)
        {
            if (d.Width <= 1) g.DrawLine(divider, d.X + 0.5f, d.Y, d.X + 0.5f, d.Bottom);
            else g.DrawLine(divider, d.X, d.Y + 0.5f, d.Right, d.Y + 0.5f);
        }
    }

    private void DrawBar(Graphics g, Rectangle rect)
    {
        // The floating editor toolbar paints its own single white card; drawing the
        // per-block cards here as well would nest a second bordered frame inside it.
        if (Flat) return;

        using var path = FluentDrawing.Rounded(new RectangleF(rect.X + .5f, rect.Y + .5f, rect.Width - 1, rect.Height - 1), Px(8));
        using (var back = new SolidBrush(BarBack))
            g.FillPath(back, path);

        using var border = new Pen(Divider, 1f);
        g.DrawPath(border, path);
    }

    // -------------------------------------------------------------- options

    private void DrawSwatch(Graphics g, Rectangle rect, Color color, bool selected)
    {
        using (var brush = new SolidBrush(color))
            g.FillRectangle(brush, rect);

        using (var border = new Pen(SwatchBorder, 1f))
            g.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

        if (!selected) return;

        var inset = Math.Max(1, Px(1));
        using var ring = new Pen(Color.White, Math.Max(2f, 2f * Scale));
        g.DrawRectangle(ring, rect.X + inset, rect.Y + inset, rect.Width - 1 - inset * 2, rect.Height - 1 - inset * 2);

        using var outline = new Pen(Accent, Math.Max(1f, Scale));
        g.DrawRectangle(outline, rect.X + inset, rect.Y + inset, rect.Width - 1 - inset * 2, rect.Height - 1 - inset * 2);
    }

    private void DrawCurrentColor(Graphics g, Rectangle rect)
    {
        if (Tool == ToolKind.Marker)
        {
            using var white = new SolidBrush(Color.White);
            using var gray = new SolidBrush(Color.FromArgb(210, 210, 210));
            var cell = Math.Max(2, Px(6));
            for (var y = 0; y < rect.Height; y += cell)
                for (var x = 0; x < rect.Width; x += cell)
                    g.FillRectangle((x / cell + y / cell) % 2 == 0 ? white : gray,
                        rect.X + x, rect.Y + y, Math.Min(cell, rect.Width - x), Math.Min(cell, rect.Height - y));
        }
        using (var brush = new SolidBrush(Tool == ToolKind.Marker ? Color.FromArgb((int)(255 * MarkerOpacity / 100d), Color) : Color))
            g.FillRectangle(brush, rect);

        using (var border = new Pen(SwatchBorder, 1f))
            g.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

        // the little rainbow corner Snipaste shows on the current colour
        var size = Math.Max(5, rect.Width / 4);

        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new Point(rect.X + 1, rect.Bottom - 2),
            new Point(rect.X + size, rect.Bottom - 2),
            new Point(rect.X + 1, rect.Bottom - size - 1)
        });

        using var rainbow = new LinearGradientBrush(
            new Point(rect.X, rect.Bottom),
            new Point(rect.X + size, rect.Bottom - size),
            Color.Red,
            Color.Blue)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue },
                Positions = new[] { 0f, 0.3f, 0.55f, 0.8f, 1f }
            }
        };

        g.FillPath(rainbow, path);

        using var edge = new Pen(Color.FromArgb(120, 0, 0, 0), 1f);
        g.DrawLine(edge, rect.X + size, rect.Bottom - 2, rect.X + 1, rect.Bottom - size - 1);
    }

    /// <summary>Fill style cell: hatch = outline only, solid square = filled.</summary>
    private void DrawFill(Graphics g, Rectangle rect, bool active)
    {
        var box = new Rectangle(
            rect.X + Px(8),
            rect.Y + Px(8),
            rect.Width - Px(16),
            rect.Height - Px(16));

        if (active)
        {
            using var brush = new SolidBrush(Color);
            g.FillRectangle(brush, box);
            return;
        }

        // diagonal hatch, clipped to the shape, like Snipaste's "no fill"
        var state = g.Save();
        g.SetClip(box);

        using (var pen = new Pen(IconColor, 1f))
        {
            for (var x = box.Left - box.Height; x < box.Right; x += Math.Max(2, Px(3)))
                g.DrawLine(pen, x, box.Bottom, x + box.Height, box.Top);
        }

        g.Restore(state);

        using var border = new Pen(IconColor, 1f);
        g.DrawRectangle(border, box);
    }

    private void DrawWidth(Graphics g, Rectangle rect)    {
        var width = Tool == ToolKind.Marker ? MarkerWidth : Tool is ToolKind.Eraser or ToolKind.Mosaic ? BrushWidth : StrokeWidth;
        var diameter = (int)Math.Clamp(width * 2.4f * Scale, 5f, 16f * Scale);
        var box = new Rectangle(rect.X + (rect.Width - diameter) / 2, rect.Y + (rect.Height - diameter) / 2, diameter, diameter);

        using (var brush = new SolidBrush(Color))
            g.FillEllipse(brush, box);

        using var ring = new Pen(Color.FromArgb(140, 0, 0, 0), 1f);
        g.DrawEllipse(ring, box);
    }

    private void DrawSlider(Graphics g, Rectangle rect, string id)
    {
        var fraction = id == "opacity" ? (MarkerOpacity - 5) / 95f : (MosaicBlock - 2) / 38f;
        var left = rect.Left + Px(8);
        var right = rect.Right - Px(40);
        var y = rect.Top + rect.Height / 2;
        using var track = new Pen(Color.FromArgb(190, 190, 190), Px(3));
        using var active = new Pen(Accent, Px(3));
        g.DrawLine(track, left, y, right, y);
        var x = left + (right - left) * fraction;
        g.DrawLine(active, left, y, x, y);
        using var fill = new SolidBrush(Color.White);
        g.FillRectangle(fill, x - Px(4), y - Px(6), Px(8), Px(12));
        g.DrawRectangle(active, x - Px(4), y - Px(6), Px(8), Px(12));
        using var font = new Font("Segoe UI", 11 * Scale, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, id == "opacity" ? $"{MarkerOpacity}%" : MosaicBlock.ToString(), font,
            new Rectangle(right + Px(5), rect.Y, Px(35), rect.Height), IconColor, TextFormatFlags.VerticalCenter);
    }

    // ---------------------------------------------------------------- icons

    private void DrawIcon(Graphics g, string id, Rectangle rect, bool active)
    {
        var s = Scale;
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var color = active ? Color.White : id == "undo" && !CanUndo || id == "redo" && !CanRedo
            ? AppTheme.Current.Disabled : IconColor;

        if (FluentIcons.Supports(id))
        {
            FluentIcons.Draw(g, id, new RectangleF(cx - 10 * s, cy - 10 * s, 20 * s, 20 * s), color);
            return;
        }

        using var pen = new Pen(color, 1.7f * s)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        using var brush = new SolidBrush(color);

        switch (id)
        {
            case "modebrush":
                g.FillEllipse(brush, cx - 5 * s, cy - 5 * s, 10 * s, 10 * s);
                break;
            case "moderect":
                g.FillRectangle(brush, cx - 7 * s, cy - 5 * s, 14 * s, 10 * s);
                break;
            case "modeline":
                using (var stroke = new Pen(color, 5 * s)) g.DrawLine(stroke, cx - 7 * s, cy + 4 * s, cx + 7 * s, cy - 4 * s);
                break;
            case "marker":
                g.DrawPolygon(pen, new[] { new PointF(cx - 8*s, cy + 3*s), new PointF(cx + 1*s, cy - 8*s), new PointF(cx + 8*s, cy - 1*s), new PointF(cx - 3*s, cy + 8*s) });
                g.DrawLine(pen, cx - 8*s, cy + 7*s, cx - 3*s, cy + 9*s);
                break;
            case "eraser":
                g.DrawPolygon(pen, new[] { new PointF(cx - 8*s, cy + 2*s), new PointF(cx + 1*s, cy - 7*s), new PointF(cx + 8*s, cy), new PointF(cx - 1*s, cy + 9*s) });
                g.DrawLine(pen, cx - 4*s, cy - 2*s, cx + 3*s, cy + 5*s);
                break;
            case "ocr":
            case "qr":
                using (var font = new Font("Segoe UI", (id == "ocr" ? 10f : 12f) * s, FontStyle.Bold, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, id.ToUpperInvariant(), font, rect, color,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                break;
            case "translate":
                using (var font = new Font("Microsoft YaHei UI", 14f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, UiText.T("译"), font, rect, color,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                break;

            case "long":
                using (var path = Rounded(
                           new Rectangle((int)(cx - 5.5f * s), (int)(cy - 8 * s), (int)(11 * s), (int)(16 * s)),
                           Math.Max(2, (int)(2 * s))))
                    g.DrawPath(pen, path);
                g.DrawLine(pen, cx, cy - 5 * s, cx, cy - 1.5f * s);
                g.DrawLine(pen, cx - 2.5f * s, cy - 4 * s, cx, cy - 6.5f * s);
                g.DrawLine(pen, cx + 2.5f * s, cy - 4 * s, cx, cy - 6.5f * s);
                g.DrawLine(pen, cx, cy + 1.5f * s, cx, cy + 5 * s);
                g.DrawLine(pen, cx - 2.5f * s, cy + 4 * s, cx, cy + 6.5f * s);
                g.DrawLine(pen, cx + 2.5f * s, cy + 4 * s, cx, cy + 6.5f * s);
                break;

            case "rec":
                using (var path = Rounded(
                           new Rectangle((int)(cx - 8 * s), (int)(cy - 5 * s), (int)(13 * s), (int)(10 * s)),
                           Math.Max(2, (int)(2 * s))))
                    g.DrawPath(pen, path);
                g.DrawPolygon(pen, new[]
                {
                    new PointF(cx + 5 * s, cy - 1.5f * s), new PointF(cx + 8 * s, cy - 4.5f * s),
                    new PointF(cx + 8 * s, cy + 4.5f * s), new PointF(cx + 5 * s, cy + 1.5f * s)
                });
                using (var dot = new SolidBrush(color))
                    g.FillEllipse(dot, cx - 6 * s, cy - 2.5f * s, 5 * s, 5 * s);
                break;

            case "gif":
                using (var path = Rounded(
                           new Rectangle((int)(cx - 8 * s), (int)(cy - 6 * s), (int)(16 * s), (int)(12 * s)),
                           Math.Max(2, (int)(2 * s))))
                    g.DrawPath(pen, path);
                using (var hole = new SolidBrush(color))
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var x = cx - 6 * s + i * 4.4f * s;
                        g.FillRectangle(hole, x, cy - 4.5f * s, 2.2f * s, 1.8f * s);
                        g.FillRectangle(hole, x, cy + 2.7f * s, 2.2f * s, 1.8f * s);
                    }
                }
                break;

            case "rect":
                using (var path = Rounded(
                           new Rectangle((int)(cx - 7 * s), (int)(cy - 5.5f * s), (int)(14 * s), (int)(11 * s)),
                           Math.Max(2, (int)(2 * s))))
                    g.DrawPath(pen, path);
                break;

            case "ellipse":
                g.DrawEllipse(pen, cx - 7 * s, cy - 5.5f * s, 14 * s, 11 * s);
                break;

            case "arrow":
                g.DrawLine(pen, cx - 7 * s, cy + 5.5f * s, cx + 4.5f * s, cy - 6 * s);
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[]
                    {
                        new PointF(cx + 7 * s, cy - 7.5f * s),
                        new PointF(cx + 1.5f * s, cy - 6.5f * s),
                        new PointF(cx + 5.5f * s, cy - 1.5f * s)
                    });
                    g.FillPath(brush, path);
                }
                break;

            case "pen":
                {
                    var state = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(-45);

                    using (var path = new GraphicsPath())
                    {
                        path.AddRectangle(new RectangleF(-1.7f * s, -8f * s, 3.4f * s, 12f * s));
                        path.AddPolygon(new[]
                        {
                            new PointF(-1.7f * s, 4f * s),
                            new PointF(1.7f * s, 4f * s),
                            new PointF(0, 8f * s)
                        });
                        g.DrawPath(pen, path);
                    }

                    g.DrawLine(pen, -1.7f * s, -2f * s, 1.7f * s, -2f * s);
                    g.Restore(state);
                }
                break;

            case "text":
                g.DrawLine(pen, cx - 6 * s, cy - 6.5f * s, cx + 6 * s, cy - 6.5f * s);
                g.DrawLine(pen, cx, cy - 6.5f * s, cx, cy + 7 * s);
                break;

            case "bold":
                using (var font = new Font("Segoe UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("B", font, brush, rect, format);
                break;

            case "italic":
                using (var font = new Font("Times New Roman", 14f * s, FontStyle.Italic, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("I", font, brush, rect, format);
                break;

            case "mosaic":
                {
                    var cell = 5.5f * s;
                    for (var row = 0; row < 2; row++)
                    {
                        for (var col = 0; col < 2; col++)
                        {
                            var box = new RectangleF(cx - cell + col * cell, cy - cell + row * cell, cell, cell);

                            if ((row + col) % 2 == 0)
                                g.FillRectangle(brush, box);
                            else
                                g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                        }
                    }
                }
                break;

            case "redact":
                {
                    // Shield outline with a 3x2 mosaic grid inside.
                    var shield = new[]
                    {
                        new PointF(cx, cy - 8.5f * s), new PointF(cx + 7 * s, cy - 6 * s),
                        new PointF(cx + 7 * s, cy), new PointF(cx + 5.5f * s, cy + 3.5f * s),
                        new PointF(cx + 2.5f * s, cy + 6.5f * s), new PointF(cx, cy + 8 * s),
                        new PointF(cx - 2.5f * s, cy + 6.5f * s), new PointF(cx - 5.5f * s, cy + 3.5f * s),
                        new PointF(cx - 7 * s, cy), new PointF(cx - 7 * s, cy - 6 * s)
                    };
                    g.DrawPolygon(pen, shield);
                    var cell = 2.6f * s;
                    for (var row = 0; row < 2; row++)
                    {
                        for (var col = 0; col < 3; col++)
                        {
                            var box = new RectangleF(cx - cell * 1.5f + col * cell, cy - cell + row * cell, cell, cell);
                            if ((row + col) % 2 == 0) g.FillRectangle(brush, box);
                            else g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                        }
                    }
                }
                break;

            case "undo":
                DrawHistory(g, rect, pen, false);
                break;

            case "redo":
                DrawHistory(g, rect, pen, true);
                break;

            case "close":
                g.DrawLine(pen, cx - 5.5f * s, cy - 5.5f * s, cx + 5.5f * s, cy + 5.5f * s);
                g.DrawLine(pen, cx + 5.5f * s, cy - 5.5f * s, cx - 5.5f * s, cy + 5.5f * s);
                break;

            case "pin":
                {
                    var state = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(35);

                    using (var path = new GraphicsPath())
                    {
                        path.AddRectangle(new RectangleF(-4.5f * s, -7.5f * s, 9 * s, 6f * s));
                        path.AddRectangle(new RectangleF(-1.4f * s, -1.5f * s, 2.8f * s, 5* s));
                        g.DrawPath(pen, path);
                    }

                    g.DrawLine(pen, 0, 3.5f * s, 0, 8.5f * s);
                    g.Restore(state);
                }
                break;

            case "save":
                using (var path = Rounded(
                           new Rectangle((int)(cx - 7 * s), (int)(cy - 7 * s), (int)(14 * s), (int)(14 * s)),
                           Math.Max(2, (int)(2 * s))))
                    g.DrawPath(pen, path);

                g.FillRectangle(brush, cx - 3.5f * s, cy - 7 * s, 7 * s, 4 * s);
                g.DrawRectangle(pen, cx - 4.5f * s, cy + 1 * s, 9 * s, 6 * s);
                break;

            case "copy":
                g.DrawRectangle(pen, cx - 7 * s, cy - 7 * s, 9.5f * s, 9.5f * s);
                g.DrawRectangle(pen, cx - 2.5f * s, cy - 2.5f * s, 9.5f * s, 9.5f * s);
                break;
        }
    }

    private void DrawHistory(Graphics g, Rectangle rect, Pen pen, bool redo)
    {
        var s = Scale;
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;

        var rx = 7f * s;
        var ry = 5.5f * s;
        var centerY = cy + 1.5f * s;

        using (var path = new GraphicsPath())
        {
            path.AddArc(cx - rx, centerY - ry, rx * 2, ry * 2, redo ? 340f : 200f, redo ? -140f : 140f);
            g.DrawPath(pen, path);
        }

        var angle = (redo ? 340f : 200f) * MathF.PI / 180f;
        var tip = new PointF(cx + rx * MathF.Cos(angle), centerY + ry * MathF.Sin(angle));
        var dir = new PointF(redo ? 0.42f : -0.42f, 0.91f);

        var head = 4.2f * s;
        var baseAngle = MathF.Atan2(dir.Y, dir.X);
        var center = new PointF(tip.X + dir.X * head * 0.5f, tip.Y + dir.Y * head * 0.5f);

        for (var side = -1; side <= 1; side += 2)
        {
            var a = baseAngle + MathF.PI + side * 0.55f;
            g.DrawLine(pen, center, new PointF(center.X + MathF.Cos(a) * head, center.Y + MathF.Sin(a) * head));
        }
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();

        if (radius <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        var d = radius * 2;

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }
}
