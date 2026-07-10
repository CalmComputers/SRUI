namespace Srui;

/// <summary>A periodic timer routed through the event loop. Created via
/// SruiApp.StartTicker; fires at event-loop cadence.</summary>
public sealed class Ticker
{
    private readonly SruiApp _app;

    public ulong Id { get; }

    public event Action? Tick;

    internal Ticker(SruiApp app, ulong id)
    {
        _app = app;
        Id = id;
    }

    internal void OnTick() => Tick?.Invoke();

    public void Stop() => _app.StopTicker(this);
}
