using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Capture.Lights;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

/// <summary>
/// Owns the complete forward light-probe publication for one exact render-pipeline instance.
/// Render-pipeline assets can serve several instances and therefore contain policy only.
/// </summary>
internal sealed class ForwardLightProbeInstanceResources
{
    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, ForwardLightProbeInstanceResources> States = new();
    private bool _detached;

    private ForwardLightProbeInstanceResources(XRRenderPipelineInstance owner)
    {
        Owner = owner;
        owner.CacheClearing += ReleaseResources;
    }

    public XRRenderPipelineInstance Owner { get; }
    public object Sync { get; } = new();

    public XRTexture2DArray? IrradianceArray;
    public XRTexture2DArray? PrefilterArray;
    public XRDataBuffer? PositionBuffer;
    public XRDataBuffer? TetraBuffer;
    public XRDataBuffer? ParamBuffer;
    public XRDataBuffer? GridCellBuffer;
    public XRDataBuffer? GridIndexBuffer;

    public Vector3 GridOrigin;
    public float GridCellSize;
    public IVector3 GridDimensions;
    public int LastProbeCount;
    public Dictionary<Guid, Vector3> CachedProbePositions { get; } = [];
    public Dictionary<Guid, (XRTexture2D Irradiance, XRTexture2D Prefilter)> CachedProbeTextures { get; } = [];
    public Dictionary<Guid, uint> ObservedProbeCaptureVersions { get; } = [];
    public ProbePositionData[] CachedProbePositionData = [];
    public ProbeParamData[] CachedProbeParamData = [];
    public Guid[] CachedProbeIds = [];
    public int[] CachedProbeSourceIndices = [];
    public ulong CachedProbeLayoutSignature;
    public IRenderApiWrapperOwner? ApiWrapperIdentityOwner;
    public object? WorldIdentity;
    public int PublicationResourceGeneration = -1;

    public volatile bool PendingProbeRefresh;
    public bool PendingProbeRefreshDeferredByBatchCapture;
    public int ObservedLightProbeBatchCompletedVersion;
    public ulong ProbeRefreshEarliestFrameId;
    public List<LightProbeComponent> CachedReadyProbes { get; } = [];
    public List<int> CachedReadyProbeSourceIndices { get; } = [];

    public ulong BindingStateFrameId = ulong.MaxValue;
    public ulong TetrahedraDebugRenderFrameId = ulong.MaxValue;
    public bool BindingResourcesEnabled;
    public bool BindingUseGrid;
    public int BindingProbeCount;
    public int BindingTetraCount;
    public int TetraProbeCount;

    public Job? TessellationJob { get; private set; }
    public long RequestToken { get; private set; }
    public ProbeTopologyResult? PendingResult { get; private set; }
    private IRenderApiWrapperOwner? TopologyRequestApiWrapperIdentityOwner { get; set; }
    private object? TopologyRequestWorldIdentity { get; set; }

    public static ForwardLightProbeInstanceResources Get(XRRenderPipelineInstance instance)
        => States.GetValue(instance, static owner => new ForwardLightProbeInstanceResources(owner));

    public static bool TryGet(XRRenderPipelineInstance? instance, out ForwardLightProbeInstanceResources? state)
    {
        if (instance is not null && States.TryGetValue(instance, out ForwardLightProbeInstanceResources? existing))
        {
            state = existing;
            return true;
        }

        state = null;
        return false;
    }

    /// <summary>
    /// Releases the state associated with an instance when its pipeline asset is destroyed.
    /// The pipeline's instance list is the lifecycle index; the weak table is lookup only.
    /// </summary>
    public static void DestroyCache(XRRenderPipelineInstance instance, RenderPipeline expectedPipeline)
    {
        if (!States.TryGetValue(instance, out ForwardLightProbeInstanceResources? state))
            return;

        if (!RuntimeEngine.IsRenderThread)
        {
            RuntimeEngine.EnqueueRenderThreadTask(
                () => DestroyCacheOnRenderThread(instance, expectedPipeline, state),
                "ForwardLightProbeInstanceResources.DestroyCache",
                RenderThreadJobKind.RenderPipelineResource);
            return;
        }

        DestroyCacheOnRenderThread(instance, expectedPipeline, state);
    }

    private static void DestroyCacheOnRenderThread(
        XRRenderPipelineInstance instance,
        RenderPipeline expectedPipeline,
        ForwardLightProbeInstanceResources expectedState)
    {
        if (!ReferenceEquals(instance.AssignedPipeline, expectedPipeline) ||
            !States.TryGetValue(instance, out ForwardLightProbeInstanceResources? state) ||
            !ReferenceEquals(state, expectedState))
        {
            return;
        }

        state.DetachAndRelease();
        States.Remove(instance);
    }

    public ProbeTopologySnapshot BeginTopologyRequest(
        IRenderApiWrapperOwner apiWrapperIdentityOwner,
        object worldIdentity)
    {
        Job? obsoleteJob;
        long requestToken;
        lock (Sync)
        {
            unchecked { requestToken = ++RequestToken; }
            PendingResult = null;
            TopologyRequestApiWrapperIdentityOwner = apiWrapperIdentityOwner;
            TopologyRequestWorldIdentity = worldIdentity;
            obsoleteJob = TessellationJob;
            TessellationJob = null;
        }

        // Cancellation is advisory. The token was advanced before invoking it so a
        // completing obsolete worker cannot publish into the new request.
        obsoleteJob?.Cancel();

        Vector3[] positions = new Vector3[CachedProbePositionData.Length];
        for (int index = 0; index < positions.Length; index++)
        {
            Vector4 position = CachedProbePositionData[index].Position;
            positions[index] = new Vector3(position.X, position.Y, position.Z);
        }

        return new ProbeTopologySnapshot(
            Owner.InstanceId,
            Owner.ResourceGeneration,
            requestToken,
            CachedProbeLayoutSignature,
            [.. CachedProbeIds],
            [.. CachedProbeSourceIndices],
            positions);
    }

    public void AttachTopologyJob(long requestToken, Job job)
    {
        bool stale;
        lock (Sync)
        {
            stale = requestToken != RequestToken || _detached ||
                PendingResult?.RequestToken == requestToken;
            if (!stale)
                TessellationJob = job;
        }

        if (stale)
            job.Cancel();
    }

    public void PublishTopologyResult(ProbeTopologyResult result)
    {
        lock (Sync)
        {
            if (_detached || result.RequestToken != RequestToken)
                return;

            PendingResult = result;
            TessellationJob = null;
        }
    }

    public bool TryPeekTopologyResult(out ProbeTopologyResult? result)
    {
        lock (Sync)
        {
            result = PendingResult;
            return result is not null;
        }
    }

    public bool IsTopologyResultCurrent(
        ProbeTopologyResult result,
        IRenderApiWrapperOwner apiWrapperIdentityOwner,
        object worldIdentity)
    {
        lock (Sync)
        {
            return result.RequestToken == RequestToken &&
                ReferenceEquals(TopologyRequestApiWrapperIdentityOwner, apiWrapperIdentityOwner) &&
                ReferenceEquals(TopologyRequestWorldIdentity, worldIdentity);
        }
    }

    public void CompleteTopologyResult(long requestToken)
    {
        lock (Sync)
        {
            if (PendingResult?.RequestToken == requestToken)
                PendingResult = null;
        }
    }

    public void DropTopologyResult(long requestToken)
        => CompleteTopologyResult(requestToken);

    public void ClearResources()
    {
        InvalidateTopologyWork();
        UnbindOwnedResources();
        IrradianceArray?.Destroy();
        IrradianceArray = null;
        PrefilterArray?.Destroy();
        PrefilterArray = null;
        DestroyBuffer(ref PositionBuffer);
        DestroyBuffer(ref ParamBuffer);
        DestroyBuffer(ref TetraBuffer);
        DestroyBuffer(ref GridCellBuffer);
        DestroyBuffer(ref GridIndexBuffer);

        GridOrigin = Vector3.Zero;
        GridCellSize = 0.0f;
        GridDimensions = IVector3.Zero;
        TetraProbeCount = 0;
        CachedProbePositions.Clear();
        CachedProbeTextures.Clear();
        ObservedProbeCaptureVersions.Clear();
        CachedProbePositionData = [];
        CachedProbeParamData = [];
        CachedProbeIds = [];
        CachedProbeSourceIndices = [];
        CachedProbeLayoutSignature = 0;
        CachedReadyProbes.Clear();
        CachedReadyProbeSourceIndices.Clear();
        ApiWrapperIdentityOwner = null;
        WorldIdentity = null;
        PublicationResourceGeneration = -1;
        LastProbeCount = 0;
        ObservedLightProbeBatchCompletedVersion = 0;
        PendingProbeRefresh = false;
        PendingProbeRefreshDeferredByBatchCapture = false;
        ProbeRefreshEarliestFrameId = 0;
        ResetBindingSnapshot();
    }

    public void ResetBindingSnapshot()
    {
        BindingStateFrameId = ulong.MaxValue;
        TetrahedraDebugRenderFrameId = ulong.MaxValue;
        BindingResourcesEnabled = false;
        BindingUseGrid = false;
        BindingProbeCount = 0;
        BindingTetraCount = 0;
    }

    public static void DestroyBuffer(ref XRDataBuffer? buffer)
    {
        XRDataBuffer? oldBuffer = buffer;
        buffer = null;
        if (oldBuffer is null)
            return;

        oldBuffer.Destroy(true);
        oldBuffer.Dispose();
    }

    public static ulong ComputeLayoutSignature(
        IReadOnlyList<Guid> probeIds,
        IReadOnlyList<int> sourceIndices,
        IReadOnlyList<ProbePositionData> positions,
        IReadOnlyList<ProbeParamData> parameters)
    {
        if (probeIds.Count != sourceIndices.Count ||
            probeIds.Count != positions.Count ||
            probeIds.Count != parameters.Count)
        {
            throw new ArgumentException("Probe layout signature inputs must have identical lengths.");
        }

        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        ulong signature = OffsetBasis;

        void Mix(uint value)
        {
            signature ^= value;
            signature *= Prime;
        }

        Span<byte> guidBytes = stackalloc byte[16];
        for (int index = 0; index < probeIds.Count; index++)
        {
            probeIds[index].TryWriteBytes(guidBytes);
            for (int byteIndex = 0; byteIndex < guidBytes.Length; byteIndex++)
                Mix(guidBytes[byteIndex]);

            Mix(unchecked((uint)sourceIndices[index]));
            MixVector(positions[index].Position);
            ProbeParamData parameter = parameters[index];
            MixVector(parameter.InfluenceInner);
            MixVector(parameter.InfluenceOuter);
            MixVector(parameter.InfluenceOffsetShape);
            MixVector(parameter.ProxyCenterEnable);
            MixVector(parameter.ProxyHalfExtents);
            MixVector(parameter.ProxyRotation);
        }

        return signature;

        void MixVector(Vector4 vector)
        {
            Mix(unchecked((uint)BitConverter.SingleToInt32Bits(vector.X)));
            Mix(unchecked((uint)BitConverter.SingleToInt32Bits(vector.Y)));
            Mix(unchecked((uint)BitConverter.SingleToInt32Bits(vector.Z)));
            Mix(unchecked((uint)BitConverter.SingleToInt32Bits(vector.W)));
        }
    }

    private void ReleaseResources()
        => ClearResources();

    private void DetachAndRelease()
    {
        if (_detached)
            return;

        _detached = true;
        Owner.CacheClearing -= ReleaseResources;
        ClearResources();
    }

    private void InvalidateTopologyWork()
    {
        Job? obsoleteJob;
        lock (Sync)
        {
            unchecked { ++RequestToken; }
            PendingResult = null;
            TopologyRequestApiWrapperIdentityOwner = null;
            TopologyRequestWorldIdentity = null;
            obsoleteJob = TessellationJob;
            TessellationJob = null;
        }

        obsoleteJob?.Cancel();
    }

    private void UnbindOwnedResources()
    {
        if (IrradianceArray?.Name is { } irradianceName)
            Owner.UnbindImportedTexture(irradianceName);
        if (PrefilterArray?.Name is { } prefilterName)
            Owner.UnbindImportedTexture(prefilterName);
        UnbindBuffer(PositionBuffer);
        UnbindBuffer(ParamBuffer);
        UnbindBuffer(TetraBuffer);
        UnbindBuffer(GridCellBuffer);
        UnbindBuffer(GridIndexBuffer);
    }

    private void UnbindBuffer(XRDataBuffer? buffer)
    {
        if (buffer is not null && !string.IsNullOrWhiteSpace(buffer.AttributeName))
            Owner.UnbindImportedBuffer(buffer.AttributeName);
    }
}
