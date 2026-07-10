using Srui;

namespace SruiTasks;

/// <summary>A Button that arms on the first press and fires on the
/// second within a time window — for mildly destructive actions where a
/// confirm dialog would be heavier than the action warrants. This is the
/// event-raiser-override path: OnActivated runs at drain time (so
/// announcing from it is safe), and the base call — which raises the
/// public Activated event — is deliberately withheld until the press
/// that confirms; gating the outcome is the point of overriding the
/// hook. Activation shortcuts and primary/cancel routing land here too,
/// so they get the same two-press protection.</summary>
public class ConfirmButton : Button
{
    private readonly string _prompt;
    private readonly ulong _windowMs;
    private ulong? _armedAt;

    public ConfirmButton(
        IWidgetContainer parent, string name,
        string prompt = "Press again to confirm.", ulong windowMs = 3000)
        : base(parent, name)
    {
        _prompt = prompt;
        _windowMs = windowMs;
    }

    protected override void OnActivated()
    {
        if (_armedAt is ulong armed && NowMs - armed <= _windowMs)
        {
            _armedAt = null;
            base.OnActivated();
            return;
        }
        _armedAt = NowMs;
        Announce(_prompt);
    }
}
