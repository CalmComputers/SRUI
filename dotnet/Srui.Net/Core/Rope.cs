using System.Text;

namespace Srui.Core;

/// <summary>A rope over UTF-16 code units: a balanced binary tree of
/// string leaves giving O(log n) local edits and O(log n + k) slicing.
/// All positions are UTF-16 code-unit indices. Newline queries treat
/// '\n' as the only line terminator (CRLF is handled by the navigation
/// layer, which strips a '\r' preceding a found '\n').</summary>
internal sealed class Rope
{
    private const int LeafMax = 512;
    private const int MaxDepth = 60;

    private abstract class RopeNode
    {
        public int Length;
        public byte Depth;
    }

    private sealed class Leaf : RopeNode
    {
        public readonly string Text;

        public Leaf(string text)
        {
            Text = text;
            Length = text.Length;
            Depth = 0;
        }
    }

    private sealed class Branch : RopeNode
    {
        public readonly RopeNode Left;
        public readonly RopeNode Right;

        public Branch(RopeNode left, RopeNode right)
        {
            Left = left;
            Right = right;
            Length = left.Length + right.Length;
            Depth = (byte)(Math.Max(left.Depth, right.Depth) + 1);
        }
    }

    private RopeNode _root;

    public Rope(string text) => _root = Build(text);

    public int Length => _root.Length;

    private static RopeNode Build(string text)
    {
        if (text.Length <= LeafMax)
            return new Leaf(text);
        var leaves = new List<RopeNode>((text.Length + LeafMax - 1) / LeafMax);
        for (var i = 0; i < text.Length; i += LeafMax)
            leaves.Add(new Leaf(text.Substring(i, Math.Min(LeafMax, text.Length - i))));
        return BuildBalanced(leaves, 0, leaves.Count);
    }

    private static RopeNode BuildBalanced(List<RopeNode> leaves, int start, int count)
    {
        if (count == 1)
            return leaves[start];
        var half = count / 2;
        return new Branch(
            BuildBalanced(leaves, start, half),
            BuildBalanced(leaves, start + half, count - half));
    }

    /// <summary>The code unit at <paramref name="pos"/>. Caller guarantees
    /// 0 &lt;= pos &lt; Length.</summary>
    public char CharAt(int pos)
    {
        var node = _root;
        while (node is Branch b)
        {
            if (pos < b.Left.Length)
            {
                node = b.Left;
            }
            else
            {
                pos -= b.Left.Length;
                node = b.Right;
            }
        }
        return ((Leaf)node).Text[pos];
    }

    public void Insert(int pos, string text)
    {
        if (text.Length == 0)
            return;
        pos = Math.Clamp(pos, 0, Length);
        _root = InsertRec(_root, pos, text);
        MaybeRebalance();
    }

    private static RopeNode InsertRec(RopeNode node, int pos, string text)
    {
        if (node is Branch b)
        {
            return pos <= b.Left.Length
                ? new Branch(InsertRec(b.Left, pos, text), b.Right)
                : new Branch(b.Left, InsertRec(b.Right, pos - b.Left.Length, text));
        }
        var leaf = (Leaf)node;
        // Typing-sized inserts rewrite the leaf; big ones rebuild locally.
        var combined = leaf.Text.Insert(pos, text);
        return combined.Length <= LeafMax ? new Leaf(combined) : Build(combined);
    }

    /// <summary>Remove the range [start, end).</summary>
    public void Remove(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, start, Length);
        if (start >= end)
            return;
        _root = RemoveRec(_root, start, end) ?? new Leaf("");
        MaybeRebalance();
    }

    private static RopeNode? RemoveRec(RopeNode node, int start, int end)
    {
        if (start <= 0 && end >= node.Length)
            return null;
        if (node is Leaf leaf)
        {
            var s = Math.Max(start, 0);
            var e = Math.Min(end, leaf.Length);
            return new Leaf(leaf.Text.Remove(s, e - s));
        }
        var b = (Branch)node;
        var leftLen = b.Left.Length;
        var left = start < leftLen ? RemoveRec(b.Left, start, Math.Min(end, leftLen)) : b.Left;
        var right = end > leftLen ? RemoveRec(b.Right, Math.Max(start - leftLen, 0), end - leftLen) : b.Right;
        if (left is null)
            return right;
        if (right is null)
            return left;
        if (left is Leaf l && right is Leaf r && l.Length + r.Length <= LeafMax)
            return new Leaf(l.Text + r.Text);
        return new Branch(left, right);
    }

    /// <summary>The text of [start, end), clamped.</summary>
    public string Substring(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, start, Length);
        if (start == end)
            return "";
        var sb = new StringBuilder(end - start);
        AppendRange(_root, start, end, sb);
        return sb.ToString();
    }

    private static void AppendRange(RopeNode node, int start, int end, StringBuilder sb)
    {
        if (node is Leaf leaf)
        {
            sb.Append(leaf.Text, start, end - start);
            return;
        }
        var b = (Branch)node;
        var leftLen = b.Left.Length;
        if (start < leftLen)
            AppendRange(b.Left, start, Math.Min(end, leftLen), sb);
        if (end > leftLen)
            AppendRange(b.Right, Math.Max(start - leftLen, 0), end - leftLen, sb);
    }

    public override string ToString() => Substring(0, Length);

    /// <summary>Chunk-wise comparison against a string — no rope
    /// materialization.</summary>
    public bool ContentEquals(string other)
    {
        if (other.Length != Length)
            return false;
        var offset = 0;
        return LeavesEqual(_root, other, ref offset);
    }

    private static bool LeavesEqual(RopeNode node, string other, ref int offset)
    {
        if (node is Leaf leaf)
        {
            if (!other.AsSpan(offset, leaf.Length).SequenceEqual(leaf.Text))
                return false;
            offset += leaf.Length;
            return true;
        }
        var b = (Branch)node;
        return LeavesEqual(b.Left, other, ref offset) && LeavesEqual(b.Right, other, ref offset);
    }

    /// <summary>Index of the first '\n' at or after <paramref name="from"/>,
    /// or -1.</summary>
    public int IndexOfNewline(int from)
    {
        from = Math.Max(from, 0);
        if (from >= Length)
            return -1;
        return IndexOfNewlineRec(_root, from);
    }

    private static int IndexOfNewlineRec(RopeNode node, int from)
    {
        if (node is Leaf leaf)
            return leaf.Text.IndexOf('\n', from);
        var b = (Branch)node;
        var leftLen = b.Left.Length;
        if (from < leftLen)
        {
            var i = IndexOfNewlineRec(b.Left, from);
            if (i >= 0)
                return i;
            from = leftLen;
        }
        var j = IndexOfNewlineRec(b.Right, from - leftLen);
        return j >= 0 ? leftLen + j : -1;
    }

    /// <summary>Index of the last '\n' strictly before <paramref name="pos"/>,
    /// or -1.</summary>
    public int LastNewlineBefore(int pos)
    {
        pos = Math.Min(pos, Length);
        if (pos <= 0)
            return -1;
        return LastNewlineRec(_root, pos);
    }

    private static int LastNewlineRec(RopeNode node, int before)
    {
        if (node is Leaf leaf)
            return leaf.Length == 0 ? -1 : leaf.Text.LastIndexOf('\n', Math.Min(before, leaf.Length) - 1);
        var b = (Branch)node;
        var leftLen = b.Left.Length;
        if (before > leftLen)
        {
            var j = LastNewlineRec(b.Right, before - leftLen);
            if (j >= 0)
                return leftLen + j;
            before = leftLen;
        }
        return before > 0 ? LastNewlineRec(b.Left, before) : -1;
    }

    private void MaybeRebalance()
    {
        if (_root.Depth > MaxDepth)
            _root = Build(ToString());
    }
}
