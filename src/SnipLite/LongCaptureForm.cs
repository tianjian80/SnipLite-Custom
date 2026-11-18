using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// Scrolls the region under the selection, stitches the frames vertically and shows
/// a live preview. The selection keeps a thin highlight border while its interior is
/// cut out of the window region, so wheel and click input reach the target window.
///
/// The capture is kept as a list of small vertical slices (one per stitched frame),
/// so a new frame only allocates the rows it adds; the full long image is assembled
/// once, when the user saves or copies.
/// </summary>
internal sealed class LongCaptureForm : Form
{
    private const int MatchRows = 20;   // consecutive content rows required to line up
    private const int MinOffset = 10;   // scroll below this many rows is jitter, not content
    private const int AutoIdleFrames = 18; // auto mode: ~5 s of frozen frames = the end
    private const int HashInsetPercent = 15; // ignore the scrollbars on both sides
    private const int HashStride = 2;   // sample every other pixel of a row
    private const int MergeBlock = 1200; // fold slices into one block every ~1200 px

    private readonly Rectangle _region;
    private readonly AppSettings _settings;
    private readonly float _ui;
    private readonly int _width;

    private readonly List<Bitmap> _chunks = new();
    private readonly object _chunkLock = new();
    private int _totalHeight;

    // The baseline is always the previous captured frame itself (frame N-1). Every
    // successful stitch replaces it with the newest frame, so frame N is only ever
    // compared with frame N-1 and scrolling can continue indefinitely.
    private ulong[] _prevHashes = Array.Empty<ulong>();
    private bool[] _prevSolid = Array.Empty<bool>();
    private int _prevRows;
    private ulong[]? _lastHashes;
    private int _autoIdle;
    private int _anchorMisses;
    private int _mergeIndex;

    // Preview thumbnail: a 1:1 pre-scaled cache so the preview window only blits.
    private readonly object _thumbLock = new();
    private Bitmap? _thumb;
    private int _thumbWidth;
    private int _thumbHeight;
    private float _thumbScale = 1f;

    private Bitmap? _pending;
    private readonly object _pendingLock = new();
    private int _notifyPending;

    private volatile bool _stopped;
    private volatile bool _reachedBottom;
    private long _autoStarted;
    private IntPtr _wheelTarget;
    private long _wheelTargetAt;
    private LongPreviewForm? _preview;
    private IntPtr _hook;
    private HookProc? _hookProc;

    public event Action? Saved;
    public event Action? Copied;
    public event Action? Cancelled;
    public event Action<Bitmap>? Edited;

    private LongToolbarForm? _bar;
    private readonly Bitmap _backdrop;

    public LongCaptureForm(Rectangle region, AppSettings settings)
    {
        _region = region;
        _settings = settings;
        _ui = Math.Max(1f, settings.CreateInterfaceFont().Size / 9f);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        DoubleBuffered = true;
        // Full-screen dim overlay: the frozen desktop is painted once under a 43 % black
        // wash, the selection is cut out of the region so the page below keeps rendering
        // and scrolling at full speed and nothing of the overlay lands in the capture.
        Bounds = SystemInformation.VirtualScreen;
        _backdrop = Capture(Bounds);

        var first = Capture(region);
        _width = first.Width;
        _chunks.Add(first);
        _totalHeight = first.Height;
        var hashes = RowHashes(first, out var solid);
        SetBaseline(hashes, solid, first.Height);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // tool window, no activate, and fully click-through: every mouse and wheel
            // event reaches the page under the selection (or the mask).
            cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00000020;
            cp.ExStyle &= ~0x00040000;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Keep the dim mask everywhere except the selection: the cut-out area shows the
        // live page and lets every click / wheel event through to it.
        using var full = new Region(new Rectangle(Point.Empty, Size));
        using var hole = new Region(SelectionClient);
        full.Exclude(hole);
        Region = full.Clone();

        _autoStarted = Environment.TickCount64;
        _preview = new LongPreviewForm(this, _ui);
        _preview.Show();
        _bar = new LongToolbarForm(this, _ui);
        _bar.Show();
        _reachedBottom = false;

        // A low-level hook makes Esc work no matter which window owns focus (the
        // scrolled page, the preview or the toolbar).
        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(13, _hookProc, GetModuleHandle(null), 0);
        _ = Task.Run(StitchLoopAsync);
        // The pump also runs off the UI thread, so grabbing frames never competes with
        // the preview's painting.
        _ = Task.Run(PumpAsync);
    }

    public int CaptureWidth => _width;
    public int CaptureHeight => Volatile.Read(ref _totalHeight);
    public Rectangle CaptureRegion => _region;
    public AppSettings Settings => _settings;

    public bool ReachedBottom => _reachedBottom;

    private Rectangle SelectionClient => new(_region.X - Left, _region.Y - Top, _region.Width, _region.Height);

    /// <summary>Immutable snapshot of the stitched slices plus their source Y offsets.</summary>
    public Bitmap[] Chunks(out int[] offsets, out int totalHeight)
    {
        lock (_chunkLock)
        {
            var slices = _chunks.ToArray();
            offsets = new int[slices.Length];
            var y = 0;
            for (var i = 0; i < slices.Length; i++)
            {
                offsets[i] = y;
                y += slices[i].Height;
            }
            totalHeight = y;
            return slices;
        }
    }

    /// <summary>Assembles the final long image from the slices (call on demand only).</summary>
    public Bitmap Export()
    {
        var slices = Chunks(out _, out var total);
        var result = new Bitmap(_width, Math.Max(1, total), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        var y = 0;
        foreach (var slice in slices)
        {
            if (y >= total) break;
            var rows = Math.Min(slice.Height, total - y);
            g.DrawImage(slice, new Rectangle(0, y, _width, rows), 0, 0, _width, rows, GraphicsUnit.Pixel);
            y += rows;
        }
        return result;
    }

    private int MaxHeight => _settings.LongCaptureMaxHeight <= 0 ? 100000 : Math.Clamp(_settings.LongCaptureMaxHeight, 1000, 100000);

    private int Delay => _settings.LongCaptureSpeed switch
    {
        LongCaptureSpeed.Slow => 500,
        LongCaptureSpeed.Fast => 250,
        _ => 350
    };

    // ------------------------------------------------------------ capture pump

    /// <summary>
    /// Only grabs frames; stitching happens on a worker. A single slot holds the
    /// newest frame, so a busy stitcher makes the pump drop stale frames instead of
    /// queueing them.
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            await Task.Delay(Delay);
            if (_stopped) return;
            var auto = _settings.LongCaptureMode == LongCaptureMode.Auto;
            while (!_stopped)
            {
                if (!auto)
                {
                    await Task.Delay(30);
                    if (_stopped) break;
                    Post(Capture(_region));
                    continue;
                }

                if (!_reachedBottom) SendScrollDown();
                // Sample the scroll animation several times per wheel so the stitched
                // image (and therefore the preview) keeps growing between wheel steps.
                var until = Environment.TickCount64 + Delay;
                while (!_stopped && Environment.TickCount64 < until)
                {
                    await Task.Delay(90);
                    if (_stopped) break;
                    Post(Capture(_region));
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("长截图抓帧失败", ex);
        }
    }

    /// <summary>
    /// Sends one wheel step straight to the window under the selection instead of
    /// dragging the pointer there, so the mouse stays completely free: the user can
    /// move it away and click 完成 / 编辑 / ↓ / × at any time while auto scrolling.
    /// </summary>
    private void SendScrollDown()
    {
        var target = WheelTarget();
        if (target == IntPtr.Zero) return;
        var point = new Point(_region.Left + _region.Width / 2, _region.Top + _region.Height / 2);
        var delta = unchecked((ushort)(short)-360); // three notches down
        var wparam = new IntPtr((int)((uint)delta << 16));
        var lparam = new IntPtr((point.Y << 16) | (point.X & 0xFFFF));
        PostMessage(target, 0x020A, wparam, lparam); // WM_MOUSEWHEEL
    }

    /// <summary>Top-most foreign window under the selection centre, cached briefly.</summary>
    private IntPtr WheelTarget()
    {
        var now = Environment.TickCount64;
        if (_wheelTarget != IntPtr.Zero && now - _wheelTargetAt < 1500) return _wheelTarget;
        _wheelTargetAt = now;
        _wheelTarget = IntPtr.Zero;

        // The preview / toolbar are ours: probe a few points until a foreign window
        // answers, so the wheel can never end up scrolling our own UI.
        var probes = new[]
        {
            new Point(_region.Left + _region.Width / 2, _region.Top + _region.Height / 2),
            new Point(_region.Left + _region.Width / 2, _region.Top + _region.Height / 5),
            new Point(_region.Left + _region.Width / 5, _region.Top + _region.Height / 2),
            new Point(_region.Right - Math.Max(1, _region.Width / 5), _region.Top + _region.Height / 2)
        };
        foreach (var point in probes)
        {
            var handle = WindowFromPoint(point);
            if (handle == IntPtr.Zero) continue;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == (uint)Environment.ProcessId) continue;
            _wheelTarget = handle;
            break;
        }
        return _wheelTarget;
    }

    private void Post(Bitmap shot)
    {
        Bitmap? stale;
        lock (_pendingLock)
        {
            stale = _pending;
            _pending = shot;
        }
        stale?.Dispose();
    }

    private async Task StitchLoopAsync()
    {
        while (!_stopped)
        {
            Bitmap? shot;
            lock (_pendingLock)
            {
                shot = _pending;
                _pending = null;
            }
            if (shot is null)
            {
                await Task.Delay(12).ConfigureAwait(false);
                continue;
            }
            try
            {
                Stitch(shot);
            }
            catch (Exception ex)
            {
                AppLog.Write("长截图拼接失败", ex);
            }
            finally
            {
                shot.Dispose();
            }
        }
    }

    private void Stitch(Bitmap shot)
    {
        if (shot.Width != _width || shot.Height < MatchRows) return;
        var hashes = RowHashes(shot, out var solid);
        var auto = _settings.LongCaptureMode == LongCaptureMode.Auto;

        // Only auto mode ever stops by itself, and only after the page has been frozen
        // for a long stretch (15–20 wheel commands, ~5 s). Manual mode keeps listening
        // until the user presses 完成 / 复制 / 取消 / Esc.
        if (auto && Identical(hashes, _lastHashes))
        {
            if (++_autoIdle >= AutoIdleFrames)
                ReachBottom(UiText.T("已到达底部，点击完成保存"));
        }
        else
        {
            _autoIdle = 0;
        }
        _lastHashes = hashes;

        if (_prevRows == 0)
        {
            SetBaseline(hashes, solid, shot.Height);
            return;
        }

        // Always frame N against frame N-1: the baseline is the previous captured
        // frame, never the first one, so the overlap can never be lost by scrolling.
        var shift = FindScroll(_prevHashes, _prevSolid, _prevRows, hashes, solid, shot.Height);
        if (shift < 0)
        {
            // Manual mode must never stop here. If the baseline lost track of the view
            // (big jump, long flat stretch) re-sync it so later frames stitch again.
            if (++_anchorMisses >= 2)
            {
                SetBaseline(hashes, solid, shot.Height);
                _anchorMisses = 0;
            }
            return;
        }
        _anchorMisses = 0;

        // Unchanged frame: refresh nothing, nothing new to append.
        if (shift == 0) return;

        // Sub-threshold jitter: keep the old baseline so the offset accumulates and is
        // stitched once it passes the threshold instead of being dropped row by row.
        if (shift < MinOffset) return;

        var start = shot.Height - shift;
        var rows = Math.Min(shift, MaxHeight - Volatile.Read(ref _totalHeight));
        if (rows <= 0)
        {
            // Height cap reached: manual mode keeps listening, auto mode stops the wheel.
            if (auto) ReachBottom(UiText.T("已达到最大高度，点击完成保存"));
            return;
        }

        var slice = shot.Clone(new Rectangle(0, start, _width, rows), PixelFormat.Format32bppPArgb);
        var offset = Volatile.Read(ref _totalHeight);
        lock (_chunkLock)
        {
            _chunks.Add(slice);
            _totalHeight += rows;
        }
        AppendThumb(offset, shot, start, rows);
        // The frame just stitched becomes the baseline for the next one.
        SetBaseline(hashes, solid, shot.Height);
        MergeTail();
        if (auto && Volatile.Read(ref _totalHeight) >= MaxHeight)
            ReachBottom(UiText.T("已达到最大高度，点击完成保存"));
        NotifyPreview();
    }

    /// <summary>
    /// Folds the freshly appended slices into one ~1200 px block so the list stays
    /// short no matter how long the capture grows (runs on the stitcher thread).
    /// </summary>
    private void MergeTail()
    {
        lock (_chunkLock)
        {
            var start = Math.Min(_mergeIndex, _chunks.Count);
            var rows = 0;
            for (var i = start; i < _chunks.Count; i++) rows += _chunks[i].Height;
            if (rows < MergeBlock) return;

            var merged = new Bitmap(_width, rows, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(merged))
            {
                var y = 0;
                for (var i = start; i < _chunks.Count; i++)
                {
                    g.DrawImageUnscaled(_chunks[i], 0, y);
                    y += _chunks[i].Height;
                    _chunks[i].Dispose();
                }
            }
            _chunks.RemoveRange(start, _chunks.Count - start);
            _chunks.Add(merged);
            _mergeIndex = _chunks.Count;
        }
    }

    private static bool Identical(ulong[] hashes, ulong[]? previous)
        => previous is not null && previous.Length == hashes.Length
           && hashes.AsSpan().SequenceEqual(previous);

    /// <summary>
    /// Replaces the comparison baseline with the frame just captured. The old baseline
    /// is dropped outright, so a frame can never be matched against anything older
    /// than its immediate predecessor.
    /// </summary>
    private void SetBaseline(ulong[] hashes, bool[] solid, int rows)
    {
        _prevHashes = hashes;
        _prevSolid = solid;
        _prevRows = rows;
    }

    private void ReachBottom(string message)
    {
        if (_reachedBottom) return;
        _reachedBottom = true;
        NotifyPreview();
    }

    /// <summary>Coalesces worker notifications into one repaint per UI message turn.</summary>
    private void NotifyPreview()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) == 1) return;
        try
        {
            if (!IsHandleCreated)
            {
                _notifyPending = 0;
                return;
            }
            BeginInvoke(new Action(() =>
            {
                _notifyPending = 0;
                _preview?.RefreshImage();
            }));
        }
        catch (InvalidOperationException)
        {
            _notifyPending = 0;
        }
    }

    /// <summary>
    /// ShareX-style row signatures: LockBits, skip 15% of both edges, fold every other
    /// pixel (low channel bits masked so anti-aliasing noise is tolerated) into an
    /// FNV-1a 64-bit hash — one comparison per row during overlap search. Rows that are
    /// a single flat colour are flagged so they never vote in the overlap match.
    /// </summary>
    private static ulong[] RowHashes(Bitmap bitmap, out bool[] solid)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var hashes = new ulong[height];
        solid = new bool[height];
        var inset = Math.Min(width * HashInsetPercent / 100, Math.Max(0, width / 2 - 2));
        var end = width - inset;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new int[width];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, width);
                var hash = 14695981039346656037UL;
                var first = row[inset] & 0x00F8F8F8;
                var flat = true;
                for (var x = inset; x < end; x += HashStride)
                {
                    var value = row[x] & 0x00F8F8F8;
                    if (value != first) flat = false;
                    hash = (hash ^ (uint)value) * 1099511628211UL;
                }
                hashes[y] = hash;
                solid[y] = flat;
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return hashes;
    }

    /// <summary>
    /// Vertical offset between two consecutive captured frames: how far the page
    /// scrolled down since the previous one. Every candidate shift is scored over the
    /// whole overlap, counting matching content rows, so a blinking caret, a sticky bar
    /// or a single changed row can no longer veto an otherwise perfect alignment (the
    /// old 96-row band aborted at the first mismatched row). Returns 0 when the frame
    /// did not move, -1 when the frames share no usable overlap.
    /// </summary>
    private static int FindScroll(ulong[] prev, bool[] prevSolid, int prevRows,
        ulong[] current, bool[] currentSolid, int currentRows)
    {
        if (prevRows < MatchRows || currentRows < MatchRows) return -1;
        var maxShift = Math.Min(prevRows, currentRows) - MatchRows;
        var bestShift = -1;
        var bestScore = MatchRows - 1;
        for (var shift = 0; shift <= maxShift; shift++)
        {
            var limit = Math.Min(currentRows, prevRows - shift);
            if (limit <= bestScore) break; // remaining shifts can never score higher
            var score = 0;
            var run = 0;
            var bestRun = 0;
            for (var y = 0; y < limit; y++)
            {
                if (prev[y + shift] != current[y]) { run = 0; continue; }
                if (++run > bestRun) bestRun = run;
                // Flat (white / solid colour) rows carry no information.
                if (!prevSolid[y + shift] && !currentSolid[y]) score++;
            }
            if (bestRun < MatchRows || score <= bestScore) continue;
            bestScore = score;
            bestShift = shift;
        }
        return bestShift;
    }

    private static new Bitmap Capture(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    // ------------------------------------------------------------ finishing

    public void FinishNow(bool exit = true)
    {
        if (_stopped) return;
        try
        {
            // Silent auto-save through the normal output pipeline: the folder, file
            // name template and image format all come from 首选项 → 输出.
            using var output = Export();
            var path = Services.SaveCaptureFile(output, _settings, _settings.SaveDirectory);
            AppLog.Write($"长截图完成并保存：{output.Width}×{output.Height} → {path}");
            Toast.Show(UiText.T("已保存：") + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            AppLog.Write("长截图保存失败", ex);
            Toast.Error(UiText.T("保存失败：") + ex.Message);
            return;
        }
        if (exit)
        {
            Stop();
            Saved?.Invoke();
        }
    }

    /// <summary>True once auto scrolling is past its warm-up and still running.</summary>
    private bool AutoRunning =>
        !_stopped && !_reachedBottom &&
        _settings.LongCaptureMode == LongCaptureMode.Auto &&
        Environment.TickCount64 - _autoStarted >= 500;

    /// <summary>Space / Enter during auto scroll: stop the wheel, save and close.</summary>
    private void FlushAuto()
    {
        if (_stopped) return;
        _reachedBottom = true; // stop sending wheel steps
        FinishNow();
    }

    public void CancelNow()
    {
        if (_stopped) return;
        Stop();
        Cancelled?.Invoke();
    }

    /// <summary>Saves the assembled long image to a file the user picks.</summary>
    public void SaveAsNow(bool exit = true)
    {
        if (_stopped) return;
        using (var dialog = new SaveFileDialog
        {
            Filter = UiText.T("PNG 图片|*.png|JPEG 图片|*.jpg"),
            FileName = Services.ImageFileName(_settings, ".png"),
            OverwritePrompt = true
        })
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;
            try
            {
                using var output = Export();
                var jpeg = Path.GetExtension(dialog.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase);
                Services.SaveImage(output, dialog.FileName, jpeg, _settings.JpegQuality);
                Toast.Show(UiText.T("已保存：") + Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                AppLog.Write("长截图另存为失败", ex);
                Toast.Error(UiText.T("保存失败：") + ex.Message);
                return;
            }
        }
        if (!exit) return;
        Stop();
        Saved?.Invoke();
    }

    /// <summary>Runs one of the configured 首选项 → 截屏 → 按键 actions on the long image.</summary>
    public void RunAction(CaptureAction action, bool exit)
    {
        if (_stopped) return;
        switch (action)
        {
            case CaptureAction.Copy:
                CopyNow(exit);
                return;
            case CaptureAction.Pin:
                PinNow(exit);
                return;
            case CaptureAction.QuickSave:
                FinishNow(exit);
                return;
            default:
                // 无操作：only the "then exit" switch has an effect.
                if (exit) CancelNow();
                return;
        }
    }

    /// <summary>Pins the assembled long image to the desktop and leaves capture mode.</summary>
    public void PinNow(bool exit = true)
    {
        if (_stopped) return;
        try
        {
            var image = Export();
            new PinForm(image, null, 100, _settings).Show();
        }
        catch (Exception ex)
        {
            AppLog.Write("长截图贴图失败", ex);
            Toast.Error(UiText.T("贴图失败：") + ex.Message);
            return;
        }
        if (exit)
        {
            Stop();
            Copied?.Invoke();
        }
    }

    /// <summary>Mouse right click on the preview: follows 首选项 → 截屏 → 鼠标右键.</summary>
    public void HandleRightClick()
    {
        if (_stopped) return;
        switch (_settings.RightClickAction)
        {
            case RightClickBehavior.ExitCapture:
                CancelNow();
                return;
            case RightClickBehavior.None:
                return;
            case RightClickBehavior.SaveFile:
                SaveAsNow(_settings.RightClickExit);
                return;
            case RightClickBehavior.QuickSave:
                RunAction(CaptureAction.QuickSave, _settings.RightClickExit);
                return;
            default:
                ShowMenu();
                return;
        }
    }

    private void ShowMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(UiText.T("复制到剪贴板"), null, (_, _) => CopyNow());
        menu.Items.Add(UiText.T("贴到屏幕"), null, (_, _) => PinNow());
        menu.Items.Add(UiText.T("快速保存"), null, (_, _) => FinishNow());
        menu.Items.Add(UiText.T("另存为…"), null, (_, _) => SaveAsNow());
        menu.Items.Add(UiText.T("编辑长图"), null, (_, _) => EditNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(UiText.T("取消"), null, (_, _) => CancelNow());
        menu.Show(Cursor.Position);
    }

    /// <summary>Copies the assembled long image to the clipboard and leaves capture mode.</summary>
    public void CopyNow(bool exit = true)
    {
        if (_stopped) return;
        try
        {
            using var output = Export();
            Clipboard.SetImage(output);
            Toast.Show(UiText.T("图像已复制"));
        }
        catch (Exception ex)
        {
            AppLog.Write("长截图复制失败", ex);
            Toast.Error(UiText.T("复制失败：") + ex.Message);
            return;
        }
        if (!exit) return;
        Stop();
        Copied?.Invoke();
    }

    /// <summary>
    /// (Re)builds the pre-scaled preview cache at the requested width. The preview then
    /// only blits it 1:1, so painting never scales a multi-thousand pixel image.
    /// </summary>
    public void PrepareThumb(int width)
    {
        if (width < 8) return;
        lock (_thumbLock)
        {
            if (_thumb is not null && _thumbWidth == width) return;
            var total = Volatile.Read(ref _totalHeight);
            if (total <= 0) return;
            var scale = width / (float)_width;
            var height = Math.Max(1, (int)Math.Round(total * scale));
            var capacity = height + Math.Max(64, height / 4);
            var thumb = new Bitmap(width, capacity, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var y = 0;
                foreach (var slice in Chunks(out _, out _))
                {
                    var rows = Math.Max(1, (int)Math.Round(slice.Height * scale));
                    g.DrawImage(slice, new Rectangle(0, y, width, rows),
                        new Rectangle(0, 0, slice.Width, slice.Height), GraphicsUnit.Pixel);
                    y += rows;
                }
            }
            _thumb?.Dispose();
            _thumb = thumb;
            _thumbWidth = width;
            _thumbScale = scale;
            _thumbHeight = Math.Min(height, capacity);
        }
    }

    /// <summary>Scales the freshly matched rows into the preview cache (stitcher thread).</summary>
    private void AppendThumb(int sourceRow, Bitmap shot, int start, int rows)
    {
        lock (_thumbLock)
        {
            if (_thumb is null) return;
            var top = (int)Math.Floor(sourceRow * _thumbScale);
            var bottom = Math.Max(top + 1, (int)Math.Ceiling((sourceRow + rows) * _thumbScale));
            if (bottom > _thumb.Height)
            {
                var capacity = bottom + Math.Max(64, bottom / 4);
                var grown = new Bitmap(_thumbWidth, capacity, PixelFormat.Format32bppPArgb);
                using (var gg = Graphics.FromImage(grown)) gg.DrawImageUnscaled(_thumb, 0, 0);
                _thumb.Dispose();
                _thumb = grown;
            }
            using (var g = Graphics.FromImage(_thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(shot, new Rectangle(0, top, _thumbWidth, bottom - top),
                    new Rectangle(0, start, shot.Width, rows), GraphicsUnit.Pixel);
            }
            _thumbHeight = Math.Max(_thumbHeight, bottom);
        }
    }

    /// <summary>Painting entry point: one 1:1 blit, no scaling and no chunk walking.</summary>
    public void DrawThumb(Graphics g, int y)
    {
        lock (_thumbLock)
        {
            if (_thumb is not null) g.DrawImageUnscaled(_thumb, 0, y);
        }
    }

    public int ThumbHeight
    {
        get { lock (_thumbLock) return _thumbHeight; }
    }

    /// <summary>Stops scrolling and hands the assembled long image to the annotation editor.</summary>
    public void EditNow()
    {
        if (_stopped) return;
        Bitmap image;
        try
        {
            image = Export();
        }
        catch (Exception ex)
        {
            AppLog.Write("长截图导出失败", ex);
            return;
        }
        Stop();
        if (Edited is null)
        {
            image.Dispose();
            return;
        }
        Edited.Invoke(image);
    }

    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _preview?.Close();
        _preview?.Dispose();
        _preview = null;
        _bar?.Close();
        _bar?.Dispose();
        _bar = null;
        Close();
    }

    /// <summary>Global Esc / Enter: the long-capture equivalent of the capture shortcuts.</summary>
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104)) // key down
        {
            var info = Marshal.PtrToStructure<KbdHook>(lParam);
            if (info.VirtualKey == 0x1B)
            {
                try
                {
                    if (IsHandleCreated) BeginInvoke(new Action(CancelNow));
                }
                catch (InvalidOperationException) { }
                return (IntPtr)1;
            }
            // While auto scrolling is running (after its 500 ms warm-up) Space or Enter
            // stops the wheel and saves the finished long image straight away.
            if ((info.VirtualKey == 0x20 || info.VirtualKey == 0x0D) && AutoRunning)
            {
                try
                {
                    if (IsHandleCreated) BeginInvoke(new Action(FlushAuto));
                }
                catch (InvalidOperationException) { }
                return (IntPtr)1;
            }
            if (info.VirtualKey == 0x0D) // Enter: 首选项 → 截屏 → 按键 → 回车键
            {
                try
                {
                    if (IsHandleCreated)
                        BeginInvoke(new Action(() => RunAction(_settings.EnterAction, _settings.EnterExit)));
                }
                catch (InvalidOperationException) { }
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopped = true;
            _backdrop?.Dispose();
            lock (_thumbLock)
            {
                _thumb?.Dispose();
                _thumb = null;
            }
            Unhook();
            lock (_pendingLock)
            {
                _pending?.Dispose();
                _pending = null;
            }
            lock (_chunkLock)
            {
                foreach (var slice in _chunks) slice.Dispose();
                _chunks.Clear();
                _totalHeight = 0;
            }
        }
        base.Dispose(disposing);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            CancelNow();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdHook
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var sel = SelectionClient;

        // Frozen desktop under a 43 % black wash: the classic "dimmed" capture look, drawn
        // once and never invalidated while scrolling (zero repaint cost during capture).
        g.DrawImageUnscaled(_backdrop, Point.Empty);
        using (var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            g.FillRectangle(dim, ClientRectangle);

        // Crisp white border right around the cut-out so the scrolling area pops out.
        var frame = new Rectangle(sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
        using var border = new Pen(Color.White, 1);
        g.DrawRectangle(border, frame.X, frame.Y, frame.Width, frame.Height);
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

internal sealed class LongPreviewForm : Form
{
    private readonly LongCaptureForm _owner;
    private readonly PreviewCanvas _canvas;
    private readonly float _ui;
    private int _scrollY;
    private int _maxScroll;
    private bool _follow = true;
    private bool _browsing;
    private bool _manual;
    private bool _autoSizing;
    private long _lastPaint;

    public LongPreviewForm(LongCaptureForm owner, float ui)
    {
        _owner = owner;
        _ui = ui;
        // Bare image card: no title bar, no size text and no buttons at all.
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(214, 221, 228);
        DoubleBuffered = true;
        // Compact floating viewport: never wider than ~320 px and never taller than the
        // selection, so the repaint cost stays constant however long the capture grows.
        var work = Screen.FromRectangle(owner.CaptureRegion).WorkingArea;
        var tall = Math.Min(owner.CaptureRegion.Height,
            Math.Min((int)(620 * ui), work.Height * 7 / 10));
        Height = Math.Max((int)(300 * ui), tall);
        Width = Math.Clamp((int)(300 * ui), (int)(260 * ui), (int)(320 * ui));

        _canvas = new PreviewCanvas { Dock = DockStyle.Fill, BackColor = AppTheme.Current.Field };
        _canvas.Paint += (_, e) => DrawPreview(e.Graphics);
        _canvas.MouseWheel += (_, e) => ScrollBy(e.Delta);
        _canvas.DoubleClick += (_, _) =>
            _owner.RunAction(owner.Settings.DoubleClickAction, owner.Settings.DoubleClickExit);
        _canvas.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Middle)
                _owner.RunAction(owner.Settings.MiddleClickAction, owner.Settings.MiddleClickExit);
        };
        _canvas.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) _owner.HandleRightClick();
        };
        // The card itself swallows the context menu so a stray system menu never appears
        // on top of the save dialog.
        _canvas.ContextMenuStrip = new ContextMenuStrip();
        MouseWheel += (_, e) => ScrollBy(e.Delta);

        Controls.Add(_canvas);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) _owner.CancelNow(); };
    }

    /// <summary>Double-buffered canvas so the growing capture repaints without flicker.</summary>
    private sealed class PreviewCanvas : Panel
    {
        public PreviewCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        // Let the host form answer the hit test, otherwise the panel swallows the
        // border drag / resize requests.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00040000;  // WS_THICKFRAME: real resize borders, no caption
            cp.ExStyle |= 0x00000080; // tool window
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero) // WM_NCCALCSIZE: drop the frame
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)1) m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == 0x0084) // WM_NCHITTEST: drag anywhere, resize on the border
        {
            var p = PointToClient(Cursor.Position);
            var edge = (int)Math.Round(6 * _ui);
            var left = p.X < edge;
            var right = p.X >= ClientSize.Width - edge;
            var top = p.Y < edge;
            var bottom = p.Y >= ClientSize.Height - edge;
            var code = 2; // HTCAPTION
            if (top && left) code = 13;
            else if (top && right) code = 14;
            else if (bottom && left) code = 16;
            else if (bottom && right) code = 17;
            else if (left) code = 10;
            else if (right) code = 11;
            else if (top) code = 12;
            else if (bottom) code = 15;
            m.Result = (IntPtr)code;
            return;
        }
        // Because the whole card answers HTCAPTION, mouse gestures arrive as non-client
        // messages: dispatch the configured 首选项 → 截屏 → 按键 actions from here.
        switch (m.Msg)
        {
            case 0x00A3: // WM_NCLBUTTONDBLCLK — 左键双击
                _owner.RunAction(_owner.Settings.DoubleClickAction, _owner.Settings.DoubleClickExit);
                return;
            case 0x00A5: // WM_NCRBUTTONUP — 鼠标右键
                _owner.HandleRightClick();
                return;
            case 0x00A8: // WM_NCMBUTTONUP — 中键单击
                _owner.RunAction(_owner.Settings.MiddleClickAction, _owner.Settings.MiddleClickExit);
                return;
            case 0x020A: // WM_MOUSEWHEEL
                ScrollBy((short)((long)m.WParam >> 16));
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Auto growth stops as soon as the user resizes the card by hand.
        SizeChanged += (_, _) => { if (!_autoSizing) _manual = true; };
        Place();
    }

    /// <summary>
    /// Right of the selection when there is room, below it for wide selections, and
    /// finally floating over the selection's bottom-right corner.
    /// </summary>
    private void Place()
    {
        var screen = Screen.FromRectangle(_owner.CaptureRegion).WorkingArea;
        var sel = _owner.CaptureRegion;
        var gap = (int)Math.Round(8 * _ui);
        var inset = (int)Math.Round(12 * _ui);
        var bottomLimit = screen.Bottom - (int)Math.Round(16 * _ui);
        var minWidth = (int)Math.Round(260 * _ui);
        var rightSpace = screen.Right - (sel.Right + gap);
        var belowSpace = bottomLimit - (sel.Bottom + gap);

        int left;
        int top;
        var overlaid = false;
        if (rightSpace >= minWidth)
        {
            Width = Math.Min(rightSpace, (int)Math.Round(320 * _ui));
            left = sel.Right + gap;
            top = Math.Max(screen.Top, sel.Top);
        }
        else if (belowSpace >= (int)Math.Round(200 * _ui))
        {
            Width = Math.Clamp((int)Math.Round(sel.Width * 0.55), minWidth, (int)Math.Round(420 * _ui));
            left = sel.Left + (sel.Width - Width) / 2;
            top = sel.Bottom + gap;
        }
        else
        {
            Width = (int)Math.Round(320 * _ui);
            left = sel.Right - Width - inset;
            top = screen.Top;
            overlaid = true;
        }
        left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - Width));

        // First "screen": the card is exactly as tall as the stitched image at this
        // width, so the picture fills it with no empty margins.
        var imageWidth = Math.Max(1, _owner.CaptureWidth);
        var imageHeight = Math.Max(1, _owner.CaptureHeight);
        var maxHeight = Math.Max((int)Math.Round(160 * _ui), bottomLimit - top);
        _autoSizing = true;
        try
        {
            Height = Math.Clamp((int)Math.Round(Width * imageHeight / (double)imageWidth),
                (int)Math.Round(120 * _ui), maxHeight);
        }
        finally
        {
            _autoSizing = false;
        }

        if (overlaid) top = sel.Bottom - Height - inset;
        top = Math.Clamp(top, screen.Top, Math.Max(screen.Top, bottomLimit - Height));
        Location = new Point(left, top);
        _manual = false;
        RefreshImage();
    }

    private int MaxHeight() =>
        Math.Max((int)Math.Round(120 * _ui),
            Screen.FromRectangle(Bounds).WorkingArea.Bottom - (int)Math.Round(16 * _ui) - Top);

    /// <summary>
    /// Stage 1 of the timeline: the window keeps growing downwards so the image always
    /// fills it completely; once it reaches the bottom of the work area it stops and the
    /// view simply scrolls (stage 2).
    /// </summary>
    private void AutoGrow()
    {
        if (_manual) return;
        var imageWidth = Math.Max(1, _owner.CaptureWidth);
        var imageHeight = Math.Max(1, _owner.CaptureHeight);
        var target = (int)Math.Round(Width * imageHeight / (double)imageWidth);
        var height = Math.Clamp(target, (int)Math.Round(120 * _ui), MaxHeight());
        if (Math.Abs(height - Height) < 1) return;
        _autoSizing = true;
        try
        {
            Height = height;
        }
        finally
        {
            _autoSizing = false;
        }
    }

    public void RefreshImage()
    {
        AutoGrow();
        // Pin the viewport to the newest rows on the UI thread so the visible bottom of
        // the long image advances with every freshly appended slice.
        _follow = !_browsing && !_owner.ReachedBottom;
        var area = _canvas.ClientRectangle;
        if (area.Width > 8 && area.Height > 8)
        {
            _owner.PrepareThumb(area.Width);
            _maxScroll = Math.Max(0, _owner.ThumbHeight - area.Height);
            if (_follow) _scrollY = _maxScroll;
        }
        var now = Environment.TickCount64;
        _canvas.Invalidate();
        // Repaint at most ~30 times per second: extra stitched frames coalesce into
        // the next normal paint instead of blocking the stitcher.
        if (now - _lastPaint < 30) return;
        _lastPaint = now;
        _canvas.Update();
    }

    private void ScrollBy(int delta)
    {
        if (_maxScroll <= 0) return;
        var step = Math.Max((int)(40 * _ui), _canvas.Height / 8);
        _scrollY = Math.Clamp(_scrollY - Math.Sign(delta) * step, 0, _maxScroll);
        // Scrolling away from the bottom pauses the auto-follow; returning to the bottom
        // resumes it, so new slices keep appearing while the user browses history.
        _browsing = _scrollY < _maxScroll - 2;
        _canvas.Invalidate();
    }

    /// <summary>
    /// Pure 1:1 blit of the pre-scaled preview cache: no scaling, no chunk walking, so a
    /// repaint costs well under a millisecond even for a 100 000 px capture.
    /// </summary>
    private void DrawPreview(Graphics g)
    {
        var area = _canvas.ClientRectangle;
        g.Clear(AppTheme.Current.Field);
        if (area.Width < 8 || area.Height < 8) return;

        _owner.PrepareThumb(area.Width);
        var contentHeight = _owner.ThumbHeight;
        if (contentHeight < 1) return;

        _maxScroll = Math.Max(0, contentHeight - area.Height);
        if (_follow) _scrollY = _maxScroll;
        _scrollY = Math.Clamp(_scrollY, 0, _maxScroll);

        var state = g.Save();
        g.SetClip(area);
        _owner.DrawThumb(g, area.Y - _scrollY);
        g.Restore(state);

        if (_maxScroll > 0) DrawScrollBar(g, area, contentHeight);

        // Hairline frame so the bare card edge reads clearly on any background.
        using var edge = new Pen(Color.FromArgb(178, 190, 200));
        g.DrawRectangle(edge, 0, 0, area.Width - 1, area.Height - 1);
    }

    private void DrawScrollBar(Graphics g, Rectangle area, int contentHeight)
    {
        var width = (int)Math.Round(4 * _ui);
        var x = area.Right - width - (int)Math.Round(3 * _ui);
        var track = new Rectangle(x, area.Y + (int)Math.Round(6 * _ui), width,
            area.Height - (int)Math.Round(12 * _ui));
        if (track.Height < 20 || contentHeight < 1) return;
        var thumbHeight = Math.Max((int)Math.Round(28 * _ui),
            (int)Math.Round(track.Height * (track.Height / (double)contentHeight)));
        var travel = track.Height - thumbHeight;
        var offset = _maxScroll <= 0 ? 0 : (int)Math.Round(travel * (_scrollY / (double)_maxScroll));
        using var brush = new SolidBrush(Color.FromArgb(140, AppTheme.Current.Text));
        using var path = FluentDrawing.Rounded(
            new RectangleF(track.X, track.Y + offset, width, thumbHeight), width / 2f);
        g.FillPath(brush, path);
    }

    private AppSettings Settings => _owner.Settings;
}
