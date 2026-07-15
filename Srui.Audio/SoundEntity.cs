namespace Srui.Audio;

/// <summary>
/// A physical sound source in 3D space — a fighter, a projectile, a placed
/// object. Positioning is entity-level by design: sounds carry no position
/// of their own and become spatial by being created in the entity's
/// <see cref="Group"/> (<see cref="SoundManager.CreateSound"/> with
/// <c>entity.Group</c>). Everything the entity emits mixes in that group
/// and is spatialized once — one pan/volume computation and, with
/// <see cref="Hrtf"/>, one Steam Audio convolution per entity, however many
/// sounds it plays. The group is also the natural place for entity-wide
/// effects (<see cref="SoundGroup.SetFxChain"/> — give a possessed fighter
/// a chorus and every sound it makes is possessed).
///
/// Positions are AABBs — for point sources min == max. Spatialization is
/// applied once per <see cref="SoundManager.Tick"/> when the entity or the
/// listener moved. Create via <see cref="SoundManager.CreateEntity"/>;
/// dispose to release the group (and every sound in it) and the convolver.
/// </summary>
public sealed class SoundEntity : IDisposable
{
    private readonly SoundManager _manager;

    // 3D position as AABB.
    private float _minX, _minY, _minZ, _maxX, _maxY, _maxZ;

    private float _minDistance = 1.0f;
    private float _maxDistance = 100.0f;
    private float _rolloff = 1.0f;
    private float _minGain;
    private float _maxGain = 1.0f;

    // Horizon-style spatialization parameters (the non-HRTF path).
    private float _panStep = 0.05f;
    private float _volumeStep = 0.0333333f;
    private float _behindPitchDecrease = 0.04f;
    private bool _hardClosePan = true;

    private bool _hrtfEnabled;
    private IntPtr _binauralNode;
    private bool _spatialDirty = true;

    public bool IsDisposed { get; private set; }

    internal SoundEntity(SoundManager manager, SoundGroup? parent)
    {
        _manager = manager;
        Group = manager.CreateGroup(parent);
        Group.Entity = this;
    }

    /// <summary>The entity's bus. Create the entity's sounds in it, and
    /// hang entity-wide effects on it.</summary>
    public SoundGroup Group { get; }

    private AudioEngine Engine => _manager.Engine;

    // ── Position ──

    /// <summary>Point position. A no-op when unchanged, so callers may
    /// refresh freely every frame or every play.</summary>
    public void SetPosition(float x, float y, float z = 0.0f) =>
        SetPositionRanged(x, x, y, y, z, z);

    /// <summary>Ranged AABB position. A no-op when unchanged.</summary>
    public void SetPositionRanged(float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        if (_minX == minX && _maxX == maxX && _minY == minY && _maxY == maxY
            && _minZ == minZ && _maxZ == maxZ)
            return;
        _minX = minX; _maxX = maxX;
        _minY = minY; _maxY = maxY;
        _minZ = minZ; _maxZ = maxZ;
        _spatialDirty = true;
    }

    public float X => _minX;
    public float Y => _minY;
    public float Z => _minZ;
    public float MaxX => _maxX;
    public float MaxY => _maxY;
    public float MaxZ => _maxZ;

    // ── Spatialization parameters ──

    /// <summary>HRTF via Steam Audio when true; pan/volume otherwise.
    /// The convolver lives as long as the flag stays on — moving the
    /// entity never rewires the graph.</summary>
    public bool Hrtf
    {
        get => _hrtfEnabled;
        set
        {
            if (_hrtfEnabled == value)
                return;
            _hrtfEnabled = value;
            if (value && Engine.HrtfAvailable)
                CreateConvolver();
            else
                DestroyConvolver();
            _spatialDirty = true;
        }
    }

    public float MinDistance
    {
        get => _minDistance;
        set { _minDistance = value; _spatialDirty = true; }
    }

    public float MaxDistance
    {
        get => _maxDistance;
        set { _maxDistance = value; _spatialDirty = true; }
    }

    public float Rolloff
    {
        get => _rolloff;
        set { _rolloff = value; _spatialDirty = true; }
    }

    public float MinGain
    {
        get => _minGain;
        set { _minGain = value; _spatialDirty = true; }
    }

    public float MaxGain
    {
        get => _maxGain;
        set { _maxGain = value; _spatialDirty = true; }
    }

    /// <summary>Pan per unit horizontal distance (default 0.05).</summary>
    public float PanStep
    {
        get => _panStep;
        set { _panStep = value; _spatialDirty = true; }
    }

    /// <summary>Volume reduction per unit distance (default 0.0333).</summary>
    public float VolumeStep
    {
        get => _volumeStep;
        set { _volumeStep = value; _spatialDirty = true; }
    }

    /// <summary>Pitch reduction for sounds behind the listener (default 0.04).</summary>
    public float BehindPitchDecrease
    {
        get => _behindPitchDecrease;
        set { _behindPitchDecrease = value; _spatialDirty = true; }
    }

    /// <summary>Extra pan separation for close sounds (default true).</summary>
    public bool HardClosePan
    {
        get => _hardClosePan;
        set { _hardClosePan = value; _spatialDirty = true; }
    }

    // ── Spatialization (zero-alloc; applied by SoundManager.Tick) ──

    internal bool NeedsSpatialUpdate => _spatialDirty;

    internal bool HasConvolver => _binauralNode != IntPtr.Zero;

    /// <summary>Apply pending spatial state now instead of at the next
    /// Tick. <see cref="Sound.Play"/> calls this so a sound's first
    /// buffer never mixes with the entity's previous spatialization —
    /// the deferred apply would land one Tick late, an audible jump.</summary>
    internal void ApplyPending()
    {
        if (_spatialDirty)
            UpdateSpatialization();
    }

    /// <summary>Recompute pan/volume/pitch-delta (and HRTF direction)
    /// from the current listener state and apply them to the group.</summary>
    internal void UpdateSpatialization()
    {
        _spatialDirty = false;
        if (_hrtfEnabled && _binauralNode != IntPtr.Zero)
            UpdateHrtfSpatialization();
        else
            UpdateBasicSpatialization();
    }

    private void ClosestPointOnAabb(out float x, out float y, out float z)
    {
        x = Math.Clamp(Engine.ListenerX, _minX, _maxX);
        y = Math.Clamp(Engine.ListenerY, _minY, _maxY);
        z = Math.Clamp(Engine.ListenerZ, _minZ, _maxZ);
    }

    private void UpdateHrtfSpatialization()
    {
        ClosestPointOnAabb(out var edgeX, out var edgeY, out var edgeZ);
        var dx = edgeX - Engine.ListenerX;
        var dy = edgeY - Engine.ListenerY;
        var dz = edgeZ - Engine.ListenerZ;

        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        // Rotate horizontal direction by listener angle.
        var rotAngle = (-Engine.ListenerAngle + 90.0f) * (MathF.PI / 180.0f);
        var cosRot = MathF.Cos(rotAngle);
        var sinRot = MathF.Sin(rotAngle);
        var rotX = dx * cosRot - dy * sinRot;
        var rotY = dx * sinRot + dy * cosRot;

        // Steam Audio coordinates: X=right, Y=up, -Z=forward.
        var steamX = rotX;
        var steamY = dz;
        var steamZ = -rotY;

        if (distance > 0.0001f)
        {
            steamX /= distance;
            steamY /= distance;
            steamZ /= distance;
        }
        else
        {
            steamX = 0.0f;
            steamY = 0.0f;
            steamZ = -1.0f;
        }

        NativeMethods.ma_phonon_binaural_node_set_direction(_binauralNode, steamX, steamY, steamZ, distance);

        // Distance attenuation.
        var attenuatedDistance = MathF.Max(distance - _minDistance, 0.0f);
        var volume = attenuatedDistance <= _maxDistance - _minDistance
            ? Volume.DbToLinear(-attenuatedDistance * _rolloff * 1.75f)
            : 0.0f;
        volume = Math.Clamp(volume, _minGain, _maxGain);

        // HRTF handles panning; pan stays neutral.
        Group.SetSpatial(volume, 0.0f, BehindPitchDelta(distance, rotY, dz));
    }

    private void UpdateBasicSpatialization()
    {
        const float epsilon = 1e-4f;

        ClosestPointOnAabb(out var edgeX, out var edgeY, out var edgeZ);
        var dx = edgeX - Engine.ListenerX;
        var dy = edgeY - Engine.ListenerY;
        var dz = edgeZ - Engine.ListenerZ;

        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        var horizontalDistance = MathF.Sqrt(dx * dx + dy * dy);

        var rotAngle = (-Engine.ListenerAngle + 90.0f) * (MathF.PI / 180.0f);
        var cosRot = MathF.Cos(rotAngle);
        var sinRot = MathF.Sin(rotAngle);
        var rotX = dx * cosRot - dy * sinRot;
        var rotY = dx * sinRot + dy * cosRot;

        // Angle-based pan.
        var pan = 0.0f;
        if (horizontalDistance > epsilon)
        {
            var azimuth = MathF.Atan2(rotY, rotX);
            var angleFactor = MathF.Cos(azimuth);
            var distanceFactor = horizontalDistance * _panStep;
            if (_hardClosePan && distance > epsilon)
                distanceFactor += 0.2f;
            pan = Math.Clamp(angleFactor * Math.Clamp(distanceFactor, -1.0f, 1.0f), -1.0f, 1.0f);
        }

        var volume = Math.Clamp(1.0f - distance * _volumeStep, _minGain, _maxGain);
        Group.SetSpatial(volume, pan, BehindPitchDelta(distance, rotY, dz));
    }

    private float BehindPitchDelta(float distance, float rotY, float dz)
    {
        const float epsilon = 1e-4f;
        var delta = 0.0f;
        if (distance > 0.7071f + epsilon)
        {
            if (rotY < -epsilon) delta -= _behindPitchDecrease;
            if (dz < -epsilon) delta -= _behindPitchDecrease;
        }
        return delta;
    }

    // ── HRTF convolver wiring ──

    // The convolver sits at the group's exit: group → fx chain →
    // convolver → downstream, so entity effects are spatialized too.
    private void CreateConvolver()
    {
        if (_binauralNode != IntPtr.Zero || !Engine.HrtfAvailable)
            return;
        var node = NativeMethods.cosmos_binaural_node_create(Engine.NodeGraph, 2);
        if (node == IntPtr.Zero)
            return; // continue without HRTF
        NativeMethods.ma_node_attach_output_bus(node, 0, Group.DownstreamPtr, 0);
        Group.SetSpatialOutput(node);
        _binauralNode = node;
    }

    private void DestroyConvolver()
    {
        if (_binauralNode == IntPtr.Zero)
            return;
        Group.SetSpatialOutput(IntPtr.Zero);
        NativeMethods.cosmos_binaural_node_destroy(_binauralNode);
        _binauralNode = IntPtr.Zero;
    }

    // ── Engine rebuild (SoundManager.Reconfigure) ──

    /// <summary>Destroy the convolver ahead of an engine swap. Runs
    /// before the groups release; the group itself rebuilds through the
    /// manager's group pass.</summary>
    internal void ReleaseNative()
    {
        if (_binauralNode == IntPtr.Zero)
            return;
        Group.ClearSpatialOutput();
        NativeMethods.cosmos_binaural_node_destroy(_binauralNode);
        _binauralNode = IntPtr.Zero;
    }

    /// <summary>Recreate the convolver against the rebuilt engine. Runs
    /// after the groups recreate.</summary>
    internal void RecreateNative()
    {
        if (_hrtfEnabled)
            CreateConvolver();
        _spatialDirty = true;
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        DestroyConvolver();
        Group.Dispose();
    }
}
