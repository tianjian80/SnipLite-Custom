using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// WASAPI shared-mode capture: the default playback device in loopback mode
/// (system sound) or the default microphone. Packets arrive as 32-bit float
/// (or 16-bit PCM) and are converted to 16-bit PCM for the AAC encoder.
/// </summary>
internal sealed class WasapiCapture : IDisposable
{
    private const int ClsCtxAll = 23;
    private const int LoopbackFlag = 0x00020000;
    private const uint SilentFlag = 0x2;
    private static readonly Guid KsDataTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly bool _loopback;
    private readonly int _targetChannels;
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private IntPtr _format;
    private int _sourceChannels = 2;
    private bool _float;
    private bool _started;
    private bool _disposed;

    public WasapiCapture(bool loopback, int targetChannels)
    {
        _loopback = loopback;
        _targetChannels = Math.Clamp(targetChannels, 1, 2);
    }

    public int SampleRate { get; private set; } = 48000;

    public int Channels => _targetChannels;

    public void Initialize()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        var flow = _loopback ? 0 : 1; // eRender for loopback, eCapture for the mic
        Check(_enumerator.GetDefaultAudioEndpoint(flow, 0, out _device));
        var iid = typeof(IAudioClient).GUID;
        Check(_device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var activated));
        _client = (IAudioClient)activated;
        Check(_client.GetMixFormat(out _format));

        var tag = Marshal.ReadInt16(_format);
        _sourceChannels = Math.Max(1, (int)Marshal.ReadInt16(_format, 2));
        SampleRate = Marshal.ReadInt32(_format, 4);
        var bits = Marshal.ReadInt16(_format, 14);
        _float = tag == 3;
        if ((ushort)tag == 0xFFFE)
            _float = Marshal.PtrToStructure<Guid>(IntPtr.Add(_format, 24)) == KsDataTypeIeeeFloat;
        if (!_float && bits != 16) throw new NotSupportedException(UiText.T("不支持的音频采样格式"));

        Check(_client.Initialize(0, _loopback ? LoopbackFlag : 0, 0, 0, _format, IntPtr.Zero));
        var captureId = typeof(IAudioCaptureClient).GUID;
        Check(_client.GetService(ref captureId, out var service));
        _capture = (IAudioCaptureClient)service;
    }

    public void Start()
    {
        if (_started || _client is null) return;
        Check(_client.Start());
        _started = true;
    }

    public void Stop()
    {
        if (!_started || _client is null) return;
        _started = false;
        try { _client.Stop(); } catch { }
    }

    /// <summary>Converts everything currently queued into interleaved 16-bit PCM.</summary>
    public byte[] Read()
    {
        if (_capture is null) return [];
        using var output = new MemoryStream();
        while (true)
        {
            var hr = _capture.GetNextPacketSize(out var available);
            if (hr < 0 || available == 0) break;
            Check(_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
            if (frames > 0)
            {
                if ((flags & SilentFlag) != 0)
                {
                    output.Write(new byte[frames * _targetChannels * 2]);
                }
                else
                {
                    WritePcm(data, (int)frames, output);
                }
            }
            Check(_capture.ReleaseBuffer(frames));
        }
        return output.ToArray();
    }

    /// <summary>Drops queued packets without converting (used while paused).</summary>
    public void Discard()
    {
        if (_capture is null) return;
        while (true)
        {
            var hr = _capture.GetNextPacketSize(out var available);
            if (hr < 0 || available == 0) break;
            Check(_capture.GetBuffer(out _, out var frames, out _, out _, out _));
            if (frames > 0) Check(_capture.ReleaseBuffer(frames));
        }
    }

    private void WritePcm(IntPtr data, int frames, MemoryStream output)
    {
        var stride = _float ? 4 : 2;
        var raw = new byte[frames * _sourceChannels * stride];
        Marshal.Copy(data, raw, 0, raw.Length);

        var pcm = new byte[frames * _targetChannels * 2];
        var target = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < _targetChannels; channel++)
            {
                // Mono in, stereo out: duplicate. Surround in: take the front pair.
                var source = _sourceChannels == 1 ? 0 : Math.Min(channel, _sourceChannels - 1);
                var index = (frame * _sourceChannels + source) * stride;
                short sample;
                if (_float)
                {
                    var value = BitConverter.ToSingle(raw, index);
                    if (value > 1f) value = 1f;
                    else if (value < -1f) value = -1f;
                    sample = (short)Math.Round(value * 32767f);
                }
                else
                {
                    sample = (short)(raw[index] | (raw[index + 1] << 8));
                }
                pcm[target++] = (byte)(sample & 0xFF);
                pcm[target++] = (byte)((sample >> 8) & 0xFF);
            }
        }
        output.Write(pcm);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_format != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_format);
            _format = IntPtr.Zero;
        }
        Release(_capture); _capture = null;
        Release(_client); _client = null;
        Release(_device); _device = null;
        Release(_enumerator); _enumerator = null;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService([In] ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
