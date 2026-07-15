using Srui;
using Srui.Audio;

namespace SruiDemo;

/// <summary>Shared navigation ping for sound-augmented lists: one
/// entity on the SFX bus, positioned by item index so list movement
/// reads left-to-right across a fixed stage two units ahead of the
/// listener. HRTF when available, pan/volume otherwise.</summary>
public sealed class ListNavSound : IDisposable
{
    private const float StageWidth = 8.0f;
    private const float Distance = 2.0f;

    private readonly SoundEntity _entity;
    private readonly Sound _sound;

    public ListNavSound(SoundManager audio, SoundGroup bus, string wavPath)
    {
        _entity = audio.CreateEntity(bus);
        _entity.Hrtf = audio.IsHrtfAvailable;
        _entity.SetPosition(0.0f, Distance, 0.0f);
        _sound = audio.CreateSound(_entity.Group);
        _sound.Load(wavPath);
    }

    /// <summary>Play from the item's direction; a null index plays
    /// centered (for lists that don't expose a position).</summary>
    public void Play(int? index = null, int count = 0)
    {
        var x = 0.0f;
        if (index is int i && i >= 0 && count > 1)
            x = (i / (float)(count - 1) - 0.5f) * StageWidth;
        _entity.SetPosition(x, Distance, 0.0f);
        _sound.Stop();
        _sound.Play();
    }

    public void Dispose()
    {
        _sound.Dispose();
        _entity.Dispose();
    }
}

/// <summary>A ListBox that pings on every selection move, positioned by
/// the selected index. The template for sound-augmented widgets:
/// subclass the wrapper, override OnChanged, keep the base call so
/// composition subscribers still fire.</summary>
public class SoundListBox : ListBox
{
    private readonly ListNavSound _nav;
    private int _count;

    public SoundListBox(
        IWidgetContainer parent, string name, IReadOnlyList<string> items,
        ListNavSound nav, bool numbered = false)
        : base(parent, name, items, numbered)
    {
        _nav = nav;
        _count = items.Count;
    }

    public override void SetItems(IReadOnlyList<IListItem> items)
    {
        _count = items.Count;
        base.SetItems(items);
    }

    protected override void OnChanged()
    {
        base.OnChanged();
        _nav.Play(SelectedIndex, _count);
    }
}

/// <summary>A FilterListBox with the same navigation ping, centered
/// (the wrapper exposes no selection index).</summary>
public class SoundFilterListBox : FilterListBox
{
    private readonly ListNavSound _nav;

    public SoundFilterListBox(
        IWidgetContainer parent, string name, IReadOnlyList<string> items, ListNavSound nav)
        : base(parent, name, items)
    {
        _nav = nav;
    }

    protected override void OnChanged()
    {
        base.OnChanged();
        _nav.Play();
    }
}
