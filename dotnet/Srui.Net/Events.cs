namespace Srui;

/// <summary>A node handle. Zero means "no node".</summary>
public readonly record struct NodeId(ulong Value)
{
    public static readonly NodeId None = new(0);
    public bool IsNone => Value == 0;
}

/// <summary>What kind of accessibility event a Speech output came from.</summary>
public enum SpeechSource
{
    Focused = 0,
    Typing = 1,
    TextNav = 2,
    Selection = 3,
    ItemNav = 4,
    TabChange = 5,
    SliderChange = 6,
    Filter = 7,
    Clipboard = 8,
    Announce = 9,
}

/// <summary>One drained output event.</summary>
public abstract record OutputEvent
{
    /// <summary>An accessibility event, pre-rendered to an utterance.</summary>
    public sealed record Speech(string Text, SpeechSource Source, NodeId Node) : OutputEvent;

    /// <summary>A button was pressed (directly, or via primary/cancel routing).</summary>
    public sealed record Activated(NodeId Node) : OutputEvent;

    public sealed record SecondaryActivated(NodeId Node) : OutputEvent;

    /// <summary>A checkbox toggled; Checked is the new value.</summary>
    public sealed record Toggled(NodeId Node, bool Checked) : OutputEvent;

    /// <summary>A widget's state changed (text, selection, slider...).</summary>
    public sealed record Changed(NodeId Node) : OutputEvent;
}

/// <summary>One pumped host event.</summary>
public abstract record HostEvent
{
    public sealed record Quit : HostEvent;

    /// <summary>A physical key went down; silence speech before handling
    /// the corresponding Input.</summary>
    public sealed record KeyDown : HostEvent;

    /// <summary>A clean Alt tap (commonly bound to a menu/palette).</summary>
    public sealed record AltTap : HostEvent;

    public sealed record Input(InputEvent Event) : HostEvent;
}
