using System.Buffers.Binary;
using System.Text;

namespace Srui.Audio;

/// <summary>A chapter marker read from an MP4 container.</summary>
public sealed record M4bChapter(string Title, ulong StartMs, ulong DurationMs);

/// <summary>Chapters and total duration from an MP4/M4B file.</summary>
public sealed record M4bMetadata(IReadOnlyList<M4bChapter> Chapters, ulong TotalDurationMs);

/// <summary>
/// M4B chapter extraction — pure header parsing, no audio data touched.
/// Two chapter forms: Nero (moov/udta/chpl, a simple binary table) and
/// QuickTime (a text track whose samples are the chapter titles), tried
/// in that order. Total duration comes from moov/mvhd. Nero durations
/// derive from adjacent start times; the last chapter's stays zero for
/// the caller to fill from the total. Throws IOException on unreadable
/// or malformed headers — user-facing callers should say why a file's
/// chapters were lost rather than silently ignoring it.
/// </summary>
public static class M4bChapters
{
    public static M4bMetadata ExtractMetadata(string path)
    {
        using var file = File.OpenRead(path);
        var fileSize = (ulong)file.Length;

        if (FindBox(file, 0, fileSize, "moov") is not { } moov)
            return new M4bMetadata(Array.Empty<M4bChapter>(), 0);

        var totalDurationMs = ReadMvhdDuration(file, moov) ?? 0;

        if (TryNeroChapters(file, moov) is { Count: > 0 } nero)
            return new M4bMetadata(nero, totalDurationMs);

        if (TryQtChapters(file, moov) is { Count: > 0 } qt)
            return new M4bMetadata(qt, totalDurationMs);

        return new M4bMetadata(Array.Empty<M4bChapter>(), totalDurationMs);
    }

    // ── Movie header duration (moov/mvhd) ──

    private static ulong? ReadMvhdDuration(FileStream file, BoxSpan moov)
    {
        if (FindBox(file, moov.DataStart, moov.DataSize, "mvhd") is not { } mvhd)
            return null;
        file.Position = (long)mvhd.DataStart;
        var version = ReadByte(file);
        if (version == 0)
        {
            // flags(3) + creation(4) + modification(4) + timescale(4) + duration(4)
            var buf = ReadExactly(file, 19);
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(11, 4));
            var duration = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(15, 4));
            return timescale > 0 ? duration * 1000UL / timescale : null;
        }
        else
        {
            // flags(3) + creation(8) + modification(8) + timescale(4) + duration(8)
            var buf = ReadExactly(file, 31);
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(19, 4));
            var duration = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(23, 8));
            return timescale > 0 ? duration * 1000UL / timescale : null;
        }
    }

    // ── Nero chapters (moov/udta/chpl) ──

    private static List<M4bChapter>? TryNeroChapters(FileStream file, BoxSpan moov)
    {
        if (FindBox(file, moov.DataStart, moov.DataSize, "udta") is not { } udta)
            return null;
        if (FindBox(file, udta.DataStart, udta.DataSize, "chpl") is not { } chpl)
            return null;

        file.Position = (long)chpl.DataStart;
        var version = ReadByte(file);
        _ = ReadExactly(file, 3); // flags
        _ = ReadExactly(file, version >= 1 ? 8 : 4); // reserved
        var count = (int)ReadU32(file);

        var starts = new List<(string Title, ulong StartMs)>(count);
        for (var i = 0; i < count; i++)
        {
            var timestamp100Ns = ReadU64(file);
            var titleLength = ReadByte(file);
            var title = Encoding.UTF8.GetString(ReadExactly(file, titleLength));
            starts.Add((title, timestamp100Ns / 10_000));
        }

        // Durations from adjacent start times; the last stays 0.
        var chapters = new List<M4bChapter>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var duration = i + 1 < starts.Count ? starts[i + 1].StartMs - starts[i].StartMs : 0;
            chapters.Add(new M4bChapter(starts[i].Title, starts[i].StartMs, duration));
        }
        return chapters;
    }

    // ── QuickTime chapters (text track) ──

    private static List<M4bChapter>? TryQtChapters(FileStream file, BoxSpan moov)
    {
        foreach (var trak in FindAllBoxes(file, moov.DataStart, moov.DataSize, "trak"))
        {
            if (FindBox(file, trak.DataStart, trak.DataSize, "mdia") is not { } mdia)
                continue;

            if (FindBox(file, mdia.DataStart, mdia.DataSize, "hdlr") is not { } hdlr)
                continue;
            file.Position = (long)hdlr.DataStart;
            // version(1) + flags(3) + pre_defined(4) + handler_type(4)
            var hdlrBuf = ReadExactly(file, 12);
            if (hdlrBuf[8] != 't' || hdlrBuf[9] != 'e' || hdlrBuf[10] != 'x' || hdlrBuf[11] != 't')
                continue;

            if (FindBox(file, mdia.DataStart, mdia.DataSize, "mdhd") is not { } mdhd)
                continue;
            var timescale = ReadMdhdTimescale(file, mdhd);
            if (timescale == 0)
                continue;

            if (FindBox(file, mdia.DataStart, mdia.DataSize, "minf") is not { } minf)
                continue;
            if (FindBox(file, minf.DataStart, minf.DataSize, "stbl") is not { } stbl)
                continue;

            var durations = ReadStts(file, stbl);
            if (durations.Count == 0)
                continue;
            var sizes = ReadStsz(file, stbl);
            var offsets = ReadStco(file, stbl);

            var sampleCount = Math.Min(sizes.Count, Math.Min(durations.Count, offsets.Count));
            if (sampleCount == 0)
                continue;

            var chapters = new List<M4bChapter>(sampleCount);
            ulong timeAcc = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var title = ReadTextSample(file, offsets[i], sizes[i]);
                chapters.Add(new M4bChapter(
                    title,
                    timeAcc * 1000UL / timescale,
                    durations[i] * 1000UL / timescale));
                timeAcc += durations[i];
            }
            if (chapters.Count > 0)
                return chapters;
        }
        return null;
    }

    private static ulong ReadMdhdTimescale(FileStream file, BoxSpan mdhd)
    {
        file.Position = (long)mdhd.DataStart;
        var version = ReadByte(file);
        if (version == 0)
        {
            var buf = ReadExactly(file, 15);
            return BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(11, 4));
        }
        else
        {
            var buf = ReadExactly(file, 23);
            return BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(19, 4));
        }
    }

    /// <summary>stts: one duration per sample, run-length expanded.</summary>
    private static List<uint> ReadStts(FileStream file, BoxSpan stbl)
    {
        var durations = new List<uint>();
        if (FindBox(file, stbl.DataStart, stbl.DataSize, "stts") is not { } stts)
            return durations;
        file.Position = (long)stts.DataStart;
        _ = ReadExactly(file, 4); // full-box header
        var entryCount = (int)ReadU32(file);
        for (var i = 0; i < entryCount; i++)
        {
            var sampleCount = ReadU32(file);
            var sampleDelta = ReadU32(file);
            for (var j = 0u; j < sampleCount; j++)
                durations.Add(sampleDelta);
        }
        return durations;
    }

    private static List<uint> ReadStsz(FileStream file, BoxSpan stbl)
    {
        var sizes = new List<uint>();
        if (FindBox(file, stbl.DataStart, stbl.DataSize, "stsz") is not { } stsz)
            return sizes;
        file.Position = (long)stsz.DataStart;
        _ = ReadExactly(file, 4);
        var defaultSize = ReadU32(file);
        var sampleCount = (int)ReadU32(file);
        if (defaultSize != 0)
        {
            for (var i = 0; i < sampleCount; i++)
                sizes.Add(defaultSize);
            return sizes;
        }
        for (var i = 0; i < sampleCount; i++)
            sizes.Add(ReadU32(file));
        return sizes;
    }

    /// <summary>Chunk offsets from stco (32-bit) or co64 (64-bit).</summary>
    private static List<ulong> ReadStco(FileStream file, BoxSpan stbl)
    {
        var offsets = new List<ulong>();
        if (FindBox(file, stbl.DataStart, stbl.DataSize, "stco") is { } stco)
        {
            file.Position = (long)stco.DataStart;
            _ = ReadExactly(file, 4);
            var count = (int)ReadU32(file);
            for (var i = 0; i < count; i++)
                offsets.Add(ReadU32(file));
            return offsets;
        }
        if (FindBox(file, stbl.DataStart, stbl.DataSize, "co64") is { } co64)
        {
            file.Position = (long)co64.DataStart;
            _ = ReadExactly(file, 4);
            var count = (int)ReadU32(file);
            for (var i = 0; i < count; i++)
                offsets.Add(ReadU64(file));
        }
        return offsets;
    }

    /// <summary>A text sample: 2-byte big-endian length prefix, then
    /// UTF-8 ('tx3g'/'text' convention).</summary>
    private static string ReadTextSample(FileStream file, ulong offset, uint size)
    {
        if (size < 2)
            return "";
        file.Position = (long)offset;
        var textLength = BinaryPrimitives.ReadUInt16BigEndian(ReadExactly(file, 2));
        var actual = Math.Min(textLength, (int)size - 2);
        return Encoding.UTF8.GetString(ReadExactly(file, actual));
    }

    // ── MP4 box navigation ──

    private readonly record struct BoxSpan(ulong DataStart, ulong DataSize);

    private static BoxSpan? FindBox(FileStream file, ulong start, ulong size, string boxType)
    {
        foreach (var (span, tag) in WalkBoxes(file, start, size))
            if (tag == boxType)
                return span;
        return null;
    }

    private static List<BoxSpan> FindAllBoxes(FileStream file, ulong start, ulong size, string boxType)
    {
        var result = new List<BoxSpan>();
        foreach (var (span, tag) in WalkBoxes(file, start, size))
            if (tag == boxType)
                result.Add(span);
        return result;
    }

    private static IEnumerable<(BoxSpan Span, string Tag)> WalkBoxes(
        FileStream file, ulong start, ulong size)
    {
        var end = start + size;
        var pos = start;
        while (pos + 8 <= end)
        {
            file.Position = (long)pos;
            var header = ReadExactly(file, 8);
            var rawSize = (ulong)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var tag = Encoding.ASCII.GetString(header, 4, 4);

            ulong dataStart, boxTotal;
            if (rawSize == 1)
            {
                boxTotal = ReadU64(file); // 64-bit extended size
                dataStart = pos + 16;
            }
            else if (rawSize == 0)
            {
                boxTotal = end - pos; // extends to end of container
                dataStart = pos + 8;
            }
            else
            {
                boxTotal = rawSize;
                dataStart = pos + 8;
            }

            if (boxTotal < dataStart - pos)
                yield break; // corrupt
            yield return (new BoxSpan(dataStart, boxTotal - (dataStart - pos)), tag);

            if (boxTotal == 0)
                yield break;
            pos += boxTotal;
        }
    }

    // ── Primitive reads ──

    private static byte ReadByte(FileStream file)
    {
        var value = file.ReadByte();
        if (value < 0)
            throw new EndOfStreamException();
        return (byte)value;
    }

    private static byte[] ReadExactly(FileStream file, int count)
    {
        var buffer = new byte[count];
        file.ReadExactly(buffer);
        return buffer;
    }

    private static uint ReadU32(FileStream file) =>
        BinaryPrimitives.ReadUInt32BigEndian(ReadExactly(file, 4));

    private static ulong ReadU64(FileStream file) =>
        BinaryPrimitives.ReadUInt64BigEndian(ReadExactly(file, 8));
}
