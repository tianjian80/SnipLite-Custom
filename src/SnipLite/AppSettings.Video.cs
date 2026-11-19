namespace SnipLite;

internal enum VideoContainer { Mp4, Mov, Mkv, Webm, Avi }
internal enum VideoSize { Native, P720, P1080, Qhd, Uhd, Portrait720, Portrait1080, Portrait2K, Portrait4K, Recent }
internal enum VideoFps { Fps24, Fps25, Fps30, Fps60 }
internal enum VideoBitrateMode { Low, Medium, High, Ultra, Ultra4K, Custom }
internal enum VideoLength { Unlimited, S30, M1, M3, M5, M10, M30 }
internal enum RecordAudioSource { Mute, System, Microphone, SystemAndMicrophone }
internal enum RecordChannels { Stereo, Mono }
internal enum GifFps { Fps10, Fps15, Fps24 }
internal enum GifSizing { Original, Balanced, Light }
internal enum GifLength { S5, S15, S30, S60, Unlimited }

internal sealed partial class AppSettings
{
    // 录屏
    public VideoContainer RecFormat { get; set; } = VideoContainer.Mp4;
    public VideoSize RecResolution { get; set; } = VideoSize.Native;
    /// <summary>Last region the user framed for a recording; reused by 记忆上次选区.</summary>
    public Rectangle LastRecordRegion { get; set; } = Rectangle.Empty;
    public VideoFps RecFps { get; set; } = VideoFps.Fps30;
    public VideoBitrateMode RecBitrate { get; set; } = VideoBitrateMode.High;
    public int RecCustomBitrateMbps { get; set; } = 15;
    public VideoLength RecLength { get; set; } = VideoLength.Unlimited;
    public RecordAudioSource RecAudio { get; set; } = RecordAudioSource.System;
    public RecordChannels RecChannels { get; set; } = RecordChannels.Stereo;
    public bool RecShowCursor { get; set; } = true;
    /// <summary>Show the floating timer / pause / stop card while recording.</summary>
    public bool RecShowTimer { get; set; } = true;
    /// <summary>Opacity of the floating timer card, in percent.</summary>
    public int RecTimerOpacity { get; set; } = 100;

    // GIF 动画
    public GifFps GifFrameRate { get; set; } = GifFps.Fps15;
    public GifSizing GifSize { get; set; } = GifSizing.Original;
    public GifLength GifLengthMode { get; set; } = GifLength.S30;
    public bool GifLoop { get; set; } = true;
    public bool GifShowCursor { get; set; } = true;
    public bool GifShowTimer { get; set; } = true;
    public int GifTimerOpacity { get; set; } = 100;

    // 截屏工具栏入口
    public bool ShowToolRec { get; set; } = true;
    public bool ShowToolGif { get; set; } = true;

    public int RecFpsValue => RecFps switch
    {
        VideoFps.Fps24 => 24,
        VideoFps.Fps25 => 25,
        VideoFps.Fps60 => 60,
        _ => 30
    };

    public int RecBitrateKbps => RecBitrate switch
    {
        VideoBitrateMode.Low => 2000,
        VideoBitrateMode.Medium => 5000,
        VideoBitrateMode.Ultra => 20000,
        VideoBitrateMode.Ultra4K => 40000,
        VideoBitrateMode.Custom => Math.Clamp(RecCustomBitrateMbps, 1, 200) * 1000,
        _ => 10000
    };

    /// <summary>0 = record until the user stops.</summary>
    public double RecDurationSeconds => RecLength switch
    {
        VideoLength.S30 => 30,
        VideoLength.M1 => 60,
        VideoLength.M3 => 180,
        VideoLength.M5 => 300,
        VideoLength.M10 => 600,
        VideoLength.M30 => 1800,
        _ => 0
    };

    public string RecFormatLabel => RecFormat switch
    {
        VideoContainer.Mov => "MOV",
        VideoContainer.Mkv => "MKV",
        VideoContainer.Webm => "WEBM",
        VideoContainer.Avi => "AVI",
        _ => "MP4"
    };

    public string RecExtension => RecFormat switch
    {
        VideoContainer.Mov => ".mov",
        VideoContainer.Mkv => ".mp4",   // 无 MKV 封装器：回退到 MP4 容器
        VideoContainer.Webm => ".mp4",  // 无 WEBM 封装器：回退到 MP4 容器
        VideoContainer.Avi => ".avi",
        _ => ".mp4"
    };

    public int GifFpsValue => GifFrameRate switch
    {
        GifFps.Fps10 => 10,
        GifFps.Fps24 => 24,
        _ => 15
    };

    public float GifScale => GifSize switch
    {
        GifSizing.Balanced => 0.75f,
        GifSizing.Light => 0.5f,
        _ => 1f
    };

    public double GifDurationSeconds => GifLengthMode switch
    {
        GifLength.S5 => 5,
        GifLength.S15 => 15,
        GifLength.S60 => 60,
        GifLength.Unlimited => 0,
        _ => 30
    };

    private void NormalizeVideo()
    {
        if (!Enum.IsDefined(RecFormat)) RecFormat = VideoContainer.Mp4;
        if (!Enum.IsDefined(RecResolution)) RecResolution = VideoSize.Native;
        if (LastRecordRegion.Width < 0 || LastRecordRegion.Height < 0) LastRecordRegion = Rectangle.Empty;
        if (!Enum.IsDefined(RecFps)) RecFps = VideoFps.Fps30;
        if (!Enum.IsDefined(RecBitrate)) RecBitrate = VideoBitrateMode.High;
        if (!Enum.IsDefined(RecLength)) RecLength = VideoLength.Unlimited;
        if (!Enum.IsDefined(RecAudio)) RecAudio = RecordAudioSource.System;
        if (!Enum.IsDefined(RecChannels)) RecChannels = RecordChannels.Stereo;
        if (!Enum.IsDefined(GifFrameRate)) GifFrameRate = GifFps.Fps15;
        if (!Enum.IsDefined(GifSize)) GifSize = GifSizing.Original;
        if (!Enum.IsDefined(GifLengthMode)) GifLengthMode = GifLength.S30;
        RecCustomBitrateMbps = Math.Clamp(RecCustomBitrateMbps, 1, 200);
        RecTimerOpacity = Math.Clamp(RecTimerOpacity, 20, 100);
        GifTimerOpacity = Math.Clamp(GifTimerOpacity, 20, 100);
    }

    /// <summary>Fixed recording frame size, or the selection size for "跟随选区".</summary>
    public Size RecTargetSize(Size selection)
    {
        var size = RecResolution switch
        {
            VideoSize.P720 => new Size(1280, 720),
            VideoSize.P1080 => new Size(1920, 1080),
            VideoSize.Qhd => new Size(2560, 1440),
            VideoSize.Uhd => new Size(3840, 2160),
            VideoSize.Portrait720 => new Size(720, 1280),
            VideoSize.Portrait1080 => new Size(1080, 1920),
            VideoSize.Portrait2K => new Size(1440, 2560),
            VideoSize.Portrait4K => new Size(2160, 3840),
            _ => Size.Empty
        };
        if (size.IsEmpty) return new Size(Math.Max(2, selection.Width / 2 * 2), Math.Max(2, selection.Height / 2 * 2));
        return size;
    }
}
