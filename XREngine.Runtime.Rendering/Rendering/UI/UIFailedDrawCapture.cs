using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Retires a partial capture without releasing its budget before cleanup succeeds.</summary>
internal sealed class UIFailedDrawCapture(
    XRMeshRenderer? renderer, XRMesh? mesh, XRMaterial? material,
    XRShader[] shaders, UITextureSnapshot?[] textures, IUIDrawProducerLease? producer, long bytes, UIDrawGeometry? geometry = null)
{
    private readonly XRDataBuffer[] _buffers = UIDrawPayload.CaptureOwnedBuffers(null, mesh);
    private static readonly object Gate = new();
    private static readonly List<UIFailedDrawCapture> Quarantined = [];

    internal void Retire()
    {
        try
        {
            if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                ReleaseResources();
            else
                RuntimeEngine.EnqueueRenderThreadTask(ReleaseResources,
                    "UI.DrawCapture.Rollback", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception exception) { Quarantine(exception); }
    }

    private void ReleaseResources()
    {
        try
        {
            if (renderer is not null)
            {
                UIDrawPayload.DestroyOwner(renderer);
                renderer.Buffers.RetireAndDisposeOwnedBuffers();
            }
            if (mesh is not null)
                UIDrawPayload.DestroyOwner(mesh);
            UIDrawPayload.VerifyBufferRetirement(_buffers);
            if (geometry is not null && !geometry.ReleaseFromRenderOwner())
                throw new InvalidOperationException("UI geometry retirement failed; dependent claims remain retained.");
            if (material is not null)
            {
                UIDrawPayload.DestroyOwner(material);
                UIDrawPayload.DestroyOwner(material.RenderOptions);
            }
            foreach (XRShader? shader in shaders)
                if (shader is not null)
                    UIDrawPayload.DestroyOwner(shader);
            foreach (UITextureSnapshot? texture in textures)
                texture?.Release();
            producer?.Release();
            UIDrawPayloadBuilder.ReleaseBudget(bytes);
        }
        catch (Exception exception) { Quarantine(exception); }
    }

    private void Quarantine(Exception exception)
    {
        lock (Gate)
            Quarantined.Add(this);
        Debug.LogWarning($"UI.DrawCapture.RollbackFailed: resources and capture budget remain retained. {exception.Message}");
    }
}
