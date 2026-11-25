using XREngine.Data.Core;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Owns immutable draw resources until every publication and backend claim ends.</summary>
public sealed class UIDrawPayload
{
    private int _references = 1;
    private int _resourcesReleased;
    private static readonly object RetirementGate = new();
    private static readonly List<UIDrawPayload> Quarantined = [];
    private readonly long _reservedBytes;
    private readonly XRShader[] _shaders;
    private readonly IUIDrawProducerLease? _producerLease;
    private readonly UITextureSnapshot?[] _textures;
    internal UIDrawGeometry Geometry { get; }
    internal UIWorldCropCapture? WorldCrop { get; }
    internal XRShader? WorldCropFragmentShader { get; }
    internal XRShader? WorldCropVertexShader { get; }

    internal UIDrawPayload(XRMeshRenderer renderer, XRMaterial material,
        RenderingParameters? renderOptions, XRShader[] shaders, long reservedBytes, IUIDrawProducerLease? producerLease, UITextureSnapshot?[] textures, UIDrawGeometry geometry,
        UIWorldCropCapture? worldCrop = null, XRShader? worldCropFragmentShader = null,
        XRShader? worldCropVertexShader = null)
    {
        Renderer = renderer;
        Material = material;
        RenderOptions = renderOptions;
        _shaders = shaders;
        _reservedBytes = reservedBytes;
        _producerLease = producerLease;
        _textures = textures;
        Geometry = geometry;
        WorldCrop = worldCrop;
        WorldCropFragmentShader = worldCropFragmentShader;
        WorldCropVertexShader = worldCropVertexShader;
    }

    internal XRMeshRenderer Renderer { get; }
    internal XRMaterial Material { get; }
    internal RenderingParameters? RenderOptions { get; }
    public bool IsRetired => Volatile.Read(ref _resourcesReleased) != 0;

    /// <summary>Retains resources for a publication, queued draw, or backend artifact.</summary>
    public void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _references, checked(current + 1), current) != current);
    }

    /// <summary>Releases one claim after both command replay and GPU use have ended.</summary>
    public void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            if (current == 0)
                throw new InvalidOperationException("UI draw payload claim was released twice.");
        } while (Interlocked.CompareExchange(ref _references, current - 1, current) != current);
        if (current != 1)
            return;
        try
        {
            if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                ReleaseResources();
            else
                RuntimeEngine.EnqueueRenderThreadTask(ReleaseResources,
                    "UI.DrawPayload.ReleaseResources", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception exception)
        {
            Quarantine(exception);
        }
    }

    private void ReleaseResources()
    {
        try
        {
            // Native owners must retire before any dependent producer or texture claim is released.
            DestroyOwner(Renderer);
            Renderer.Buffers.RetireAndDisposeOwnedBuffers();
            if (!Geometry.ReleaseFromRenderOwner())
                throw new InvalidOperationException("UI geometry retirement failed; dependent claims remain retained.");
            DestroyOwner(Material);
            DestroyOwner(Material.RenderOptions);
            if (RenderOptions is { } options)
                DestroyOwner(options);
            foreach (XRShader shader in _shaders)
                DestroyOwner(shader);
            foreach (UITextureSnapshot? texture in _textures)
                texture?.Release();
            _producerLease?.Release();
            UIDrawPayloadBuilder.ReleaseBudget(_reservedBytes);
            Volatile.Write(ref _resourcesReleased, 1);
        }
        catch (Exception exception) { Quarantine(exception); }
    }

    internal static void DestroyOwner(XRObjectBase owner)
    {
        owner.Destroy(now: true);
        if (!owner.IsDestroyed)
            throw new InvalidOperationException("UI draw resource destruction was vetoed; dependent claims remain retained.");
    }

    internal static XRDataBuffer[] CaptureOwnedBuffers(XRMeshRenderer? renderer, XRMesh? mesh)
    {
        KeyValuePair<string, XRDataBuffer>[] rendererBuffers = renderer?.Buffers.CaptureBindingSnapshot(out _) ?? [];
        KeyValuePair<string, XRDataBuffer>[] meshBuffers = mesh?.Buffers.CaptureBindingSnapshot(out _) ?? [];
        XRDataBuffer[] buffers = new XRDataBuffer[rendererBuffers.Length + meshBuffers.Length];
        for (int index = 0; index < rendererBuffers.Length; index++)
            buffers[index] = rendererBuffers[index].Value;
        for (int index = 0; index < meshBuffers.Length; index++)
            buffers[rendererBuffers.Length + index] = meshBuffers[index].Value;
        return buffers;
    }

    internal static void VerifyBufferRetirement(XRDataBuffer[] buffers)
    {
        foreach (XRDataBuffer buffer in buffers)
            if (!buffer.IsDestroyed)
                throw new InvalidOperationException("UI draw buffer destruction was vetoed; dependent claims remain retained.");
    }

    private void Quarantine(Exception exception)
    {
        lock (RetirementGate)
            Quarantined.Add(this);
        Debug.LogWarning($"UI.DrawPayload.RetirementFailed: resources and capture budget remain retained. {exception.Message}");
    }
}
