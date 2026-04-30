using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Vectors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture;

/// <summary>
/// Spawns a grid of child nodes each with a light probe when play begins, and cleans them up when play ends.
/// </summary>
public class LightProbeGridSpawnerComponent : XRComponent
{
    private readonly record struct ProbeOccluder(RenderableMesh[] Renderables);

    private readonly record struct GridBuildRequest(
        int Version,
        bool ReplaceExistingGrid,
        bool RestartSequentialCapture,
        int CountX,
        int CountY,
        int CountZ,
        Vector3 Spacing,
        Vector3 Offset,
        Matrix4x4 ParentWorldMatrix,
        Matrix4x4 ParentInverseWorldMatrix,
        bool UsePlacementBoundsModels,
        ModelComponent[] PlacementBoundsModels,
        Vector3 PlacementBoundsPadding,
        bool AdjustProbePositionsAgainstGeometry,
        float ProbeCollisionRadius,
        float PushOutPadding,
        float MaxPushOutDistance,
        int MaxPushOutSteps,
        float CurrentInfluenceSphereOuterRadius,
        SceneNode[] WorldRootNodes);

    private sealed class GridBuildResult
    {
        public required GridBuildRequest Request { get; init; }
        public bool DeferredForPlacementBounds { get; init; }
        public bool UsedPlacementBounds { get; init; }
        public AABB PlacementBounds { get; init; }
        public float? AutoInfluenceOuterRadius { get; init; }
        public required Vector3[] LocalPositions { get; init; }
        public string? DiagnosticMessage { get; init; }
    }

    private readonly List<SceneNode> _spawnedNodes = new();
    private readonly List<LightProbeComponent> _spawnedProbes = new();
    private readonly HashSet<ModelComponent> _subscribedPlacementBoundsModels = [];
    private readonly object _gridBuildSync = new();

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
    private int _gridBuildRequestedVersion;
    private int _gridBuildRunningVersion;
    private bool _gridBuildRunning;
    private bool _gridBuildReplaceExistingRequested;
    private bool _gridBuildRestartCaptureRequested;
    private bool _gridBuildRequestsBlocked;
    private bool _placementModelRebuildPending;
    private bool _placementModelRebuildTickRegistered;
    private bool _placementModelRebuildReplaceExisting;
    private long _lastPlacementModelChangedTimestamp;

    private static readonly TimeSpan DeferredSpawnRetryInterval = TimeSpan.FromMilliseconds(250.0);
    private static readonly TimeSpan PlacementModelRebuildQuietPeriod = TimeSpan.FromMilliseconds(750.0);

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
        {
            if (enabled && normalized.Length > 0)
                SchedulePlacementModelRebuild(_spawnedNodes.Count > 0);
            else
                SpawnOrRegenerateGrid();
        }
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
        InvalidatePendingGridBuilds();
        UnregisterDeferredSpawnRetry();
        UnregisterPlacementModelRebuild();
        UnsubscribeModelChangedEvents();
        CleanupGrid();
        base.OnComponentDeactivated();
    }

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (AutoSequentialCaptureOnBeginPlay)
            RequestGridBuild(replaceExistingGrid: _spawnedNodes.Count > 0, restartSequentialCapture: true);
        else
            SpawnGrid();
    }

    protected override void OnEndPlay()
    {
        StopSequentialCapture("Idle");
        InvalidatePendingGridBuilds();
        UnregisterDeferredSpawnRetry();
        UnregisterPlacementModelRebuild();
        UnsubscribeModelChangedEvents();
        CleanupGrid();
        base.OnEndPlay();
    }

    protected override void OnDestroying()
    {
        StopSequentialCapture("Idle");
        InvalidatePendingGridBuilds();
        UnregisterDeferredSpawnRetry();
        UnregisterPlacementModelRebuild();
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

        IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
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

        RequestGridBuild(replaceExistingGrid: false, restartSequentialCapture: false);
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
        if (_isSequentialCaptureRunning)
            StopSequentialCapture("Rebuilding probe grid.");

        RequestGridBuild(replaceExistingGrid: true, restartSequentialCapture);
    }

    private void RequestGridBuild(bool replaceExistingGrid, bool restartSequentialCapture)
    {
        bool shouldStart;
        lock (_gridBuildSync)
        {
            _gridBuildRequestedVersion++;
            _gridBuildReplaceExistingRequested |= replaceExistingGrid;
            _gridBuildRestartCaptureRequested |= restartSequentialCapture;
            _gridBuildRequestsBlocked = false;
            shouldStart = !_gridBuildRunning;
        }

        if (shouldStart)
            StartNextGridBuild();
    }

    private void InvalidatePendingGridBuilds()
    {
        lock (_gridBuildSync)
        {
            _gridBuildRequestedVersion++;
            _gridBuildReplaceExistingRequested = false;
            _gridBuildRestartCaptureRequested = false;
            _gridBuildRequestsBlocked = true;
        }
    }

    private void StartNextGridBuild()
    {
        GridBuildRequest request;
        lock (_gridBuildSync)
        {
            if (_gridBuildRunning || _gridBuildRequestsBlocked)
                return;

            int version = _gridBuildRequestedVersion;
            bool replaceExistingGrid = _gridBuildReplaceExistingRequested;
            bool restartSequentialCapture = _gridBuildRestartCaptureRequested;
            _gridBuildReplaceExistingRequested = false;
            _gridBuildRestartCaptureRequested = false;

            if (!TryCreateGridBuildRequest(version, replaceExistingGrid, restartSequentialCapture, out request))
                return;

            _gridBuildRunning = true;
            _gridBuildRunningVersion = version;
        }

        Engine.Jobs.Schedule(new ActionJob(() =>
        {
            GridBuildResult? result = null;
            try
            {
                using var _ = Engine.Profiler.Start("LightProbeGrid.BuildPlacement");
                result = BuildGridPlacement(request);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "[LightProbeGrid] Background grid placement failed.");
            }

            Engine.EnqueueAppThreadTask(
                () => CompleteGridBuildOnAppThread(request.Version, result),
                "LightProbeGrid.ApplyBackgroundBuild");
        }), JobPriority.Low);
    }

    private bool TryCreateGridBuildRequest(
        int version,
        bool replaceExistingGrid,
        bool restartSequentialCapture,
        out GridBuildRequest request)
    {
        request = default;
        if (SceneNode is null)
            return false;

        Transform parentTransform = SceneNode.GetTransformAs<Transform>(true)!;
        IVector3 counts = ProbeCounts;
        IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
        SceneNode[] worldRootNodes = world is null ? [] : [.. world.RootNodes];

        request = new GridBuildRequest(
            version,
            replaceExistingGrid,
            restartSequentialCapture,
            Math.Max(1, counts.X),
            Math.Max(1, counts.Y),
            Math.Max(1, counts.Z),
            Spacing,
            Offset,
            parentTransform.WorldMatrix,
            parentTransform.InverseWorldMatrix,
            UsePlacementBoundsModels,
            [.. PlacementBoundsModels],
            PlacementBoundsPadding,
            AdjustProbePositionsAgainstGeometry,
            ProbeCollisionRadius,
            PushOutPadding,
            MaxPushOutDistance,
            MaxPushOutSteps,
            InfluenceSphereOuterRadius,
            worldRootNodes);
        return true;
    }

    private GridBuildResult BuildGridPlacement(GridBuildRequest request)
    {
        bool usePlacementBounds = TryGetPlacementBounds(request, out AABB placementBounds);
        if (request.UsePlacementBoundsModels && !usePlacementBounds)
        {
            return new GridBuildResult
            {
                Request = request,
                DeferredForPlacementBounds = true,
                LocalPositions = [],
                DiagnosticMessage = "[LightProbeGrid] SpawnGrid: model-based placement requested but no valid bounds yet - deferring spawn.",
            };
        }

        ProbeOccluder[] occluders = CollectProbeOccluders(request);
        Vector3 totalSize = new(
            (request.CountX - 1) * request.Spacing.X,
            (request.CountY - 1) * request.Spacing.Y,
            (request.CountZ - 1) * request.Spacing.Z);
        Vector3 start = -0.5f * totalSize + request.Offset;

        float? autoOuterRadius = null;
        if (usePlacementBounds)
        {
            Vector3 boundsSize = placementBounds.Max - placementBounds.Min;
            Vector3 cellSize = new(
                request.CountX > 1 ? boundsSize.X / (request.CountX - 1) : boundsSize.X,
                request.CountY > 1 ? boundsSize.Y / (request.CountY - 1) : boundsSize.Y,
                request.CountZ > 1 ? boundsSize.Z / (request.CountZ - 1) : boundsSize.Z);
            float candidateRadius = cellSize.Length();
            if (candidateRadius > request.CurrentInfluenceSphereOuterRadius)
                autoOuterRadius = candidateRadius;
        }

        int totalCount = checked(request.CountX * request.CountY * request.CountZ);
        Vector3[] localPositions = new Vector3[totalCount];
        int index = 0;
        for (int x = 0; x < request.CountX; ++x)
        {
            for (int y = 0; y < request.CountY; ++y)
            {
                for (int z = 0; z < request.CountZ; ++z)
                {
                    Vector3 localPos = usePlacementBounds
                        ? CalculatePlacementBoundsLocalPosition(placementBounds, request, x, y, z)
                        : start + new Vector3(x * request.Spacing.X, y * request.Spacing.Y, z * request.Spacing.Z);

                    localPositions[index++] = AdjustLocalProbePosition(request, localPos, occluders);
                }
            }
        }

        string diagnostic = usePlacementBounds
            ? $"[LightProbeGrid] SpawnGrid: using placement bounds {placementBounds} for {request.CountX}x{request.CountY}x{request.CountZ} grid."
            : $"[LightProbeGrid] SpawnGrid: using Spacing={request.Spacing}, Offset={request.Offset} for {request.CountX}x{request.CountY}x{request.CountZ} grid (start={start}).";

        return new GridBuildResult
        {
            Request = request,
            UsedPlacementBounds = usePlacementBounds,
            PlacementBounds = placementBounds,
            AutoInfluenceOuterRadius = autoOuterRadius,
            LocalPositions = localPositions,
            DiagnosticMessage = diagnostic,
        };
    }

    private void CompleteGridBuildOnAppThread(int version, GridBuildResult? result)
    {
        try
        {
            if (result is not null && version == _gridBuildRequestedVersion && !_gridBuildRequestsBlocked)
                ApplyGridBuildResult(result);
        }
        finally
        {
            FinishGridBuild(version);
        }
    }

    private void FinishGridBuild(int version)
    {
        bool shouldStartNext;
        lock (_gridBuildSync)
        {
            if (_gridBuildRunningVersion == version)
            {
                _gridBuildRunning = false;
                _gridBuildRunningVersion = 0;
            }

            shouldStartNext = !_gridBuildRequestsBlocked && _gridBuildRequestedVersion != version;
        }

        if (shouldStartNext)
            StartNextGridBuild();
    }

    private void ApplyGridBuildResult(GridBuildResult result)
    {
        GridBuildRequest request = result.Request;
        if (result.DeferredForPlacementBounds)
        {
            if (!string.IsNullOrEmpty(result.DiagnosticMessage))
                Debug.Out(result.DiagnosticMessage);

            RefreshPlacementModelSubscriptions();
            _deferredSpawnPending = true;
            RegisterDeferredSpawnRetry();
            return;
        }

        UnregisterDeferredSpawnRetry();
        if (result.UsedPlacementBounds)
            RefreshPlacementModelSubscriptions();
        else
            UnsubscribeModelChangedEvents();
        _deferredSpawnPending = false;

        if (request.ReplaceExistingGrid || _spawnedNodes.Count > 0)
            CleanupGrid();

        if (result.AutoInfluenceOuterRadius is float autoOuterRadius && autoOuterRadius > InfluenceSphereOuterRadius)
        {
            Debug.Out($"[LightProbeGrid] Auto-sizing influence outer radius: {InfluenceSphereOuterRadius:F2} -> {autoOuterRadius:F2} (cell diagonal).");
            SetField(ref _influenceSphereOuterRadius, autoOuterRadius);
        }

        if (!string.IsNullOrEmpty(result.DiagnosticMessage))
            Debug.Out(result.DiagnosticMessage);

        int index = 0;
        for (int x = 0; x < request.CountX; ++x)
        {
            for (int y = 0; y < request.CountY; ++y)
            {
                for (int z = 0; z < request.CountZ; ++z)
                {
                    Vector3 localPos = result.LocalPositions[index++];

                    // Create detached so AddComponent does not trigger activation
                    // before ApplyDefaults configures the probe.
                    var child = new SceneNode($"LightProbe[{x},{y},{z}]", new Transform(localPos));
                    var probe = child.AddComponent<LightProbeComponent>();
                    if (probe is not null)
                    {
                        ApplyDefaults(probe);
                        _spawnedProbes.Add(probe);
                    }
                    _spawnedNodes.Add(child);

                    // Parent into the tree after defaults are applied.
                    child.Transform.Parent = SceneNode.Transform;
                }
            }
        }

        _captureStatus = $"Ready: {_spawnedProbes.Count} probes.";

        if (request.RestartSequentialCapture && _spawnedNodes.Count > 0)
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

        IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
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
            IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
            Debug.Out($"[LightProbeBatch] Queueing {_sequentialCaptureIndex + 1}/{_spawnedProbes.Count} probe={probe.SceneNode.Name} queueDepth={world?.Lights.PendingCaptureWorkItemCount ?? 0} pendingComponents={world?.Lights.PendingCaptureComponentCount ?? 0}");
            probe.QueueCapture();
            return true;
        }

        return false;
    }

    private void StopSequentialCapture(string status)
    {
        bool wasRunning = _isSequentialCaptureRunning;
        IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
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

    private ProbeOccluder[] CollectProbeOccluders(GridBuildRequest request)
    {
        if (!request.AdjustProbePositionsAgainstGeometry || request.WorldRootNodes.Length == 0)
            return [];

        var occluders = new List<ProbeOccluder>();
        foreach (SceneNode root in request.WorldRootNodes)
        {
            ModelComponent[] models;
            try
            {
                models = [.. root.FindAllDescendantComponents<ModelComponent>()];
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LightProbeGrid] Skipped occluder root during background placement: {ex.Message}");
                continue;
            }

            foreach (ModelComponent model in models)
            {
                if (ReferenceEquals(model.SceneNode, SceneNode))
                    continue;

                RenderableMesh[] renderables = SnapshotRenderableMeshes(model);
                foreach (RenderableMesh rm in renderables)
                {
                    var renderer = rm.GetCurrentOrFirstLodRenderer();
                    if (renderer?.Mesh is { } m)
                        m.GenerateBVH();
                }

                if (renderables.Length > 0)
                    occluders.Add(new ProbeOccluder(renderables));
            }
        }

        return [.. occluders];
    }

    private static RenderableMesh[] SnapshotRenderableMeshes(ModelComponent model)
    {
        try
        {
            return [.. model.Meshes];
        }
        catch
        {
            return [];
        }
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
        Debug.Out("[LightProbeGrid] Placement model meshes changed - scheduling coalesced background grid rebuild.");
        SchedulePlacementModelRebuild(_spawnedNodes.Count > 0);
    }

    private void SchedulePlacementModelRebuild(bool replaceExistingGrid)
    {
        _placementModelRebuildPending = true;
        _placementModelRebuildReplaceExisting |= replaceExistingGrid;
        _lastPlacementModelChangedTimestamp = Stopwatch.GetTimestamp();

        if (_placementModelRebuildTickRegistered)
            return;

        RegisterTick(ETickGroup.Late, ETickOrder.Scene, TickPlacementModelRebuild);
        _placementModelRebuildTickRegistered = true;
    }

    private void UnregisterPlacementModelRebuild()
    {
        if (!_placementModelRebuildTickRegistered)
            return;

        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TickPlacementModelRebuild);
        _placementModelRebuildTickRegistered = false;
        _placementModelRebuildPending = false;
        _placementModelRebuildReplaceExisting = false;
        _lastPlacementModelChangedTimestamp = 0;
    }

    private void TickPlacementModelRebuild()
    {
        if (!_placementModelRebuildPending)
        {
            UnregisterPlacementModelRebuild();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastPlacementModelChangedTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastPlacementModelChangedTimestamp, now) < PlacementModelRebuildQuietPeriod)
        {
            return;
        }

        bool replaceExistingGrid = _placementModelRebuildReplaceExisting;
        _placementModelRebuildPending = false;
        _placementModelRebuildReplaceExisting = false;
        UnregisterPlacementModelRebuild();

        RequestGridBuild(replaceExistingGrid, restartSequentialCapture: false);
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
        lock (_gridBuildSync)
        {
            if (_gridBuildRunning)
                return;
        }

        Debug.Out("[LightProbeGrid] Deferred placement retry - scheduling background bounds check.");
        RequestGridBuild(replaceExistingGrid: false, restartSequentialCapture: false);
    }

    private static bool TryGetPlacementBounds(GridBuildRequest request, out AABB bounds, bool logDiagnostics = true)
    {
        bounds = default;
        if (!request.UsePlacementBoundsModels || request.PlacementBoundsModels.Length == 0)
        {
            if (logDiagnostics)
                Debug.Out($"[LightProbeGrid] TryGetPlacementBounds: skipped (enabled={request.UsePlacementBoundsModels}, models={request.PlacementBoundsModels.Length}).");
            return false;
        }

        bool found = false;
        int totalMeshes = 0;
        int validBounds = 0;
        foreach (ModelComponent model in request.PlacementBoundsModels)
        {
            if (model is null)
                continue;

            RenderableMesh[] meshes = SnapshotRenderableMeshes(model);
            totalMeshes += meshes.Length;
            foreach (RenderableMesh renderable in meshes)
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
                Debug.Out($"[LightProbeGrid] TryGetPlacementBounds: no valid bounds (models={request.PlacementBoundsModels.Length}, meshes={totalMeshes}, validBounds={validBounds}).");
            return false;
        }

        bounds = new AABB(bounds.Min - request.PlacementBoundsPadding, bounds.Max + request.PlacementBoundsPadding);
        if (logDiagnostics)
            Debug.Out($"[LightProbeGrid] TryGetPlacementBounds: success (models={request.PlacementBoundsModels.Length}, meshes={totalMeshes}, validBounds={validBounds}, bounds={bounds}).");
        return bounds.IsValid;
    }

    private static Vector3 CalculatePlacementBoundsLocalPosition(AABB bounds, GridBuildRequest request, int x, int y, int z)
    {
        Vector3 worldPosition = new(
            CalculatePlacementAxis(bounds.Min.X, bounds.Max.X, request.CountX, x),
            CalculatePlacementAxis(bounds.Min.Y, bounds.Max.Y, request.CountY, y),
            CalculatePlacementAxis(bounds.Min.Z, bounds.Max.Z, request.CountZ, z));

        return Vector3.Transform(worldPosition, request.ParentInverseWorldMatrix) + request.Offset;
    }

    private static float CalculatePlacementAxis(float min, float max, int count, int index)
    {
        if (count <= 1)
            return (min + max) * 0.5f;

        float t = index / (float)(count - 1);
        return min + (max - min) * t;
    }

    private Vector3 AdjustLocalProbePosition(GridBuildRequest request, Vector3 localPosition, ProbeOccluder[] occluders)
    {
        if (occluders.Length == 0)
            return localPosition;

        Vector3 originalWorld = Vector3.Transform(localPosition, request.ParentWorldMatrix);
        Vector3 adjustedWorld = originalWorld;
        bool moved = false;

        int maxPasses = Math.Min(4, request.MaxPushOutSteps);
        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool passMoved = false;
            foreach (ProbeOccluder occluder in occluders)
            {
                if (TryResolveAgainstModelBvh(request, occluder.Renderables, adjustedWorld, out Vector3 bvhAdjusted))
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
            return localPosition;

        return Vector3.Transform(adjustedWorld, request.ParentInverseWorldMatrix);
    }

    private bool TryResolveAgainstModelBvh(GridBuildRequest request, RenderableMesh[] renderables, Vector3 worldPosition, out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        float bestDistance = float.MaxValue;
        bool found = false;

        foreach (RenderableMesh renderable in renderables)
        {
            if (!renderable.TryGetWorldBounds(out AABB worldBounds))
                continue;

            Vector3 radius = new(request.ProbeCollisionRadius);
            AABB paddedBounds = new(worldBounds.Min - radius, worldBounds.Max + radius);
            if (!Contains(paddedBounds, worldPosition))
                continue;

            if (!TryFindMeshExit(request, renderable, worldPosition, out Vector3 candidate))
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

    private bool TryFindMeshExit(GridBuildRequest request, RenderableMesh renderable, Vector3 worldPosition, out Vector3 adjustedPosition)
    {
        adjustedPosition = worldPosition;
        if (!renderable.TryGetWorldBounds(out AABB worldBounds))
            return false;

        Vector3 boundsCenter = (worldBounds.Min + worldBounds.Max) * 0.5f;
        float bestDistance = float.MaxValue;
        bool found = false;

        foreach (Vector3 direction in EnumeratePushDirections(worldPosition, boundsCenter))
        {
            if (!TryFindMeshExitAlongDirection(request, renderable, worldPosition, direction, out Vector3 candidate))
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

    private bool TryFindMeshExitAlongDirection(GridBuildRequest request, RenderableMesh renderable, Vector3 worldPosition, Vector3 direction, out Vector3 adjustedPosition)
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
        Vector3 localFarPoint = Vector3.Transform(worldPosition + normalizedDirection * request.MaxPushOutDistance, worldToLocal);
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
        adjustedPosition = worldHitPoint + normalizedDirection * request.PushOutPadding;
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
