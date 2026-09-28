using System.Buffers.Binary;

namespace XREngine.Rendering.WebGPU;

/// <summary>Owns the managed half of one WebGPU session and its resource identities.</summary>
public sealed partial class WebGpuRendererHost : IBrowserRendererHost
{
    private readonly BrowserCanvasRenderTarget _target;
    private readonly Action<WebGpuRendererHost> _onDisposed;
    private readonly HashSet<int> _resources = [];
    private int _session;
    private bool _submittedFrame;
    private bool _deviceLost;

    internal WebGpuRendererHost(BrowserCanvasRenderTarget target, long generation, Action<WebGpuRendererHost> onDisposed)
    {
        _target = target;
        BackendGeneration = generation;
        _onDisposed = onDisposed;
    }

    public RendererBackendId BackendId => RendererBackendId.WebGPU;
    public long BackendGeneration { get; }
    public BrowserRendererState State { get; private set; } = BrowserRendererState.Pending;
    public bool IsDeviceLost => _deviceLost;
    public bool IsBackendReplacementFrameReady => State == BrowserRendererState.Ready && _submittedFrame;

    public bool TryDescribeFrameOutput(out RenderFrameOutputDescription output)
    {
        output = default;
        return State == BrowserRendererState.Ready && _target.TryDescribeFrameOutput(out output);
    }

    public void MarkReady(int sessionId)
    {
        if (State != BrowserRendererState.Pending || sessionId <= 0)
            throw new InvalidOperationException("Only a pending renderer can bind one positive browser session.");
        DeviceCapabilities = ReadCapabilities(sessionId);
        _session = sessionId;
        State = BrowserRendererState.Ready;
    }

    public void MarkFailed(bool deviceLost)
    {
        if (State == BrowserRendererState.Disposed)
            return;
        _deviceLost |= deviceLost;
        State = _deviceLost ? BrowserRendererState.Lost : BrowserRendererState.Failed;
        _submittedFrame = false;
        DeviceCapabilities = null;
    }

    private void RequireReady()
    {
        if (State != BrowserRendererState.Ready)
            throw new InvalidOperationException($"WebGPU renderer is {State}; wait for startup or restart the canvas.");
    }

    private int Track(int handle)
    {
        _ = BrowserResourceHandle.FromPacked(handle);
        if (!_resources.Add(handle))
            throw new InvalidOperationException("WebGPU returned a duplicate live resource handle.");
        return handle;
    }

    public int CreateMesh(BrowserMeshData mesh)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(mesh);
        // Immutable descriptors cross an assembly boundary. These bounded copies occur
        // only when resources are created, never for existing draws in the frame loop.
        byte[] vertices = mesh.CopyVertexBytes();
        byte[] indices = mesh.CopyIndexBytes();
        return Track(WebGpuImports.CreateMesh(_session, vertices, indices));
    }

    public int CreateTexture(BrowserTextureData texture)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(texture);
        byte[] pixels = texture.CopyRgbaBytes();
        return Track(WebGpuImports.CreateTexture(_session, texture.Width, texture.Height, pixels));
    }

    public int CreateMaterial(BrowserMaterialData material, int textureHandle)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(material);
        if ((material.Texture is null) != (textureHandle == 0) || (textureHandle != 0 && !_resources.Contains(textureHandle)))
            throw new InvalidOperationException("Material texture must belong to this renderer and match its descriptor.");
        var tint = material.Tint;
        int handle = Track(WebGpuImports.CreateMaterial(_session, textureHandle, tint.X, tint.Y, tint.Z, tint.W));
        try
        {
            ConfigureMaterial(BrowserResourceHandle.FromPacked(handle), material);
            return handle;
        }
        catch
        {
            DestroyResource(handle);
            throw;
        }
    }

    public void DestroyResource(int handle)
    {
        if (!_resources.Contains(handle))
            throw new InvalidOperationException("Resource does not belong to this WebGPU renderer.");
        if (State is BrowserRendererState.Failed or BrowserRendererState.Lost)
        {
            // The terminal executor disposal releases physical resources as one device lifetime.
            _resources.Remove(handle);
            return;
        }
        RequireReady();
        WebGpuImports.DestroyResource(_session, handle);
        _resources.Remove(handle);
    }

    public void SubmitPacket(BrowserFramePacket packet)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(packet);
        Span<byte> bytes = packet.BeginConsume();
        try
        {
            if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output) ||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24)) != _session ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(32)) != output.TargetGeneration ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(84)) != output.Properties.Width ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(88)) != output.Properties.Height)
                throw new InvalidOperationException("Frame owner, extent or target generation does not match a drawable renderer output.");
            WebGpuImports.SubmitPacket(_session, bytes);
            _submittedFrame = true;
        }
        finally
        {
            packet.EndConsume();
        }
    }

    public void SubmitUploads(BrowserUploadBatch batch)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(batch);
        batch.BeginConsume(out Span<byte> commands, out Span<byte> payload);
        try
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(commands.Slice(24)) != _session ||
                BinaryPrimitives.ReadInt32LittleEndian(commands.Slice(28)) != _session)
                throw new InvalidOperationException("Upload owner and device generation must match this renderer.");
            WebGpuImports.SubmitUploads(_session, commands, payload);
        }
        finally
        {
            batch.EndConsume();
        }
    }

    public void CopyTexture(BrowserTextureCopyDescription copy)
    {
        RequireReady();
        copy.Validate();
        if (!_resources.Contains(copy.SourceHandle) || !_resources.Contains(copy.DestinationHandle))
            throw new InvalidOperationException("Both copy textures must belong to this WebGPU renderer.");
        WebGpuImports.CopyTexture(_session, copy.SourceHandle, copy.DestinationHandle,
            copy.SourceX, copy.SourceY, copy.DestinationX, copy.DestinationY, copy.Width, copy.Height);
    }

    public Task CompleteSubmittedWorkAsync(CancellationToken cancellationToken = default) =>
        CompleteSubmittedWorkTicketAsync(cancellationToken);

    public void Dispose()
    {
        if (State == BrowserRendererState.Disposed)
            return;
        State = BrowserRendererState.Disposed;
        DeviceCapabilities = null;
        _submittedFrame = false;
        try
        {
            if (_session != 0)
                WebGpuImports.DisposeRenderer(_session);
        }
        finally
        {
            _resources.Clear();
            _session = 0;
            _onDisposed(this);
        }
    }
}
