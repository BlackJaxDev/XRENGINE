using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Publishes authored instance rows through an ordinary renderer-owned storage binding.</summary>
/// <remarks>
/// Create and publish at the scene update boundary, before the resident scene swap.
/// The caller supplies every current/previous matrix and bound; this owner never
/// fills missing rows with identity transforms or derives bounds from unknown shaders.
/// Disposing removes only its own binding and retires its buffer through the existing resource lifetime.
/// </remarks>
public sealed class AuthoredMeshInstancePublisher : IAuthoredMeshInstanceProvider, IDisposable
{
    private readonly XRMeshRenderer _owner;
    private readonly XRMesh _mesh;
    private readonly string _bindingName;
    private readonly bool _acceptsSharedDeformedGeometry;
    private bool _disposed;
    private uint _count;

    public XRDataBuffer<AuthoredMeshInstanceData> Buffer { get; }
    public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Instance;
    public ulong Generation { get; private set; } = 1;
    public ulong ResourceGeneration { get; private set; } = 1;
    public bool RequiresReadyDescriptorResources => true;

    public AuthoredMeshInstancePublisher(XRMeshRenderer owner, XRMesh mesh, string bindingName,
        ReadOnlySpan<AuthoredMeshInstanceData> instances, bool acceptsSharedDeformedGeometry = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        if (owner.Buffers.ContainsKey(bindingName))
            throw new ArgumentException("The renderer already owns this storage binding.", nameof(bindingName));
        _owner = owner;
        _mesh = mesh;
        _bindingName = bindingName;
        _acceptsSharedDeformedGeometry = acceptsSharedDeformedGeometry;
        Buffer = new(bindingName, EBufferTarget.ShaderStorageBuffer)
        {
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false,
        };
        try
        {
            Publish(instances);
            owner.Buffers.Add(bindingName, Buffer);
            owner.BindingPublishers.Add(this);
        }
        catch (Exception constructionError)
        {
            _disposed = true;
            try { ReleaseBinding(); }
            catch (Exception cleanupError) { throw new AggregateException(constructionError, cleanupError); }
            throw;
        }
    }

    /// <summary>Copies CPU-authored rows into the established buffer upload owner; never reads GPU results.</summary>
    public void Publish(ReadOnlySpan<AuthoredMeshInstanceData> instances)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint previousCount = _count;
        uint previousLength = Buffer.Length;
        ulong previousRevision = Buffer.Revision;
        Buffer.SetData(instances);
        _count = checked((uint)instances.Length);
        if (Buffer.Revision != previousRevision || _count != previousCount) Generation = checked(Generation + 1);
        if (_count != previousCount || Buffer.Length != previousLength) ResourceGeneration = checked(ResourceGeneration + 1);
    }

    public bool TryGetInstanceSource(XRMesh mesh, out AuthoredMeshInstanceSource source)
    {
        source = default;
        if (_disposed || !ReferenceEquals(mesh, _mesh)) return false;
        source = new(_bindingName, Buffer, _count, 144, 0, 64, 128, _acceptsSharedDeformedGeometry);
        return true;
    }

    // Storage publication is performed by the existing exact-name renderer binding owner.
    public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram) { }
    public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Generation = checked(Generation + 1);
        ResourceGeneration = checked(ResourceGeneration + 1);
        ReleaseBinding();
    }

    private void ReleaseBinding()
    {
        try { _owner.BindingPublishers.Remove(this); }
        finally
        {
            try
            {
                if (_owner.Buffers.TryGetValue(_bindingName, out XRDataBuffer? current) && ReferenceEquals(current, Buffer))
                    _owner.Buffers.Remove(_bindingName);
            }
            finally { Buffer.Dispose(); }
        }
    }
}
