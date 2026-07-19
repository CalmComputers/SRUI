using Srui.Core;
using Xunit;

namespace Srui.Net.Tests;

public class FuzzyTests
{
    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        Assert.True(Fuzzy.FuzzyMatch("", "anything"));
        Assert.True(Fuzzy.FuzzyMatch("", ""));
        Assert.Equal(0, Fuzzy.FuzzyScore("", "anything"));
    }

    [Fact]
    public void ExactMatch() => Assert.True(Fuzzy.FuzzyMatch("hello", "hello"));

    [Fact]
    public void SubsequenceMatch()
    {
        Assert.True(Fuzzy.FuzzyMatch("hlo", "hello"));
        Assert.True(Fuzzy.FuzzyMatch("ed", "Open Editor"));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.True(Fuzzy.FuzzyMatch("HLO", "hello"));
        Assert.True(Fuzzy.FuzzyMatch("hello", "HELLO"));
    }

    [Fact]
    public void NonMatch()
    {
        Assert.False(Fuzzy.FuzzyMatch("xyz", "hello"));
        Assert.Null(Fuzzy.FuzzyScore("xyz", "hello"));
    }

    [Fact]
    public void OrderMatters()
    {
        Assert.False(Fuzzy.FuzzyMatch("ba", "abc"));
        Assert.True(Fuzzy.FuzzyMatch("ab", "abc"));
    }

    [Fact]
    public void QueryLongerThanTarget() => Assert.False(Fuzzy.FuzzyMatch("abcdef", "abc"));

    [Fact]
    public void PrefixMatch() => Assert.True(Fuzzy.FuzzyMatch("hel", "hello world"));

    private static List<string> Rank(string query, params string[] candidates) =>
        Fuzzy.FilterItems(query, candidates);

    [Fact]
    public void ExactMatchScoresHighest()
    {
        var r = Rank("save", "Save File", "Save", "Autosave");
        Assert.Equal("Save", r[0]);
    }

    [Fact]
    public void PrefixBeatsMidstring()
    {
        var r = Rank("save", "Autosave File", "Save File");
        Assert.Equal("Save File", r[0]);
    }

    [Fact]
    public void WordInitialsRankHigh()
    {
        var r = Rank("sf", "Selfless", "Save File");
        Assert.Equal("Save File", r[0]);
    }

    [Fact]
    public void WordInitialsBeatScatteredMid()
    {
        var r = Rank("oe", "Somebody Else", "Open Editor");
        Assert.Equal("Open Editor", r[0]);
    }

    [Fact]
    public void ConsecutiveBeatsGaps()
    {
        var r = Rank("open", "Orphan Pen", "Open");
        Assert.Equal("Open", r[0]);
    }

    [Fact]
    public void FilterItemsEmptyQueryKeepsOrder()
    {
        var items = new List<string> { "b", "a" };
        Assert.Equal(items, Fuzzy.FilterItems("", items));
    }

    [Fact]
    public void FilterItemsSortsAndDrops()
    {
        var items = new List<string> { "Autosave File", "Save File", "Quit" };
        var filtered = Fuzzy.FilterItems("save", items);
        Assert.Equal("Save File", filtered[0]);
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void EqualScoresPreferTheShorterTarget()
    {
        // "find" scores the first four characters of both identically;
        // the item the query explains more of must win. The palette
        // case in miniature: the command named exactly what the user
        // typed outranks the longer command starting with it.
        var items = new List<string>
        {
            "Find Next, unavailable, f3",
            "Find, control f",
        };
        var filtered = Fuzzy.FilterItems("find", items);
        Assert.Equal("Find, control f", filtered[0]);

        // The same rule through the IListItem overload.
        var wrapped = ListBox.Wrap(items);
        Assert.Equal("Find, control f", Fuzzy.FilterItems("find", wrapped)[0].Text);
    }
}
