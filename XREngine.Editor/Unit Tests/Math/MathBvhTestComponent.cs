using System.ComponentModel;
using System.Numerics;
using System.Threading;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;

namespace XREngine.Editor;

/// <summary>
/// Runs one of the four scene/mesh BVH workloads exposed by the Math Intersections World.
/// Workload maintenance and debug rendering are deliberately separate so benchmark copies
/// can keep exercising the selected BVH while their visualization is disabled.
/// </summary>
public sealed partial class MathBvhTestComponent : XRComponent, IRenderable
{
    private const uint SceneLeafCapacity = 2u;
    private const uint MaxDebugNodeCount = 4096u;

    private readonly GpuBvhDebugLineRenderer _gpuDebugRenderer = new();

    private MathBvhTestMode _mode;
    private ModelComponent? _targetModel;
    private List<Triangle>? _sourceTriangles;
    private bool _configured;
    private bool _debugRenderEnabled = true;
    private bool _workloadReady;
    private bool _validationPassed;
    private int _lastHitCount;
    private long _buildOperationCount;
    private long _updateOperationCount;
    private long _queryOperationCount;
    private int _gpuWorkQueued;

    public MathBvhTestComponent()
    {
        RenderInfo3D debugRenderInfo = RenderInfo3D.New(
            this,
            new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, RenderDebug));
        RenderedObjects = [debugRenderInfo];
        debugRenderInfo.Layer = XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex;
    }

    [Category("BVH Test")]
    [Description("Acceleration structure exercised by this Math Intersections World rig.")]
    public MathBvhTestMode Mode
    {
        get => _mode;
        private set => SetField(ref _mode, value);
    }

    [Category("BVH Test")]
    [Description("Draws the BVH topology, source primitives, query, and validation marker.")]
    public bool DebugRenderEnabled
    {
        get => _debugRenderEnabled;
        set => SetField(ref _debugRenderEnabled, value);
    }

    [Browsable(false)]
    public bool WorkloadReady => Volatile.Read(ref _workloadReady);

    [Browsable(false)]
    public bool ValidationPassed => Volatile.Read(ref _validationPassed);

    [Browsable(false)]
    public int LastHitCount => Volatile.Read(ref _lastHitCount);

    [Browsable(false)]
    public long BuildOperationCount => Interlocked.Read(ref _buildOperationCount);

    [Browsable(false)]
    public long UpdateOperationCount => Interlocked.Read(ref _updateOperationCount);

    [Browsable(false)]
    public long QueryOperationCount => Interlocked.Read(ref _queryOperationCount);

    [Browsable(false)]
    public int PrimitiveCount => GetPrimitiveCount();

    [Browsable(false)]
    public int NodeCount => GetNodeCount();

    public RenderInfo[] RenderedObjects { get; }

    /// <summary>
    /// Configures a freshly created test component. CPU structures are constructed immediately
    /// so their cost is included in benchmark spawning; GPU resources remain render-thread owned.
    /// </summary>
    internal void Configure(
        MathBvhTestMode mode,
        ModelComponent? targetModel = null,
        IReadOnlyList<Triangle>? sourceTriangles = null)
    {
        if (_configured)
            throw new InvalidOperationException("A Math BVH test component can only be configured once.");

        Mode = mode;
        _targetModel = targetModel;
        _sourceTriangles = sourceTriangles is null ? null : [.. sourceTriangles];
        _configured = true;
        InitializeCpuWorkload();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Logic, UpdateWorkload);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Logic, UpdateWorkload);
        base.OnComponentDeactivated();
    }

    private void UpdateWorkload()
    {
        if (!_configured)
            return;

        switch (Mode)
        {
            case MathBvhTestMode.CpuScene:
                UpdateCpuSceneWorkload();
                break;
            case MathBvhTestMode.LegacyCpuMesh:
                UpdateCpuMeshWorkload();
                break;
            case MathBvhTestMode.GpuScene:
            case MathBvhTestMode.GpuMesh:
                QueueGpuWorkload();
                break;
        }
    }

    private void QueueGpuWorkload()
        => Interlocked.Exchange(ref _gpuWorkQueued, 1);

    private void ExecuteQueuedGpuWorkload()
    {
        if (Interlocked.Exchange(ref _gpuWorkQueued, 0) == 0 ||
            IsDestroyed || !_configured || !IsActiveInHierarchy)
            return;

        if (Mode == MathBvhTestMode.GpuScene)
            PrepareGpuSceneWorkload();
        else if (Mode == MathBvhTestMode.GpuMesh)
            PrepareGpuMeshWorkload();
    }

    private static AABB[] CreateScenePrimitiveBounds()
    {
        const int width = 5;
        const int height = 3;
        const int depth = 5;
        var bounds = new AABB[width * height * depth];
        int index = 0;
        for (int y = 0; y < height; y++)
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            Vector3 center = new(
                (x - (width - 1) * 0.5f) * 1.65f,
                1.0f + y * 1.45f,
                (z - (depth - 1) * 0.5f) * 1.65f);
            float sizeBias = ((x * 13 + y * 7 + z * 3) % 5) * 0.06f;
            Vector3 size = new(0.62f + sizeBias, 0.72f + sizeBias, 0.58f + sizeBias);
            bounds[index++] = AABB.FromCenterSize(center, size);
        }

        return bounds;
    }

    private static uint CalculateBinaryBvhNodeCount(uint primitiveCount, uint leafCapacity)
    {
        if (primitiveCount == 0u)
            return 0u;

        uint leafCount = (primitiveCount + Math.Max(leafCapacity, 1u) - 1u) / Math.Max(leafCapacity, 1u);
        return leafCount * 2u - 1u;
    }

    private int GetPrimitiveCount()
        => Mode switch
        {
            MathBvhTestMode.CpuScene => _cpuSceneItems?.Length ?? 0,
            MathBvhTestMode.GpuScene => _gpuSceneAabbs?.Length ?? 0,
            MathBvhTestMode.LegacyCpuMesh => _sourceTriangles?.Count ?? 0,
            MathBvhTestMode.GpuMesh => (int)(_gpuMesh?.GpuMeshBvh?.TriangleCount ?? (uint)(_sourceTriangles?.Count ?? 0)),
            _ => 0,
        };

    private int GetNodeCount()
        => Mode switch
        {
            MathBvhTestMode.CpuScene => _cpuSceneTree?.GetDiagnostics().NodeCount ?? 0,
            MathBvhTestMode.GpuScene => (int)(_gpuSceneTree?.NodeCount ?? 0u),
            MathBvhTestMode.LegacyCpuMesh => _cpuMeshTree?._nodeCount ?? 0,
            MathBvhTestMode.GpuMesh => (int)(_gpuMesh?.GpuMeshBvh?.BvhNodeCount ?? 0u),
            _ => 0,
        };

    private void SetValidationState(bool ready, bool passed, int hitCount = 0)
    {
        Volatile.Write(ref _workloadReady, ready);
        Volatile.Write(ref _validationPassed, passed);
        Volatile.Write(ref _lastHitCount, hitCount);
    }

    protected override void OnDestroying()
    {
        ReleaseCpuWorkload();
        ReleaseGpuWorkload();
        _gpuDebugRenderer.Dispose();
        base.OnDestroying();
    }
}
