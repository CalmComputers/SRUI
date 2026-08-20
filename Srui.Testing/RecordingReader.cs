namespace Srui.Testing;

/// <summary>A reader that records every accessibility event and interrupt
/// it hears, for assertion. <see cref="TestApp"/> installs one on its app
/// automatically; attach additional instances with
/// <see cref="SruiApp.AddReader"/> for tests that watch two streams.</summary>
public sealed class RecordingReader : IReader
{
    /// <summary>The events heard, in delivery order. Consumers that
    /// assert in batches (see <see cref="TestApp.Spoken"/>) clear the
    /// list between batches.</summary>
    public List<AccessibilityEvent> Events { get; } = new();

    /// <summary>How many speech interrupts have been requested.</summary>
    public int Interrupts { get; private set; }

    /// <inheritdoc/>
    public void OnEvent(AccessibilityEvent e) => Events.Add(e);

    /// <inheritdoc/>
    public void OnInterrupt() => Interrupts++;
}
