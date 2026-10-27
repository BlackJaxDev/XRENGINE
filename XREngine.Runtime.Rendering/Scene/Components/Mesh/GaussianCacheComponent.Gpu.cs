using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Gaussian;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Scene.Mesh;

// GPU buffers, the two compute passes, the sort, and the draw.
public partial class GaussianCacheComponent
{
    /// <summary>Driver pose compute shader, relative to the engine shader folder.</summary>
    public const string DriverPoseShaderPath = "Gaussian/GaussianCacheDriverPose.comp";
    /// <summary>Cut and splat pose compute shader, relative to the engine shader folder.</summary>
    public const string SplatsShaderPath = "Gaussian/GaussianCacheSplats.comp";

    /// <summary>Storage binding of the splat records of all levels (48 bytes each).</summary>
    public const uint RecordsBinding = 50u;
    /// <summary>Storage binding of the parent index of each node (int, -1 for none).</summary>
    public const uint ParentsBinding = 51u;
    /// <summary>Storage binding of the driver triangle indices of all driver levels.</summary>
    public const uint DriverIndicesBinding = 52u;
    /// <summary>Storage binding of the quantized driver frames (16-bit values, two in each word).</summary>
    public const uint FramesBinding = 53u;
    /// <summary>Storage binding of the posed driver vertices (vec4).</summary>
    public const uint DriverPoseBinding = 54u;
    /// <summary>Storage binding of the level table (<see cref="GaussianCacheGpuLevel"/>).</summary>
    public const uint LevelsBinding = 55u;

    /// <summary>Invocations in one workgroup of both compute shaders.</summary>
    public const uint ComputeLocalSize = 64u;

    public const string DriverVertexCountUniform = "DriverVertexCount";
    public const string DriverLevelCountUniform = "DriverLevelCount";
    public const string FrameCountUniform = "FrameCount";
    public const string FractionalFrameUniform = "FractionalFrame";
    public const string LoopModeUniform = "LoopMode";
    public const string GridOriginUniform = "GridOrigin";
    public const string GridStepUniform = "GridStep";
    public const string TotalNodeCountUniform = "TotalNodeCount";
    public const string LevelCountUniform = "LevelCount";
    public const string SplatCapacityUniform = "SplatCapacity";
    public const string ModelViewUniform = "ModelViewMatrix";
    public const string FocalPixelsUniform = "FocalPixels";
    public const string SigmaScaleUniform = "SigmaScale";
    public const string PerspectiveUniform = "PerspectiveProjection";
    public const string LodThresholdUniform = "LodThresholdPixels";
    public const string ColorScaleUniform = "ColorScale";

    /// <summary>The quad extent of the renderer, in standard deviations. The culling volume uses it.</summary>
    private const float QuadExtentSigma = 3.0f;

    /// <summary>The culling volume grows by this multiple of the largest rest offset of a splat from its triangle.</summary>
    private const float BindingOffsetStretch = 1.5f;

    /// <summary>The label of the GPU copy of the overflow counter.</summary>
    private const string OverflowCopyLabel = "GaussianCacheOverflowReadback";

    /// <summary>Reads the first unsigned integer of a mapped buffer. The delegate is made one time.</summary>
    private static readonly DataBufferMappedReadCallback<uint> ReadFirstUInt = ReadFirstUIntCore;

    private readonly List<CacheUpload> _drainedUploads = [];

    // Render-thread state.
    private readonly GaussianCacheUploadGate _gate = new();
    private readonly GaussianCacheOverflowCheck _overflowCheck = new();
    private CacheLayout? _gpuLayout;
    private long _gpuLoadedNodeCount;
    private bool _hasPreparedSplats;
    private XRDataBuffer? _overflowReadback;
    private XRDataBuffer? _records;
    private XRDataBuffer? _parents;
    private XRDataBuffer? _driverIndices;
    private XRDataBuffer? _frames;
    private XRDataBuffer? _driverPose;
    private XRDataBuffer? _levels;
    private XRRenderProgram? _driverPoseProgram;
    private XRRenderProgram? _splatsProgram;
    private GaussianSplatRenderer? _splatRenderer;
    private XRMeshRenderer? _quad;
    private XRMaterial? _ownedQuadMaterial;
    private XRGpuFence? _overflowFence;

    /// <summary>Number of workgroups that the compute shaders dispatch for <paramref name="count"/> invocations.</summary>
    public static uint GetGroupCount(uint count)
        => Math.Max(1u, (count + ComputeLocalSize - 1u) / ComputeLocalSize);

    /// <summary>PreRender callback: applies new data, poses the drivers, chooses and poses the splats, and sorts them.</summary>
    private void PrepareSplats()
    {
        if (IsDestroyed)
            return;

        try
        {
            ApplyPendingUploads();
            // Until the level table of the current cache is on the GPU, the buffers can hold data of another cache.
            if (!_gate.CanDispatch || _gpuLayout is not { TotalNodeCount: > 0 } layout)
                return;

            XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
            if (camera is null || !EnsureProgramsReady())
                return;

            GaussianSplatRenderer renderer = EnsureRenderer(layout);
            Matrix4x4 model = Transform.RenderMatrix;
            Matrix4x4 view = camera.Transform.InverseRenderMatrix;
            float viewportHeight = RuntimeEngine.Rendering.State.RenderArea.Height;
            if (viewportHeight <= 0.0f)
                viewportHeight = RuntimeEngine.Rendering.State.RenderingViewport?.Height ?? 0;
            float frame = GaussianCachePoseReference.FractionalFrame(layout.Header, _playbackTimeSeconds);

            if (layout.DriverVertexCount > 0)
                DispatchDriverPose(layout, frame);
            renderer.ResetCounters();
            DispatchSplats(layout, renderer, model * view, camera.ProjectionMatrixUnjittered, viewportHeight);

            renderer.ModelMatrix = model;
            renderer.Prepare(in view);
            _hasPreparedSplats = renderer.HasOrder;
            TrackOverflow(renderer);
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, "Failed to prepare gaussian cache splats.");
        }
    }

    /// <summary>
    /// TransparentForward callback: draws the splats in the order from the PreRender call. It draws nothing until the
    /// passes of the current cache and renderer have run.
    /// </summary>
    private void DrawSplats()
    {
        if (IsDestroyed || !_hasPreparedSplats || !_gate.CanDispatch || _splatRenderer is not { HasOrder: true } renderer)
            return;

        try
        {
            renderer.Draw(EnsureQuad());
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, "Failed to draw gaussian cache splats.");
        }
    }

    private void DispatchDriverPose(CacheLayout layout, float frame)
    {
        XRRenderProgram program = _driverPoseProgram!;
        program.BindBuffer(_frames!, FramesBinding);
        program.BindBuffer(_driverPose!, DriverPoseBinding);
        program.BindBuffer(_levels!, LevelsBinding);
        program.Uniform(DriverVertexCountUniform, (uint)layout.DriverVertexCount);
        program.Uniform(DriverLevelCountUniform, (uint)layout.DriverLevelCount);
        program.Uniform(FrameCountUniform, layout.FrameCount);
        program.Uniform(FractionalFrameUniform, frame);
        program.Uniform(LoopModeUniform, (int)layout.Header.LoopMode);
        program.Uniform(GridOriginUniform, layout.GridOrigin);
        program.Uniform(GridStepUniform, layout.GridCellSize);
        program.DispatchCompute(GetGroupCount((uint)layout.DriverVertexCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchSplats(CacheLayout layout, GaussianSplatRenderer renderer, Matrix4x4 modelView, Matrix4x4 projection, float viewportHeight)
    {
        XRRenderProgram program = _splatsProgram!;
        program.BindBuffer(renderer.Splats, GaussianSplatRenderer.SplatsBinding);
        program.BindBuffer(renderer.VisibleCount, GaussianSplatRenderer.VisibleCountBinding);
        program.BindBuffer(renderer.Overflow, GaussianSplatRenderer.OverflowBinding);
        program.BindBuffer(_records!, RecordsBinding);
        program.BindBuffer(_parents!, ParentsBinding);
        program.BindBuffer(_driverIndices!, DriverIndicesBinding);
        program.BindBuffer(_frames!, FramesBinding);
        program.BindBuffer(_driverPose!, DriverPoseBinding);
        program.BindBuffer(_levels!, LevelsBinding);
        program.Uniform(TotalNodeCountUniform, (uint)layout.TotalNodeCount);
        program.Uniform(LevelCountUniform, (uint)layout.LevelCount);
        program.Uniform(SplatCapacityUniform, (uint)renderer.Capacity);
        program.Uniform(ModelViewUniform, modelView);
        program.Uniform(FocalPixelsUniform, GaussianCachePoseReference.FocalPixels(projection, viewportHeight));
        program.Uniform(SigmaScaleUniform, GaussianCachePoseReference.SigmaScale(modelView));
        program.Uniform(PerspectiveUniform, GaussianCachePoseReference.IsPerspective(projection) ? 1 : 0);
        program.Uniform(LodThresholdUniform, _lodThresholdPixels);
        program.Uniform(ColorScaleUniform, layout.Header.ColorScale);
        program.Uniform(GridOriginUniform, layout.GridOrigin);
        program.Uniform(GridStepUniform, layout.GridCellSize);
        program.DispatchCompute(GetGroupCount((uint)layout.TotalNodeCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    /// <summary>Moves the queued uploads to the GPU buffers. A new cache first sizes the buffers for its header.</summary>
    private void ApplyPendingUploads()
    {
        UploadTracker? tracker;
        bool changed;
        lock (_uploadSync)
        {
            if (_pendingUploads.Count == 0 && !_trackerChanged)
                return;
            changed = _trackerChanged;
            _trackerChanged = false;
            tracker = _tracker;
            _drainedUploads.AddRange(_pendingUploads);
            _pendingUploads.Clear();
        }

        try
        {
            if (changed)
                BeginGpuCache(tracker);
            foreach (CacheUpload upload in _drainedUploads)
            {
                if (_gate.Accepts(upload.Tracker))
                    ApplyUpload(upload);
            }
        }
        finally
        {
            _drainedUploads.Clear();
        }
    }

    /// <summary>
    /// Gives the GPU buffers to a new cache. The passes and the draw stay off until the first level table of the cache
    /// arrives, and the level table on the GPU is cleared now, so no level of the old cache stays ready.
    /// </summary>
    private void BeginGpuCache(UploadTracker? tracker)
    {
        _gate.Begin(tracker);
        _gpuLayout = tracker?.Layout;
        _gpuLoadedNodeCount = 0;
        _hasPreparedSplats = false;
        RearmOverflowCheck();
        if (_gpuLayout is not { } layout)
        {
            DisposeCacheBuffers();
            return;
        }

        // The buffers grow only when a cache needs more space. Old data past the loaded ranges is never read.
        _records = EnsureStorage<GaussianCacheSplatRecord>(_records, "GaussianCacheRecords", RecordsBinding, layout.TotalNodeCount);
        _parents = EnsureStorage<int>(_parents, "GaussianCacheParents", ParentsBinding, layout.TotalNodeCount);
        _driverIndices = EnsureStorage<uint>(_driverIndices, "GaussianCacheDriverIndices", DriverIndicesBinding, layout.TotalIndexCount);
        _frames = EnsureStorage<ushort>(_frames, "GaussianCacheFrames", FramesBinding, layout.FrameValueCount);
        _levels = EnsureStorage<GaussianCacheGpuLevel>(_levels, "GaussianCacheLevels", LevelsBinding, layout.LevelCount);
        Write(_levels, new UploadRange<GaussianCacheGpuLevel>(0u, new GaussianCacheGpuLevel[layout.LevelCount]));
        uint poseCount = (uint)Math.Max(layout.DriverVertexCount, 1);
        if (_driverPose is null || _driverPose.ElementCount < poseCount)
        {
            _driverPose?.Dispose();
            _driverPose = new XRDataBuffer("GaussianCacheDriverPose", EBufferTarget.ShaderStorageBuffer, poseCount, EComponentType.Float, 4u, false, false, allocateClientSideSource: false)
            {
                // The driver pose pass writes every vertex that the cut pass reads.
                GpuProduced = true,
                BindingIndexOverride = DriverPoseBinding,
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
            };
        }
    }

    private void ApplyUpload(CacheUpload upload)
    {
        foreach (UploadRange<GaussianCacheSplatRecord> range in upload.Records)
            Write(_records!, range);
        foreach (UploadRange<int> range in upload.Parents)
            Write(_parents!, range);
        foreach (UploadRange<uint> range in upload.Indices)
            Write(_driverIndices!, range);
        foreach (UploadRange<ushort> range in upload.Frames)
            Write(_frames!, range);

        // The level table and the loaded count come from the newest collect call.
        if (!_gate.AcceptLevelTable(upload.Tracker, upload.Sequence))
            return;
        if (upload.LoadedNodeCount != _gpuLoadedNodeCount)
        {
            _gpuLoadedNodeCount = upload.LoadedNodeCount;
            _overflowCheck.RearmForNewNodes();
        }

        Write(_levels!, new UploadRange<GaussianCacheGpuLevel>(0u, upload.Levels));
    }

    private static void Write<T>(XRDataBuffer buffer, in UploadRange<T> range) where T : unmanaged
    {
        uint byteLength = buffer.WriteDataRaw<T>(range.Data, range.ElementOffset);
        buffer.PushSubData(checked((int)(range.ElementOffset * (uint)Unsafe.SizeOf<T>())), byteLength);
    }

    private static XRDataBuffer EnsureStorage<T>(XRDataBuffer? buffer, string name, uint binding, int count) where T : unmanaged
    {
        buffer ??= new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, false)
        {
            BindingIndexOverride = binding,
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };
        buffer.EnsureRawCapacity<T>((uint)Math.Max(count, 1), growCapacityGeometrically: false);
        return buffer;
    }

    /// <summary>
    /// Creates the renderer on the first call, and again when the capacity changes. The capacity is
    /// <see cref="MaxRenderedSplats"/>, but not more than the node count of the cache, because the cut chooses each node
    /// at most one time.
    /// </summary>
    private GaussianSplatRenderer EnsureRenderer(CacheLayout layout)
    {
        int capacity = Math.Min(_maxRenderedSplats, Math.Max(layout.TotalNodeCount, 1));
        if (_splatRenderer is { } existing && existing.Capacity == capacity)
            return existing;

        // The overflow counter of the old renderer is gone, and a new capacity can change the result of a read.
        RearmOverflowCheck();
        _hasPreparedSplats = false;
        _splatRenderer?.Dispose();
        _splatRenderer = null;
        _splatRenderer = new GaussianSplatRenderer(capacity);
        return _splatRenderer;
    }

    private bool EnsureProgramsReady()
    {
        _driverPoseProgram ??= new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader(DriverPoseShaderPath, EShaderType.Compute))
        {
            Name = "GaussianCacheDriverPose",
        };
        _splatsProgram ??= new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader(SplatsShaderPath, EShaderType.Compute))
        {
            Name = "GaussianCacheSplats",
        };

        bool driverReady = LinkReady(_driverPoseProgram);
        bool splatsReady = LinkReady(_splatsProgram);
        return driverReady && splatsReady;
    }

    private static bool LinkReady(XRRenderProgram program)
    {
        if (program.IsLinked)
            return true;
        program.Link();
        return program.IsLinked;
    }

    private XRMeshRenderer EnsureQuad()
    {
        if (_quad is not null)
            return _quad;
        _ownedQuadMaterial = GaussianSplatRenderer.CreateQuadMaterial();
        _quad = GaussianSplatRenderer.CreateQuadRenderer(_ownedQuadMaterial);
        return _quad;
    }

    /// <summary>
    /// Logs one time when chosen splats did not fit in the renderer (see <see cref="GaussianCacheOverflowCheck"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A read runs only while the loaded nodes are more than the capacity. It copies the overflow counter of this frame,
    /// on the GPU and after the cut pass, into a small readback buffer that stays mapped (persistent, coherent, read
    /// only), and then inserts a fence. A later frame polls the fence without a wait. After the fence signals, the copy
    /// is complete and the CPU reads the mapped value. The read does not map a buffer and does not touch a buffer that
    /// in-flight GPU work uses, so it does not wait for the GPU. No other read starts until this one ends.
    /// </para>
    /// <para>
    /// One read ends the checks until the capacity, <see cref="MaxRenderedSplats"/>, or the cache changes, or more nodes
    /// load (only when no overflow is logged). A backend without fences or GPU buffer copies never reads the counter, so
    /// it does not log overflow. The strict zero-readback submission mode also stops the reads.
    /// </para>
    /// <para>
    /// The readback buffer, its mapping, and the cached read delegate are made one time. The fence comes from the pool of
    /// the backend and goes back to it when the read ends.
    /// </para>
    /// </remarks>
    private void TrackOverflow(GaussianSplatRenderer renderer)
    {
        if (_overflowFence is not null)
        {
            EGpuFenceStatus status = _overflowFence.Poll();
            if (status == EGpuFenceStatus.Pending)
                return;

            DropOverflowFence();
            if (status == EGpuFenceStatus.Failed)
            {
                _overflowCheck.ReadFailed();
                return;
            }

            uint dropped = ReadMappedCounter(_overflowReadback);
            if (_overflowCheck.ReadCompleted(dropped))
            {
                Debug.RenderingWarning(
                    $"Gaussian cache component on '{SceneNode?.Name ?? GetType().Name}' chose {dropped} more splats than MaxRenderedSplats ({renderer.Capacity}) allows. The renderer drops them.");
            }
            return;
        }

        if (!_overflowCheck.ShouldStartRead(_gpuLoadedNodeCount, renderer.Capacity))
            return;

        AbstractRenderer? api = AbstractRenderer.Current;
        if (api is null || RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy())
        {
            _overflowCheck.ReadUnavailable();
            return;
        }

        XRDataBuffer readback = EnsureOverflowReadback();
        if (api.TryEnqueueGpuBufferCopy(renderer.Overflow, 0, readback, 0, sizeof(uint), OverflowCopyLabel) != ERendererComputeEnqueueStatus.Enqueued)
        {
            _overflowCheck.ReadUnavailable();
            return;
        }

        if (!readback.IsMapped)
            readback.MapBufferData();
        api.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);
        _overflowFence = readback.IsMapped ? api.InsertGpuFence() : null;
        if (_overflowFence is null)
        {
            _overflowCheck.ReadUnavailable();
            return;
        }

        _overflowCheck.ReadStarted();
    }

    /// <summary>Creates the readback buffer of the overflow counter on the first call. It stays mapped for reading.</summary>
    private XRDataBuffer EnsureOverflowReadback()
        => _overflowReadback ??= new XRDataBuffer(OverflowCopyLabel, EBufferTarget.ShaderStorageBuffer, 4u, EComponentType.UInt, 1u, false, true)
        {
            Usage = EBufferUsage.StreamRead,
            Resizable = false,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            StorageFlags = EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
            RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
        };

    private static uint ReadMappedCounter(XRDataBuffer? readback)
    {
        uint value = 0u;
        readback?.TryReadMapped(ref value, ReadFirstUInt);
        return value;
    }

    private static bool ReadFirstUIntCore(scoped ReadOnlySpan<byte> bytes, ref uint value)
    {
        if (bytes.Length < sizeof(uint))
            return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    /// <summary>Starts the overflow checks again and abandons a pending read.</summary>
    private void RearmOverflowCheck()
    {
        DropOverflowFence();
        _overflowCheck.Rearm();
    }

    private void DropOverflowFence()
    {
        _overflowFence?.Dispose();
        _overflowFence = null;
    }

    private void DisposeCacheBuffers()
    {
        _splatRenderer?.Dispose();
        _splatRenderer = null;
        _records?.Dispose();
        _parents?.Dispose();
        _driverIndices?.Dispose();
        _frames?.Dispose();
        _driverPose?.Dispose();
        _levels?.Dispose();
        _records = _parents = _driverIndices = _frames = _driverPose = _levels = null;
    }

    /// <summary>Releases the buffers, the programs, the renderer, and the quad on the render thread.</summary>
    private void ReleaseGpuResources()
    {
        lock (_uploadSync)
        {
            _tracker = null;
            _trackerChanged = false;
            _pendingUploads.Clear();
        }

        XRDataBuffer?[] buffers = [_records, _parents, _driverIndices, _frames, _driverPose, _levels, _overflowReadback];
        GaussianSplatRenderer? renderer = _splatRenderer;
        XRRenderProgram? driverPoseProgram = _driverPoseProgram;
        XRRenderProgram? splatsProgram = _splatsProgram;
        XRMeshRenderer? quad = _quad;
        XRMaterial? ownedMaterial = _ownedQuadMaterial;
        XRGpuFence? fence = _overflowFence;
        _records = _parents = _driverIndices = _frames = _driverPose = _levels = _overflowReadback = null;
        _splatRenderer = null;
        _driverPoseProgram = _splatsProgram = null;
        _quad = null;
        _ownedQuadMaterial = null;
        _overflowFence = null;
        _gate.Begin(null);
        _gpuLayout = null;
        _hasPreparedSplats = false;

        RuntimeEngine.InvokeOnMainThread(
            () =>
            {
                fence?.Dispose();
                renderer?.Dispose();
                foreach (XRDataBuffer? buffer in buffers)
                    buffer?.Dispose();
                driverPoseProgram?.Destroy();
                splatsProgram?.Destroy();
                XRMesh? mesh = quad?.Mesh;
                TryDestroy(quad);
                TryDestroy(mesh);
                TryDestroy(ownedMaterial);
            },
            "GaussianCacheComponent.ReleaseGpuResources",
            executeNowIfAlreadyMainThread: true);
    }

    private static void TryDestroy(XREngine.Data.Core.XRObjectBase? asset)
    {
        if (asset is null || asset.IsDestroyed)
            return;
        try
        {
            asset.Destroy(now: true);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Failed to release a gaussian cache resource: {0}", error);
        }
    }
}
