using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>
/// One renderer-owned packed GPU deformation generation. Its stream layouts are backend
/// lowering details and must never masquerade as XRMeshRenderer's canonical Skinned* buffers.
/// </summary>
internal sealed partial class WebGpuMeshDeformation : IDisposable
{
    private readonly WebGpuRendererHost _host;
    private readonly XRMeshRenderer _owner;
    private readonly XRMesh _mesh;
    private readonly XRMeshSkinningBufferState _skinningState;
    private readonly XRMeshBlendshapeBufferState _blendshapeState;
    private readonly long _geometryRevision;
    private readonly long _bufferRevision;
    private readonly bool _skinning;
    private readonly bool _blendshapes;
    private readonly bool _hasMorphRecords;
    private readonly bool _maximumMorphAccumulation;
    private readonly uint _paletteCount;
    private readonly int _vertexCount;
    private readonly bool _hasNormals;
    private readonly bool _hasTangents;
    private readonly (bool Interleaved, uint Stride, uint Position, uint? Normal, uint? Tangent) _vertexLayout;
    private readonly XRDataBuffer _packed;
    private readonly XRDataBuffer _emptyPalette;
    private readonly XRDataBuffer _emptyMorphs;
    private readonly List<WebGpuDeformationSource> _sources = [];
    private uint _recordedFrame;
    private int _ownerDestroyed;
    private bool _seeded;
    private XRDataBuffer? _seedPalette;
    private bool _disposed;
    private int _packedDirtyStart = int.MaxValue;
    private int _packedDirtyEnd;

    public XRDataBuffer Positions { get; }
    public XRDataBuffer Attributes { get; }
    public bool OwnerDestroyed => Volatile.Read(ref _ownerDestroyed) != 0;
    public bool HasNormals => _hasNormals;
    public bool HasTangents => _hasTangents;

    public WebGpuMeshDeformation(WebGpuRendererHost host, XRMeshRenderer owner, XRMesh mesh, bool skinning, bool blendshapes)
    {
        _host = host;
        _owner = owner;
        _mesh = mesh;
        _skinning = skinning;
        _blendshapes = blendshapes;
        _skinningState = mesh.GetSkinningBufferStateSnapshot();
        _blendshapeState = mesh.GetBlendshapeBufferStateSnapshot();
        _geometryRevision = mesh.GeometryRevision;
        _bufferRevision = mesh.Buffers.MutationRevision;
        _maximumMorphAccumulation = mesh.MaxBlendshapeAccumulation;
        _paletteCount = skinning ? owner.ActiveSkinPaletteCount : 0;
        _vertexCount = mesh.VertexCount;
        _hasNormals = mesh.HasNormals;
        _hasTangents = mesh.HasTangents;
        _vertexLayout = (mesh.Interleaved, mesh.InterleavedStride, mesh.PositionOffset, mesh.NormalOffset, mesh.TangentOffset);
        if (_vertexCount is <= 0 or > 16384 || _paletteCount > 1024 || blendshapes && mesh.BlendshapeCount > 256)
            throw Unsupported("the packed profile admits at most 16384 vertices, 1024 palette entries and 256 morph shapes per renderer");
        if (skinning && (_skinningState.SpillEntries?.Length ?? 0) > 65536 * 4 ||
            blendshapes && ((_blendshapeState.SparseRecords?.Length ?? 0) > 65536 * 16 ||
                (_blendshapeState.QuantizedDeltas?.Length ?? 0) > 65536 * 8))
            throw Unsupported("the packed profile admits at most 65536 spill influences, sparse morph records and quantized deltas");
        _hasMorphRecords = blendshapes && !IsCanonicalIdentityMorphGeneration(_blendshapeState, mesh.BlendshapeCount);

        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        try
        {
            _packed = CreatePackedInput();
            Positions = CreateOutput("DeformedPositions", 5);
            Attributes = CreateOutput("DeformedAttributes", 8);
            _emptyPalette = new("EmptyDeformationPalette", EBufferTarget.ShaderStorageBuffer, 1, EComponentType.Float, 12, false, false);
            _emptyPalette.Set(0, SkinPaletteMatrix.Identity);
            _emptyMorphs = new("EmptyActiveMorphs", EBufferTarget.ShaderStorageBuffer, 1, EComponentType.Float, 2, false, false);
            _emptyMorphs.SetVector2(0, Vector2.Zero);
            publication.Complete();
            _owner.Destroyed += OnOwnerDestroyed;
        }
        catch
        {
            foreach (WebGpuDeformationSource source in _sources) source.Dispose();
            throw;
        }
    }

    public bool Matches(XRMesh mesh, bool skinning, bool blendshapes)
    {
        if (_disposed || OwnerDestroyed || !ReferenceEquals(_mesh, mesh) || skinning != _skinning || blendshapes != _blendshapes ||
            _geometryRevision != mesh.GeometryRevision || _bufferRevision != mesh.Buffers.MutationRevision ||
            _maximumMorphAccumulation != mesh.MaxBlendshapeAccumulation ||
            _vertexCount != mesh.VertexCount || _hasNormals != mesh.HasNormals || _hasTangents != mesh.HasTangents ||
            _vertexLayout != (mesh.Interleaved, mesh.InterleavedStride, mesh.PositionOffset, mesh.NormalOffset, mesh.TangentOffset) ||
            !ReferenceEquals(_skinningState, mesh.GetSkinningBufferStateSnapshot()) ||
            !ReferenceEquals(_blendshapeState, mesh.GetBlendshapeBufferStateSnapshot()) ||
            (_skinning && _paletteCount != _owner.ActiveSkinPaletteCount))
            return false;
        foreach (WebGpuDeformationSource source in _sources)
            if (!source.HasSameLayout) return false;
        return true;
    }

    public bool TryRecord(XRRenderProgram program)
    {
        ObjectDisposedException.ThrowIf(_disposed || OwnerDestroyed, this);
        if (_recordedFrame == _host.EngineFrameSequence)
            return true;
        WebGpuRenderProgram api = (WebGpuRenderProgram)_host.GetOrCreateAPIRenderObject(program)!;
        if (!api.TryPrepareForCompute()) return false;
        RefreshPackedInputs();

        if (_skinning && !_owner.HasExternalSkinPaletteSource)
        {
            XRDataBuffer? currentPalette = _owner.ActiveSkinPaletteBuffer;
            if (!ReferenceEquals(_seedPalette, currentPalette))
            {
                _seedPalette = currentPalette;
                _seeded = false;
                _owner.ResetSkinPaletteSeedState();
            }
            if (!_seeded)
                _seeded = _owner.ReseedSkinPaletteUntilPoseStable();
            _owner.PushBoneMatricesToGPU();
        }
        if (_hasMorphRecords) _owner.PushBlendshapeWeightsToGPU();

        XRMeshRenderer.BlendshapeResourceSnapshot morphs = _owner.CaptureBlendshapeResources();
        XRDataBuffer palette = _skinning ? _owner.ActiveSkinPaletteBuffer
            ?? throw Unsupported("the canonical skin palette is unavailable") : _emptyPalette;
        XRDataBuffer active = _hasMorphRecords ? morphs.ActiveWeights
            ?? throw Unsupported("the canonical active morph list is unavailable") : _emptyMorphs;
        uint paletteBase = _skinning ? _owner.ActiveSkinPaletteBase : 0;
        if (paletteBase > 0x7fffffffu || palette.ComponentType != EComponentType.Float ||
            (ulong)paletteBase + _paletteCount > palette.Length / 48u)
            throw Unsupported("the canonical affine palette does not cover its declared base and count");
        uint activeCount = _hasMorphRecords ? checked((uint)morphs.ActiveCount) : 0;
        if (activeCount > active.Length / 8u || activeCount > _mesh.BlendshapeCount ||
            active.ComponentType != EComponentType.Float || active.ElementSize != 8)
            throw Unsupported("the canonical active morph list does not cover its declared count");
        ValidateActiveMorphs(active, activeCount);
        float threshold = _hasMorphRecords ? _owner.BlendshapeActiveWeightThreshold : 0;
        if (!float.IsFinite(threshold) || threshold < 0)
            throw Unsupported("the morph weight threshold must be finite and nonnegative");

        api.BeginResourceBindings();
        program.BindBuffer(_packed, 0);
        program.BindBuffer(palette, 1);
        program.BindBuffer(active, 2);
        program.BindBuffer(Positions, 3);
        program.BindBuffer(Attributes, 4);
        int influenceCap = _owner.ActiveSkinningInfluenceCap;
        uint effectiveCap = influenceCap <= 0 ? uint.MaxValue : checked((uint)influenceCap);
        program.Uniform("ActiveMorphCount", activeCount);
        program.Uniform("Reserved0", 0x80000000u | paletteBase);
        program.Uniform("Reserved1", effectiveCap);
        program.Uniform("Reserved2", BitConverter.SingleToUInt32Bits(threshold));
        ERendererComputeEnqueueStatus status = _host.TryDispatchCompute(program, ((uint)_mesh.VertexCount + 63u) / 64u, 1, 1);
        if (status == ERendererComputeEnqueueStatus.ProgramPending) return false;
        if (status != ERendererComputeEnqueueStatus.Enqueued)
            throw new InvalidOperationException($"WebGPU.Deformation.DispatchRejected: {status}.");
        // Enqueued is valid only within this attempted frame. An aborted atomic frame
        // never promotes the engine's persistent SkinnedOutputClean state.
        _recordedFrame = _host.EngineFrameSequence;
        _host.CountEngineMeshDeformation(_mesh.VertexCount);
        return true;
    }

    public bool TryPrepare(XRRenderProgram program)
    {
        ObjectDisposedException.ThrowIf(_disposed || OwnerDestroyed, this);
        // Pipeline warm-up is not a producer. Actual draws call TryRecord after their
        // canonical render-data callbacks, so those callbacks cannot trail the dispatch.
        return ((WebGpuRenderProgram)_host.GetOrCreateAPIRenderObject(program)!).TryPrepareForCompute();
    }

    private XRDataBuffer CreateOutput(string name, uint components)
        => new(name, EBufferTarget.ArrayBuffer, checked((uint)_mesh.VertexCount), EComponentType.Float,
            components, false, false, allocateClientSideSource: false)
        {
            GpuProduced = true,
            Resizable = false,
            Usage = EBufferUsage.DynamicCopy,
        };

    private void OnOwnerDestroyed(XRObjectBase owner) => Interlocked.Exchange(ref _ownerDestroyed, 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Destroyed -= OnOwnerDestroyed;
        foreach (WebGpuDeformationSource source in _sources) source.Dispose();
        _sources.Clear();
        _packed.Dispose();
        Positions.Dispose();
        Attributes.Dispose();
        _emptyPalette.Dispose();
        _emptyMorphs.Dispose();
    }

    private static NotSupportedException Unsupported(string reason)
        => new($"WebGPU.Deformation.Unsupported: {reason}.");
}
