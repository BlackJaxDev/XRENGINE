using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Vectors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture;

/// <summary>
/// Spawns a grid of child nodes each with a light probe when play begins, and cleans them up when play ends.
/// </summary>
public class LightProbeGridSpawnerComponent : XRComponent
{
    private readonly record struct ProbeOccluder(ModelComponent Model, StaticRigidBodyComponent? StaticRigidBody);

    private readonly List<SceneNode> _spawnedNodes = new();
    private readonly List<LightProbeComponent> _spawnedProbes = new();
    private readonly HashSet<ModelComponent> _subscribedPlacementBoundsModels = [];

    private bool _isSequentialCaptureRunning;
    private int _sequentialCaptureIndex = -1;
    private int _sequentialCaptureCompletedCount;
    private LightProbeComponent? _activeCaptureProbe;
    private uint _activeCaptureVersion;
    private long _activeCaptureStartTimestamp;
    private long _batchCaptureStartTimestamp;
    private string _captureStatus = "Idle";

    private IVector3 _probeCounts = new(2, 2, 2);
    /// <summary>
    /// Number of probes to create along each axis. Values less than one are clamped to one.
    /// </summary>
    [Category("Grid")]
    public IVector3 ProbeCounts
    {
        get => _probeCounts;
        set
        {
            var clamped = new IVector3(
                Math.Max(1, value.X),
                Math.Max(1, value.Y),
                Math.Max(1, value.Z));
            if (SetField(ref _probeCounts, clamped))
                RegenerateGridIfSpawned();
        }
    }

    private Vector3 _spacing = new(1f, 1f, 1f);
    /// <summary>
    /// Distance in world units between probes along each axis. Components are clamped to a minimum positive value.
    /// </summary>
    [Category("Grid")]
    public Vector3 Spacing
    {
        get => _spacing;
        set
        {
            var clamped = new Vector3(
                MathF.Max(0.001f, value.X),
                MathF.Max(0.001f, value.Y),
                MathF.Max(0.001f, value.Z));
            if (SetField(ref _spacing, clamped))
                RegenerateGridIfSpawned();
        }
    }

    private Vector3 _offset = Vector3.Zero;
    /// <summary>
    /// Local-space offset to apply to the generated grid origin.
    /// </summary>
    [Category("Grid")]
    public Vector3 Offset
    {
        get => _offset;
        set
        {
            if (SetField(ref _offset, value))
                RegenerateGridIfSpawned();
        }
    }

    private bool _realtimeCapture = false;
    private bool _autoCaptureOnActivate = true;
    private bool _autoSequentialCaptureOnBeginPlay;
    private TimeSpan? _realTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(100.0);
    private TimeSpan? _stopRealtimeCaptureAfter;
    private uint _irradianceResolution = 32;
    private bool _releaseTransientEnvironmentTexturesAfterCapture = true;
    private LightProbeComponent.EInfluenceShape _influenceShape = LightProbeComponent.EInfluenceShape.Sphere;
    private Vector3 _influenceOffset = Vector3.Zero;
    private float _influenceSphereInnerRadius = 0.0f;
    private float _influenceSphereOuterRadius = 5.0f;
    private Vector3 _influenceBoxInnerExtents = Vector3.Zero;
    private Vector3 _influenceBoxOuterExtents = new(5.0f, 5.0f, 5.0f);
    private bool _parallaxCorrectionEnabled = false;
    private Vector3 _proxyBoxCenterOffset = Vector3.Zero;
    private Vector3 _proxyBoxHalfExtents = Vector3.One;
    private Quaternion _proxyBoxRotation = Quaternion.Identity;
    private bool _previewProbes;
    private LightProbeComponent.ERenderPreview _previewDisplay = LightProbeComponent.ERenderPreview.Environment;
    private bool _usePlacementBoundsModels;
    private ModelComponent[] _placementBoundsModels = [];
    private Vector3 _placementBoundsPadding = Vector3.Zero;
    private bool _adjustProbePositionsAgainstGeometry = true;
    private float _probeCollisionRadius = 0.25f;
    private float _pushOutPadding = 0.1f;
    private float _maxPushOutDistance = 8.0f;
    private int _maxPushOutSteps = 24;
    private bool _deferredSpawnPending;
    private bool _deferredSpawnRetryRegistered;
    private long _lastDeferredSpawnRetryTimestamp;

    private static readonly TimeSpan DeferredSpawnRetryInterval = TimeSpan.FromMilliseconds(250.0);

    private static readonly Vector3[] s_probePushDirections =
    [
        Vector3.UnitY,
        -Vector3.UnitY,
        Vector3.UnitX,
        -Vector3.UnitX,
        Vector3.UnitZ,
        -Vector3.UnitZ,
        Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f)),
        Vector3.Normalize(new Vector3(-1.0f, 1.0f, 1.0f)),
        Vector3.Normalize(new Vector3(1.0f, 1.0f, -1.0f)),
        Vector3.Normalize(new Vector3(-1.0f, 1.0f, -1.0f)),
    ];

    [Category("Probe Defaults")]
    public bool RealtimeCapture
    {
        get => _realtimeCapture;
        set
        {
            if (SetField(ref _realtimeCapture, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public bool AutoCaptureOnActivate
    {
        get => _autoCaptureOnActivate;
        set
        {
            if (SetField(ref _autoCaptureOnActivate, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public bool AutoSequentialCaptureOnBeginPlay
    {
        get => _autoSequentialCaptureOnBeginPlay;
        set => SetField(ref _autoSequentialCaptureOnBeginPlay, value);
    }

    [Category("Probe Defaults")]
    public TimeSpan? RealTimeCaptureUpdateInterval
    {
        get => _realTimeCaptureUpdateInterval;
        set
        {
            if (SetField(ref _realTimeCaptureUpdateInterval, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public TimeSpan? StopRealtimeCaptureAfter
    {
        get => _stopRealtimeCaptureAfter;
        set
        {
            if (SetField(ref _stopRealtimeCaptureAfter, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public uint IrradianceResolution
    {
        get => _irradianceResolution;
        set
        {
            if (SetField(ref _irradianceResolution, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public bool ReleaseTransientEnvironmentTexturesAfterCapture
    {
        get => _releaseTransientEnvironmentTexturesAfterCapture;
        set
        {
            if (SetField(ref _releaseTransientEnvironmentTexturesAfterCapture, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public LightProbeComponent.EInfluenceShape InfluenceShape
    {
        get => _influenceShape;
        set
        {
            if (SetField(ref _influenceShape, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public Vector3 InfluenceOffset
    {
        get => _influenceOffset;
        set
        {
            if (SetField(ref _influenceOffset, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public float InfluenceSphereInnerRadius
    {
        get => _influenceSphereInnerRadius;
        set
        {
            if (SetField(ref _influenceSphereInnerRadius, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public float InfluenceSphereOuterRadius
    {
        get => _influenceSphereOuterRadius;
        set
        {
            if (SetField(ref _influenceSphereOuterRadius, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public Vector3 InfluenceBoxInnerExtents
    {
        get => _influenceBoxInnerExtents;
        set
        {
            if (SetField(ref _influenceBoxInnerExtents, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public Vector3 InfluenceBoxOuterExtents
    {
        get => _influenceBoxOuterExtents;
        set
        {
            if (SetField(ref _influenceBoxOuterExtents, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public bool ParallaxCorrectionEnabled
    {
        get => _parallaxCorrectionEnabled;
        set
        {
            if (SetField(ref _parallaxCorrectionEnabled, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public Vector3 ProxyBoxCenterOffset
    {
        get => _proxyBoxCenterOffset;
        set
        {
            if (SetField(ref _proxyBoxCenterOffset, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public Vector3 ProxyBoxHalfExtents
    {
        get => _proxyBoxHalfExtents;
        set
        {
            if (SetField(ref _proxyBoxHalfExtents, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Probe Defaults")]
    public Quaternion ProxyBoxRotation
    {
        get => _proxyBoxRotation;
        set
        {
            if (SetField(ref _proxyBoxRotation, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Preview")]
    public bool PreviewProbes
    {
        get => _previewProbes;
        set
        {
            if (SetField(ref _previewProbes, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Preview")]
    public LightProbeComponent.ERenderPreview PreviewDisplay
    {
        get => _previewDisplay;
        set
        {
            if (SetField(ref _previewDisplay, value))
                ApplyDefaultsToExistingProbes();
        }
    }

    [Category("Placement")]
    public bool UsePlacementBoundsModels
    {
        get => _usePlacementBoundsModels;
        set
        {
            if (SetField(ref _usePlacementBoundsModels, value))
            {
                RefreshPlacementModelSubscriptions();
                RegenerateGridIfSpawned();
            }
        }
    }

    [Category("Placement")]
    public ModelComponent[] PlacementBoundsModels
    {
        get => _placementBoundsModels;
        set
        {
            ModelComponent[] normalized = value ?? [];
            if (SetField(ref _placementBoundsModels, normalized))
            {
                RefreshPlacementModelSubscriptions();
                RegenerateGridIfSpawned();
            }
        }
    }

    public void ConfigurePlacementBoundsModels(ModelComponent[]? models, bool enabled)
    {
        ModelComponent[] normalized = models ?? [];
        bool modelsChanged = SetField(ref _placementBoundsModels, normalized);
        bool enabledChanged = SetField(ref _usePlacementBoundsModels, enabled);
        RefreshPlacementModelSubscriptions();
        if (modelsChanged || enabledChanged)
            SpawnOrRegenerateGrid();
    }

    [Category("Placement")]
    public Vector3 PlacementBoundsPadding
    {
        get => _placementBoundsPadding;
        set
        {
            var clamped = new Vector3(
                MathF.Max(0.0f, value.X),
                MathF.Max(0.0f, value.Y),
                MathF.Max(0.0f, value.Z));
            if (SetField(ref _placementBoundsPadding, clamped))
                RegenerateGridIfSpawned();
        }
    }

    [Category("Placement")]
    public bool AdjustProbePositionsAgainstGeometry
    {
        get => _adjustProbePositionsAgainstGeometry;
        set => SetField(ref _adjustProbePositionsAgainstGeometry, value);
    }

    [Category("Placement")]
    public float ProbeCollisionRadius
    {
        get => _probeCollisionRadius;
        set => SetField(ref _probeCollisionRadius, MathF.Max(0.01f, value));
    }

    [Category("Placement")]
    public float PushOutPadding
    {
        get => _pushOutPadding;
        set => SetField(ref _pushOutPadding, MathF.Max(0.0f, value));
    }

    [Category("Placement")]
    public float MaxPushOutDistance
    {
        get => _maxPushOutDistance;
        set => SetField(ref _maxPushOutDistance, MathF.Max(0.5f, value));
    }

    [Category("Placement")]
    public int MaxPushOutSteps
    {
        get => _maxPushOutSteps;
        set => SetField(ref _maxPushOutSteps, Math.Max(1, value));
    }

    [Browsable(false)]
    public bool IsSequentialCaptureRunning => _isSequentialCaptureRunning;

    [Browsable(false)]
    public string CaptureStatus => _captureStatus;

    [Browsable(false)]
    public IReadOnlyList<LightProbeComponent> SpawnedProbes => _spawnedProbes;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        SpawnGrid();
    }

    protected override void OnComponentDeactivated()
    {
        StopSequentialCapture("Idle");
        UnregisterDeferredSpawnRetry();
        UnsubscribeModelChangedEvents();
        CleanupGrid();
        base.OnComponentDeactivated();
    }

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        SpawnGrid();

        if (AutoSequentialCaptureOnBeginPlay)
            BeginSequentialCapture();
    }

    protected override void OnEndPlay()
    {
        StopSequentialCapture("Idle");
        UnregisterDeferredSpawnRetry();
        UnsubscribeModelChangedEvents();
        CleanupGrid();
        base.OnEndPlay();
    }

    protected override void OnDestroying()
    {
        StopSequentialCapture("Idle");
        UnregisterDeferredSpawnRetry();
        UnsubscribeModelChangedEvents();
        CleanupGrid();
        base.OnDestroying();
    }

    public void BeginSequentialCapture()
    {
        if (_spawnedProbes.Count == 0)
        {
            _captureStatus = "No spawned probes to capture.";
            return;
        }

        StopSequentialCapture("Idle");

        _isSequentialCaptureRunning = true;
        _sequentialCaptureIndex = -1;
        _sequentialCaptureCompletedCount = 0;
        _activeCaptureProbe = null;
        _activeCaptureVersion = 0;
        _activeCaptureStartTimestamp = 0;
        _batchCaptureStartTimestamp = Stopwatch.GetTimestamp();
        _captureStatus = $"Queued 0/{_spawnedProbes.Count} probes.";

        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        world?.Lights.BeginLightProbeBatchCapture();
        Debug.Out($"[LightProbeBatch] Starting sequential capture total={_spawnedProbes.Count} queueDepth={world?.Lights.PendingCaptureWorkItemCount ?? 0} pendingComponents={world?.Lights.PendingCaptureComponentCount ?? 0}");

        RegisterTick(ETickGroup.Late, ETickOrder.Scene, TickSequentialCapture);
        QueueNextSequentialCapture();
    }

    public void CancelSequentialCapture()
        => StopSequentialCapture($"Canceled after {_sequentialCaptureCompletedCount}/{_spawnedProbes.Count} probes.");

    private void SpawnGrid()
    {
        if (_spawnedNodes.Count > 0)
            return;

        // Ensure the parent has a default transform before spawning children
        _ = SceneNode.GetTransformAs<Transform>(true)!;

        var counts = ProbeCounts;
        Matrix4x4 parentInverse = SceneNode.Transform.InverseWorldMatrix;

        bool usePlacementBounds = TryGetPlacementBounds(out AABB placementBounds);

        // When model-based placement is requested but bounds are not yet
        // available (models still loading), defer spawning rather than
        // falling back to the manual grid — subscribe to ModelChanged
        // so we retry once mesh data arrives.
        if (UsePlacementBoundsModels && !usePlacementBounds)
        {
            Debug.Out("[LightProbeGrid] SpawnGrid: model-based placement requested but no valid bounds yet — deferring spawn.");
            RefreshPlacementModelSubscriptions();
            _deferredSpawnPending = true;
            RegisterDeferredSpawnRetry();
            return;
        }

        UnregisterDeferredSpawnRetry();
        if (usePlacementBounds)
            RefreshPlacementModelSubscriptions();
        else
            UnsubscribeModelChangedEvents();
        _deferredSpawnPending = false;
        ProbeOccluder[] occluders = CollectProbeOccluders();
        Vector3 totalSize = new(
            (counts.X - 1) * Spacing.X,
            (counts.Y - 1) * Spacing.Y,
            (counts.Z - 1) * Spacing.Z);
        Vector3 start = -0.5f * totalSize + Offset;

        // When using model-based placement bounds, auto-size the influence
        // outer radius so adjacent probes overlap fully.  The radius is set
        // to the length of the diagonal between adjacent grid cells — this
        // guarantees every point in the volume is covered by at least one
        // probe's influence region.
        if (usePlacementBounds)
        {
            Vector3 boundsSize = placementBounds.Max - placementBounds.Min;
            Vector3 cellSize = new(
                counts.X > 1 ? boundsSize.X / (counts.X - 1) : boundsSize.X,
                counts.Y > 1 ? boundsSize.Y / (counts.Y - 1) : boundsSize.Y,
                counts.Z > 1 ? boundsSize.Z / (counts.Z - 1) : boundsSize.Z);
            float autoOuterRadius = cellSize.Length();
            if (autoOuterRadius > InfluenceSphereOuterRadius)
            {
                Debug.Out($"[LightProbeGrid] Auto-sizing influence outer radius: {InfluenceSphereOuterRadius:F2} → {autoOuterRadius:F2} (cell diagonal).");
                SetField(ref _influenceSphereOuterRadius, autoOuterRadius);
            }
        }

        if (usePlacementBounds)
            Debug.Out($"[LightProbeGrid] SpawnGrid: using placement bounds {placementBounds} for {counts.X}x{counts.Y}x{counts.Z} grid.");
        else
            Debug.Out($"[LightProbeGrid] SpawnGrid: using Spacing={Spacing}, Offset={Offset} for {counts.X}x{counts.Y}x{counts.Z} grid (start={start}).");

        for (int x = 0; x < counts.X; ++x)
        {
            for (int y = 0; y < counts.Y; ++y)
            {
                for (int z = 0; z < counts.Z; ++z)
                {
                    Vector3 localPos = usePlacementBounds
                        ? CalculatePlacementBoundsLocalPosition(placementBounds, counts, x, y, z, parentInverse)
                        : start + new Vector3(x * Spacing.X, y * Spacing.Y, z * Spacing.Z);

                    // Create the child DETACHED so that AddComponent does not trigger
                    // OnComponentActivated (and thus AutoCaptureOnActivate) before
                    // ApplyDefaults has a chance to configure the probe.
                    var child = new SceneNode($"LightProbe[{x},{y},{z}]", new Transform(localPos));
                    var probe = child.AddComponent<LightProbeComponent>();
                    if (probe is not null)
                    {
                        ApplyDefaults(probe);
                        _spawnedProbes.Add(probe);
                    }
                    _spawnedNodes.Add(child);

                    // Parent into the tree AFTER defaults are applied — this triggers
                    // OnBeginPlay + OnComponentActivated with correct settings.
                    child.Transform.Parent = SceneNode.Transform;

                    if (probe is not null)
                        MoveProbeOutOfGeometry(child, probe, occluders);
                }
            }
        }

        _captureStatus = $"Ready: {_spawnedProbes.Count} probes.";
    }

    /// <summary>
    /// Spawns the grid if it hasn't been spawned yet, or regenerates it if it has.
    /// </summary>
    private void SpawnOrRegenerateGrid()
    {
        if (_spawnedNodes.Count == 0)
        {
            // Grid not yet spawned — attempt initial spawn (will defer if bounds unavailable).
            SpawnGrid();
            return;
        }

        RegenerateGrid();
    }

    private void RegenerateGridIfSpawned()
    {
        if (_spawnedNodes.Count == 0 && !_deferredSpawnPending)
            return;

        if (_spawnedNodes.Count == 0)
        {
            // Deferred spawn was pending — try again.
            SpawnGrid();
            return;
        }

        RegenerateGrid();
    }

    private void RegenerateGrid()
    {
        bool restartSequentialCapture = _isSequentialCaptureRunning || AutoSequentialCaptureOnBeginPlay;
        CleanupGrid();
        SpawnGrid();

        if (restartSequentialCapture && _spawnedNodes.Count > 0)
            BeginSequentialCapture();
    }

    private void ApplyDefaultsToExistingProbes()
    {
        if (_spawnedNodes.Count == 0)
            return;

        foreach (var probe in _spawnedProbes)
            ApplyDefaults(probe);
    }

    private void ApplyDefaults(LightProbeComponent probe)
    {
        probe.RealtimeCapture = RealtimeCapture;
        probe.AutoCaptureOnActivate = AutoCaptureOnActivate;
        probe.RealTimeCaptureUpdateInterval = RealTimeCaptureUpdateInterval;
        probe.StopRealtimeCaptureAfter = StopRealtimeCaptureAfter;
        probe.IrradianceResolution = IrradianceResolution;
        probe.ReleaseTransientEnvironmentTexturesAfterCapture = ReleaseTransientEnvironmentTexturesAfterCapture;
        probe.InfluenceShape = InfluenceShape;
        probe.InfluenceOffset = InfluenceOffset;
        probe.InfluenceSphereInnerRadius = InfluenceSphereInnerRadius;
        probe.InfluenceSphereOuterRadius = InfluenceSphereOuterRadius;
        probe.InfluenceBoxInnerExtents = InfluenceBoxInnerExtents;
        probe.InfluenceBoxOuterExtents = InfluenceBoxOuterExtents;
        probe.ParallaxCorrectionEnabled = ParallaxCorrectionEnabled;
        probe.ProxyBoxCenterOffset = ProxyBoxCenterOffset;
        probe.ProxyBoxHalfExtents = ProxyBoxHalfExtents;
        probe.ProxyBoxRotation = ProxyBoxRotation;
        probe.AutoShowPreviewOnSelect = !PreviewProbes;
        probe.PreviewEnabled = PreviewProbes;
        probe.PreviewDisplay = PreviewDisplay;
    }

    private void CleanupGrid()
    {
        if (_spawnedNodes.Count == 0)
            return;

        foreach (var node in _spawnedNodes)
            node.Destroy();

        _spawnedNodes.Clear();
        _spawnedProbes.Clear();
    }

    private void TickSequentialCapture()
    {
        if (!_isSequentialCaptureRunning)
            return;

        if (_activeCaptureProbe is null)
        {
            if (!QueueNextSequentialCapture())
                StopSequentialCapture($"Completed {_sequentialCaptureCompletedCount}/{_spawnedProbes.Count} probes.");
            return;
        }

        if (!_activeCaptureProbe.IsActiveInHierarchy)
        {
            _activeCaptureProbe = null;
            return;
        }

        if (_activeCaptureProbe.CaptureVersion == _activeCaptureVersion)
            return;

        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        var batchDiagnostics = world?.Lights.ConsumeLightProbeBatchDiagnostics();
        double captureMs = _activeCaptureStartTimestamp == 0
            ? 0.0
            : Stopwatch.GetElapsedTime(_activeCaptureStartTimestamp).TotalMilliseconds;

        _sequentialCaptureCompletedCount++;
        Debug.Out(
            $"[LightProbeBatch] Completed {_sequentialCaptureCompletedCount}/{_spawnedProbes.Count} probe={_activeCaptureProbe.SceneNode.Name} captureMs={captureMs:F2} queueDepth={world?.Lights.PendingCaptureWorkItemCount ?? 0} pendingComponents={world?.Lights.PendingCaptureComponentCount ?? 0} structuralRefreshMs={batchDiagnostics?.StructuralRefreshTime.TotalMilliseconds ?? 0.0:F2} structuralRefreshes={batchDiagnostics?.StructuralRefreshCount ?? 0} contentRefreshMs={batchDiagnostics?.ContentRefreshTime.TotalMilliseconds ?? 0.0:F2} contentRefreshes={batchDiagnostics?.ContentRefreshCount ?? 0}");
        _activeCaptureProbe = null;
        _activeCaptureStartTimestamp = 0;

        if (!QueueNextSequentialCapture())
            StopSequentialCapture($"Completed {_sequentialCaptureCompletedCount}/{_spawnedProbes.Count} probes.");
    }

    private bool QueueNextSequentialCapture()
    {
        while (++_sequentialCaptureIndex < _spawnedProbes.Count)
        {
            LightProbeComponent probe = _spawnedProbes[_sequentialCaptureIndex];
            if (!probe.IsActiveInHierarchy)
                continue;

            _activeCaptureProbe = probe;
            _activeCaptureVersion = probe.CaptureVersion;
            _activeCaptureStartTimestamp = Stopwatch.GetTimestamp();
            _captureStatus = $"Capturing {_sequentialCaptureIndex + 1}/{_spawnedProbes.Count}: {probe.SceneNode.Name}";
            XRWorldInstance? world = WorldAs<XRWorldInstance>();
            Debug.Out($"[LightProbeBatch] Queueing {_sequentialCaptureIndex + 1}/{_spawnedProbes.Count} probe={probe.SceneNode.Name} queueDepth={world?.Lights.PendingCaptureWorkItemCount ?? 0} pendingComponents={world?.Lights.PendingCaptureComponentCount ?? 0}");
            probe.QueueCapture();
            return true;
        }

        return false;
    }

    private void StopSequentialCapture(string status)
    {
        bool wasRunning = _isSequentialCaptureRunning;
        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        var batchDiagnostics = wasRunning ? world?.Lights.ConsumeLightProbeBatchDiagnostics() : null;
        double totalMs = wasRunning && _batchCaptureStartTimestamp != 0
            ? Stopwatch.GetElapsedTime(_batchCaptureStartTimestamp).TotalMilliseconds
            : 0.0;

        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TickSequentialCapture);
        _isSequentialCaptureRunning = false;
        _activeCaptureProbe = null;
        _activeCaptureVersion = 0;
        _activeCaptureStartTimestamp = 0;
        _batchCaptureStartTimestamp = 0;
        _captureStatus = status;

        if (wasRunning)
        {
            Debug.Out(
                $"[LightProbeBatch] {status} totalMs={totalMs:F2} avgProbeMs={(_sequentialCaptureCompletedCount > 0 ? totalMs / _sequentialCaptureCompletedCount : 0.0):F2} queueDepth={world?.Lights.PendingCaptureWorkItemCount ?? 0} pendingComponents={world?.Lights.PendingCaptureComponentCount ?? 0} structuralRefreshMs={batchDiagnostics?.StructuralRefreshTime.TotalMilliseconds ?? 0.0:F2} structuralRefreshes={batchDiagnostics?.StructuralRefreshCount ?? 0} contentRefreshMs={batchDiagnostics?.ContentRefreshTime.TotalMilliseconds ?? 0.0:F2} contentRefreshes={batchDiagnostics?.ContentRefreshCount ?? 0}");
            world?.Lights.EndLightProbeBatchCapture();
        }
    }

    private ProbeOccluder[] CollectProbeOccluders()
    {
        if (!AdjustProbePositionsAgainstGeometry)
            return [];

        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        if (world is null)
            return [];

        var occluders = new List<ProbeOccluder>();
        foreach (SceneNode root in world.RootNodes)
        {
            foreach (ModelComponent model in root.FindAllDescendantComponents<ModelComponent>())
            {
                if (ReferenceEquals(model.SceneNode, SceneNode))
                    continue;

                // Pre-trigger async BVH builds so they're likely ready by the
                // time MoveProbeOutOfGeometry needs them.
                foreach (RenderableMesh rm in model.Meshes)
                {
                    var renderer = rm.GetCurrentOrFirstLodRenderer();
                    if (renderer?.Mesh is { } m)
                        _ = m.BVHTree;
                }

                model.SceneNode.TryGetComponent(out StaticRigidBodyComponent? rigidBody);
                occluders.Add(new ProbeOccluder(model, rigidBody));
            }
        }

        return [.. occluders];
    }

    private void UnsubscribeModelChangedEvents()
    {
        foreach (ModelComponent model in _subscribedPlacementBoundsModels)
            model.ModelChanged -= OnPlacementModelChanged;

        _subscribedPlacementBoundsModels.Clear();
    }

    private void RefreshPlacementModelSubscriptions()
    {
        if (!UsePlacementBoundsModels || PlacementBoundsModels.Length == 0)
        {
            UnsubscribeModelChangedEvents();
            return;
        }

        List<ModelComponent>? removedModels = null;
        foreach (ModelComponent model in _subscribedPlacementBoundsModels)
        {
            if (!ContainsPlacementBoundsModel(model))
                (removedModels ??= []).Add(model);
        }

        if (removedModels is not null)
        {
            foreach (ModelComponent model in removedModels)
            {
                model.ModelChanged -= OnPlacementModelChanged;
                _subscribedPlacementBoundsModels.Remove(model);
            }
        }

        foreach (ModelComponent model in PlacementBoundsModels)
        {
            if (model is null || _subscribedPlacementBoundsModels.Contains(model))
                continue;

            model.ModelChanged += OnPlacementModelChanged;
            _subscribedPlacementBoundsModels.Add(model);
        }
    }

    private bool ContainsPlacementBoundsModel(ModelComponent target)
    {
        foreach (ModelComponent model in PlacementBoundsModels)
        {
            if (ReferenceEquals(model, target))
                return true;
        }

        return false;
    }

    private void OnPlacementModelChanged()
    {
        // A placement model's mesh data just arrived — if we can now compute
        // valid bounds, spawn (or regenerate) the probe grid.
        if (!TryGetPlacementBounds(out _))
            return; // Still not ready — stay subscribed.

        Debug.Out("[LightProbeGrid] Placement model meshes changed — regenerating grid.");
        UnregisterDeferredSpawnRetry();
        _deferredSpawnPending = false;
        SpawnOrRegenerateGrid();
    }

    private void RegisterDeferredSpawnRetry()
    {
        if (_deferredSpawnRetryRegistered)
            return;

        _lastDeferredSpawnRetryTimestamp = 0;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, TickDeferredSpawnRetry);
        _deferredSpawnRetryRegistered = true;
    }

    private void UnregisterDeferredSpawnRetry()
    {
        if (!_deferredSpawnRetryRegistered)
            return;

        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TickDeferredSpawnRetry);
        _deferredSpawnRetryRegistered = false;
        _lastDeferredSpawnRetryTimestamp = 0;
    }

    private void TickDeferredSpawnRetry()
    {
        if (!_deferredSpawnPending || _spawnedNodes.Count > 0)
        {
            UnregisterDeferredSpawnRetry();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastDeferredSpawnRetryTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastDeferredSpawnRetryTimestamp, now) < DeferredSpawnRetryInterval)
        {
            return;
        }

        _lastDeferredSpawnRetryTimestamp = now;
        if (!TryGetPlacementBounds(out _, logDiagnostics: false))
            return;

        Debug.Out("[LightProbeGrid] Deferred placement bounds are now available — spawning grid.");
        SpawnOrRegenerateGrid();
    }

    private bool TryGetPlacementBounds(out AABB bounds, bool logDiagnostics = true)
    {
        bounds = default;
        if (!UsePlacementBoundsModels || PlacementBoundsModels.Length == 0)
        {
            if (logDiagnostics)
                Debug.Out($"[LightProbeGrid] TryGetPlacementBounds: skipped (enabled={UsePlacementBoundsModels}, models={PlacementBoundsModels.Length}).");
            return false;
        }

        bool found = false;
        int totalMeshes = 0;
        int validBounds = 0;
        foreach (ModelComponent model in PlacementBoundsModels)
        {
            if (model is null)
                continue;

            int meshCount = model.Meshes.Count;
            totalMeshes += meshCount;
            foreach (RenderableMesh renderable in model.Meshes)
            {
                if (!renderable.TryGetWorldBounds(out AABB worldBounds) || !worldBounds.IsValid)
                    continue;

                validBounds++;
                bounds = found ? AABB.Union(bounds, worldBounds) : worldBounds;
                found = true;
            }
        }

        if (!found)
        {
            if (logDiagnostics)
                Debug.Out($"[LightProbeGrid] TryGetPlacementBounds: no valid bounds (models={PlacementBoundsModels.Length}, meshes={totalMeshes}, validBounds={validBounds}).");
            return false;
        }

        bounds = new AABB(bounds.Min - PlacementBoundsPadding, bounds.Max + PlacementBoundsPadding);
        if (logDiagnostics)
            Debug.Out($"[LightProbeGrid] TryGetPlacementBounds: success (models={PlacementBoundsModels.Length}, meshes={totalMeshes}, validBounds={validBounds}, bounds={bounds}).");
        return bounds.IsValid;
    }

    private Vector3 CalculatePlacementBoundsLocalPosition(AABB bounds, IVector3 counts, int x, int y, int z, Matrix4x4 parentInverse)
    {
        Vector3 worldPosition = new(
            CalculatePlacementAxis(bounds.Min.X, bounds.Max.X, counts.X, x),
            CalculatePlacementAxis(bounds.Min.Y, bounds.Max.Y, counts.Y, y),
            CalculatePlacementAxis(bounds.Min.Z, bounds.Max.Z, counts.Z, z));

        return Vector3.Transform(worldPosition, parentInverse) + Offset;
    }

    private static float CalculatePlacementAxis(float min, float max, int count, int index)
    {
        if (count <= 1)
            return (min + max) * 0.5f;

        float t = index / (float)(count - 1);
        return min + (max - min) * t;
    }

    private void MoveProbeOutOfGeometry(SceneNode probeNode, LightProbeComponent probe, ProbeOccluder[] occluders)
    {
        if (occluders.Length == 0)
            return;

        Vector3 adjustedWorld = probe.Transform.WorldTranslation;
        bool moved = false;

        for (int pass = 0; pass < 4; pass++)
        {
            bool passMoved = false;
            foreach (ProbeOccluder occluder in occluders)
            {
                if (TryResolveAgainstCollider(occluder.StaticRigidBody, adjustedWorld, out Vector3 colliderAdjusted))
                {
                    adjustedWorld = colliderAdjusted;
                    moved = true;
                    passMoved = true;
                    continue;
                }

                // Collider failed or not present — always attempt BVH mesh fallback.
                if (TryResolveAgainstModelBvh(occluder.Model, adjustedWorld, out Vector3 bvhAdjusted))
                {
                    adjustedWorld = bvhAdjusted;
                    moved = true;
                    passMoved = true;
                }
            }

            if (!passMoved)
                break;
        }

        if (!moved)
            return;

        Matrix4x4 parentInverse = SceneNode.Transform.InverseWorldMatrix;
        probeNode.GetTransformAs<Transform>(true)!.Translation = Vector3.Transform(adjustedWorld, parentInverse);
    }

    private bool TryResolveAgainstCollider(StaticRigidBodyComponent? rigidBodyComponent, Vector3 worldPosition, out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        if (rigidBodyComponent?.RigidBody is not PhysxRigidActor rigidBody || rigidBody.ShapeCount == 0)
            return false;

        var testSphere = new IPhysicsGeometry.Sphere(ProbeCollisionRadius);
        float bestDistance = float.MaxValue;
        bool found = false;

        foreach (PhysxShape shape in rigidBody.GetShapes())
        {
            if (!shape.Overlap(rigidBody, testSphere, (worldPosition, Quaternion.Identity)))
                continue;

            if (!TryFindColliderExit(shape, rigidBody, worldPosition, testSphere, out Vector3 candidate))
                continue;

            float distance = Vector3.DistanceSquared(worldPosition, candidate);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            adjustedPosition = candidate;
            found = true;
        }

        return found;
    }

    private bool TryFindColliderExit(
        PhysxShape shape,
        PhysxRigidActor rigidBody,
        Vector3 worldPosition,
        IPhysicsGeometry.Sphere testSphere,
        out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        AABB bounds = shape.GetWorldBounds(rigidBody, 0.05f);
        Vector3 boundsCenter = (bounds.Min + bounds.Max) * 0.5f;

        float bestDistance = float.MaxValue;
        bool found = false;
        foreach (Vector3 direction in EnumeratePushDirections(worldPosition, boundsCenter))
        {
            if (!TryStepOutOfShape(shape, rigidBody, testSphere, worldPosition, direction, bounds, out Vector3 candidate))
                continue;

            float distance = Vector3.DistanceSquared(worldPosition, candidate);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            adjustedPosition = candidate;
            found = true;
        }

        return found;
    }

    private bool TryStepOutOfShape(
        PhysxShape shape,
        PhysxRigidActor rigidBody,
        IPhysicsGeometry.Sphere testSphere,
        Vector3 worldPosition,
        Vector3 direction,
        AABB bounds,
        out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        Vector3 normalizedDirection = NormalizeOrFallback(direction, Vector3.UnitY);
        float stepDistance = MathF.Max(ProbeCollisionRadius * 0.5f, 0.05f);
        float maxDistance = MathF.Max(MaxPushOutDistance, Vector3.Distance(bounds.Min, bounds.Max) + PushOutPadding + ProbeCollisionRadius);

        for (int step = 1; step <= MaxPushOutSteps; step++)
        {
            float distance = MathF.Min(stepDistance * step, maxDistance);
            Vector3 candidate = worldPosition + normalizedDirection * distance;
            if (shape.Overlap(rigidBody, testSphere, (candidate, Quaternion.Identity)))
                continue;

            adjustedPosition = candidate + normalizedDirection * PushOutPadding;
            return true;
        }

        return false;
    }

    private bool TryResolveAgainstModelBvh(ModelComponent model, Vector3 worldPosition, out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        float bestDistance = float.MaxValue;
        bool found = false;

        foreach (RenderableMesh renderable in model.Meshes)
        {
            if (!renderable.TryGetWorldBounds(out AABB worldBounds) || !Contains(worldBounds, worldPosition))
                continue;

            if (!TryFindMeshExit(renderable, worldPosition, out Vector3 candidate))
                continue;

            float distance = Vector3.DistanceSquared(worldPosition, candidate);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            adjustedPosition = candidate;
            found = true;
        }

        return found;
    }

    private bool TryFindMeshExit(RenderableMesh renderable, Vector3 worldPosition, out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        if (!renderable.TryGetWorldBounds(out AABB worldBounds))
            return false;

        Vector3 boundsCenter = (worldBounds.Min + worldBounds.Max) * 0.5f;
        float bestDistance = float.MaxValue;
        bool found = false;

        foreach (Vector3 direction in EnumeratePushDirections(worldPosition, boundsCenter))
        {
            if (!TryFindMeshExitAlongDirection(renderable, worldPosition, direction, out Vector3 candidate))
                continue;

            float distance = Vector3.DistanceSquared(worldPosition, candidate);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            adjustedPosition = candidate;
            found = true;
        }

        return found;
    }

    private bool TryFindMeshExitAlongDirection(RenderableMesh renderable, Vector3 worldPosition, Vector3 direction, out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        var renderer = renderable.GetCurrentOrFirstLodRenderer();
        var mesh = renderer?.Mesh;
        if (mesh is null)
            return false;

        bool skinned = renderable.IsSkinned;
        SimpleScene.Util.ssBVH.BVH<Triangle>? bvh = skinned ? renderable.GetSkinnedBvh() : mesh.BVHTree;
        if (bvh is null && !skinned)
        {
            // BVHTree property fires an async build; generate synchronously so
            // probe displacement doesn't silently skip this mesh.
            mesh.GenerateBVH();
            bvh = mesh.BVHTree;
        }
        if (bvh is null)
            return false;

        Matrix4x4 worldToLocal;
        if (skinned)
        {
            worldToLocal = renderable.SkinnedBvhWorldToLocalMatrix;
        }
        else
        {
            TransformBase transform = renderable.Component.Transform;
            worldToLocal = transform.InverseWorldMatrix;
        }

        Vector3 normalizedDirection = NormalizeOrFallback(direction, Vector3.UnitY);
        Vector3 localStart = Vector3.Transform(worldPosition, worldToLocal);
        Vector3 localFarPoint = Vector3.Transform(worldPosition + normalizedDirection * MaxPushOutDistance, worldToLocal);
        Vector3 localDirection = localFarPoint - localStart;
        float maxDistance = localDirection.Length();
        if (maxDistance <= 1e-4f)
            return false;

        localDirection /= maxDistance;
        if (!TryCountRayIntersections(bvh, localStart, localDirection, maxDistance, out int hitCount, out float nearestHitDistance) || hitCount <= 0)
            return false;

        if ((hitCount & 1) == 0 || nearestHitDistance <= 1e-4f)
            return false;

        // nearestHitDistance is in LOCAL space — transform the hit point back to
        // world space so scaling is handled correctly.
        Vector3 localHitPoint = localStart + localDirection * nearestHitDistance;
        Matrix4x4.Invert(worldToLocal, out Matrix4x4 localToWorld);
        Vector3 worldHitPoint = Vector3.Transform(localHitPoint, localToWorld);
        adjustedPosition = worldHitPoint + normalizedDirection * PushOutPadding;
        return true;
    }

    private static bool TryCountRayIntersections(SimpleScene.Util.ssBVH.BVH<Triangle> bvh, Vector3 origin, Vector3 direction, float maxDistance, out int hitCount, out float nearestHitDistance)
    {
        hitCount = 0;
        nearestHitDistance = float.MaxValue;

        Vector3 segmentEnd = origin + direction * maxDistance;
        var matches = bvh.Traverse(node => GeoUtil.Intersect.SegmentWithAABB(origin, segmentEnd, node.Min, node.Max, out _, out _));
        if (matches is null)
            return false;

        List<float> intersections = [];
        foreach (var node in matches)
        {
            if (node.gobjects is null)
                continue;

            foreach (Triangle triangle in node.gobjects)
            {
                if (!GeoUtil.Intersect.RayWithTriangle(origin, direction, triangle.A, triangle.B, triangle.C, out float hitDistance))
                    continue;

                if (hitDistance <= 1e-4f || hitDistance > maxDistance)
                    continue;

                intersections.Add(hitDistance);
            }
        }

        if (intersections.Count == 0)
            return false;

        intersections.Sort();
        const float epsilon = 1e-3f;
        float previous = float.MinValue;
        foreach (float distance in intersections)
        {
            if (nearestHitDistance == float.MaxValue)
                nearestHitDistance = distance;

            if (MathF.Abs(distance - previous) <= epsilon)
                continue;

            previous = distance;
            hitCount++;
        }

        return hitCount > 0;
    }

    private static IEnumerable<Vector3> EnumeratePushDirections(Vector3 worldPosition, Vector3 boundsCenter)
    {
        yield return boundsCenter == worldPosition
            ? Vector3.UnitY
            : NormalizeOrFallback(worldPosition - boundsCenter, Vector3.UnitY);

        foreach (Vector3 direction in s_probePushDirections)
            yield return direction;
    }

    private static Vector3 NormalizeOrFallback(Vector3 direction, Vector3 fallback)
        => direction.LengthSquared() <= 1e-6f ? fallback : Vector3.Normalize(direction);

    private static bool Contains(AABB bounds, Vector3 point)
        => point.X >= bounds.Min.X && point.X <= bounds.Max.X
        && point.Y >= bounds.Min.Y && point.Y <= bounds.Max.Y
        && point.Z >= bounds.Min.Z && point.Z <= bounds.Max.Z;
}