namespace Srui.Testing;

/// <summary>A reader that records every tick and interrupt it hears,
/// for assertion. <see cref="TestApp"/> installs one on its app
/// automatically; attach additional instances with
/// <see cref="SruiApp.AddReader"/> for tests that watch two streams.</summary>
public sealed class RecordingReader : IReader
{
    /// <summary>The ticks heard, in delivery order, each a copy of its
    /// events. Consumers that assert in batches (see
    /// <see cref="Harness.Spoken"/>) clear the list between batches.</summary>
    public List<List<AccessibilityEvent>> Ticks { get; } = new();

    /// <summary>Every event heard, in delivery order, across ticks.</summary>
    public IEnumerable<AccessibilityEvent> Events => Ticks.SelectMany(t => t);

    /// <summary>How many speech interrupts have been requested.</summary>
    public int Interrupts { get; private set; }

    /// <inheritdoc/>
    public void OnTick(IReadOnlyList<AccessibilityEvent> events) => Ticks.Add(new List<AccessibilityEvent>(events));

    /// <inheritdoc/>
    public void OnInterrupt() => Interrupts++;
}
