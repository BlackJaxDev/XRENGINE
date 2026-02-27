using System.ComponentModel;
using System.Numerics;
using XREngine.Audio;
using XREngine.Audio.Steam;
using YamlDotNet.Serialization;

namespace XREngine.Components;

/// <summary>
/// Scene component that places a Steam Audio probe batch at this node's position.
/// <para>
/// The component manages a <see cref="SteamAudioProbeBatch"/> and integrates it with
/// the active <see cref="SteamAudioProcessor"/> on the world's listener. Probes are either
/// auto-generated from the acoustic scene geometry (uniform floor placement) or manually
/// placed, depending on <see cref="GenerationMode"/>.
/// </para>
/// <para>
/// Attach this component to a <see cref="SceneNode"/> that defines the center of the
/// probe volume. Set the <see cref="VolumeExtents"/> to define the bounding box size.
/// </para>
/// </summary>
[Category("Audio")]
[DisplayName("Steam Audio Probes")]
[Description("Places and manages a Steam Audio probe batch for reflections and pathing simulation.")]
[XRComponentEditor("XREngine.Editor.ComponentEditors.SteamAudioProbeComponentEditor")]
public class SteamAudioProbeComponent : XRComponent
{
    // ------------------------------------------------------------------
    //  Enums
    // ------------------------------------------------------------------

    public enum EProbeGenerationMode
    {
        /// <summary>Auto-generate probes on a uniform floor grid within the volume.</summary>
        UniformFloor,
        /// <summary>Place a single probe at the component's world position.</summary>
        Manual,
    }

    public enum EBakeStatus
    {
        NotBaked,
        BakingReflections,
        BakingPathing,
        Baked,
    }

    // ------------------------------------------------------------------
    //  Serialized properties
    // ------------------------------------------------------------------

    private EProbeGenerationMode _generationMode = EProbeGenerationMode.UniformFloor;
    private float _probeSpacing = 2.0f;
    private float _probeHeight = 1.5f;
    private Vector3 _volumeExtents = new(20.0f, 10.0f, 20.0f);
    private float _manualProbeRadius = 1.0f;
    private bool _autoGenerate = true;
    private bool _autoAttach = true;

    /// <summary>
    /// How probes are generated in this batch.
    /// </summary>
    [Category("Probe Generation")]
    [DisplayName("Generation Mode")]
    [Description("UniformFloor auto-generates probes on floor surfaces; Manual places a single probe at the node position.")]
    public EProbeGenerationMode GenerationMode
    {
        get => _generationMode;
        set => _generationMode = value;
    }

    /// <summary>
    /// Spacing in meters between auto-generated probes (UniformFloor mode).
    /// </summary>
    [Category("Probe Generation")]
    [DisplayName("Probe Spacing")]
    [Description("Distance in meters between adjacent probes for uniform floor generation.")]
    public float ProbeSpacing
    {
        get => _probeSpacing;
        set => _probeSpacing = MathF.Max(0.1f, value);
    }

    /// <summary>
    /// Height above floor surfaces for auto-generated probes (UniformFloor mode).
    /// </summary>
    [Category("Probe Generation")]
    [DisplayName("Probe Height")]
    [Description("Height in meters above floor surfaces for probe placement.")]
    public float ProbeHeight
    {
        get => _probeHeight;
        set => _probeHeight = MathF.Max(0.0f, value);
    }

    /// <summary>
    /// Half-extents of the bounding volume for probe generation, relative to this node's position.
    /// </summary>
    [Category("Probe Generation")]
    [DisplayName("Volume Extents")]
    [Description("Half-size of the axis-aligned bounding box (in meters) defining the probe generation volume.")]
    public Vector3 VolumeExtents
    {
        get => _volumeExtents;
        set => _volumeExtents = Vector3.Max(value, new Vector3(0.1f));
    }

    /// <summary>
    /// Influence radius for manually-placed probes.
    /// </summary>
    [Category("Probe Generation")]
    [DisplayName("Manual Probe Radius")]
    [Description("Influence radius for a manually-placed probe (Manual mode only).")]
    public float ManualProbeRadius
    {
        get => _manualProbeRadius;
        set => _manualProbeRadius = MathF.Max(0.01f, value);
    }

    /// <summary>
    /// When true, probes are generated automatically on component activation.
    /// When false, generation must be triggered via the editor or <see cref="RegenerateProbes"/>.
    /// </summary>
    [Category("Probe Generation")]
    [DisplayName("Auto Generate")]
    [Description("Automatically generate probes when the component activates.")]
    public bool AutoGenerate
    {
        get => _autoGenerate;
        set => _autoGenerate = value;
    }

    /// <summary>
    /// When true, the probe batch is automatically attached to the active Steam Audio processor.
    /// </summary>
    [Category("Probe Generation")]
    [DisplayName("Auto Attach")]
    [Description("Automatically attach/detach the probe batch to the simulator on activation/deactivation.")]
    public bool AutoAttach
    {
        get => _autoAttach;
        set => _autoAttach = value;
    }

    // ------------------------------------------------------------------
    //  Runtime state (not serialized)
    // ------------------------------------------------------------------

    private SteamAudioProbeBatch? _batch;
    private SteamAudioProcessor? _processor;
    private EBakeStatus _bakeStatus = EBakeStatus.NotBaked;

    /// <summary>The managed probe batch, or null if not yet created.</summary>
    [YamlIgnore]
    [Browsable(false)]
    public SteamAudioProbeBatch? ProbeBatch => _batch;

    /// <summary>Number of probes currently in the committed batch.</summary>
    [YamlIgnore]
    [Category("Status")]
    [DisplayName("Probe Count")]
    [Description("Number of probes in the current batch (0 if not generated).")]
    public int ProbeCount => _batch?.ProbeCount ?? 0;

    /// <summary>Whether the batch is committed and ready for simulation.</summary>
    [YamlIgnore]
    [Category("Status")]
    [DisplayName("Is Committed")]
    public bool IsCommitted => _batch?.IsCommitted ?? false;

    /// <summary>Current bake status.</summary>
    [YamlIgnore]
    [Category("Status")]
    [DisplayName("Bake Status")]
    public EBakeStatus BakeStatus => _bakeStatus;

    /// <summary>Whether probes are currently attached to a processor's simulator.</summary>
    [YamlIgnore]
    [Category("Status")]
    [DisplayName("Is Attached")]
    public bool IsAttached => _processor != null;

    // ------------------------------------------------------------------
    //  Lifecycle
    // ------------------------------------------------------------------

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();

        if (_autoGenerate)
            RegenerateProbes();
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        DetachFromProcessor();
        DisposeBatch();
    }

    protected override void OnDestroying()
    {
        DetachFromProcessor();
        DisposeBatch();
        base.OnDestroying();
    }

    // ------------------------------------------------------------------
    //  Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Regenerates the probe batch based on current settings.
    /// If <see cref="AutoAttach"/> is true, automatically attaches to the simulator.
    /// </summary>
    public bool RegenerateProbes()
    {
        // Find the active processor
        var processor = FindProcessor();
        if (processor is null)
        {
            Debug.Out("[SteamAudioProbeComponent] No active SteamAudioProcessor found â€” cannot generate probes.");
            return false;
        }

        // Ensure scene exists
        var scene = processor.Scene;
        if (scene is null)
        {
            Debug.Out("[SteamAudioProbeComponent] No SteamAudioScene â€” cannot generate probes.");
            return false;
        }

        // Detach old batch if attached
        DetachFromProcessor();
        DisposeBatch();

        // Create new batch
        _batch = processor.CreateProbeBatch();

        try
        {
            switch (_generationMode)
            {
                case EProbeGenerationMode.UniformFloor:
                {
                    Vector3 worldPos = Transform.WorldTranslation;
                    Vector3 min = worldPos - _volumeExtents;
                    Vector3 max = worldPos + _volumeExtents;
                    var volumeTransform = SteamAudioProbeBatch.CreateVolumeTransform(min, max);
                    _batch.GenerateProbes(scene, _probeSpacing, _probeHeight, volumeTransform);
                    break;
                }
                case EProbeGenerationMode.Manual:
                {
                    _batch.AddProbe(Transform.WorldTranslation, _manualProbeRadius);
                    break;
                }
            }

            _batch.Commit();
            Debug.Out($"[SteamAudioProbeComponent] Generated {_batch.ProbeCount} probes ({_generationMode}).");

            if (_autoAttach)
                AttachToProcessor(processor);

            return true;
        }
        catch (Exception ex)
        {
            Debug.Out($"[SteamAudioProbeComponent] Probe generation failed: {ex.Message}");
            DisposeBatch();
            return false;
        }
    }

    /// <summary>
    /// Bakes reflection data into the probe batch.
    /// This is a blocking operation.
    /// </summary>
    public bool BakeReflections(ReflectionsBakeSettings? settings = null)
    {
        if (_batch is null || _processor is null)
        {
            Debug.Out("[SteamAudioProbeComponent] Cannot bake: no probe batch or processor.");
            return false;
        }

        var scene = _processor.Scene;
        if (scene is null)
            return false;

        var baker = _processor.CreateBaker();
        _bakeStatus = EBakeStatus.BakingReflections;

        try
        {
            baker.BakeReflections(scene, _batch, settings ?? new ReflectionsBakeSettings());
            _bakeStatus = EBakeStatus.Baked;
            Debug.Out("[SteamAudioProbeComponent] Reflections bake complete.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.Out($"[SteamAudioProbeComponent] Reflections bake failed: {ex.Message}");
            _bakeStatus = EBakeStatus.NotBaked;
            return false;
        }
    }

    /// <summary>
    /// Bakes pathing data into the probe batch.
    /// This is a blocking operation.
    /// </summary>
    public bool BakePathing(PathBakeSettings? settings = null)
    {
        if (_batch is null || _processor is null)
        {
            Debug.Out("[SteamAudioProbeComponent] Cannot bake: no probe batch or processor.");
            return false;
        }

        var scene = _processor.Scene;
        if (scene is null)
            return false;

        var baker = _processor.CreateBaker();
        _bakeStatus = EBakeStatus.BakingPathing;

        try
        {
            baker.BakePathing(scene, _batch, settings ?? new PathBakeSettings());
            _bakeStatus = EBakeStatus.Baked;
            Debug.Out("[SteamAudioProbeComponent] Pathing bake complete.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.Out($"[SteamAudioProbeComponent] Pathing bake failed: {ex.Message}");
            _bakeStatus = EBakeStatus.NotBaked;
            return false;
        }
    }

    /// <summary>
    /// Manually attaches the probe batch to the specified (or active) processor.
    /// </summary>
    public void AttachToProcessor(SteamAudioProcessor? processor = null)
    {
        processor ??= FindProcessor();
        if (processor is null || _batch is null || !_batch.IsCommitted)
            return;

        if (_processor == processor)
            return; // Already attached

        DetachFromProcessor();
        processor.AddProbeBatch(_batch);
        _processor = processor;
    }

    /// <summary>
    /// Detaches the probe batch from the current processor.
    /// </summary>
    public void DetachFromProcessor()
    {
        if (_processor is null || _batch is null)
            return;

        try
        {
            _processor.RemoveProbeBatch(_batch);
        }
        catch (Exception ex)
        {
            Debug.Out($"[SteamAudioProbeComponent] Error detaching: {ex.Message}");
        }

        _processor = null;
    }

    // ------------------------------------------------------------------
    //  Internal helpers
    // ------------------------------------------------------------------

    private SteamAudioProcessor? FindProcessor()
    {
        if (World is null)
            return null;

        foreach (var listener in World.Listeners)
        {
            if (listener.EffectsProcessor is SteamAudioProcessor processor)
                return processor;
        }

        return null;
    }

    private void DisposeBatch()
    {
        _batch?.Dispose();
        _batch = null;
        _bakeStatus = EBakeStatus.NotBaked;
    }
}
