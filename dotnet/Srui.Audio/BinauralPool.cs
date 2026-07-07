namespace Srui.Audio;

/// <summary>Identity of a shareable convolver: sounds with the same
/// source AABB (hence the same direction from the listener at all
/// times), the same stationary flag, and the same downstream node can
/// share one HRTF convolver. Stationary sounds normalize their position
/// away, so they all share per bus.</summary>
internal readonly record struct BinauralKey(
    float MinX, float MinY, float MinZ,
    float MaxX, float MaxY, float MaxZ,
    bool Stationary,
    IntPtr Downstream);

/// <summary>
/// Refcounted pool of Steam Audio binaural nodes. Multiple sound nodes
/// attach to a shared convolver's input (miniaudio mixes them), halving
/// or better the HRTF cost whenever emitters coincide. Contract: callers
/// detach themselves from a node before releasing their reference.
/// </summary>
internal sealed class BinauralPool
{
    private sealed class Entry
    {
        public IntPtr Node;
        public int Refs;
    }

    private readonly AudioEngine _engine;
    private readonly Dictionary<BinauralKey, Entry> _entries = new();

    public BinauralPool(AudioEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Number of live convolvers (diagnostic).</summary>
    public int ActiveConvolvers => _entries.Count;

    /// <summary>Get or create the convolver for a key. `created` tells
    /// the caller to wire the node's output to the key's downstream.
    /// Zero when Steam Audio node creation fails.</summary>
    public IntPtr Acquire(in BinauralKey key, out bool created)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.Refs++;
            created = false;
            return entry.Node;
        }
        var node = NativeMethods.cosmos_binaural_node_create(_engine.NodeGraph, 2);
        created = node != IntPtr.Zero;
        if (created)
            _entries[key] = new Entry { Node = node, Refs = 1 };
        return node;
    }

    /// <summary>Drop a reference; the convolver is destroyed at zero.
    /// The caller must already be detached from it.</summary>
    public void Release(in BinauralKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            return;
        if (--entry.Refs <= 0)
        {
            _entries.Remove(key);
            NativeMethods.cosmos_binaural_node_destroy(entry.Node);
        }
    }

    /// <summary>Re-lease from one key to another. A sole occupant is
    /// rekeyed in place (same node, no Steam Audio churn for moving
    /// sources); otherwise the target is joined or a new convolver is
    /// split off. `releaseOld` tells the caller to Release(from) after
    /// rewiring; `created` as in Acquire. Zero on creation failure — the
    /// caller keeps its current lease.</summary>
    public IntPtr Move(in BinauralKey from, in BinauralKey to, out bool created, out bool releaseOld)
    {
        created = false;
        releaseOld = false;

        if (from == to)
            return _entries.TryGetValue(from, out var same) ? same.Node : IntPtr.Zero;

        if (_entries.TryGetValue(to, out var target))
        {
            target.Refs++;
            releaseOld = true;
            return target.Node;
        }

        if (_entries.TryGetValue(from, out var source) && source.Refs == 1)
        {
            _entries.Remove(from);
            _entries[to] = source;
            return source.Node;
        }

        // Splitting off a shared convolver.
        var node = NativeMethods.cosmos_binaural_node_create(_engine.NodeGraph, 2);
        if (node == IntPtr.Zero)
            return IntPtr.Zero;
        _entries[to] = new Entry { Node = node, Refs = 1 };
        created = true;
        releaseOld = true;
        return node;
    }

    /// <summary>Destroy any remaining convolvers (manager teardown).</summary>
    public void Clear()
    {
        foreach (var entry in _entries.Values)
            NativeMethods.cosmos_binaural_node_destroy(entry.Node);
        _entries.Clear();
    }
}
