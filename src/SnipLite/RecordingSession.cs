using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipLite;

internal interface IFrameWriter : IDisposable
{
    void WriteFrame(Bitmap frame, double seconds);
    void Complete();
}

/// <summary>
/// Grabs the selected screen region on a background thread and streams every frame
/// into a GIF or video writer, so long recordings never pile up bitmaps in memory.
/// </summary>
internal sealed class RecordingSession
{
    private readonly Rectangle _region;
    private readonly AppSettings _settings;
    private readonly bool _gif;
    private readonly SynchronizationContext? _sync;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile bool _save = true;
    private volatile bool _paused;
    private double _seconds;
    private double _limit;
    private bool _limitReached;
    /// <summary>PCM handed over from the audio thread; only the capture thread writes it.</summary>
    private readonly ConcurrentQueue<byte[]> _audioQueue = new();
    private string? _audioNote;
    private MediaFoundationWriter? _audioTarget;
    private int _audioRate = 48000;
    private int _audioChannels = 2;
    private long _audioWritten;

    /// <summary>
    /// Writes every PCM chunk captured so far with a sample-accurate timestamp, so the
    /// audio never runs ahead of the video and never loses its tail on stop.
    /// </summary>
    private void DrainAudio()
    {
        if (_audioTarget is null) return;
        while (_audioQueue.TryDequeue(out var chunk))
        {
            if (chunk.Length == 0) continue;
            _audioTarget.WriteAudio(chunk, chunk.Length,
                _audioWritten * 10_000_000L / ((long)_audioRate * _audioChannels));
            _audioWritten += chunk.Length / 2;
        }
    }

    private static RecordingSession? _current;

    /// <summary>The session in progress, if any (drives the hotkey toggle).</summary>
    public static RecordingSession? Current => _current;

    public event Action<RecordingSession>? Finished;

    public string? FilePath { get; private set; }
    public string? Error { get; private set; }
    public string? Note { get; private set; }
    public bool LimitReached => _limitReached;
    public bool Paused => _paused;
    public double Seconds => _seconds;

    public RecordingSession(Rectangle region, AppSettings settings, bool gif)
    {
        _region = region;
        _settings = settings;
        _gif = gif;
        _sync = SynchronizationContext.Current;
        _thread = new Thread(Run) { IsBackground = true, Name = "SnipLite recording" };
    }

    public void Start()
    {
        _current = this;
        _thread.Start();
    }

    public void TogglePause() => _paused = !_paused;

    /// <summary>Stops the loop; <paramref name="save"/> = false discards the file.</summary>
    public void Stop(bool save = true)
    {
        _save = save;
        _running = false;
    }

    private void Run()
    {
        CoInitializeEx(IntPtr.Zero, 0); // MTA: the Media Foundation objects live here
        IFrameWriter? writer = null;
        WasapiCapture? audio = null;
        Thread? audioThread = null;
        var capture = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppPArgb);
        Bitmap? scaled = null;
        string? tempPath = null;
        var finalPath = "";

        try
        {
            var fps = _gif ? _settings.GifFpsValue : _settings.RecFpsValue;
            var showCursor = _gif ? _settings.GifShowCursor : _settings.RecShowCursor;
            var size = _gif
                ? new Size(Math.Max(2, (int)Math.Round(_region.Width * _settings.GifScale)),
                    Math.Max(2, (int)Math.Round(_region.Height * _settings.GifScale)))
                : _settings.RecTargetSize(_region.Size);
            _limit = _gif ? _settings.GifDurationSeconds : _settings.RecDurationSeconds;

            if (_gif)
            {
                finalPath = ResolvePath(".gif");
                writer = new GifWriter(finalPath, size, fps, _settings.GifLoop);
            }
            else
            {
                finalPath = ResolvePath(_settings.RecExtension);
                if (_settings.RecFormat == VideoContainer.Avi)
                {
                    if (_settings.RecAudio != RecordAudioSource.Mute)
                        Note = UiText.T("AVI 暂不支持音轨，已按无声录制。");
                    writer = new AviMjpegWriter(finalPath, size.Width, size.Height, fps);
                }
                else
                {
                    audio = TryStartAudio();
                    // Media Foundation writes MP4 boxes; a .mov file is the same ISO BMFF
                    // container, so record into a temporary .mp4 and rename on completion.
                    tempPath = Path.Combine(Path.GetDirectoryName(finalPath)!,
                        Path.GetFileNameWithoutExtension(finalPath) + ".rec.mp4");
                    try
                    {
                        writer = new MediaFoundationWriter(tempPath, size.Width, size.Height, fps,
                            _settings.RecBitrateKbps * 1000,
                            audio?.SampleRate ?? 0, audio?.Channels ?? 0);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("H.264 编码器不可用，回退到 AVI/MJPEG", ex);
                        writer?.Dispose();
                        writer = null;
                        audio?.Dispose();
                        audio = null;
                        TryDelete(tempPath);
                        tempPath = null;
                        finalPath = Path.ChangeExtension(finalPath, ".avi");
                        writer = new AviMjpegWriter(finalPath, size.Width, size.Height, fps);
                        Note = UiText.T("H.264 编码器不可用，已改用 AVI (MJPEG) 保存。");
                    }

                    if (writer is MediaFoundationWriter mf)
                    {
                        if (mf.AudioNote is not null) Note = mf.AudioNote;
                        if (!mf.AudioEnabled && audio is not null)
                        {
                            audio.Dispose();
                            audio = null;
                        }
                    }
                }
                if (audio is not null && writer is MediaFoundationWriter)
                {
                    // Capturing runs on its own thread so a slow video frame can never let
                    // the WASAPI ring overflow, but the PCM is written here on the capture
                    // thread: two threads inside WriteSample would queue behind the video
                    // encoder and the tail of the audio was lost whenever recording stopped.
                    audio.Start();
                    var device = audio;
                    audioThread = new Thread(() =>
                    {
                        while (_running)
                        {
                            if (_paused)
                            {
                                try { device.Discard(); } catch { }
                                Thread.Sleep(20);
                                continue;
                            }
                            try
                            {
                                var pcm = device.Read();
                                if (pcm.Length > 0) _audioQueue.Enqueue(pcm);
                            }
                            catch (Exception ex)
                            {
                                AppLog.Write("音频采集中断", ex);
                                _audioNote = UiText.T("音频采集中断，视频已无声保存。");
                                break;
                            }
                            Thread.Sleep(10);
                        }
                        // Pick up the tail the device still holds, otherwise the last
                        // fraction of a second is cut off on every stop.
                        try { var tail = device.Read(); if (tail.Length > 0) _audioQueue.Enqueue(tail); }
                        catch { }
                    })
                    { IsBackground = true, Name = "SnipLite audio" };
                    audioThread.Start();
                }

                _audioTarget = writer as MediaFoundationWriter;
                _audioRate = Math.Max(8000, audio?.SampleRate ?? 48000);
                _audioChannels = Math.Max(1, audio?.Channels ?? 2);
            }

            if (size != _region.Size)
            {
                scaled = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            }

            var interval = 1.0 / Math.Max(1, fps);
            var frames = 0;
            double next = 0;
            var clock = Stopwatch.StartNew();
            double pausedTotal = 0;
            double pausedAt = 0;
            while (_running)
            {
                if (_paused)
                {
                    if (pausedAt <= 0) pausedAt = clock.Elapsed.TotalSeconds;
                    Thread.Sleep(25);
                    continue;
                }
                if (pausedAt > 0)
                {
                    pausedTotal += clock.Elapsed.TotalSeconds - pausedAt;
                    pausedAt = 0;
                }

                var elapsed = clock.Elapsed.TotalSeconds - pausedTotal;
                if (elapsed + 1e-6 < next)
                {
                    // Sleep out the remainder of the frame slot instead of spinning on
                    // 1 ms slices, so an idle capture barely shows up in CPU usage.
                    var wait = (int)Math.Round((next - elapsed) * 1000) - 1;
                    Thread.Sleep(Math.Clamp(wait, 1, 40));
                    continue;
                }

                using (var graphics = Graphics.FromImage(capture))
                {
                    graphics.CopyFromScreen(_region.Location, Point.Empty, _region.Size, CopyPixelOperation.SourceCopy);
                    if (showCursor) Win32.DrawCursor(graphics, _region.Location);
                }

                DrainAudio();

                Bitmap frame = capture;
                if (scaled is not null)
                {
                    using (var graphics = Graphics.FromImage(scaled))
                    {
                        graphics.Clear(Color.Black);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        var ratio = Math.Min((float)scaled.Width / capture.Width, (float)scaled.Height / capture.Height);
                        var width = Math.Max(1, (int)Math.Round(capture.Width * ratio));
                        var height = Math.Max(1, (int)Math.Round(capture.Height * ratio));
                        graphics.DrawImage(capture,
                            new Rectangle((scaled.Width - width) / 2, (scaled.Height - height) / 2, width, height));
                    }
                    frame = scaled;
                }

                // Wall-clock timestamps keep the video in step with the real-time audio
                // stream even when encoding cannot keep up with the requested frame rate.
                writer.WriteFrame(frame, elapsed);
                frames++;
                next += interval;
                _seconds = elapsed;
                if (elapsed - next > interval * 4) next = elapsed; // never try to catch up

                if (_limit > 0 && elapsed >= _limit)
                {
                    _limitReached = true;
                    _running = false;
                }
            }

            // The capture is over: the tray badge drops back immediately, even while the
            // encoder is still finishing the file.
            if (ReferenceEquals(_current, this)) _current = null;

            if (_running == false && _save && frames == 0)
            {
                // Nothing was captured: do not leave a broken, empty file behind.
                writer.Dispose();
                writer = null;
                TryDelete(finalPath);
                if (tempPath is not null) TryDelete(tempPath);
            }
            else if (_running == false && _save)
            {
                audioThread?.Join(1000);
                DrainAudio(); // write everything captured before the file is closed
                audio?.Stop();
                if (_audioNote is not null) Note = _audioNote;
                writer.Complete();
                writer = null;
                if (tempPath is not null && File.Exists(tempPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    File.Move(tempPath, finalPath, true);
                    tempPath = null;
                }
                FilePath = finalPath;
            }
            else
            {
                writer.Dispose();
                writer = null;
                TryDelete(finalPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("录制失败", ex);
            Error = ex.Message;
            try { writer?.Dispose(); } catch { }
            writer = null;
            if (tempPath is not null) TryDelete(tempPath);
            if (!string.IsNullOrEmpty(finalPath)) TryDelete(finalPath);
        }
        finally
        {
            _running = false;
            audioThread?.Join(1000);
            audio?.Dispose();
            scaled?.Dispose();
            capture.Dispose();
            CoUninitialize();
            if (ReferenceEquals(_current, this)) _current = null;
            if (_sync is not null) _sync.Post(_ => Finished?.Invoke(this), null);
            else Finished?.Invoke(this);
        }
    }

    /// <summary>
    /// Opens the configured audio source: WASAPI loopback for the system mix, the default
    /// microphone, or the loopback when both are requested (mic mixing is not implemented).
    /// </summary>
    private WasapiCapture? TryStartAudio()
    {
        if (_settings.RecAudio == RecordAudioSource.Mute) return null;
        try
        {
            var loopback = _settings.RecAudio != RecordAudioSource.Microphone;
            var channels = _settings.RecChannels == RecordChannels.Mono ? 1 : 2;
            var audio = new WasapiCapture(loopback, channels);
            audio.Initialize();
            if (_settings.RecAudio == RecordAudioSource.SystemAndMicrophone)
                Note = UiText.T("已录制系统声音；麦克风混音暂未支持。");
            return audio;
        }
        catch (Exception ex)
        {
            AppLog.Write("音频捕获不可用", ex);
            Note = UiText.T("音频捕获不可用，视频已无声保存。");
            return null;
        }
    }

    private string ResolvePath(string extension)
    {
        var folder = _settings.SaveDirectory;
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException();
            Directory.CreateDirectory(folder);
        }
        catch
        {
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnipLite");
            Directory.CreateDirectory(folder);
        }
        var name = Services.ImageFileName(_settings, extension);
        return Services.UniqueFilePath(folder, name, extension);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
