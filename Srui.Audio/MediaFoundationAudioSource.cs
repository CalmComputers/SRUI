using System.Runtime.InteropServices;

namespace Srui.Audio;

/// <summary>
/// AAC / M4A / M4B / ALAC decoding through Windows Media Foundation's
/// source reader, surfaced as a <see cref="PullAudioSource"/> — the
/// decode path for formats cosmos has no native backend for. Streaming:
/// samples are pulled and converted to interleaved f32 as the engine
/// asks, never decoded whole. Interop is raw vtable calls (slot orders
/// and GUIDs transcribed from mfreadwrite.h / mfapi.h / mfidl.h), so
/// Native AOT needs no COM runtime support.
/// </summary>
public sealed unsafe class MediaFoundationAudioSource : PullAudioSource
{
    private readonly object _lock = new();
    private IntPtr _reader;
    private float[] _pending = Array.Empty<float>();
    private int _pendingLength;
    private int _pendingOffset;
    private bool _endOfStream;

    /// <summary>Total duration read from the presentation descriptor.</summary>
    public ulong DurationMs { get; }

    private MediaFoundationAudioSource(
        IntPtr reader, uint channels, uint sampleRate, ulong lengthFrames, ulong durationMs)
        : base(channels, sampleRate, lengthFrames)
    {
        _reader = reader;
        DurationMs = durationMs;
    }

    /// <summary>Open a file for decoding. Throws AudioException when
    /// Media Foundation cannot read it.</summary>
    public static MediaFoundationAudioSource Open(string path)
    {
        EnsureStartup();
        Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader),
            $"Media Foundation could not open '{path}'");
        try
        {
            // Audio stream only, decoded to float PCM.
            Check(SetStreamSelection(reader, MF_SOURCE_READER_ALL_STREAMS, 0), "stream deselect");
            Check(SetStreamSelection(reader, MF_SOURCE_READER_FIRST_AUDIO_STREAM, 1), "stream select");

            Check(MFCreateMediaType(out var wanted), "media type");
            var majorType = MF_MT_MAJOR_TYPE;
            var audio = MFMediaType_Audio;
            var subtype = MF_MT_SUBTYPE;
            var floatFormat = MFAudioFormat_Float;
            Check(SetGuid(wanted, &majorType, &audio), "major type");
            Check(SetGuid(wanted, &subtype, &floatFormat), "subtype");
            var hr = SetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, wanted);
            Release(wanted);
            Check(hr, $"'{path}' has no decodable audio stream");

            // The reader completes a partial output type with
            // placeholder rate/channels (44100/2 observed for a
            // 22050/1 AAC stream) and only settles on the decoder's
            // real output format once the first sample is decoded,
            // flagging CURRENTMEDIATYPECHANGED. Advertising the
            // placeholder would have the engine consume the stream at
            // the wrong rate — decode one sample so the format
            // settles, then rewind before reading the settled type.
            for (var i = 0; i < 16; i++)
            {
                if (ReadSample(reader, MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                        out _, out var primeFlags, out _, out var primed) != 0)
                    break;
                var got = primed != IntPtr.Zero;
                if (got)
                    Release(primed);
                if (got || (primeFlags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    break;
            }
            var rewind = new PropVariant { Vt = VT_I8, Value = 0 };
            var guidNull = default(Guid);
            SetCurrentPosition(reader, &guidNull, &rewind);

            Check(GetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var actual),
                "negotiated type");
            var channelsKey = MF_MT_AUDIO_NUM_CHANNELS;
            var rateKey = MF_MT_AUDIO_SAMPLES_PER_SECOND;
            var hrChannels = GetUInt32(actual, &channelsKey, out var channels);
            var hrRate = GetUInt32(actual, &rateKey, out var sampleRate);
            Release(actual);
            Check(hrChannels, "channel count");
            Check(hrRate, "sample rate");

            // Duration (100ns units) from the presentation descriptor;
            // absent for some streams, in which case length is unknown.
            ulong durationMs = 0;
            ulong lengthFrames = 0;
            var durationKey = MF_PD_DURATION;
            PropVariant duration = default;
            if (GetPresentationAttribute(reader, MF_SOURCE_READER_MEDIASOURCE,
                    &durationKey, &duration) == 0 && duration.Vt == VT_UI8)
            {
                var hns = (ulong)duration.Value;
                durationMs = hns / 10_000;
                lengthFrames = hns * sampleRate / 10_000_000;
            }
            PropVariantClear(&duration);

            return new MediaFoundationAudioSource(reader, channels, sampleRate, lengthFrames, durationMs);
        }
        catch
        {
            Release(reader);
            throw;
        }
    }

    /// <summary>Duration-only probe: open, read the attribute, close.
    /// 0 when the file is unreadable — the managed complement of
    /// <see cref="Sound.ProbeDurationMs"/>.</summary>
    public static ulong ProbeDurationMs(string path)
    {
        try
        {
            using var source = Open(path);
            return source.DurationMs;
        }
        catch (AudioException)
        {
            return 0;
        }
    }

    public override ulong Read(Span<float> buffer, ulong frameCount)
    {
        lock (_lock)
        {
            if (_reader == IntPtr.Zero)
                return 0;
            var channels = (int)Channels;
            var wanted = checked((int)frameCount) * channels;
            var written = 0;

            while (written < wanted)
            {
                // Drain the leftover from the previous MF sample first.
                var pending = _pendingLength - _pendingOffset;
                if (pending > 0)
                {
                    var take = Math.Min(pending, wanted - written);
                    _pending.AsSpan(_pendingOffset, take).CopyTo(buffer[written..]);
                    _pendingOffset += take;
                    written += take;
                    continue;
                }
                if (_endOfStream || !PullNextSample())
                    break;
            }
            return (ulong)(written / channels);
        }
    }

    private bool PullNextSample()
    {
        while (true)
        {
            if (ReadSample(_reader, MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                    out _, out var flags, out _, out var sample) != 0)
            {
                _endOfStream = true;
                return false;
            }
            if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                if (sample != IntPtr.Zero)
                    Release(sample);
                _endOfStream = true;
                return false;
            }
            if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0
                && !FormatStillMatches())
            {
                // The decoder renegotiated to a different rate or
                // channel count mid-stream. The engine's view of this
                // source is fixed at load, so the remainder cannot be
                // rendered correctly — stop instead of playing it at
                // the wrong speed.
                if (sample != IntPtr.Zero)
                    Release(sample);
                _endOfStream = true;
                return false;
            }
            if (sample == IntPtr.Zero)
                continue; // a gap or stream tick; ask again

            var hr = ConvertToContiguousBuffer(sample, out var mediaBuffer);
            if (hr != 0)
            {
                Release(sample);
                _endOfStream = true;
                return false;
            }
            hr = Lock(mediaBuffer, out var data, IntPtr.Zero, out var byteCount);
            if (hr == 0)
            {
                var floats = (int)(byteCount / sizeof(float));
                if (_pending.Length < floats)
                    _pending = new float[floats]; // grows to the largest MF sample, then reused
                new ReadOnlySpan<float>((void*)data, floats)
                    .CopyTo(_pending.AsSpan(0, floats));
                _pendingLength = floats;
                _pendingOffset = 0;
                Unlock(mediaBuffer);
            }
            Release(mediaBuffer);
            Release(sample);
            if (hr == 0)
                return true;
            _endOfStream = true;
            return false;
        }
    }

    private bool FormatStillMatches()
    {
        if (GetCurrentMediaType(_reader, MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var type) != 0)
            return false;
        var channelsKey = MF_MT_AUDIO_NUM_CHANNELS;
        var rateKey = MF_MT_AUDIO_SAMPLES_PER_SECOND;
        var hrChannels = GetUInt32(type, &channelsKey, out var channels);
        var hrRate = GetUInt32(type, &rateKey, out var sampleRate);
        Release(type);
        return hrChannels == 0 && hrRate == 0
            && channels == Channels && sampleRate == SampleRate;
    }

    public override bool Seek(ulong frameIndex)
    {
        lock (_lock)
        {
            if (_reader == IntPtr.Zero)
                return false;
            _pendingLength = 0;
            _pendingOffset = 0;
            _endOfStream = false;
            var position = new PropVariant
            {
                Vt = VT_I8,
                Value = (long)(frameIndex * 10_000_000 / SampleRate),
            };
            var guidNull = default(Guid);
            return SetCurrentPosition(_reader, &guidNull, &position) == 0;
        }
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            if (_reader != IntPtr.Zero)
            {
                Release(_reader);
                _reader = IntPtr.Zero;
            }
        }
        base.Dispose();
    }

    // ── Media Foundation lifetime ──

    private static readonly object StartupLock = new();
    private static bool _started;

    private static void EnsureStartup()
    {
        lock (StartupLock)
        {
            if (_started)
                return;
            Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "Media Foundation startup");
            _started = true; // left running for the process lifetime
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0)
            throw new AudioException($"{what} failed (0x{hr:X8})");
    }

    // ── Constants (mfapi.h / mfidl.h / mfreadwrite.h / mfobjects.h) ──

    private const uint MF_VERSION = 0x0002_0070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private const uint MFSTARTUP_LITE = 0x1;
    private const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    private const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x20;
    private const ushort VT_I8 = 20;
    private const ushort VT_UI8 = 21;

    private static readonly Guid MF_MT_MAJOR_TYPE =
        new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
    private static readonly Guid MF_MT_SUBTYPE =
        new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS =
        new(0x37e48bf5, 0x645e, 0x4c5b, 0x89, 0xde, 0xad, 0xa9, 0xe2, 0x9b, 0x69, 0x6a);
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND =
        new(0x5faeeae7, 0x0290, 0x4c31, 0x9e, 0x8a, 0xc5, 0x34, 0xf6, 0x8d, 0x9d, 0xba);
    private static readonly Guid MF_PD_DURATION =
        new(0x6c990d33, 0xbb8e, 0x477a, 0x85, 0x98, 0x0d, 0x5d, 0x96, 0xfc, 0xd8, 0x8a);
    private static readonly Guid MFMediaType_Audio =
        new(0x73647561, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    private static readonly Guid MFAudioFormat_Float =
        new(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public long Value;
        public IntPtr Pad;
    }

    // ── Flat imports ──

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        string url, IntPtr attributes, out IntPtr reader);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(PropVariant* pv);

    // ── Raw vtable calls (slot orders from the SDK headers) ──

    private static void** Vtbl(IntPtr obj) => *(void***)obj;

    private static void Release(IntPtr obj) =>
        ((delegate* unmanaged<IntPtr, uint>)Vtbl(obj)[2])(obj);

    // IMFSourceReader (mfreadwrite.h): slots 3.. = GetStreamSelection,
    // SetStreamSelection, GetNativeMediaType, GetCurrentMediaType,
    // SetCurrentMediaType, SetCurrentPosition, ReadSample, Flush,
    // GetServiceForStream, GetPresentationAttribute.

    private static int SetStreamSelection(IntPtr reader, uint stream, int selected) =>
        ((delegate* unmanaged<IntPtr, uint, int, int>)Vtbl(reader)[4])(reader, stream, selected);

    private static int GetCurrentMediaType(IntPtr reader, uint stream, out IntPtr type)
    {
        fixed (IntPtr* p = &type)
            return ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Vtbl(reader)[6])(reader, stream, p);
    }

    private static int SetCurrentMediaType(IntPtr reader, uint stream, IntPtr reserved, IntPtr type) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, int>)Vtbl(reader)[7])(reader, stream, reserved, type);

    private static int SetCurrentPosition(IntPtr reader, Guid* format, PropVariant* position) =>
        ((delegate* unmanaged<IntPtr, Guid*, PropVariant*, int>)Vtbl(reader)[8])(reader, format, position);

    private static int ReadSample(
        IntPtr reader, uint stream, uint controlFlags,
        out uint actualStream, out uint sampleFlags, out long timestamp, out IntPtr sample)
    {
        fixed (uint* pStream = &actualStream)
        fixed (uint* pFlags = &sampleFlags)
        fixed (long* pTime = &timestamp)
        fixed (IntPtr* pSample = &sample)
            return ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)
                Vtbl(reader)[9])(reader, stream, controlFlags, pStream, pFlags, pTime, pSample);
    }

    private static int GetPresentationAttribute(
        IntPtr reader, uint stream, Guid* attribute, PropVariant* value) =>
        ((delegate* unmanaged<IntPtr, uint, Guid*, PropVariant*, int>)Vtbl(reader)[12])(
            reader, stream, attribute, value);

    // IMFAttributes (mfobjects.h): GetUINT32 is slot 7, SetGUID slot 24.

    private static int GetUInt32(IntPtr attributes, Guid* key, out uint value)
    {
        fixed (uint* p = &value)
            return ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Vtbl(attributes)[7])(attributes, key, p);
    }

    private static int SetGuid(IntPtr attributes, Guid* key, Guid* value) =>
        ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Vtbl(attributes)[24])(attributes, key, value);

    // IMFSample (mfobjects.h): ConvertToContiguousBuffer is slot 41.

    private static int ConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        fixed (IntPtr* p = &buffer)
            return ((delegate* unmanaged<IntPtr, IntPtr*, int>)Vtbl(sample)[41])(sample, p);
    }

    // IMFMediaBuffer (mfobjects.h): Lock 3, Unlock 4.

    private static int Lock(IntPtr buffer, out IntPtr data, IntPtr maxLength, out uint currentLength)
    {
        fixed (IntPtr* pData = &data)
        fixed (uint* pLength = &currentLength)
            return ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr, uint*, int>)Vtbl(buffer)[3])(
                buffer, pData, maxLength, pLength);
    }

    private static int Unlock(IntPtr buffer) =>
        ((delegate* unmanaged<IntPtr, int>)Vtbl(buffer)[4])(buffer);
}
