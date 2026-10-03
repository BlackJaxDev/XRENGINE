using XREngine.Data;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable source-binding membership retained by resident command publications.
/// Buffer content and typed publisher generations keep their existing upload owners.
/// </summary>
public sealed class GpuMeshSubmissionSourceBindings
{
    private XRMesh? _sourceMesh;
    private XRMeshRenderer? _sourceRenderer;
    private XRMaterial? _sourceMaterial;
    private XRMesh.BufferCollection? _meshBufferOwner;
    private XRMesh.BufferCollection? _rendererBufferOwner;
    private KeyValuePair<string, XRDataBuffer>[] _meshBuffers;
    private KeyValuePair<string, XRDataBuffer>[] _rendererBuffers;
    private IRenderBindingPublisher[] _rendererPublishers;
    private IRenderBindingPublisher[] _materialPublishers;

    private ulong[] _rendererPublisherGenerations = [];
    private ulong[] _materialPublisherGenerations = [];
    private ulong[] _rendererResourceGenerations = [];
    private ulong[] _materialResourceGenerations = [];

    private GpuMeshSubmissionSourceBindings(XRMesh mesh, XRMeshRenderer renderer, XRMaterial material,
        IRenderBindingPublisher[] rendererPublishers, IRenderBindingPublisher[] materialPublishers)
    {
        _sourceMesh = mesh;
        _sourceRenderer = renderer;
        _sourceMaterial = material;
        MaterialShaderRevision = material.ShaderStateRevision;
        _meshBufferOwner = mesh.Buffers;
        _rendererBufferOwner = renderer.Buffers;
        _meshBuffers = _meshBufferOwner.CaptureBindingSnapshot(out long meshRevision);
        _rendererBuffers = _rendererBufferOwner.CaptureBindingSnapshot(out long rendererRevision);
        MeshBufferRevision = meshRevision;
        RendererBufferRevision = rendererRevision;
        _rendererPublishers = rendererPublishers;
        _materialPublishers = materialPublishers;
    }

    public long MeshBufferRevision { get; private set; }
    public long RendererBufferRevision { get; private set; }
    public long MaterialShaderRevision { get; private set; }
    public ReadOnlySpan<KeyValuePair<string, XRDataBuffer>> MeshBuffers => _meshBuffers;
    public ReadOnlySpan<KeyValuePair<string, XRDataBuffer>> RendererBuffers => _rendererBuffers;
    public ReadOnlySpan<IRenderBindingPublisher> RendererPublishers => _rendererPublishers;
    public ReadOnlySpan<IRenderBindingPublisher> MaterialPublishers => _materialPublishers;

    public bool TryGetMeshBuffer(string name, out XRDataBuffer? buffer)
        => TryGetBuffer(_meshBuffers, name, out buffer);

    public bool TryGetRendererBuffer(string name, out XRDataBuffer? buffer)
        => TryGetBuffer(_rendererBuffers, name, out buffer);

    internal static GpuMeshSubmissionSourceBindings Capture(XRMesh mesh, XRMeshRenderer renderer,
        XRMaterial material, GpuMeshSubmissionSourceBindings? previous)
    {
        IRenderBindingPublisher[] rendererPublishers = renderer.BindingPublishers.CaptureSnapshot();
        IRenderBindingPublisher[] materialPublishers = material.BindingPublishers.CaptureSnapshot();
        if (previous is not null
            && ReferenceEquals(previous._sourceMaterial, material)
            && previous.MaterialShaderRevision == material.ShaderStateRevision
            && ReferenceEquals(previous._meshBufferOwner, mesh.Buffers)
            && ReferenceEquals(previous._rendererBufferOwner, renderer.Buffers)
            && previous.MeshBufferRevision == mesh.Buffers.MutationRevision
            && previous.RendererBufferRevision == renderer.Buffers.MutationRevision
            && ReferenceEquals(previous._rendererPublishers, rendererPublishers)
            && ReferenceEquals(previous._materialPublishers, materialPublishers))
            return previous;

        // Source membership changes are cold owner transitions. Warm command and
        // transform publication reuses these arrays without per-frame allocation.
        return new(mesh, renderer, material, rendererPublishers, materialPublishers);
    }

    /// <summary>Rejects later structural mutation instead of mixing source bindings across publications.</summary>
    public bool AreSourceBindingsCurrent
        => _sourceMesh is not null && _sourceRenderer is not null
           && _sourceMaterial?.ShaderStateRevision == MaterialShaderRevision
           && ReferenceEquals(_sourceMesh.Buffers, _meshBufferOwner)
           && ReferenceEquals(_sourceRenderer.Buffers, _rendererBufferOwner)
           && _meshBufferOwner?.MutationRevision == MeshBufferRevision
           && _rendererBufferOwner?.MutationRevision == RendererBufferRevision;

    /// <summary>Checks captured typed publisher versions before and after intentional render callbacks.</summary>
    public bool ArePublisherGenerationsCurrent
        => MatchGenerations(_rendererPublishers, _rendererPublisherGenerations, _rendererResourceGenerations)
           && MatchGenerations(_materialPublishers, _materialPublisherGenerations, _materialResourceGenerations);

    internal GpuMeshSubmissionSourceBindings CapturePublication(GpuMeshSubmissionSourceBindings? destination)
    {
        // Each ring slot owns its closure. Only an unleased slot reaches this method.
        destination ??= (GpuMeshSubmissionSourceBindings)MemberwiseClone();
        destination._sourceMesh = _sourceMesh;
        destination._sourceRenderer = _sourceRenderer;
        destination._sourceMaterial = _sourceMaterial;
        destination.MaterialShaderRevision = MaterialShaderRevision;
        destination._meshBufferOwner = _meshBufferOwner;
        destination._rendererBufferOwner = _rendererBufferOwner;
        destination._meshBuffers = _meshBuffers;
        destination._rendererBuffers = _rendererBuffers;
        destination._rendererPublishers = _rendererPublishers;
        destination._materialPublishers = _materialPublishers;
        destination.MeshBufferRevision = MeshBufferRevision;
        destination.RendererBufferRevision = RendererBufferRevision;
        CaptureGenerations(_rendererPublishers, ref destination._rendererPublisherGenerations, ref destination._rendererResourceGenerations);
        CaptureGenerations(_materialPublishers, ref destination._materialPublisherGenerations, ref destination._materialResourceGenerations);
        return destination;
    }

    internal void ReleaseRetainedSources()
    {
        _sourceMesh = null;
        _sourceRenderer = null;
        _sourceMaterial = null;
        _meshBufferOwner = null;
        _rendererBufferOwner = null;
        _meshBuffers = [];
        _rendererBuffers = [];
        _rendererPublishers = [];
        _materialPublishers = [];
    }

    private static void CaptureGenerations(IRenderBindingPublisher[] publishers, ref ulong[] generations, ref ulong[] resources)
    {
        if (generations.Length < publishers.Length)
        {
            generations = new ulong[publishers.Length];
            resources = new ulong[publishers.Length];
        }
        for (int index = 0; index < publishers.Length; index++)
        {
            generations[index] = publishers[index].Generation;
            resources[index] = publishers[index] is IRenderResourceBindingPublisher resource ? resource.ResourceGeneration : 0;
        }
    }

    private static bool MatchGenerations(IRenderBindingPublisher[] publishers, ulong[] generations, ulong[] resources)
    {
        if (generations.Length < publishers.Length)
            return false;
        for (int index = 0; index < publishers.Length; index++)
        {
            if (publishers[index].Generation != generations[index]
                || (publishers[index] is IRenderResourceBindingPublisher resource && resource.ResourceGeneration != resources[index]))
                return false;
        }
        return true;
    }

    private static bool TryGetBuffer(ReadOnlySpan<KeyValuePair<string, XRDataBuffer>> buffers,
        string name, out XRDataBuffer? buffer)
    {
        for (int index = 0; index < buffers.Length; index++)
        {
            if (!string.Equals(buffers[index].Key, name, StringComparison.Ordinal))
                continue;
            buffer = buffers[index].Value;
            return true;
        }
        buffer = null;
        return false;
    }
}
