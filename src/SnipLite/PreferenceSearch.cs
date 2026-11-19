using System.Drawing.Drawing2D;
using System.Text;

namespace SnipLite;

/// <summary>
/// Cross-category search for the preferences window. The left tabs and the page
/// builders stay untouched: every card is rebuilt once to harvest its labels,
/// then matched cards are re-created on a scrollable result surface so their
/// switches and dropdowns keep working.
/// </summary>
internal sealed partial class PreferencesForm
{
    private readonly SearchBox _search = new();
    private readonly Panel _searchHost = new() { AutoScroll = true, Visible = false };
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 220 };
    private bool _searching;
    private bool _suppressSearch;
    private List<SearchEntry>? _searchIndex;

    private sealed record SearchEntry(int Tab, int Sub, string Path, string Text, string Aliases);

    private static readonly string[][] SubNames =
    {
        Array.Empty<string>(),
        new[] { "风格", "放大镜" },
        new[] { "显示", "行为", "按键", "截图" },
        new[] { "录屏", "GIF动画" },
        new[] { "显示", "行为", "转换" },
        new[] { "文件", "剪贴板" },
        new[] { "全局快捷键", "鼠标" },
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>()
    };

    /// <summary>Chinese setting terms with their pinyin initials, so "jt" finds 截屏.</summary>
    private static readonly (string Term, string Aliases)[] Terms =
    {
        ("截屏", "jt jieping jp"), ("截图", "jt jietu"), ("识别", "sb shibie"), ("文字识别", "wzsb wenzishibie ocr"),
        ("翻译", "fy fanyi translate"), ("剪贴板", "jtb jiantieban clipboard"), ("快捷键", "kjj kuaijiejian hotkey"),
        ("贴图", "tt tietu pin"), ("水印", "sy shuiyin watermark"), ("录屏", "lp luping record"),
        ("视频", "sp shipin video"), ("动画", "dh donghua animation"), ("输出", "sc shuchu output"),
        ("保存", "bc baocun save"), ("路径", "lj lujing path"), ("布局", "bj buju layout"),
        ("托盘", "tp tuopan tray"), ("工具", "gj gongju tool"), ("主题", "zt zhuti theme"),
        ("语言", "yy yuyan language"), ("字体", "zt ziti font"), ("更新", "gx gengxin update"),
        ("关于", "gy guanyu about"), ("鼠标", "sb shubiao mouse"), ("滚轮", "gl gunlun wheel"),
        ("放大镜", "fdj fangdajing magnifier"), ("颜色", "ys yanse color"), ("边框", "bk biankuang border"),
        ("阴影", "yy yinying shadow"), ("历史", "ls lishi history"), ("二维码", "ewm erweima qr"),
        ("隐私", "ys yinsi privacy"), ("打码", "dm dama redact"), ("长截图", "ljt changjietu"),
        ("延时", "ys yanshi delay"), ("倒计时", "djs daojishi countdown"), ("质量", "zl zhiliang quality"),
        ("格式", "gs geshi format"), ("目录", "ml mulu folder"), ("文件", "wj wenjian file"),
        ("通知", "tz tongzhi notify"), ("声音", "sy shengyin sound"), ("帧率", "zl zhenlv fps"),
        ("窗口", "ck chuangkou window"), ("启动", "qd qidong startup"), ("引擎", "yq yinqing engine"),
        ("设置", "sz shezhi settings"), ("显示", "xs xianshi show"), ("行为", "xw xingwei behavior"),
        ("按键", "aj anjian key"), ("缩放", "sf suofang zoom"), ("编号", "bh bianhao numbering"),
        ("自动", "zd zidong auto"), ("复制", "fz fuzhi copy"), ("默认", "mr moren default"),
        ("图片", "tp tupian image"), ("屏幕", "pm pingmu screen")
    };

    private void CreateSearchUi()
    {
        _searchHost.BackColor = Page;
        Controls.Add(_searchHost);
        Controls.Add(_search);

        _search.QueryChanged += query =>
        {
            if (_suppressSearch) return;
            _searchDebounce.Stop();
            if (string.IsNullOrWhiteSpace(query))
            {
                EndSearch(rebuild: true);
                return;
            }
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RunSearch(_search.Query);
        };
    }

    private void ClearPageControls()
    {
        while (_page.Controls.Count > 0) _page.Controls[0].Dispose();
    }

    private void ClearHost()
    {
        while (_searchHost.Controls.Count > 0) _searchHost.Controls[0].Dispose();
        _searchHost.AutoScrollMinSize = Size.Empty;
        _searchHost.AutoScrollPosition = Point.Empty;
    }

    /// <summary>Rebuilds a tab off-screen so its cards can be inspected or harvested.</summary>
    private void BuildForSearch(int tab, int sub)
    {
        ClearPageControls();
        _selected = tab;
        _subPages[tab] = sub;
        _y = Px(PagePad);
        _labelX = Px(PagePad);
        _controlX = _labelX + Px(LabelW + ControlGap);
        _groups = 0;
        _captions = tab is 0 or 1 or 5;
        BuildPage(tab);
        PrepareTree(_page);
    }

    private void EnsureSearchIndex()
    {
        if (_searchIndex is not null) return;

        var entries = new List<SearchEntry>();
        var tab = _selected;
        var subs = (int[])_subPages.Clone();
        try
        {
            for (var t = 0; t <= 7; t++)
            {
                var count = Math.Max(1, SubNames[t].Length);
                for (var s = 0; s < count; s++)
                {
                    try
                    {
                        BuildForSearch(t, s);
                        var path = SubNames[t].Length == 0
                            ? _tabs[t]
                            : $"{_tabs[t]} › {SubNames[t][s]}";
                        foreach (var card in _page.Controls.OfType<FluentSettingRow>())
                        {
                            var text = ControlText(card);
                            if (text.Length == 0) continue;
                            entries.Add(new SearchEntry(t, s, path, text, AliasesFor(text)));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"设置搜索索引构建失败：{_tabs[t]}", ex);
                    }
                    ClearPageControls();
                }
            }
        }
        finally
        {
            _selected = tab;
            Array.Copy(subs, _subPages, _subPages.Length);
        }
        _searchIndex = entries;
    }

    private void RunSearch(string query)
    {
        var q = Normalize(query);
        if (q.Length == 0)
        {
            EndSearch(rebuild: true);
            return;
        }

        var tab = _selected;
        var subs = (int[])_subPages.Clone();
        _searching = true;
        _page.Visible = false;
        _searchHost.Visible = true;
        _reset.Visible = false;
        _searchHost.SuspendLayout();
        ClearHost();

        try
        {
            EnsureSearchIndex();
            var hits = _searchIndex!.Where(entry => Matches(entry, q)).ToList();
            var y = Px(6);

            Heading($"搜索结果 · {hits.Count} 项", ref y);
            if (hits.Count == 0) Note("未找到匹配的设置项，试试「截屏」「翻译」「快捷键」等关键词。", ref y);

            foreach (var group in hits.GroupBy(h => (h.Tab, h.Sub)))
            {
                try
                {
                    BuildForSearch(group.Key.Tab, group.Key.Sub);
                    var path = group.First().Path;
                    var cards = _page.Controls.OfType<FluentSettingRow>().ToList();
                    foreach (var card in cards)
                    {
                        var text = ControlText(card);
                        if (!Matches(new SearchEntry(group.Key.Tab, group.Key.Sub, path, text, AliasesFor(text)), q))
                            continue;

                        PathLabel(path, ref y);
                        _page.Controls.Remove(card);
                        card.Anchor = AnchorStyles.None;
                        card.Bounds = new Rectangle(Px(PagePad + 4), y,
                            Math.Max(Px(200), _searchHost.ClientSize.Width - Px(PagePad * 2 + 10)), card.Height);
                        _searchHost.Controls.Add(card);
                        y += card.Height + Px(10);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("设置搜索结果构建失败", ex);
                }
                ClearPageControls();
            }

            _searchHost.AutoScrollMinSize = new Size(0, y + Px(PagePad));
        }
        finally
        {
            _searchHost.ResumeLayout();
            _selected = tab;
            Array.Copy(subs, _subPages, _subPages.Length);
            Invalidate();
        }
    }

    /// <summary>Leaves the result view and restores the page the user was on.</summary>
    private void EndSearch(bool rebuild)
    {
        _searchDebounce.Stop();
        if (!_searching) return;

        _searching = false;
        _searchHost.Visible = false;
        _page.Visible = true;
        ClearHost();
        _suppressSearch = true;
        _search.ClearInput();
        _suppressSearch = false;
        _reset.Visible = _selected <= 7;
        if (rebuild) ShowTab(_selected);
        Invalidate();
    }

    private void Heading(string text, ref int y)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(Px(PagePad + 4), y, Math.Max(Px(200), _searchHost.ClientSize.Width - Px(PagePad * 2 + 10)), Px(26)),
            Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold),
            ForeColor = Ink,
            BackColor = Page
        };
        _searchHost.Controls.Add(label);
        y += Px(32);
    }

    private void Note(string text, ref int y)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(Px(PagePad + 4), y, Math.Max(Px(200), _searchHost.ClientSize.Width - Px(PagePad * 2 + 10)), Px(48)),
            ForeColor = Muted,
            BackColor = Page
        };
        _searchHost.Controls.Add(label);
        y += Px(56);
    }

    private void PathLabel(string path, ref int y)
    {
        var label = new Label
        {
            Text = path,
            AutoSize = false,
            Bounds = new Rectangle(Px(PagePad + 6), y, Math.Max(Px(200), _searchHost.ClientSize.Width - Px(PagePad * 2 + 14)), Px(18)),
            ForeColor = Muted,
            BackColor = Page
        };
        _searchHost.Controls.Add(label);
        y += Px(20);
    }

    // ------------------------------------------------------------ matching

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static bool IsAsciiLetters(string text)
    {
        foreach (var ch in text)
            if (ch > 'z' || ch < '0' || (ch < 'a' && ch > '9'))
                return false;
        return text.Length > 0;
    }

    private static bool Matches(SearchEntry entry, string q)
    {
        if (Normalize(entry.Text).Contains(q)) return true;
        if (!IsAsciiLetters(q) || entry.Aliases.Length == 0) return false;

        foreach (var alias in entry.Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (alias.StartsWith(q, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string AliasesFor(string text)
    {
        var sb = new StringBuilder();
        foreach (var (term, aliases) in Terms)
        {
            if (!text.Contains(term, StringComparison.Ordinal)) continue;
            foreach (var alias in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                sb.Append('|').Append(alias);
        }
        return sb.ToString();
    }

    /// <summary>Every visible word inside a card, so labels and dropdown values are searchable.</summary>
    private static string ControlText(Control root)
    {
        var sb = new StringBuilder();
        Walk(root);
        return sb.ToString();

        void Walk(Control control)
        {
            switch (control)
            {
                case Label label: sb.Append(' ').Append(label.Text); break;
                case CheckBox check: sb.Append(' ').Append(check.Text); break;
                case Button button: sb.Append(' ').Append(button.Text); break;
            }
            if (!string.IsNullOrEmpty(control.AccessibleName)) sb.Append(' ').Append(control.AccessibleName);
            foreach (Control child in control.Controls) Walk(child);
        }
    }
}

/// <summary>
/// Rounded search field with an inline magnifier, a placeholder and a clear
/// button. Ctrl+F focuses it; Esc inside the form clears the query.
/// </summary>
internal sealed class SearchBox : Control
{
    private readonly TextBox _input = new()
    {
        BorderStyle = BorderStyle.None,
        AutoSize = false
    };
    private bool _hot;
    private bool _hoverClear;

    public event Action<string>? QueryChanged;

    public SearchBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.IBeam;

        _input.GotFocus += (_, _) => { _hot = true; Invalidate(); };
        _input.LostFocus += (_, _) => { _hot = false; Invalidate(); };
        _input.TextChanged += (_, _) => { QueryChanged?.Invoke(_input.Text); Invalidate(); };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            ClearInput();
            e.Handled = e.SuppressKeyPress = true;
        };
        Controls.Add(_input);
    }

    public string Query => _input.Text;

    public void FocusInput()
    {
        _input.Focus();
        _input.SelectAll();
    }

    public void ClearInput()
    {
        if (_input.Text.Length > 0) _input.Clear();
        _input.Focus();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _input.Font = Font;
        LayoutInput();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInput();
    }

    private void LayoutInput() => _input.Bounds = new Rectangle(Px(30), Px(6),
        Math.Max(10, Width - Px(58)), Math.Max(10, Height - Px(12)));

    private int Px(int value) => (int)Math.Round(value * (DeviceDpi / 96f));

    private float Px(float value) => (float)(value * (DeviceDpi / 96f));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = Query.Length > 0 && ClearBounds().Contains(e.Location);
        if (hover == _hoverClear) return;
        _hoverClear = hover;
        Cursor = hover ? Cursors.Hand : Cursors.IBeam;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Query.Length > 0 && ClearBounds().Contains(e.Location))
        {
            ClearInput();
            return;
        }
        base.OnMouseDown(e);
        FocusInput();
    }

    private Rectangle ClearBounds() => new(Width - Px(28), Px(8), Px(20), Height - Px(16));

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = AppTheme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _input.BackColor = theme.Field;
        _input.ForeColor = theme.Text;

        var box = new Rectangle(Px(1), Px(1), Width - Px(2), Height - Px(2));
        using (var path = Rounded(box, Px(8)))
        {
            using var fill = new SolidBrush(theme.Field);
            g.FillPath(fill, path);
            using var border = new Pen(_hot ? theme.Accent : theme.Border, Px(1));
            g.DrawPath(border, path);
        }

        var icon = new Rectangle(Px(10), (Height - Px(14)) / 2, Px(14), Px(14));
        using (var pen = new Pen(theme.Muted, Math.Max(1f, Px(1.4f))))
        {
            g.DrawEllipse(pen, icon.X, icon.Y, icon.Width - Px(4), icon.Height - Px(4));
            g.DrawLine(pen, icon.Right - Px(4), icon.Bottom - Px(4), icon.Right, icon.Bottom);
        }

        if (Query.Length > 0)
        {
            var clear = ClearBounds();
            var mid = new Point(clear.X + clear.Width / 2, clear.Y + clear.Height / 2);
            var r = Px(5);
            using var pen = new Pen(_hoverClear ? theme.Accent : theme.Muted, Math.Max(1f, Px(1.5f)));
            g.DrawLine(pen, mid.X - r, mid.Y - r, mid.X + r, mid.Y + r);
            g.DrawLine(pen, mid.X + r, mid.Y - r, mid.X - r, mid.Y + r);
        }
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0 || bounds.Width <= d || bounds.Height <= d)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
