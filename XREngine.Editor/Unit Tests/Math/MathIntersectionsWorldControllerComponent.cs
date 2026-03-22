using System.Diagnostics;
using System.Numerics;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public delegate SceneNode MathIntersectionsWorldTestFactory(SceneNode parentNode, MathIntersectionsWorldControllerComponent? controller);

public sealed class MathIntersectionsWorldControllerComponent : XRComponent, IRenderable
{
    private const float TitleFontSize = 22.0f;
    private const float DescriptionFontSize = 18.0f;
    private const float LabelScale = 0.001f;
    private const float LabelHeightOffset = 0.85f;
    private const float TitleLineYOffset = 22.0f;
    private const float DescriptionLineYOffset = -20.0f;
    private const float SubLabelFontSize = 15.0f;
    private const float SubLabelScale = 0.00065f;
    private const int MaxBenchmarkCopies = 10000;
    private const float MaxBenchmarkDurationSeconds = 120.0f;

    private readonly List<MathIntersectionsWorldTestEntry> _tests = [];
    private readonly Dictionary<MathIntersectionsWorldTestEntry, MathIntersectionsWorldLabelSet> _labels = [];
    private readonly List<SubLabelDefinition> _subLabelDefs = [];
    private readonly Dictionary<SubLabelDefinition, SubLabelRenderable> _subLabels = [];
    private readonly List<double> _benchmarkFrameTimesMs = [];
    private readonly RenderInfo3D _renderInfo;
    private CustomUIComponent? _customUi;
    private FontGlyphSet? _titleLabelFont;
    private FontGlyphSet? _detailLabelFont;
    private SceneNode? _benchmarkRoot;
    private MathIntersectionsWorldTestEntry? _benchmarkEntry;
    private Stopwatch? _benchmarkStopwatch;
    private long _benchmarkLastTimestamp;
    private bool _benchmarkRunning;
    private bool _benchmarkRunToggle;
    private bool _benchmarkRunWithDebugDisplaysToggle;
    private bool _benchmarkIncludeDebugDisplays;
    private float _benchmarkCopyCount = 1000.0f;
    private float _benchmarkDurationSeconds = 10.0f;
    private string _benchmarkStatus = "Select one active test, then run a benchmark.";
    private int _benchmarkFrameCount;
    private double _benchmarkSpawnMilliseconds;
    private int _benchmarkActiveCopyCount;
    private float _benchmarkActiveDurationSeconds;
    private GPUPhysicsChainBandwidthSnapshot _benchmarkBandwidthStart;
    private bool _benchmarkSourceWasActive;
    private Queue<SceneNode>? _pendingBenchmarkTeardown;
    private SceneNode? _pendingBenchmarkTeardownRoot;
    private MathIntersectionsWorldTestEntry? _pendingBenchmarkRestoreEntry;
    private bool _pendingBenchmarkRestoreSourceWasActive;
    private const double MaxBenchmarkDestroyBudgetMs = 2.0;

    public RenderInfo RenderInfo => _renderInfo;
    public RenderInfo[] RenderedObjects { get; }

    public MathIntersectionsWorldControllerComponent()
    {
        RenderedObjects = [_renderInfo = RenderInfo3D.New(this, EDefaultRenderPass.OnTopForward, RenderLabels)];
        _renderInfo.Layer = XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex;
    }

    public void RegisterTest(SceneNode rootNode, string displayName, string description, AABB bounds, MathIntersectionsWorldTestFactory factory)
    {
        var entry = new MathIntersectionsWorldTestEntry(rootNode, displayName, description, bounds, rootNode.GetTransformAs<Transform>(true)!, factory);
        _tests.Add(entry);

        if (IsActiveInHierarchy)
        {
            RebuildUi();
            Relayout();
        }
    }

    public void RegisterSubLabel(SceneNode testRoot, Transform target, string text, float heightOffset = 2.8f)
    {
        _subLabelDefs.Add(new SubLabelDefinition(testRoot, target, text, heightOffset));
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Late, ETickOrder.Logic, UpdateBenchmark);
        _titleLabelFont ??= LoadTitleLabelFont();
        _detailLabelFont ??= LoadDetailLabelFont();

        _customUi = GetSiblingComponent<CustomUIComponent>(createIfNotExist: true);
        if (_customUi is not null)
        {
            _customUi.Name = "Math Intersections Test Controls";
            RebuildUi();
        }

        Relayout();
    }

    protected override void OnComponentDeactivated()
    {
        StopBenchmark(destroyInstances: true, updateStatus: false, cancelled: true);
        UnregisterTick(ETickGroup.Late, ETickOrder.Logic, UpdateBenchmark);
        _customUi?.ClearFields();
        _customUi = null;
        DestroyLabels();
        DestroySubLabels();
        base.OnComponentDeactivated();
    }

    private void RebuildUi()
    {
        if (_customUi is null)
            return;

        _customUi.ClearFields();

        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            MathIntersectionsWorldTestEntry capturedEntry = entry;
            _customUi.AddBoolField(
                capturedEntry.DisplayName,
                () => capturedEntry.RootNode.IsActiveSelf,
                value => SetTestEnabled(capturedEntry, value),
                $"Toggle the {capturedEntry.DisplayName} math intersection test.");
        }

        _customUi.AddFloatField(
            "Benchmark Copies",
            () => _benchmarkCopyCount,
            value => _benchmarkCopyCount = Math.Clamp(MathF.Round(value), 1.0f, MaxBenchmarkCopies),
            1.0f,
            MaxBenchmarkCopies,
            1.0f,
            "%.0f",
            "Number of copies to spawn for the selected benchmark run.");
        _customUi.AddFloatField(
            "Benchmark Duration (s)",
            () => _benchmarkDurationSeconds,
            value => _benchmarkDurationSeconds = Math.Clamp(value, 0.5f, MaxBenchmarkDurationSeconds),
            0.5f,
            MaxBenchmarkDurationSeconds,
            0.5f,
            "%.1f",
            "Wall-clock duration to keep the benchmark copies alive before teardown.");
        _customUi.AddBoolField(
            "Run Benchmark",
            () => _benchmarkRunToggle,
            value => SetBenchmarkRunToggle(value, includeDebugDisplays: false),
            "Enable this with exactly one active test to spawn benchmark copies with debug visuals disabled. Disable it to cancel the active run.");
        _customUi.AddBoolField(
            "Run Benchmark With Debug Displays",
            () => _benchmarkRunWithDebugDisplaysToggle,
            value => SetBenchmarkRunToggle(value, includeDebugDisplays: true),
            "Enable this with exactly one active test to spawn benchmark copies while keeping debug visuals enabled. Disable it to cancel the active run.");
        _customUi.AddTextField(
            "Benchmark Status",
            () => _benchmarkStatus,
            "Latest benchmark summary, including spawn cost and frame-time statistics.");
    }

    private void SetTestEnabled(MathIntersectionsWorldTestEntry entry, bool enabled)
    {
        entry.RootNode.IsActiveSelf = enabled;
        Relayout();
    }

    private void SetBenchmarkRunToggle(bool enabled, bool includeDebugDisplays)
    {
        _benchmarkRunToggle = enabled && !includeDebugDisplays;
        _benchmarkRunWithDebugDisplaysToggle = enabled && includeDebugDisplays;
        if (enabled)
        {
            FlushPendingBenchmarkTeardown();
            TryStartBenchmark(includeDebugDisplays);
        }
        else if (_benchmarkRunning)
            StopBenchmark(destroyInstances: true, updateStatus: true, cancelled: true);
    }

    private void FlushPendingBenchmarkTeardown()
    {
        if (_pendingBenchmarkTeardown is null)
            return;

        while (_pendingBenchmarkTeardown.TryDequeue(out SceneNode? child))
            child.Destroy(true);

        _pendingBenchmarkTeardownRoot?.Destroy(true);
        CompletePendingBenchmarkTeardown();
    }

    private void TryStartBenchmark(bool includeDebugDisplays)
    {
        if (_benchmarkRunning)
            return;

        List<MathIntersectionsWorldTestEntry> activeEntries = [];
        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            if (entry.RootNode.IsActiveSelf)
                activeEntries.Add(entry);
        }

        if (activeEntries.Count != 1)
        {
            _benchmarkRunToggle = false;
            _benchmarkRunWithDebugDisplaysToggle = false;
            _benchmarkStatus = $"Benchmark requires exactly one active test; found {activeEntries.Count}.";
            return;
        }

        StopBenchmark(destroyInstances: true, updateStatus: false, cancelled: false);

        _benchmarkEntry = activeEntries[0];
        _benchmarkFrameCount = 0;
        _benchmarkFrameTimesMs.Clear();
        _benchmarkBandwidthStart = GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot();

        int copyCount = GetBenchmarkCopyCount();
        float durationSeconds = Math.Clamp(_benchmarkDurationSeconds, 0.5f, MaxBenchmarkDurationSeconds);

        var benchmarkRoot = SceneNode.NewChild($"{_benchmarkEntry.DisplayName} Benchmark Instances");
        benchmarkRoot.SetTransform<Transform>();

        Stopwatch spawnStopwatch = Stopwatch.StartNew();
        SpawnBenchmarkInstances(benchmarkRoot, _benchmarkEntry, copyCount, includeDebugDisplays);
        spawnStopwatch.Stop();

        _benchmarkSourceWasActive = _benchmarkEntry.RootNode.IsActiveSelf;
        _benchmarkEntry.RootNode.IsActiveSelf = false;
        Relayout();

        _benchmarkRoot = benchmarkRoot;
        _benchmarkIncludeDebugDisplays = includeDebugDisplays;
        _benchmarkSpawnMilliseconds = spawnStopwatch.Elapsed.TotalMilliseconds;
        _benchmarkActiveCopyCount = copyCount;
        _benchmarkActiveDurationSeconds = durationSeconds;
        _benchmarkStopwatch = Stopwatch.StartNew();
        _benchmarkLastTimestamp = Stopwatch.GetTimestamp();
        _benchmarkRunning = true;
        _benchmarkStatus = $"Running {_benchmarkEntry.DisplayName}: {copyCount} copies for {durationSeconds:0.0}s (debug displays {(includeDebugDisplays ? "enabled" : "disabled")}).";
    }

    private void UpdateBenchmark()
    {
        ProcessPendingBenchmarkTeardown();

        if (!_benchmarkRunning || _benchmarkStopwatch is null)
            return;

        long nowTimestamp = Stopwatch.GetTimestamp();
        if (_benchmarkLastTimestamp != 0)
        {
            double frameMilliseconds = (nowTimestamp - _benchmarkLastTimestamp) * 1000.0 / Stopwatch.Frequency;
            _benchmarkFrameTimesMs.Add(frameMilliseconds);
            _benchmarkFrameCount++;
        }

        _benchmarkLastTimestamp = nowTimestamp;
        if (_benchmarkStopwatch.Elapsed.TotalSeconds >= _benchmarkActiveDurationSeconds)
            StopBenchmark(destroyInstances: true, updateStatus: true, cancelled: false);
    }

    private void ProcessPendingBenchmarkTeardown()
    {
        if (_pendingBenchmarkTeardown is null)
            return;

        Stopwatch budget = Stopwatch.StartNew();
        while (budget.Elapsed.TotalMilliseconds < MaxBenchmarkDestroyBudgetMs
            && _pendingBenchmarkTeardown.TryDequeue(out SceneNode? child))
        {
            child.Destroy(true);
        }

        if (_pendingBenchmarkTeardown.Count == 0)
        {
            _pendingBenchmarkTeardownRoot?.Destroy(true);
            CompletePendingBenchmarkTeardown();
        }
    }

    private void SpawnBenchmarkInstances(SceneNode benchmarkRoot, MathIntersectionsWorldTestEntry entry, int copyCount, bool includeDebugDisplays)
    {
        Vector3 size = entry.Bounds.Size;
        float cellWidth = MathF.Max(size.X, 1.0f) + 2.0f;
        float cellDepth = MathF.Max(size.Z, 1.0f) + 2.0f;
        int columns = (int)MathF.Ceiling(MathF.Sqrt(copyCount));

        for (int index = 0; index < copyCount; index++)
        {
            int row = index / columns;
            int column = index % columns;
            SceneNode instanceRoot = entry.Factory(benchmarkRoot, null);
            instanceRoot.IsActiveSelf = true;
            instanceRoot.Name = $"{entry.DisplayName} Benchmark {index + 1}";
            SyncBenchmarkInstanceSettings(entry.RootNode, instanceRoot, includeDebugDisplays);
            Transform instanceTransform = instanceRoot.GetTransformAs<Transform>(true) ?? instanceRoot.SetTransform<Transform>();
            Vector3 desiredCenter = new(
                column * cellWidth,
                0.0f,
                row * cellDepth);
            CenterWithinCell(instanceTransform, entry.Bounds, desiredCenter);
        }

        Transform benchmarkTransform = benchmarkRoot.GetTransformAs<Transform>(true) ?? benchmarkRoot.SetTransform<Transform>();
        benchmarkTransform.Translation = new Vector3(
            -((columns - 1) * cellWidth) * 0.5f,
            0.0f,
            -((MathF.Ceiling(copyCount / (float)columns) - 1) * cellDepth) * 0.5f);
    }

    private void StopBenchmark(bool destroyInstances, bool updateStatus, bool cancelled)
    {
        if (!_benchmarkRunning && _benchmarkRoot is null)
        {
            _benchmarkRunToggle = false;
            return;
        }

        Stopwatch? stopwatch = _benchmarkStopwatch;
        MathIntersectionsWorldTestEntry? entry = _benchmarkEntry;
        int copyCount = _benchmarkActiveCopyCount;
        double elapsedSeconds = stopwatch?.Elapsed.TotalSeconds ?? 0.0;
        bool includeDebugDisplays = _benchmarkIncludeDebugDisplays;
        bool sourceRigWasActive = _benchmarkSourceWasActive;

        Stopwatch? destroyStopwatch = null;
        if (destroyInstances && _benchmarkRoot is not null)
        {
            destroyStopwatch = Stopwatch.StartNew();
            BeginGradualBenchmarkTeardown(_benchmarkRoot, entry, sourceRigWasActive);
            destroyStopwatch.Stop();
        }

        _benchmarkRoot = null;
        _benchmarkEntry = null;
        _benchmarkStopwatch = null;
        _benchmarkRunning = false;
        _benchmarkRunToggle = false;
        _benchmarkRunWithDebugDisplaysToggle = false;
        _benchmarkIncludeDebugDisplays = false;
        _benchmarkLastTimestamp = 0;
        _benchmarkActiveCopyCount = 0;
        _benchmarkActiveDurationSeconds = 0.0f;

        _benchmarkSourceWasActive = false;

        if (!destroyInstances || _pendingBenchmarkTeardown is null)
            RestoreSourceRigAfterBenchmark();

        if (!updateStatus)
            return;

        if (cancelled)
        {
            _benchmarkStatus = entry is null
                ? "Benchmark cancelled."
                : $"Benchmark cancelled for {entry.DisplayName}.";
            return;
        }

        if (_benchmarkFrameTimesMs.Count == 0)
        {
            _benchmarkStatus = entry is null
                ? "Benchmark completed with no frame samples recorded."
                : $"{entry.DisplayName}: completed with no frame samples recorded.";
            return;
        }

        _benchmarkFrameTimesMs.Sort();
        double minMs = _benchmarkFrameTimesMs[0];
        double maxMs = _benchmarkFrameTimesMs[^1];
        double avgMs = 0.0;
        foreach (double sample in _benchmarkFrameTimesMs)
            avgMs += sample;
        avgMs /= _benchmarkFrameTimesMs.Count;

        int p95Index = Math.Clamp((int)Math.Ceiling(_benchmarkFrameTimesMs.Count * 0.95) - 1, 0, _benchmarkFrameTimesMs.Count - 1);
        double p95Ms = _benchmarkFrameTimesMs[p95Index];
        double destroyMilliseconds = destroyStopwatch?.Elapsed.TotalMilliseconds ?? 0.0;
        GPUPhysicsChainBandwidthSnapshot bandwidthDelta = GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot().Delta(_benchmarkBandwidthStart);
        double uploadMiB = bandwidthDelta.CpuUploadBytes / (1024.0 * 1024.0);
        double gpuCopyMiB = bandwidthDelta.GpuCopyBytes / (1024.0 * 1024.0);
        double readbackMiB = bandwidthDelta.CpuReadbackBytes / (1024.0 * 1024.0);
        double standaloneUploadMiB = bandwidthDelta.StandaloneCpuUploadBytes / (1024.0 * 1024.0);
        double standaloneReadbackMiB = bandwidthDelta.StandaloneCpuReadbackBytes / (1024.0 * 1024.0);
        double batchedUploadMiB = bandwidthDelta.BatchedCpuUploadBytes / (1024.0 * 1024.0);
        double batchedGpuCopyMiB = bandwidthDelta.BatchedGpuCopyBytes / (1024.0 * 1024.0);
        double batchedReadbackMiB = bandwidthDelta.BatchedCpuReadbackBytes / (1024.0 * 1024.0);
        double totalMiB = bandwidthDelta.TotalTransferBytes / (1024.0 * 1024.0);
        double elapsedForRate = Math.Max(elapsedSeconds, 1e-6);
        double transferMiBPerSecond = totalMiB / elapsedForRate;
        double hierarchyRecalcMs = bandwidthDelta.HierarchyRecalcMilliseconds;
        string name = entry?.DisplayName ?? "Benchmark";
        _benchmarkStatus =
            $"{name}: {copyCount} copies, {elapsedSeconds:0.00}s, spawn {_benchmarkSpawnMilliseconds:0.00}ms, destroy {destroyMilliseconds:0.00}ms, " +
            $"frames {_benchmarkFrameCount}, avg {avgMs:0.###}ms, p95 {p95Ms:0.###}ms, min {minMs:0.###}ms, max {maxMs:0.###}ms, " +
            $"debug displays {(includeDebugDisplays ? "on" : "off")}, " +
            $"bandwidth up {uploadMiB:0.###} MiB, gpu-copy {gpuCopyMiB:0.###} MiB, readback {readbackMiB:0.###} MiB, total {totalMiB:0.###} MiB ({transferMiBPerSecond:0.###} MiB/s), " +
            $"standalone up {standaloneUploadMiB:0.###} MiB, standalone readback {standaloneReadbackMiB:0.###} MiB, batched up {batchedUploadMiB:0.###} MiB, batched gpu-copy {batchedGpuCopyMiB:0.###} MiB, batched readback {batchedReadbackMiB:0.###} MiB, hierarchy recalc {hierarchyRecalcMs:0.###}ms.";

        string logDirectory = XREngine.Debug.EnsureLogRunDirectory();
        XREngine.Debug.WriteAuxiliaryLog(
            "math-intersections-benchmarks.log",
            $"[{DateTimeOffset.Now:O}] Benchmark='{name}' Copies={copyCount} DurationSeconds={elapsedSeconds:0.000} SpawnMs={_benchmarkSpawnMilliseconds:0.###} DestroyMs={destroyMilliseconds:0.###} Frames={_benchmarkFrameCount} AvgFrameMs={avgMs:0.###} P95FrameMs={p95Ms:0.###} MinFrameMs={minMs:0.###} MaxFrameMs={maxMs:0.###} DebugDisplays={includeDebugDisplays} SourceRigDisabled={sourceRigWasActive} CpuUploadBytes={bandwidthDelta.CpuUploadBytes} GpuCopyBytes={bandwidthDelta.GpuCopyBytes} CpuReadbackBytes={bandwidthDelta.CpuReadbackBytes} StandaloneCpuUploadBytes={bandwidthDelta.StandaloneCpuUploadBytes} StandaloneCpuReadbackBytes={bandwidthDelta.StandaloneCpuReadbackBytes} BatchedCpuUploadBytes={bandwidthDelta.BatchedCpuUploadBytes} BatchedGpuCopyBytes={bandwidthDelta.BatchedGpuCopyBytes} BatchedCpuReadbackBytes={bandwidthDelta.BatchedCpuReadbackBytes} HierarchyRecalcMs={hierarchyRecalcMs:0.###} TotalTransferBytes={bandwidthDelta.TotalTransferBytes} DispatchGroups={bandwidthDelta.DispatchGroupCount} DispatchIterations={bandwidthDelta.DispatchIterationCount} ResidentParticleBytes={bandwidthDelta.ResidentParticleBytes} LogDirectory='{logDirectory}'");
    }

    private int GetBenchmarkCopyCount()
        => Math.Clamp((int)MathF.Round(_benchmarkCopyCount), 1, MaxBenchmarkCopies);

    private void BeginGradualBenchmarkTeardown(SceneNode benchmarkRoot, MathIntersectionsWorldTestEntry? sourceEntry, bool sourceRigWasActive)
    {
        // Deactivate immediately — this stops all component ticking, removes
        // renderables from the visual scene, and prevents further GPU dispatcher
        // submissions. The dispatcher’s SubmitData snapshots list data into
        // request-owned arrays, so any in-flight render-thread dispatch safely
        // iterates stale snapshots rather than the component’s live lists.
        benchmarkRoot.IsActiveSelf = false;
        // Safety net: clear any ticks registered directly on SceneNodes in the
        // benchmark subtree. Component ticks are auto-cleared by OnComponentDeactivated,
        // but SceneNode-level ticks survive deactivation and would continue running
        // as undead per-frame work until the node is destroyed.
        ClearSceneNodeTicks(benchmarkRoot);
        // Collect direct children for gradual per-frame destruction.
        // Each child is already deactivated (cascaded from the root), so
        // Destroy(true) is cheap — no component activation or GPU work.
        var children = new Queue<SceneNode>();
        if (benchmarkRoot.Transform is { } rootTransform)
        {
            foreach (TransformBase childTransform in rootTransform.Children)
            {
                if (childTransform.SceneNode is SceneNode childNode)
                    children.Enqueue(childNode);
            }
        }

        _pendingBenchmarkTeardown = children;
        _pendingBenchmarkTeardownRoot = benchmarkRoot;
        _pendingBenchmarkRestoreEntry = sourceEntry;
        _pendingBenchmarkRestoreSourceWasActive = sourceRigWasActive;
    }

    private void CompletePendingBenchmarkTeardown()
    {
        _pendingBenchmarkTeardownRoot = null;
        _pendingBenchmarkTeardown = null;
        RestoreSourceRigAfterBenchmark();
    }

    private void RestoreSourceRigAfterBenchmark()
    {
        if (_pendingBenchmarkRestoreEntry is { } entry && _pendingBenchmarkRestoreSourceWasActive)
            entry.RootNode.IsActiveSelf = true;

        _pendingBenchmarkRestoreEntry = null;
        _pendingBenchmarkRestoreSourceWasActive = false;
        Relayout();
    }

    private static void DisableBenchmarkDebugVisuals(SceneNode instanceRoot)
    {
        instanceRoot.IterateComponents<PhysicsChainComponent>(component => component.DebugDrawChains = false, true);
        instanceRoot.IterateComponents<DebugDrawComponent>(component => component.IsActive = false, true);
    }

    /// <summary>
    /// Clears ticks registered directly on SceneNodes in the hierarchy.
    /// SceneNode-level ticks are NOT auto-cleared on deactivation (only component
    /// ticks are), so this prevents undead per-frame work when benchmark copies
    /// are deactivated but not yet destroyed.
    /// </summary>
    private static void ClearSceneNodeTicks(SceneNode root)
        => root.IterateHierarchy(static node => node.ClearTicks());

    private static void SyncBenchmarkInstanceSettings(SceneNode sourceRoot, SceneNode targetRoot, bool includeDebugDisplays)
    {
        SyncPhysicsChainComponents(sourceRoot, targetRoot, includeDebugDisplays);
        SyncDebugDrawComponents(sourceRoot, targetRoot, includeDebugDisplays);
        SyncSphereColliders(sourceRoot, targetRoot);
        SyncCapsuleColliders(sourceRoot, targetRoot);
        SyncBoxColliders(sourceRoot, targetRoot);
        SyncPlaneColliders(sourceRoot, targetRoot);

        if (!includeDebugDisplays)
            DisableBenchmarkDebugVisuals(targetRoot);
    }

    private static void SyncPhysicsChainComponents(SceneNode sourceRoot, SceneNode targetRoot, bool includeDebugDisplays)
    {
        List<PhysicsChainComponent> sourceComponents = CollectComponents<PhysicsChainComponent>(sourceRoot);
        List<PhysicsChainComponent> targetComponents = CollectComponents<PhysicsChainComponent>(targetRoot);
        int count = Math.Min(sourceComponents.Count, targetComponents.Count);
        for (int index = 0; index < count; index++)
        {
            PhysicsChainComponent source = sourceComponents[index];
            PhysicsChainComponent target = targetComponents[index];
            target.IsActive = source.IsActive;
            target.UpdateRate = source.UpdateRate;
            target.Speed = source.Speed;
            target.UpdateMode = source.UpdateMode;
            target.InterpolationMode = source.InterpolationMode;
            target.Damping = source.Damping;
            target.DampingDistrib = source.DampingDistrib;
            target.Elasticity = source.Elasticity;
            target.ElasticityDistrib = source.ElasticityDistrib;
            target.Stiffness = source.Stiffness;
            target.StiffnessDistrib = source.StiffnessDistrib;
            target.Inert = source.Inert;
            target.InertDistrib = source.InertDistrib;
            target.Friction = source.Friction;
            target.FrictionDistrib = source.FrictionDistrib;
            target.Radius = source.Radius;
            target.RadiusDistrib = source.RadiusDistrib;
            target.EndLength = source.EndLength;
            target.EndOffset = source.EndOffset;
            target.Gravity = source.Gravity;
            target.Force = source.Force;
            target.BlendWeight = source.BlendWeight;
            target.FreezeAxis = source.FreezeAxis;
            target.DistantDisable = source.DistantDisable;
            target.DistanceToObject = source.DistanceToObject;
            target.GpuSyncToBones = source.GpuSyncToBones;
            target.Multithread = source.Multithread;
            target.UseGPU = source.UseGPU;
            target.UseBatchedDispatcher = source.UseBatchedDispatcher;
            target.DebugDrawChains = includeDebugDisplays && source.DebugDrawChains;
            target.RootInertia = source.RootInertia;
            target.VelocitySmoothing = source.VelocitySmoothing;
        }
    }

    private static void SyncDebugDrawComponents(SceneNode sourceRoot, SceneNode targetRoot, bool includeDebugDisplays)
    {
        List<DebugDrawComponent> sourceComponents = CollectComponents<DebugDrawComponent>(sourceRoot);
        List<DebugDrawComponent> targetComponents = CollectComponents<DebugDrawComponent>(targetRoot);
        int count = Math.Min(sourceComponents.Count, targetComponents.Count);
        for (int index = 0; index < count; index++)
            targetComponents[index].IsActive = includeDebugDisplays && sourceComponents[index].IsActive;
    }

    private static void SyncSphereColliders(SceneNode sourceRoot, SceneNode targetRoot)
    {
        List<PhysicsChainSphereCollider> sourceComponents = CollectComponents<PhysicsChainSphereCollider>(sourceRoot);
        List<PhysicsChainSphereCollider> targetComponents = CollectComponents<PhysicsChainSphereCollider>(targetRoot);
        int count = Math.Min(sourceComponents.Count, targetComponents.Count);
        for (int index = 0; index < count; index++)
        {
            PhysicsChainSphereCollider source = sourceComponents[index];
            PhysicsChainSphereCollider target = targetComponents[index];
            SyncColliderBaseSettings(source, target);
            target.Radius = source.Radius;
        }
    }

    private static void SyncCapsuleColliders(SceneNode sourceRoot, SceneNode targetRoot)
    {
        List<PhysicsChainCapsuleCollider> sourceComponents = CollectComponents<PhysicsChainCapsuleCollider>(sourceRoot);
        List<PhysicsChainCapsuleCollider> targetComponents = CollectComponents<PhysicsChainCapsuleCollider>(targetRoot);
        int count = Math.Min(sourceComponents.Count, targetComponents.Count);
        for (int index = 0; index < count; index++)
        {
            PhysicsChainCapsuleCollider source = sourceComponents[index];
            PhysicsChainCapsuleCollider target = targetComponents[index];
            SyncColliderBaseSettings(source, target);
            target.Radius = source.Radius;
            target.Height = source.Height;
        }
    }

    private static void SyncBoxColliders(SceneNode sourceRoot, SceneNode targetRoot)
    {
        List<PhysicsChainBoxCollider> sourceComponents = CollectComponents<PhysicsChainBoxCollider>(sourceRoot);
        List<PhysicsChainBoxCollider> targetComponents = CollectComponents<PhysicsChainBoxCollider>(targetRoot);
        int count = Math.Min(sourceComponents.Count, targetComponents.Count);
        for (int index = 0; index < count; index++)
        {
            PhysicsChainBoxCollider source = sourceComponents[index];
            PhysicsChainBoxCollider target = targetComponents[index];
            SyncColliderBaseSettings(source, target);
            target.Size = source.Size;
        }
    }

    private static void SyncPlaneColliders(SceneNode sourceRoot, SceneNode targetRoot)
    {
        List<PhysicsChainPlaneCollider> sourceComponents = CollectComponents<PhysicsChainPlaneCollider>(sourceRoot);
        List<PhysicsChainPlaneCollider> targetComponents = CollectComponents<PhysicsChainPlaneCollider>(targetRoot);
        int count = Math.Min(sourceComponents.Count, targetComponents.Count);
        for (int index = 0; index < count; index++)
        {
            PhysicsChainPlaneCollider source = sourceComponents[index];
            PhysicsChainPlaneCollider target = targetComponents[index];
            SyncColliderBaseSettings(source, target);
        }
    }

    private static void SyncColliderBaseSettings(PhysicsChainColliderBase source, PhysicsChainColliderBase target)
    {
        target.IsActive = source.IsActive;
        target._direction = source._direction;
        target._center = source._center;
        target._bound = source._bound;
    }

    private static List<T> CollectComponents<T>(SceneNode root) where T : XRComponent
    {
        List<T> components = [];
        root.IterateComponents<T>(components.Add, true);
        return components;
    }

    private void Relayout()
    {
        if (_tests.Count == 0)
            return;

        List<MathIntersectionsWorldTestEntry> activeTests = [];
        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            if (entry.RootNode.IsActiveSelf)
                activeTests.Add(entry);
        }

        if (activeTests.Count == 0)
            return;

        if (activeTests.Count == 1)
        {
            CenterAtOrigin(activeTests[0]);
            UpdateLabelPlacement(activeTests[0]);
            return;
        }

        float cellWidth = 0.0f;
        float cellDepth = 0.0f;
        foreach (MathIntersectionsWorldTestEntry entry in activeTests)
        {
            Vector3 size = entry.Bounds.Size;
            cellWidth = MathF.Max(cellWidth, size.X);
            cellDepth = MathF.Max(cellDepth, size.Z);
        }

        const float cellPadding = 4.0f;
        cellWidth += cellPadding;
        cellDepth += cellPadding;

        int columns = (int)MathF.Ceiling(MathF.Sqrt(activeTests.Count));
        int rows = (int)MathF.Ceiling(activeTests.Count / (float)columns);
        float startX = -((columns - 1) * cellWidth) * 0.5f;
        float startZ = -((rows - 1) * cellDepth) * 0.5f;

        for (int index = 0; index < activeTests.Count; index++)
        {
            int row = index / columns;
            int column = index % columns;
            Vector3 desiredCenter = new(
                startX + column * cellWidth,
                0.0f,
                startZ + row * cellDepth);

            CenterWithinCell(activeTests[index], desiredCenter);
            UpdateLabelPlacement(activeTests[index]);
        }
    }

    private static void CenterAtOrigin(MathIntersectionsWorldTestEntry entry)
        => CenterWithinCell(entry.RootTransform, entry.Bounds, Vector3.Zero);

    private static void CenterWithinCell(MathIntersectionsWorldTestEntry entry, Vector3 desiredCenter)
        => CenterWithinCell(entry.RootTransform, entry.Bounds, desiredCenter);

    private static void CenterWithinCell(Transform rootTransform, AABB bounds, Vector3 desiredCenter)
    {
        Vector3 boundsCenter = bounds.Center;
        rootTransform.Translation = new Vector3(
            desiredCenter.X - boundsCenter.X,
            0.0f,
            desiredCenter.Z - boundsCenter.Z);
    }

    private void RenderLabels()
    {
        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            if (!entry.RootNode.IsActiveSelf)
                continue;

            MathIntersectionsWorldLabelSet labels = EnsureLabels(entry);
            UpdateLabelPlacement(entry);
            labels.Render();
        }

        foreach (SubLabelDefinition def in _subLabelDefs)
        {
            if (!def.TestRoot.IsActiveSelf)
                continue;

            SubLabelRenderable subLabel = EnsureSubLabel(def);
            subLabel.Render();
        }
    }

    private MathIntersectionsWorldLabelSet EnsureLabels(MathIntersectionsWorldTestEntry entry)
    {
        if (_labels.TryGetValue(entry, out MathIntersectionsWorldLabelSet? labels))
            return labels;

        FontGlyphSet titleFont = _titleLabelFont ??= LoadTitleLabelFont();
        FontGlyphSet detailFont = _detailLabelFont ??= LoadDetailLabelFont();
        float titleWidth = titleFont.MeasureString(entry.DisplayName, TitleFontSize).X;
        float descriptionWidth = detailFont.MeasureString(entry.Description, DescriptionFontSize).X;
        float blockWidth = MathF.Max(titleWidth, descriptionWidth);

        labels = new MathIntersectionsWorldLabelSet(
            CreateLabelText(entry.RootTransform, titleFont, entry.DisplayName, TitleFontSize, LabelScale, ColorF4.White, ColorF4.Transparent, 0.0f, blockWidth, TitleLineYOffset),
            CreateLabelText(entry.RootTransform, detailFont, entry.Description, DescriptionFontSize, LabelScale, new ColorF4(0.85f, 0.85f, 0.85f, 1.0f), ColorF4.Transparent, 0.0f, blockWidth, DescriptionLineYOffset));

        _labels.Add(entry, labels);
        return labels;
    }

    private SubLabelRenderable EnsureSubLabel(SubLabelDefinition def)
    {
        if (_subLabels.TryGetValue(def, out SubLabelRenderable? subLabel))
            return subLabel;

        FontGlyphSet font = _detailLabelFont ??= LoadDetailLabelFont();
        subLabel = new SubLabelRenderable(
            CreateSubLabelText(def.Target, font, def.Text, SubLabelScale, new ColorF4(0.9f, 0.9f, 0.8f, 1.0f), ColorF4.Transparent, 0.0f, def.HeightOffset));

        _subLabels.Add(def, subLabel);
        return subLabel;
    }

    private static FontGlyphSet LoadTitleLabelFont()
        => FontGlyphSet.LoadEngineFont(
            Engine.Rendering.Settings.DefaultFontFolder,
            Engine.Rendering.Settings.DefaultFontFileName,
            new XRFontImportOptions
            {
                AtlasMode = EFontAtlasImportMode.Mtsdf,
                MsdfFontSize = 192.0f,
                MsdfPixelRange = 16.0f,
                MsdfInnerPixelPadding = 2.0f,
                MsdfOuterPixelPadding = 4.0f,
            });

    private static FontGlyphSet LoadDetailLabelFont()
        => FontGlyphSet.LoadEngineFont(
            Engine.Rendering.Settings.DefaultFontFolder,
            Engine.Rendering.Settings.DefaultFontFileName,
            new XRFontImportOptions
            {
                AtlasMode = EFontAtlasImportMode.Mtsdf,
                MsdfFontSize = 160.0f,
                MsdfPixelRange = 14.0f,
                MsdfInnerPixelPadding = 2.0f,
                MsdfOuterPixelPadding = 4.0f,
            });

    private static UIText CreateLabelText(Transform textTransform, FontGlyphSet font, string content, float fontSize, float scale, ColorF4 color, ColorF4 outlineColor, float outlineThickness, float blockWidth, float yOffset)
    {
        var text = new UIText
        {
            TextTransform = textTransform,
            Text = content,
            Font = font,
            FontSize = fontSize,
            AnimatableTransforms = true,
            Scale = scale,
            Color = color,
            OutlineColor = outlineColor,
            OutlineThickness = outlineThickness,
            RenderPass = (int)EDefaultRenderPass.OnTopForward,
        };

        PositionText(text, font, content, fontSize, blockWidth, yOffset);
        return text;
    }

    private static UIText CreateSubLabelText(Transform textTransform, FontGlyphSet font, string content, float scale, ColorF4 color, ColorF4 outlineColor, float outlineThickness, float heightOffset)
    {
        var text = new UIText
        {
            TextTransform = textTransform,
            Text = content,
            Font = font,
            FontSize = SubLabelFontSize,
            AnimatableTransforms = true,
            Scale = scale,
            Color = color,
            OutlineColor = outlineColor,
            OutlineThickness = outlineThickness,
            RenderPass = (int)EDefaultRenderPass.OnTopForward,
            LocalTranslation = new Vector3(0.0f, heightOffset, 0.0f),
        };

        float width = font.MeasureString(content, SubLabelFontSize).X;
        PositionText(text, font, content, SubLabelFontSize, width, 0.0f);
        return text;
    }

    private static void PositionText(UIText text, FontGlyphSet font, string content, float fontSize, float blockWidth, float yOffset)
    {
        Dictionary<int, (Vector2 translation, Vector2 scale, float rotation)> glyphOffsets = [];
        Vector2 translation = new(-blockWidth * 0.5f, yOffset);
        for (int i = 0; i < content.Length; i++)
            glyphOffsets[i] = (translation, Vector2.One, 0.0f);

        text.GlyphRelativeTransforms = glyphOffsets;
    }

    private void UpdateLabelPlacement(MathIntersectionsWorldTestEntry entry)
    {
        if (!_labels.TryGetValue(entry, out MathIntersectionsWorldLabelSet? labels))
            return;

        Vector3 labelPosition = new(
            entry.Bounds.Center.X,
            entry.Bounds.Max.Y + LabelHeightOffset,
            entry.Bounds.Center.Z);

        labels.Title.LocalTranslation = labelPosition;
        labels.Description.LocalTranslation = labelPosition;
    }

    private void DestroyLabels()
    {
        foreach (MathIntersectionsWorldLabelSet labels in _labels.Values)
            labels.Destroy();

        _labels.Clear();
    }

    private void DestroySubLabels()
    {
        foreach (SubLabelRenderable subLabel in _subLabels.Values)
            subLabel.Destroy();

        _subLabels.Clear();
    }

    private sealed record MathIntersectionsWorldTestEntry(SceneNode RootNode, string DisplayName, string Description, AABB Bounds, Transform RootTransform, MathIntersectionsWorldTestFactory Factory);

    private sealed record SubLabelDefinition(SceneNode TestRoot, Transform Target, string Text, float HeightOffset);

    private sealed record SubLabelRenderable(UIText Text)
    {
        public void Render()
        {
            Text.Render();
        }

        public void Destroy()
        {
            if (Text.Mesh is not null)
                Text.Mesh.Destroy();
        }
    }

    private sealed record MathIntersectionsWorldLabelSet(UIText Title, UIText Description)
    {
        public void Render()
        {
            Title.Render();
            Description.Render();
        }

        public void Destroy()
        {
            DestroyText(Title);
            DestroyText(Description);
        }

        private static void DestroyText(UIText text)
        {
            if (text.Mesh is not null)
                text.Mesh.Destroy();
        }
    }
}