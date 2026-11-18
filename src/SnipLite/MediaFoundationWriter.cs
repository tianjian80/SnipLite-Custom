using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// H.264 / MP4 writer on top of the Media Foundation sink writer (mfreadwrite.dll).
/// Frames are handed over as top-down RGB32 samples and encoded by the in-box
/// H.264 encoder, so no external codec or ffmpeg build is required.
/// </summary>
internal sealed class MediaFoundationWriter : IFrameWriter
{
    private const int MfVersion = 0x00020070;
    private const int MfStartupLite = 1;

    private static readonly Guid MfSinkWriterDisableThrottling = new("08B845D8-2B74-4AFE-9D53-BE16D2D5AE4F");
    private static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MfMtAvgBitrate = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    private static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MfMtFrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    private static readonly Guid MfMtInterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    private static readonly Guid MfMtDefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    private static readonly Guid MfMtAllSamplesIndependent = new("C9173739-5E56-461C-B713-46FB995CB95F");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    private static readonly Guid MfMtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    private static readonly Guid MfMtAudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    private static readonly Guid MfMtAudioBlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    private static readonly Guid MfMtAudioAvgBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _audioRate;
    private readonly int _audioChannels;
    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private int _audioIndex = -1;
    private byte[]? _bytes;
    private bool _closed;
    private readonly object _gate = new(); // video and audio arrive on different threads

    public bool AudioEnabled => _audioIndex >= 0;
    public string? AudioNote { get; private set; }

    public MediaFoundationWriter(string path, int width, int height, int fps, int bitrate,
        int audioRate = 0, int audioChannels = 0)
    {
        _audioRate = audioRate;
        _audioChannels = audioChannels;
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        Check(MFStartup(MfVersion, MfStartupLite));

        MFCreateAttributes(out var attributes, 1);
        try
        {
            attributes.SetUINT32(MfSinkWriterDisableThrottling, 1);
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out _writer));
        }
        finally
        {
            Release(attributes);
        }

        MFCreateMediaType(out var output);
        try
        {
            output.SetGUID(MfMtMajorType, MfMediaTypeVideo);
            output.SetGUID(MfMtSubtype, MfVideoFormatH264);
            output.SetUINT32(MfMtAvgBitrate, (uint)Math.Max(500, bitrate));
            output.SetUINT32(MfMtInterlaceMode, 2); // progressive
            output.SetUINT64(MfMtFrameSize, Pack(width, height));
            output.SetUINT64(MfMtFrameRate, Pack(_fps, 1));
            Check(_writer!.AddStream(output, out _streamIndex));
        }
        finally
        {
            Release(output);
        }

        MFCreateMediaType(out var input);
        try
        {
            input.SetGUID(MfMtMajorType, MfMediaTypeVideo);
            input.SetGUID(MfMtSubtype, MfVideoFormatRgb32);
            input.SetUINT32(MfMtInterlaceMode, 2);
            input.SetUINT64(MfMtFrameSize, Pack(width, height));
            input.SetUINT64(MfMtFrameRate, Pack(_fps, 1));
            input.SetUINT32(MfMtAllSamplesIndependent, 1);
            input.SetUINT32(MfMtDefaultStride, unchecked((uint)(-width * 4))); // bottom-up rows
            Check(_writer!.SetInputMediaType(_streamIndex, input, null));
        }
        finally
        {
            Release(input);
        }

        if (_audioRate > 0 && _audioChannels > 0) AddAudioStream();
        Check(_writer!.BeginWriting());
    }

    /// <summary>
    /// Adds an AAC audio stream fed with 16-bit PCM. Failure is not fatal: the video is
    /// still recorded, and <see cref="AudioNote"/> tells the caller why there is no sound.
    /// </summary>
    private void AddAudioStream()
    {
        try
        {
            MFCreateMediaType(out var output);
            try
            {
                output.SetGUID(MfMtMajorType, MfMediaTypeAudio);
                output.SetGUID(MfMtSubtype, MfAudioFormatAac);
                output.SetUINT32(MfMtAudioBitsPerSample, 16);
                output.SetUINT32(MfMtAudioSamplesPerSecond, (uint)_audioRate);
                output.SetUINT32(MfMtAudioNumChannels, (uint)_audioChannels);
                output.SetUINT32(MfMtAudioAvgBytesPerSecond, 24000); // ~192 kbps
                Check(_writer!.AddStream(output, out _audioIndex));
            }
            finally
            {
                Release(output);
            }

            MFCreateMediaType(out var input);
            try
            {
                input.SetGUID(MfMtMajorType, MfMediaTypeAudio);
                input.SetGUID(MfMtSubtype, MfAudioFormatPcm);
                input.SetUINT32(MfMtAudioBitsPerSample, 16);
                input.SetUINT32(MfMtAudioSamplesPerSecond, (uint)_audioRate);
                input.SetUINT32(MfMtAudioNumChannels, (uint)_audioChannels);
                input.SetUINT32(MfMtAudioBlockAlignment, (uint)(_audioChannels * 2));
                input.SetUINT32(MfMtAudioAvgBytesPerSecond, (uint)(_audioRate * _audioChannels * 2));
                Check(_writer!.SetInputMediaType(_audioIndex, input, null));
            }
            finally
            {
                Release(input);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("音频编码流不可用", ex);
            _audioIndex = -1;
            AudioNote = UiText.T("音频编码不可用，视频已无声保存。");
        }
    }

    /// <summary>Writes one PCM packet; <paramref name="timeHns"/> is in 100 ns units.</summary>
    public void WriteAudio(byte[] pcm, int bytes, long timeHns)
    {
        if (_closed || _writer is null || _audioIndex < 0 || bytes <= 0) return;

        MFCreateMemoryBuffer((uint)bytes, out var buffer);
        IMFSample? sample = null;
        try
        {
            Check(buffer.Lock(out var destination, out _, out _));
            Marshal.Copy(pcm, 0, destination, bytes);
            buffer.Unlock();
            buffer.SetCurrentLength(bytes);
            MFCreateSample(out sample);
            sample.AddBuffer(buffer);
            var frames = bytes / (2 * _audioChannels);
            sample.SetSampleTime(timeHns);
            sample.SetSampleDuration(frames * 10_000_000L / _audioRate);
            lock (_gate) Check(_writer.WriteSample(_audioIndex, sample));
        }
        finally
        {
            Release(sample);
            Release(buffer);
        }
    }

    public void WriteFrame(Bitmap frame, double seconds)
    {
        if (_closed || _writer is null) return;
        var stride = _width * 4;
        var size = stride * _height;

        MFCreateMemoryBuffer((uint)size, out var buffer);
        IMFSample? sample = null;
        try
        {
            Check(buffer.Lock(out var destination, out _, out _));
            _bytes ??= new byte[size];
            var data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                // GDI+ bitmaps are top-down while the RGB32 media buffer is consumed
                // bottom-up, so copy the rows in reverse: handing them over in screen
                // order produced upside-down video.
                for (var y = 0; y < _height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride),
                        _bytes, (_height - 1 - y) * stride, stride);
            }
            finally
            {
                frame.UnlockBits(data);
            }

            Marshal.Copy(_bytes, 0, destination, size);
            buffer.Unlock();
            buffer.SetCurrentLength(size);
            MFCreateSample(out sample);
            sample.AddBuffer(buffer);
            sample.SetSampleTime((long)Math.Round(seconds * 10_000_000));
            sample.SetSampleDuration(10_000_000L / _fps);
            lock (_gate) Check(_writer.WriteSample(_streamIndex, sample));
        }
        finally
        {
            Release(sample);
            Release(buffer);
        }
    }

    public void Complete()
    {
        if (_closed) return;
        _closed = true;
        try { lock (_gate) _writer?.FinishWriting(); }
        finally
        {
            Release(_writer);
            _writer = null;
            MFShutdown();
        }
    }

    public void Dispose() => Complete();

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // ------------------------------------------------------------------ interop

    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IMFAttributes attributes, int size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType mediaType);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream,
        IMFAttributes? attributes, out IMFSinkWriter writer);

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem([In] ref Guid key, out IntPtr value);
        [PreserveSig] int GetItemType([In] ref Guid key, out int type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes? other, int matchType, out bool result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, IntPtr value, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr value, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, byte[] value, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        [PreserveSig] int GetItem([In] ref Guid key, out IntPtr value);
        [PreserveSig] int GetItemType([In] ref Guid key, out int type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes? other, int matchType, out bool result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, IntPtr value, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr value, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, byte[] value, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);

        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType? other, out uint flags);
        [PreserveSig] int GetRepresentation([In] ref Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation([In] ref Guid representation, IntPtr value);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig] int GetItem([In] ref Guid key, out IntPtr value);
        [PreserveSig] int GetItemType([In] ref Guid key, out int type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes? other, int matchType, out bool result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, IntPtr value, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr value, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, byte[] value, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);

        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetMediaType, out int streamIndex);
        [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IMFAttributes? encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
        [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
        [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
        [PreserveSig] int NotifyEndOfSegment(int streamIndex);
        [PreserveSig] int Flush(int streamIndex);
        [PreserveSig] int FinishWriting();
        [PreserveSig] int GetServiceForStream(int streamIndex, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] int GetStatistics(int streamIndex, IntPtr statistics);
    }
}
