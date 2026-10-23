namespace XREngine.Rendering.UI;

/// <summary>Shares unchanged immutable mesh and fixed buffer storage between draw materials.</summary>
internal sealed class UIDrawGeometry
{
    private static readonly List<UIDrawGeometry> Quarantined = [];
    private static readonly object Gate = new();
    private readonly UISharedDrawResource _mesh;
    private readonly UISharedDrawResource?[] _buffers;
    internal XRMesh Mesh => (XRMesh)_mesh.Owner;
    internal KeyValuePair<string, XRDataBuffer>[] MeshBuffers { get; }
    internal KeyValuePair<string, XRDataBuffer>[] RendererBuffers { get; }
    internal XRDataBuffer[] FixedBuffers { get; }

    private UIDrawGeometry(UISharedDrawResource mesh, int count)
    {
        _mesh = mesh;
        _buffers = new UISharedDrawResource?[count];
        MeshBuffers = Mesh.Buffers.CaptureBindingSnapshot(out _);
        RendererBuffers = new KeyValuePair<string, XRDataBuffer>[count];
        FixedBuffers = new XRDataBuffer[count];
    }

    internal static UIDrawGeometry Capture(XRMesh source,
        KeyValuePair<string, XRDataBuffer>[] meshBuffers,
        KeyValuePair<string, XRDataBuffer>[] rendererBuffers, long meshBytes, UIDrawGeometry? previous,
        KeyValuePair<string, XRDataBuffer>[] previousSources, ulong[] previousRevisions, bool committedGlyphs)
    {
        UISharedDrawResource mesh;
        if (previous is not null && UIDrawPayloadBuilder.MeshMetadataEquals(source, previous.Mesh) &&
            UIDrawPayloadBuilder.BuffersEqual(source.Buffers, previous.MeshBuffers, meshBuffers))
        {
            mesh = previous._mesh;
            mesh.Retain();
        }
        else
            mesh = UISharedDrawResource.CaptureMesh(source, meshBytes);
        UIDrawGeometry? geometry = null;
        try
        {
            geometry = new(mesh, rendererBuffers.Length);
            for (int index = 0; index < rendererBuffers.Length; index++)
            {
                var entry = rendererBuffers[index];
                UISharedDrawResource? resource = null;
                if (previous is not null)
                    for (int oldIndex = 0; oldIndex < previous.RendererBuffers.Length; oldIndex++)
                        if (previous.RendererBuffers[oldIndex].Key == entry.Key &&
                            ((committedGlyphs && oldIndex < previousSources.Length && oldIndex < previousRevisions.Length &&
                              ReferenceEquals(entry.Value, previousSources[oldIndex].Value) &&
                              entry.Value.Revision == previousRevisions[oldIndex] &&
                              UIDrawPayloadBuilder.BufferMetadataEquals(entry.Value, previous.RendererBuffers[oldIndex].Value)) ||
                             UIDrawPayloadBuilder.BufferEquals(entry.Value, previous.RendererBuffers[oldIndex].Value)))
                        {
                            resource = previous._buffers[oldIndex]!;
                            resource.Retain();
                            break;
                        }
                resource ??= UISharedDrawResource.CaptureBuffer(entry.Value, UIDrawPayloadBuilder.MeasureBuffer(entry.Value));
                geometry._buffers[index] = resource;
                XRDataBuffer buffer = (XRDataBuffer)resource.Owner;
                geometry.RendererBuffers[index] = new(entry.Key, buffer);
                geometry.FixedBuffers[index] = buffer;
            }
            return geometry;
        }
        catch
        {
            if (geometry is not null)
                geometry.RetirePartialCapture();
            else
                mesh.RetirePartialCapture();
            throw;
        }
    }

    internal bool ReleaseFromRenderOwner()
    {
        // Stop if native retirement fails. Remaining claims must stay quarantined with this owner.
        if (!_mesh.ReleaseFromRenderOwner())
            return Quarantine();
        foreach (UISharedDrawResource? buffer in _buffers)
            if (buffer is not null && !buffer.ReleaseFromRenderOwner())
                return Quarantine();
        return true;
    }

    private bool Quarantine()
    {
        lock (Gate)
            Quarantined.Add(this);
        return false;
    }

    private void RetirePartialCapture()
    {
        try
        {
            if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                ReleaseFromRenderOwner();
            else
                RuntimeEngine.EnqueueRenderThreadTask(() => ReleaseFromRenderOwner(),
                    "UI.DrawGeometry.Rollback", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception exception)
        {
            Quarantine();
            Debug.LogWarning($"UI.DrawGeometry.RollbackFailed: resource claims remain retained. {exception.Message}");
        }
    }
}
