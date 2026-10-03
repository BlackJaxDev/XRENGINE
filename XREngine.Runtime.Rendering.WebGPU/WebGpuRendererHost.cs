using System.Buffers.Binary;

namespace XREngine.Rendering.WebGPU;

/// <summary>Owns the managed half of one WebGPU session and its resource identities.</summary>
public sealed partial class WebGpuRendererHost : AbstractRenderer, IBrowserRendererHost
{
    private readonly IBrowserCanvasPresentationTarget _target;
    private readonly Action<WebGpuRendererHost> _onDisposed;
    private readonly HashSet<int> _resources = [];
    private int _session;
    private bool _submittedFrame;
    private bool _deviceLost;
    private BrowserRendererState _state = BrowserRendererState.Pending;

    internal WebGpuRendererHost(IBrowserCanvasPresentationTarget target, long generation, Action<WebGpuRendererHost> onDisposed)
        : base(new RendererHostContext(target, backendGeneration: generation))
    {
        _target = target;
        _onDisposed = onDisposed;
    }

    public override RendererBackendId BackendId => RendererBackendId.WebGPU;
    public override bool RequiresAtomicFrameAuthoring => true;
    public BrowserRendererState State
    {
        get => _state;
        private set => SetField(ref _state, value);
    }
    public override bool IsDeviceLost => _deviceLost;
    public override bool IsBackendReplacementFrameReady => State == BrowserRendererState.Ready && _submittedFrame;

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
        SetField(ref _session, sessionId);
        State = BrowserRendererState.Ready;
    }

    public void MarkFailed(bool deviceLost)
    {
        if (State == BrowserRendererState.Disposed)
            return;
        SetField(ref _deviceLost, _deviceLost || deviceLost);
        State = _deviceLost ? BrowserRendererState.Lost : BrowserRendererState.Failed;
        SetField(ref _submittedFrame, false);
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
            SetField(ref _submittedFrame, true);
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

    public override void Dispose()
    {
        if (State == BrowserRendererState.Disposed)
            return;
        BeginBackendRetirement();
        try
        {
            try
            {
                PrepareForApiObjectTeardown();
                _indirectCountKernel?.Dispose();
                SetField(ref _indirectCountKernel, null, publishNotifications: false);
                DestroyMeshDeformationResources();
                DestroyAutoExposureHistories();
                DestroyCachedAPIRenderObjects();
            }
            finally
            {
                DestroyDirectionalShadowDefaults();
                DestroyAmbientOcclusionDefaults();
                DestroyAdvancedStagePrograms();
            }
        }
        finally
        {
            State = BrowserRendererState.Disposed;
            ArmPendingEngineFences();
            DeviceCapabilities = null;
            SetField(ref _submittedFrame, false);
            try
            {
                if (_session != 0)
                    WebGpuImports.DisposeRenderer(_session);
            }
            finally
            {
                DisposeAdvancedSceneResidency();
                _resources.Clear();
                SetField(ref _engineClearCommands, 0);
                SetField(ref _engineUniformBuffer, 0);
                SetField(ref _engineUniformArena, null);
                SetField(ref _engineViewport, null);
                SetField(ref _shaderArtifacts, null);
                SetField(ref _materialVariants, null);
                SetField(ref _session, 0);
                _onDisposed(this);
            }
        }
    }
}
