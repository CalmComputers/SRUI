using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class EditorStateTests
{
    [Fact]
    public void InsertAndSpeak()
    {
        var editor = new EditorState("", false);
        Assert.Equal("h", editor.InsertChar('h'));
        Assert.Equal("i", editor.InsertChar('i'));
        Assert.Equal("hi", editor.Text());
        Assert.Equal(2, editor.Cursor);
    }

    [Fact]
    public void InsertUppercaseSpeaksCap()
    {
        var editor = new EditorState("", false);
        Assert.Equal("cap A", editor.InsertChar('A'));
    }

    [Fact]
    public void InsertSpaceSpeaksSpace()
    {
        var editor = new EditorState("", false);
        Assert.Equal("space", editor.InsertChar(' '));
    }

    [Fact]
    public void DeleteBackward()
    {
        var editor = new EditorState("abc", false) { Cursor = 3 };
        Assert.Equal("c", editor.DeleteBackward());
        Assert.Equal("ab", editor.Text());
        Assert.Equal(2, editor.Cursor);
    }

    [Fact]
    public void DeleteBackwardAtStart()
    {
        var editor = new EditorState("abc", false) { Cursor = 0 };
        Assert.Null(editor.DeleteBackward());
    }

    [Fact]
    public void DeleteForward()
    {
        var editor = new EditorState("abc", false) { Cursor = 0 };
        Assert.Equal("a", editor.DeleteForward());
        Assert.Equal("bc", editor.Text());
        Assert.Equal(0, editor.Cursor);
    }

    [Fact]
    public void DeleteWordBackward()
    {
        var editor = new EditorState("hello world", false) { Cursor = 11 };
        Assert.Equal("world", editor.DeleteWordBackward());
        Assert.Equal("hello ", editor.Text());
    }

    [Fact]
    public void MoveLeftRight()
    {
        var editor = new EditorState("abc", false) { Cursor = 1 };
        Assert.Equal("a", editor.MoveLeft());
        Assert.Equal(0, editor.Cursor);
        Assert.Equal("blank", editor.MoveLeft());

        Assert.Equal("b", editor.MoveRight());
        Assert.Equal(1, editor.Cursor);
    }

    [Fact]
    public void MoveWordLeftRight()
    {
        var editor = new EditorState("hello world", false) { Cursor = 11 };
        editor.MoveWordLeft();
        Assert.Equal(6, editor.Cursor);
        editor.MoveWordLeft();
        Assert.Equal(0, editor.Cursor);

        editor.MoveWordRight();
        Assert.Equal(6, editor.Cursor);
        editor.MoveWordRight();
        Assert.Equal(11, editor.Cursor);
    }

    [Fact]
    public void MoveToLineStartEnd()
    {
        var editor = new EditorState("hello", false) { Cursor = 3 };
        Assert.Equal("h", editor.MoveToLineStart());
        Assert.Equal(0, editor.Cursor);
        Assert.Equal("o", editor.MoveToLineEnd());
        Assert.Equal(5, editor.Cursor);
    }

    [Fact]
    public void MoveToLineEndMultilineNonLast()
    {
        var editor = new EditorState("abc\ndef\nghi", true) { Cursor = 0 };
        Assert.Equal("c", editor.MoveToLineEnd());
        Assert.Equal(3, editor.Cursor);
    }

    [Fact]
    public void MoveToLineEndEmptyLine()
    {
        var editor = new EditorState("abc\n\nghi", true) { Cursor = 4 };
        Assert.Equal("blank", editor.MoveToLineEnd());
        Assert.Equal(4, editor.Cursor);
    }

    [Fact]
    public void MoveToDocStartEnd()
    {
        var editor = new EditorState("hello\nworld", true) { Cursor = 8 };
        Assert.Equal("h", editor.MoveToDocStart());
        Assert.Equal(0, editor.Cursor);
        Assert.Equal("d", editor.MoveToDocEnd());
        Assert.Equal(11, editor.Cursor);
    }

    [Fact]
    public void LineUpDownMultiline()
    {
        var editor = new EditorState("abc\ndef\nghi", true) { Cursor = 4 };
        Assert.Equal("abc", editor.MoveLineUp());
        Assert.Equal(0, editor.Cursor);
        Assert.Equal("top", editor.MoveLineUp());

        Assert.Equal("def", editor.MoveLineDown());
        Assert.Equal("ghi", editor.MoveLineDown());
        Assert.Equal("bottom", editor.MoveLineDown());
    }

    [Fact]
    public void LineUpDownSingleline()
    {
        var editor = new EditorState("hello", false);
        Assert.Equal("hello", editor.MoveLineUp());
        Assert.Equal("hello", editor.MoveLineDown());
    }

    [Fact]
    public void SelectAndCopy()
    {
        var editor = new EditorState("hello", false) { Cursor = 0 };
        editor.SelectRight();
        editor.SelectRight();
        editor.SelectRight();
        Assert.Equal("hel", editor.SelectedText());

        var (clip, speech) = editor.Copy();
        Assert.Equal("hel", clip);
        Assert.Equal("copied", speech);
    }

    [Fact]
    public void SelectAll()
    {
        var editor = new EditorState("hello", false);
        Assert.Equal("hello selected", editor.SelectAll());
    }

    [Fact]
    public void SelectAllEmpty()
    {
        var editor = new EditorState("", false);
        Assert.Equal("blank", editor.SelectAll());
    }

    [Fact]
    public void Cut()
    {
        var editor = new EditorState("hello", false) { Selection = (0, 3), Cursor = 3 };
        var (clip, speech) = editor.Cut();
        Assert.Equal("hel", clip);
        Assert.Equal("cut", speech);
        Assert.Equal("lo", editor.Text());
    }

    [Fact]
    public void Paste()
    {
        var editor = new EditorState("hd", false) { Cursor = 1 };
        Assert.Equal("pasted", editor.Paste("ello worl"));
        Assert.Equal("hello world", editor.Text());
    }

    [Fact]
    public void ReadAllEmpty()
    {
        var editor = new EditorState("", false);
        Assert.Equal("blank", editor.ReadAll());
    }

    [Fact]
    public void ReadAllWithContent()
    {
        var editor = new EditorState("hello", false);
        Assert.Equal("hello", editor.ReadAll());
    }

    [Fact]
    public void ReadOnlyBlocksEdits()
    {
        var editor = new EditorState("hello", false) { ReadOnly = true };
        Assert.Equal("", editor.InsertChar('x'));
        Assert.Null(editor.DeleteBackward());
        Assert.Null(editor.DeleteForward());
        Assert.Equal("hello", editor.Text());
    }

    [Fact]
    public void SelectToLineEnd()
    {
        var editor = new EditorState("hello", false) { Cursor = 0 };
        Assert.Equal("hello selected", editor.SelectToLineEnd());
    }

    [Fact]
    public void MoveLeftCollapsesSelectionToStart()
    {
        var editor = new EditorState("hello", false) { Selection = (1, 4), Cursor = 4 };
        var speech = editor.MoveLeft();
        Assert.Equal(1, editor.Cursor);
        Assert.Null(editor.Selection);
        Assert.Equal("e", speech);
    }

    [Fact]
    public void MoveRightCollapsesSelectionToEnd()
    {
        var editor = new EditorState("hello", false) { Selection = (1, 4), Cursor = 4 };
        var speech = editor.MoveRight();
        Assert.Equal(4, editor.Cursor);
        Assert.Null(editor.Selection);
        Assert.Equal("o", speech);
    }

    [Fact]
    public void MoveWordLeftCollapsesSelectionToStart()
    {
        var editor = new EditorState("hello world", false) { Selection = (0, 5), Cursor = 5 };
        var speech = editor.MoveWordLeft();
        Assert.Equal(0, editor.Cursor);
        Assert.Null(editor.Selection);
        Assert.Equal("hello", speech);
    }

    [Fact]
    public void MoveWordRightCollapsesSelectionToEnd()
    {
        var editor = new EditorState("hello world", false) { Selection = (0, 5), Cursor = 5 };
        var speech = editor.MoveWordRight();
        Assert.Equal(5, editor.Cursor);
        Assert.Null(editor.Selection);
        Assert.Equal(" ", speech);
    }

    [Fact]
    public void PasteSingleLineConvertsNewlines()
    {
        var editor = new EditorState("", false);
        editor.Paste("hello\nworld\r\n!");
        Assert.Equal("hello world !", editor.Text());
    }

    [Fact]
    public void CursorClampOnSetText()
    {
        var editor = new EditorState("hello world", false) { Cursor = 11 };
        editor.SetText("hi");
        Assert.Equal(2, editor.Cursor);
    }

    // ── Property test: cursor always in range (ported from proptest) ──

    [Fact]
    public void CursorAlwaysInValidRange()
    {
        var rng = new Random(23);
        for (var iteration = 0; iteration < 150; iteration++)
        {
            var initial = RandomInitialText(rng);
            var editor = new EditorState(initial, rng.Next(2) == 0);
            Assert.InRange(editor.Cursor, 0, editor.Length);

            var opCount = rng.Next(1, 50);
            for (var i = 0; i < opCount; i++)
            {
                ApplyRandomOp(editor, rng);
                Assert.InRange(editor.Cursor, 0, editor.Length);
                if (editor.Selection is (var a, var c))
                {
                    Assert.InRange(a, 0, editor.Length);
                    Assert.InRange(c, 0, editor.Length);
                }
            }
        }
    }

    private static string RandomInitialText(Random rng)
    {
        const string alphabet = "abcXYZ019 \n";
        var length = rng.Next(0, 50);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    private static void ApplyRandomOp(EditorState editor, Random rng)
    {
        switch (rng.Next(26))
        {
            case 0: editor.InsertChar((char)rng.Next(0x20, 0x7F)); break;
            case 1: editor.InsertNewline(); break;
            case 2: editor.DeleteBackward(); break;
            case 3: editor.DeleteForward(); break;
            case 4: editor.DeleteWordBackward(); break;
            case 5: editor.DeleteWordForward(); break;
            case 6: editor.MoveLeft(); break;
            case 7: editor.MoveRight(); break;
            case 8: editor.MoveWordLeft(); break;
            case 9: editor.MoveWordRight(); break;
            case 10: editor.MoveToLineStart(); break;
            case 11: editor.MoveToLineEnd(); break;
            case 12: editor.MoveToDocStart(); break;
            case 13: editor.MoveToDocEnd(); break;
            case 14: editor.MoveLineUp(); break;
            case 15: editor.MoveLineDown(); break;
            case 16: editor.SelectLeft(); break;
            case 17: editor.SelectRight(); break;
            case 18: editor.SelectWordLeft(); break;
            case 19: editor.SelectWordRight(); break;
            case 20: editor.SelectToLineStart(); break;
            case 21: editor.SelectToLineEnd(); break;
            case 22: editor.SelectToDocStart(); break;
            case 23: editor.SelectToDocEnd(); break;
            case 24: editor.SelectAll(); break;
            default:
            {
                var length = rng.Next(0, 20);
                var chars = new char[length];
                for (var i = 0; i < length; i++)
                    chars[i] = (char)rng.Next('a', 'z' + 1);
                editor.Paste(new string(chars));
                break;
            }
        }
    }
}
