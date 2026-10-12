namespace XREngine.Rendering.Commands;

public sealed partial class GpuMeshSubmissionSourceBindings
{
    public AuthoredMeshInstanceSource? InstanceSource { get; private set; }
    public bool HasAmbiguousInstanceSources { get; private set; }
    private ulong _instanceBufferRevision;
    private uint _instanceBufferLength;

    private void CaptureInstanceSource()
    {
        InstanceSource = null;
        HasAmbiguousInstanceSources = false;
        _instanceBufferRevision = 0;
        _instanceBufferLength = 0;
        if (_sourceMesh is null) return;
        foreach (IRenderBindingPublisher publisher in _rendererPublishers)
        {
            if (publisher is not IAuthoredMeshInstanceProvider provider ||
                !provider.TryGetInstanceSource(_sourceMesh, out AuthoredMeshInstanceSource source)) continue;
            if (InstanceSource.HasValue) HasAmbiguousInstanceSources = true;
            InstanceSource = source;
            _instanceBufferRevision = source.Buffer?.Revision ?? 0;
            _instanceBufferLength = source.Buffer?.Length ?? 0;
        }
    }

    /// <summary>Checks exact borrowed instance identity/layout against the frozen renderer membership.</summary>
    public bool AreInstanceBindingsCurrent
        => !HasAmbiguousInstanceSources && (!InstanceSource.HasValue ||
           InstanceSource is { } source && source.HasValidLayout && source.Buffer.Revision == _instanceBufferRevision &&
           source.Buffer.Length == _instanceBufferLength &&
           TryGetRendererBuffer(source.BindingName, out XRDataBuffer? buffer) && ReferenceEquals(buffer, source.Buffer));
}
