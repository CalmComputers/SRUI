using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class TextNavTests
{
    private static Rope R(string s) => new(s);

    [Fact]
    public void PrevNextGraphemeAscii()
    {
        var r = R("hello");
        Assert.Null(TextNav.PrevGrapheme(r, 0));
        Assert.Equal(0, TextNav.PrevGrapheme(r, 1));
        Assert.Equal(4, TextNav.PrevGrapheme(r, 5));

        Assert.Equal(1, TextNav.NextGrapheme(r, 0));
        Assert.Equal(5, TextNav.NextGrapheme(r, 4));
        Assert.Null(TextNav.NextGrapheme(r, 5));
    }

    [Fact]
    public void GraphemeAtAndBefore()
    {
        var r = R("abc");
        Assert.Equal("a", TextNav.GraphemeAt(r, 0));
        Assert.Equal("c", TextNav.GraphemeAt(r, 2));
        Assert.Null(TextNav.GraphemeAt(r, 3));

        Assert.Null(TextNav.GraphemeBefore(r, 0));
        Assert.Equal("a", TextNav.GraphemeBefore(r, 1));
        Assert.Equal("c", TextNav.GraphemeBefore(r, 3));
    }

    [Fact]
    public void GraphemeClusters()
    {
        // "e" + combining acute is one cluster; navigation never splits it.
        var r = R("ae\u0301b");
        Assert.Equal(1, TextNav.NextGrapheme(r, 0));
        Assert.Equal(3, TextNav.NextGrapheme(r, 1)); // skips the combining mark
        Assert.Equal("e\u0301", TextNav.GraphemeAt(r, 1));
        Assert.Equal(1, TextNav.PrevGrapheme(r, 3));
        Assert.Equal("e\u0301", TextNav.GraphemeBefore(r, 3));
    }

    [Fact]
    public void GraphemeSurrogatePairs()
    {
        // Astral plane: one emoji = one surrogate pair = 2 units, 1 cluster.
        var r = R("a\U0001F600b");
        Assert.Equal(1, TextNav.NextGrapheme(r, 0));
        Assert.Equal(3, TextNav.NextGrapheme(r, 1));
        Assert.Equal("\U0001F600", TextNav.GraphemeAt(r, 1));
        Assert.Equal(1, TextNav.PrevGrapheme(r, 3));
    }

    [Fact]
    public void CrlfIsOneGrapheme()
    {
        var r = R("a\r\nb");
        Assert.Equal(3, TextNav.NextGrapheme(r, 1));
        Assert.Equal("\r\n", TextNav.GraphemeAt(r, 1));
        Assert.Equal(1, TextNav.PrevGrapheme(r, 3));
    }

    [Fact]
    public void WordBoundaries()
    {
        var r = R("hello world");
        Assert.Equal(5, TextNav.NextWordBoundary(r, 0)); // "hello"
        Assert.Equal(11, TextNav.NextWordBoundary(r, 5)); // " world"

        Assert.Equal(5, TextNav.PrevWordBoundary(r, 11)); // back over "world"
        Assert.Equal(0, TextNav.PrevWordBoundary(r, 5)); // back over "hello"
    }

    [Fact]
    public void WordBoundariesUnderscore()
    {
        var r = R("foo_bar baz");
        Assert.Equal(7, TextNav.NextWordBoundary(r, 0));
        Assert.Equal(11, TextNav.NextWordBoundary(r, 7));
        Assert.Equal(7, TextNav.PrevWordBoundary(r, 11));
        Assert.Equal(0, TextNav.PrevWordBoundary(r, 7));
    }

    [Fact]
    public void WordBoundariesPunctuationGrouping()
    {
        var r = R("is....full");
        Assert.Equal(2, TextNav.NextWordBoundary(r, 0));
        Assert.Equal(6, TextNav.NextWordBoundary(r, 2));
        Assert.Equal(10, TextNav.NextWordBoundary(r, 6));

        Assert.Equal(6, TextNav.PrevWordBoundary(r, 10));
        Assert.Equal(2, TextNav.PrevWordBoundary(r, 6));
        Assert.Equal(0, TextNav.PrevWordBoundary(r, 2));
    }

    [Fact]
    public void WordBoundariesMixedPunctAndIdent()
    {
        var r = R("other-symbols");
        Assert.Equal(5, TextNav.NextWordBoundary(r, 0));
        Assert.Equal(6, TextNav.NextWordBoundary(r, 5));
        Assert.Equal(13, TextNav.NextWordBoundary(r, 6));

        Assert.Equal(6, TextNav.PrevWordBoundary(r, 13));
        Assert.Equal(5, TextNav.PrevWordBoundary(r, 6));
        Assert.Equal(0, TextNav.PrevWordBoundary(r, 5));
    }

    [Fact]
    public void WordBoundariesMultipleSpaces()
    {
        var r = R("this      text");
        Assert.Equal(4, TextNav.NextWordBoundary(r, 0));
        Assert.Equal(14, TextNav.NextWordBoundary(r, 4));

        Assert.Equal(4, TextNav.PrevWordBoundary(r, 14));
        Assert.Equal(0, TextNav.PrevWordBoundary(r, 4));
    }

    [Fact]
    public void LineStartEnd()
    {
        var r = R("abc\ndef\nghi");
        Assert.Equal(0, TextNav.LineStart(r, 0));
        Assert.Equal(0, TextNav.LineStart(r, 2));
        Assert.Equal(4, TextNav.LineStart(r, 4));
        Assert.Equal(8, TextNav.LineStart(r, 8));

        Assert.Equal(3, TextNav.LineEnd(r, 0));
        Assert.Equal(7, TextNav.LineEnd(r, 4));
        Assert.Equal(11, TextNav.LineEnd(r, 8));
    }

    [Fact]
    public void LineUpDown()
    {
        var r = R("abc\ndef\nghi");
        var up = TextNav.LineUp(r, 4, 0);
        Assert.Equal((0, 0), up);

        Assert.Null(TextNav.LineUp(r, 0, 0));

        var down = TextNav.LineDown(r, 0, 0);
        Assert.Equal((4, 0), down);

        Assert.Null(TextNav.LineDown(r, 8, 0));
    }

    [Fact]
    public void LineUpPreservesColumn()
    {
        var r = R("abcde\nfg\nhijkl");
        // From "hijkl" pos 13 (col 4), up to "fg" â€” clamp to col 2.
        var up = TextNav.LineUp(r, 13, 4);
        Assert.Equal((8, 2), up);
    }

    [Fact]
    public void CurrentLineTextExtraction()
    {
        var r = R("abc\ndef\nghi");
        Assert.Equal("abc", TextNav.CurrentLineText(r, 0));
        Assert.Equal("def", TextNav.CurrentLineText(r, 4));
        Assert.Equal("ghi", TextNav.CurrentLineText(r, 8));
    }

    [Fact]
    public void WordAtPosition()
    {
        var r = R("hello world");
        Assert.Equal("hello", TextNav.WordAt(r, 0));
        Assert.Equal("hello", TextNav.WordAt(r, 3));
        Assert.Equal("world", TextNav.WordAt(r, 6));
    }

    [Fact]
    public void WordAtUnderscore()
    {
        var r = R("foo_bar baz");
        Assert.Equal("foo_bar", TextNav.WordAt(r, 0));
        Assert.Equal("foo_bar", TextNav.WordAt(r, 4));
        Assert.Equal("foo_bar", TextNav.WordAt(r, 5));
    }

    // â”€â”€ Windows-style word start navigation â”€â”€

    [Fact]
    public void PrevWordStartBasic()
    {
        var r = R("hello world");
        Assert.Equal(6, TextNav.PrevWordStart(r, 11));
        Assert.Equal(6, TextNav.PrevWordStart(r, 8));
        Assert.Equal(0, TextNav.PrevWordStart(r, 6));
        Assert.Equal(0, TextNav.PrevWordStart(r, 5));
        Assert.Equal(0, TextNav.PrevWordStart(r, 0));
    }

    [Fact]
    public void NextWordStartBasic()
    {
        var r = R("hello world");
        Assert.Equal(6, TextNav.NextWordStart(r, 0));
        Assert.Equal(6, TextNav.NextWordStart(r, 3));
        Assert.Equal(11, TextNav.NextWordStart(r, 6));
        Assert.Equal(11, TextNav.NextWordStart(r, 11));
    }

    [Fact]
    public void PrevWordStartMultipleSpaces()
    {
        var r = R("foo   bar");
        Assert.Equal(6, TextNav.PrevWordStart(r, 9));
        Assert.Equal(0, TextNav.PrevWordStart(r, 6));
        Assert.Equal(0, TextNav.PrevWordStart(r, 5));
    }

    [Fact]
    public void NextWordStartMultipleSpaces()
    {
        var r = R("foo   bar");
        Assert.Equal(6, TextNav.NextWordStart(r, 0));
        Assert.Equal(6, TextNav.NextWordStart(r, 3));
    }

    [Fact]
    public void PrevWordStartPunctuation()
    {
        var r = R("is....full");
        Assert.Equal(6, TextNav.PrevWordStart(r, 10));
        Assert.Equal(2, TextNav.PrevWordStart(r, 6));
        Assert.Equal(0, TextNav.PrevWordStart(r, 2));
    }

    [Fact]
    public void NextWordStartPunctuation()
    {
        var r = R("is....full");
        Assert.Equal(2, TextNav.NextWordStart(r, 0));
        Assert.Equal(6, TextNav.NextWordStart(r, 2));
        Assert.Equal(10, TextNav.NextWordStart(r, 6));
    }

    [Fact]
    public void WordStartEmpty()
    {
        var r = R("");
        Assert.Equal(0, TextNav.PrevWordStart(r, 0));
        Assert.Equal(0, TextNav.NextWordStart(r, 0));
    }

    // â”€â”€ Word-extent (Notepad-style) â”€â”€

    [Fact]
    public void NextWordExtentBasic()
    {
        var r = R("hello world");
        Assert.Equal(6, TextNav.NextWordExtent(r, 0));
        Assert.Equal(11, TextNav.NextWordExtent(r, 6));
    }

    [Fact]
    public void PrevWordExtentBasic()
    {
        var r = R("hello world");
        Assert.Equal(6, TextNav.PrevWordExtent(r, 11));
        Assert.Equal(0, TextNav.PrevWordExtent(r, 6));
    }

    [Fact]
    public void WordExtentCommaSpace()
    {
        var r = R("Hello, world!");
        Assert.Equal(7, TextNav.NextWordExtent(r, 0));
        Assert.Equal(13, TextNav.NextWordExtent(r, 7));
        Assert.Equal(7, TextNav.PrevWordExtent(r, 13));
        Assert.Equal(0, TextNav.PrevWordExtent(r, 7));
    }

    [Fact]
    public void WordExtentPunctuation()
    {
        var r = R("is....full");
        Assert.Equal(6, TextNav.NextWordExtent(r, 0));
        Assert.Equal(10, TextNav.NextWordExtent(r, 6));
        Assert.Equal(6, TextNav.PrevWordExtent(r, 10));
        Assert.Equal(0, TextNav.PrevWordExtent(r, 6));
    }

    [Fact]
    public void WordExtentEmpty()
    {
        var r = R("");
        Assert.Equal(0, TextNav.PrevWordExtent(r, 0));
        Assert.Equal(0, TextNav.NextWordExtent(r, 0));
    }

    [Fact]
    public void EmptyRope()
    {
        var r = R("");
        Assert.Null(TextNav.PrevGrapheme(r, 0));
        Assert.Null(TextNav.NextGrapheme(r, 0));
        Assert.Null(TextNav.GraphemeAt(r, 0));
        Assert.Equal(0, TextNav.LineStart(r, 0));
        Assert.Equal(0, TextNav.LineEnd(r, 0));
    }

    // â”€â”€ Property tests (ported from proptest, seeded random) â”€â”€

    private const string BoundaryAlphabet = "abcXYZ019_.,:;!? ";

    private static string RandomText(Random rng, int maxLen)
    {
        var length = rng.Next(1, maxLen + 1);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = BoundaryAlphabet[rng.Next(BoundaryAlphabet.Length)];
        return new string(chars);
    }

    [Fact]
    public void NextWordBoundaryMakesProgress()
    {
        var rng = new Random(7);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var text = RandomText(rng, 100);
            var r = R(text);
            var pos = 0;
            var steps = 0;
            while (pos < r.Length)
            {
                var next = TextNav.NextWordBoundary(r, pos);
                if (next == pos)
                {
                    // Only trailing whitespace remains.
                    var remaining = r.Substring(pos, r.Length);
                    Assert.True(remaining.All(char.IsWhiteSpace),
                        $"NextWordBoundary({pos}) stalled but non-ws remains: {remaining}");
                    break;
                }
                Assert.True(next > pos, $"NextWordBoundary({pos}) did not advance in {text}");
                pos = next;
                steps++;
                Assert.True(steps <= r.Length, "infinite loop detected");
            }
        }
    }

    [Fact]
    public void PrevWordBoundaryMakesProgress()
    {
        var rng = new Random(11);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var text = RandomText(rng, 100);
            var r = R(text);
            var pos = r.Length;
            var steps = 0;
            while (pos > 0)
            {
                var prev = TextNav.PrevWordBoundary(r, pos);
                Assert.True(prev < pos, $"PrevWordBoundary({pos}) did not retreat in {text}");
                pos = prev;
                steps++;
                Assert.True(steps <= r.Length, "infinite loop detected");
            }
            Assert.Equal(0, pos);
        }
    }

    [Fact]
    public void WordBoundariesStayInRange()
    {
        var rng = new Random(13);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var text = RandomText(rng, 100);
            var r = R(text);

            var pos = 0;
            while (pos < r.Length)
            {
                var next = TextNav.NextWordBoundary(r, pos);
                if (next == pos)
                    break;
                Assert.InRange(next, 0, r.Length);
                pos = next;
            }

            pos = r.Length;
            while (pos > 0)
            {
                var prev = TextNav.PrevWordBoundary(r, pos);
                Assert.InRange(prev, 0, r.Length);
                Assert.True(prev < pos);
                pos = prev;
            }
        }
    }
}

public class RopeTests
{
    [Fact]
    public void BasicOperations()
    {
        var r = new Rope("hello world");
        Assert.Equal(11, r.Length);
        Assert.Equal('h', r.CharAt(0));
        Assert.Equal('d', r.CharAt(10));
        Assert.Equal("hello world", r.ToString());
        Assert.Equal("lo wo", r.Substring(3, 8));
    }

    [Fact]
    public void InsertAndRemove()
    {
        var r = new Rope("hd");
        r.Insert(1, "ello worl");
        Assert.Equal("hello world", r.ToString());
        r.Remove(0, 6);
        Assert.Equal("world", r.ToString());
        r.Remove(0, 5);
        Assert.Equal("", r.ToString());
        Assert.Equal(0, r.Length);
    }

    [Fact]
    public void NewlineQueries()
    {
        var r = new Rope("abc\ndef\nghi");
        Assert.Equal(3, r.IndexOfNewline(0));
        Assert.Equal(3, r.IndexOfNewline(3));
        Assert.Equal(7, r.IndexOfNewline(4));
        Assert.Equal(-1, r.IndexOfNewline(8));

        Assert.Equal(-1, r.LastNewlineBefore(0));
        Assert.Equal(-1, r.LastNewlineBefore(3));
        Assert.Equal(3, r.LastNewlineBefore(4));
        Assert.Equal(7, r.LastNewlineBefore(11));
    }

    [Fact]
    public void ContentEquals()
    {
        var r = new Rope("hello");
        Assert.True(r.ContentEquals("hello"));
        Assert.False(r.ContentEquals("hellp"));
        Assert.False(r.ContentEquals("hell"));
        r.Insert(5, "!");
        Assert.True(r.ContentEquals("hello!"));
    }

    [Fact]
    public void LargeDocumentRoundTrip()
    {
        // Cross the leaf threshold repeatedly and verify against a
        // reference StringBuilder implementation.
        var rng = new Random(99);
        var reference = new System.Text.StringBuilder("seed text with\nnewlines\nin it ");
        var rope = new Rope(reference.ToString());

        for (var i = 0; i < 2000; i++)
        {
            var op = rng.Next(3);
            if (op == 0 || reference.Length == 0)
            {
                var pos = rng.Next(reference.Length + 1);
                var text = i % 7 == 0 ? "\n" : new string((char)('a' + rng.Next(26)), rng.Next(1, 20));
                rope.Insert(pos, text);
                reference.Insert(pos, text);
            }
            else if (op == 1)
            {
                var start = rng.Next(reference.Length + 1);
                var end = Math.Min(reference.Length, start + rng.Next(30));
                rope.Remove(start, end);
                reference.Remove(start, end - start);
            }
            else
            {
                var start = rng.Next(reference.Length + 1);
                var end = Math.Min(reference.Length, start + rng.Next(50));
                Assert.Equal(reference.ToString(start, end - start), rope.Substring(start, end));
            }
        }
        Assert.Equal(reference.ToString(), rope.ToString());
        Assert.True(rope.ContentEquals(reference.ToString()));

        // Newline queries agree with the reference.
        var refText = reference.ToString();
        for (var probe = 0; probe <= refText.Length; probe += Math.Max(1, refText.Length / 50))
        {
            var expectedNext = refText.IndexOf('\n', Math.Min(probe, refText.Length));
            Assert.Equal(expectedNext, rope.IndexOfNewline(probe));
            var expectedPrev = probe == 0 ? -1 : refText.LastIndexOf('\n', probe - 1);
            Assert.Equal(expectedPrev, rope.LastNewlineBefore(probe));
        }
    }
}
