using System.Buffers.Binary;
using System.Text;
using Srui.Audio;
using Xunit;

namespace Srui.Net.Tests;

/// <summary>The MP4 chapter parser against synthesized containers —
/// Nero chpl tables, QuickTime text tracks, and the mvhd duration.</summary>
public class M4bChapterTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "srui-m4b-" + Guid.NewGuid().ToString("N") + ".m4b");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // ── Box builders ──

    private static byte[] Box(string tag, params byte[][] payload)
    {
        var content = payload.SelectMany(p => p).ToArray();
        var result = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(tag).CopyTo(result, 4);
        content.CopyTo(result, 8);
        return result;
    }

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    private static byte[] U64(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        return b;
    }

    private static byte[] U16(ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        return b;
    }

    private static byte[] Bytes(params byte[] raw) => raw;

    /// <summary>mvhd version 0 with the given timescale and duration.</summary>
    private static byte[] Mvhd(uint timescale, uint duration) => Box("mvhd",
        Bytes(0, 0, 0, 0),      // version + flags
        U32(0), U32(0),          // creation, modification
        U32(timescale), U32(duration));

    private static byte[] NeroChpl(params (string Title, ulong StartMs)[] chapters)
    {
        var payload = new List<byte[]>
        {
            Bytes(1, 0, 0, 0),   // version 1 + flags
            U64(0),              // reserved (8 bytes in version 1)
            U32((uint)chapters.Length),
        };
        foreach (var (title, startMs) in chapters)
        {
            payload.Add(U64(startMs * 10_000)); // 100ns units
            var utf8 = Encoding.UTF8.GetBytes(title);
            payload.Add(Bytes((byte)utf8.Length));
            payload.Add(utf8);
        }
        return Box("chpl", payload.ToArray());
    }

    [Fact]
    public void NeroChaptersParseWithDerivedDurations()
    {
        var moov = Box("moov",
            Mvhd(timescale: 1000, duration: 20_000), // 20 s
            Box("udta", NeroChpl(("Intro", 0), ("Middle", 5_000), ("End", 12_000))));
        File.WriteAllBytes(_path, moov);

        var meta = M4bChapters.ExtractMetadata(_path);

        Assert.Equal(20_000UL, meta.TotalDurationMs);
        Assert.Equal(3, meta.Chapters.Count);
        Assert.Equal(new M4bChapter("Intro", 0, 5_000), meta.Chapters[0]);
        Assert.Equal(new M4bChapter("Middle", 5_000, 7_000), meta.Chapters[1]);
        // The last chapter's duration is the caller's to fill.
        Assert.Equal(new M4bChapter("End", 12_000, 0), meta.Chapters[2]);
    }

    [Fact]
    public void QuickTimeTextTrackParses()
    {
        // Two text samples appended after moov; their offsets are
        // computed from the moov size, so build moov twice.
        var title1 = Encoding.UTF8.GetBytes("Chapter One");
        var title2 = Encoding.UTF8.GetBytes("Chapter Two");
        var sample1 = U16((ushort)title1.Length).Concat(title1).ToArray();
        var sample2 = U16((ushort)title2.Length).Concat(title2).ToArray();

        byte[] BuildMoov(uint offset1, uint offset2) => Box("moov",
            Mvhd(timescale: 1000, duration: 30_000),
            Box("trak", Box("mdia",
                Box("hdlr",
                    Bytes(0, 0, 0, 0), // version + flags
                    U32(0),            // pre_defined
                    Encoding.ASCII.GetBytes("text")),
                Box("mdhd",
                    Bytes(0, 0, 0, 0),
                    U32(0), U32(0),    // creation, modification
                    U32(1000)),        // timescale
                Box("minf", Box("stbl",
                    Box("stts",
                        Bytes(0, 0, 0, 0),
                        U32(2),                    // entries
                        U32(1), U32(10_000),       // 1 sample of 10 s
                        U32(1), U32(20_000)),      // 1 sample of 20 s
                    Box("stsz",
                        Bytes(0, 0, 0, 0),
                        U32(0),                    // per-sample sizes
                        U32(2),
                        U32((uint)sample1.Length), U32((uint)sample2.Length)),
                    Box("stco",
                        Bytes(0, 0, 0, 0),
                        U32(2),
                        U32(offset1), U32(offset2)))))));

        var probe = BuildMoov(0, 0);
        var offset1 = (uint)probe.Length;
        var offset2 = offset1 + (uint)sample1.Length;
        var final = BuildMoov(offset1, offset2)
            .Concat(sample1).Concat(sample2).ToArray();
        File.WriteAllBytes(_path, final);

        var meta = M4bChapters.ExtractMetadata(_path);

        Assert.Equal(30_000UL, meta.TotalDurationMs);
        Assert.Equal(2, meta.Chapters.Count);
        Assert.Equal(new M4bChapter("Chapter One", 0, 10_000), meta.Chapters[0]);
        Assert.Equal(new M4bChapter("Chapter Two", 10_000, 20_000), meta.Chapters[1]);
    }

    [Fact]
    public void NoMoovYieldsEmptyMetadata()
    {
        File.WriteAllBytes(_path, Box("ftyp", U32(0)));
        var meta = M4bChapters.ExtractMetadata(_path);
        Assert.Empty(meta.Chapters);
        Assert.Equal(0UL, meta.TotalDurationMs);
    }

    [Fact]
    public void TruncatedHeaderThrows()
    {
        // A chpl that promises more chapters than the file holds.
        var moov = Box("moov",
            Mvhd(1000, 1000),
            Box("udta", Box("chpl",
                Bytes(1, 0, 0, 0),
                U64(0),
                U32(99)))); // 99 chapters, no data
        File.WriteAllBytes(_path, moov);
        Assert.ThrowsAny<IOException>(() => M4bChapters.ExtractMetadata(_path));
    }
}
