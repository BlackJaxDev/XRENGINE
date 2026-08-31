using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns a production Vulkan renderer whose target supports deterministic,
/// presentation-independent frame submission.
/// </summary>
public sealed unsafe partial class VulkanExplicitTargetRendererHost :
    IRuntimeRendererHost,
    IMaterialTableBackendCapability,
    IDisposable
{
    private readonly VulkanRenderer _renderer;
    private bool _disposed;

    public VulkanExplicitTargetRendererHost(
        IRendererPresentationTarget target,
        long backendGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        _renderer = new VulkanRenderer(
            new RendererHostContext(target, backendGeneration: backendGeneration));

        try
        {
            _renderer.Initialize();
            if (!_renderer.HasExplicitFrameTarget)
            {
                throw new InvalidOperationException(
                    $"The Vulkan target '{target.ExecutionMode}' does not support explicit target-frame submission.");
            }
        }
        catch
        {
            try { _renderer.CleanUp(); }
            catch
            {
                // Preserve the initialization error when partial native teardown also fails.
            }

            throw;
        }
    }

    public RendererBackendId BackendId => RendererBackendId.Vulkan;
    public long BackendGeneration => _renderer.BackendGeneration;
    public bool IsDeviceLost => _renderer.IsDeviceLost || _renderer.ExplicitTargetIsDeviceLost;
    public EMeshShaderDialect MeshShaderDialect => _renderer.MeshShaderDialect;
    public string MeshletDispatchUnsupportedReason => _renderer.MeshletDispatchUnsupportedReason;
    public RenderTargetOutputProperties OutputProperties => _renderer.ExplicitTargetOutputProperties;
    public ulong TargetGeneration => _renderer.ExplicitTargetGeneration;
    public double LastCompletedGpuFrameNanoseconds => _renderer.ExplicitTargetLastCompletedGpuFrameNanoseconds;
    public string PresentationDescription => _renderer.ExplicitTargetPresentationDescription;
    /// <summary>Enables optional allocation attribution for explicit target frames.</summary>
    public bool ExplicitTargetAllocationDiagnosticsEnabled
    {
        get => _renderer.ExplicitTargetAllocationDiagnosticsEnabled;
        set => _renderer.ExplicitTargetAllocationDiagnosticsEnabled = value;
    }
    /// <summary>Allocation attribution for the most recently completed explicit target frame.</summary>
    public VulkanExplicitTargetFrameAllocationCounters LastExplicitTargetFrameAllocationCounters
        => _renderer.LastExplicitTargetFrameAllocationCounters;
    public bool PresentationUsesDesktopCompositor => false;
    public IReadOnlyList<string> EnabledInstanceExtensions => _renderer.EnabledInstanceExtensions;
    public IReadOnlyList<string> EnabledDeviceExtensions => _renderer.EnabledDeviceExtensions;
    public VulkanRenderer Renderer => _renderer;
    /// <summary>Native API used by prepared deterministic component fixtures.</summary>
    public Vk Api => _renderer.VulkanApi;
    /// <summary>Logical device used only to precreate fixture-owned Vulkan objects.</summary>
    public Device Device => _renderer.Device;
    /// <summary>Selected physical adapter handle for fixture memory-type selection.</summary>
    public PhysicalDevice PhysicalDevice => _renderer.PhysicalDevice;
    public Instance Instance => _renderer.Instance;
    /// <summary>Loaded debug-label extension, when enabled for this host.</summary>
    public ExtDebugUtils? DebugUtils => _renderer.DeviceContext.DebugUtils;
    /// <summary>Graphics queue family used for fixture-owned secondary command pools.</summary>
    public uint GraphicsQueueFamilyIndex => _renderer.DeviceContext.QueueFamilies.GraphicsFamilyIndex
        ?? throw new InvalidOperationException("The Vulkan host has no selected graphics queue family.");
    /// <summary>Whether dynamic rendering is enabled for the selected device.</summary>
    public bool SupportsDynamicRendering => _renderer.DeviceContext.SupportsDynamicRendering;

    /// <summary>Captures this device's cumulative native validation evidence before teardown.</summary>
    public VulkanValidationDiagnosticSnapshot CaptureValidationDiagnostics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var device = _renderer.DeviceContext;
        return device.ValidationDiagnostics.CaptureSnapshot(
            device.ValidationLayersEnabled,
            device.SynchronizationValidationEnabled,
            device.HasDebugMessenger);
    }

    public string AdapterName
    {
        get
        {
            _renderer.VulkanApi.GetPhysicalDeviceProperties(
                _renderer.PhysicalDevice,
                out PhysicalDeviceProperties properties);
            return SilkMarshal.PtrToString((nint)properties.DeviceName) ?? string.Empty;
        }
    }

    public uint DriverVersion
    {
        get
        {
            _renderer.VulkanApi.GetPhysicalDeviceProperties(
                _renderer.PhysicalDevice,
                out PhysicalDeviceProperties properties);
            return properties.DriverVersion;
        }
    }

    public uint VendorId
    {
        get
        {
            _renderer.VulkanApi.GetPhysicalDeviceProperties(
                _renderer.PhysicalDevice,
                out PhysicalDeviceProperties properties);
            return properties.VendorID;
        }
    }

    public uint DeviceId
    {
        get
        {
            _renderer.VulkanApi.GetPhysicalDeviceProperties(
                _renderer.PhysicalDevice,
                out PhysicalDeviceProperties properties);
            return properties.DeviceID;
        }
    }

    public bool SupportsIndirectCountDraw() => _renderer.SupportsIndirectCountDraw();
    public bool SupportsDirectMeshTaskDispatch() => _renderer.SupportsDirectMeshTaskDispatch();
    public bool SupportsIndirectCountMeshTaskDispatch() => _renderer.SupportsIndirectCountMeshTaskDispatch();
    public bool SupportsProductionMeshletShaders() => _renderer.SupportsProductionMeshletShaders();
    public bool SupportsMeshletDispatch() => _renderer.SupportsMeshletDispatch();
    private IMaterialTableBackendCapability MaterialTableCapability => _renderer;
    bool IMaterialTableBackendCapability.SupportsBufferDeviceAddress
        => MaterialTableCapability.SupportsBufferDeviceAddress;
    bool IMaterialTableBackendCapability.SupportsBindlessMaterialTable
        => MaterialTableCapability.SupportsBindlessMaterialTable;
    bool IMaterialTableBackendCapability.SupportsBindlessTextureHandles
        => MaterialTableCapability.SupportsBindlessTextureHandles;
    string IMaterialTableBackendCapability.BindlessMaterialUnavailableReason
        => MaterialTableCapability.BindlessMaterialUnavailableReason;
    bool IMaterialTableBackendCapability.TryEnsureMaterialTextureTable(out string reason)
        => MaterialTableCapability.TryEnsureMaterialTextureTable(out reason);
    XREngine.Rendering.Materials.MaterialTextureReferenceResolution IMaterialTableBackendCapability.ResolveMaterialTextureReference(
        XRTexture texture,
        string semantic)
        => MaterialTableCapability.ResolveMaterialTextureReference(texture, semantic);
    void IMaterialTableBackendCapability.FlushMaterialTextureTableUpdates()
        => MaterialTableCapability.FlushMaterialTextureTableUpdates();
    void IMaterialTableBackendCapability.ReleaseMaterialTextureReference(
        in XREngine.Rendering.Materials.GPUMaterialRetiredHandle retired)
        => MaterialTableCapability.ReleaseMaterialTextureReference(in retired);
    bool IMaterialTableBackendCapability.BeginGlobalMaterialTextureDescriptorScope(
        XRRenderProgram program,
        string consumer)
        => MaterialTableCapability.BeginGlobalMaterialTextureDescriptorScope(program, consumer);
    bool IMaterialTableBackendCapability.BeginGlobalMaterialTextureDescriptorScope(
        XRRenderProgram program,
        string consumer,
        XREngine.Rendering.Materials.GPUMaterialTablePublication? publication)
        => MaterialTableCapability.BeginGlobalMaterialTextureDescriptorScope(
            program,
            consumer,
            publication);
    void IMaterialTableBackendCapability.EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program)
        => MaterialTableCapability.EndGlobalMaterialTextureDescriptorScope(program);
    public AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
        => _renderer.GetAdvancedRenderPipelineCapabilities();

    public bool TryReserveAdvancedVisibilityFamily(ulong outputId, out AdvancedVisibilityFamilyReservation reservation, out string failureReason)
        => _renderer.TryReserveAdvancedVisibilityFamily(outputId, out reservation, out failureReason);

    public bool IsAdvancedVisibilityFamilyReservationCurrent(in AdvancedVisibilityFamilyReservation reservation)
        => _renderer.IsAdvancedVisibilityFamilyReservationCurrent(in reservation);

    public bool TryDrawMeshTasksIndirectCount(
        XRRenderProgram program,
        XRDataBuffer indirectBuffer,
        XRDataBuffer countBuffer,
        uint maxDrawCount,
        uint stride,
        out string failureReason,
        nuint byteOffset = 0,
        nuint countByteOffset = 0)
        => _renderer.TryDrawMeshTasksIndirectCount(
            program,
            indirectBuffer,
            countBuffer,
            maxDrawCount,
            stride,
            out failureReason,
            byteOffset,
            countByteOffset);

    /// <summary>Records and submits one real target-owned Vulkan frame.</summary>
    public void SubmitFrame(Action<Vk, CommandBuffer, VulkanRenderFrameTarget> record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer.SubmitExplicitTargetFrame(record);
    }

    /// <summary>
    /// Runs ordinary viewport/render-pipeline work against an acquired
    /// presentation-independent output. The callback should invoke the same
    /// <see cref="XRViewport.Render(XRFrameBuffer?, IRuntimeRenderWorld?, XRCamera?, bool, XRMaterial?)"/>
    /// path used by a desktop viewport; the host then records and submits the
    /// resulting production frame operations.
    /// First-use shader linking is synchronous within this explicit preparation
    /// scope, so shader warmup cannot silently substitute an empty first frame.
    /// This cold preparation cost is not steady-state performance evidence.
    /// </summary>
    public VulkanExplicitProductionSubmissionReceipt SubmitProductionFrame(
        Action<RenderFrameOutputDescription> buildFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buildFrame);
        return _renderer.SubmitExplicitProductionFrame(buildFrame);
    }

    public VulkanExplicitProductionSubmissionReceipt SubmitProductionFrame(
        Action<RenderFrameOutputDescription> buildFrame,
        VulkanExplicitProductionBufferStressProbeRequest probeRequest)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.SubmitExplicitProductionFrame(buildFrame, probeRequest);
    }

    public bool TryGetLastProductionBufferStressProbeEvidence(out VulkanExplicitProductionBufferStressProbeEvidence? evidence)
        => _renderer.TryGetLastExplicitProductionBufferStressProbeEvidence(out evidence);

    /// <summary>
    /// Polls optional Hi-Z timestamps without waiting, after authenticating a completed
    /// production receipt. Samples retain their own source frames and can be older
    /// than the receipt; callers must not attribute them to the current frame.
    /// </summary>
    public bool TryGetProductionOcclusionTiming(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        out Occlusion.OcclusionGpuElapsedSample build,
        out Occlusion.OcclusionGpuElapsedSample test,
        out Occlusion.OcclusionGpuElapsedRingDiagnostic ring)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        build = default;
        test = default;
        ring = default;
        if (!TryGetProductionSubmissionCompletion(in receipt, out bool completed) || !completed)
            return false;
        using var rendererScope = AbstractRenderer.PushThreadCurrent(_renderer);
        Occlusion.OcclusionGpuElapsedTiming timing = Occlusion.OcclusionGpuElapsedTiming.Instance;
        timing.Resolve(_renderer, receipt.EngineFrameId);
        build = timing.GetSample(Occlusion.EOcclusionGpuElapsedStage.Build, receipt.EngineFrameId);
        test = timing.GetSample(Occlusion.EOcclusionGpuElapsedStage.Test, receipt.EngineFrameId);
        ring = timing.CaptureRingDiagnostic(_renderer);
        return true;
    }

    /// <summary>Nonblocking completion query for an authentic submission from this host.</summary>
    public bool TryGetProductionSubmissionCompletion(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        out bool completed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.TryGetExplicitProductionSubmissionCompletion(in receipt, out completed);
    }

    /// <summary>
    /// Reads the target color only after this exact, current receipt has completed.
    /// This cold diagnostic path does not change the production zero-readback policy.
    /// </summary>
    public bool TryReadbackProductionColor(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        int maxByteCount,
        out byte[]? color,
        ImageLayout sourceLayout = ImageLayout.TransferSrcOptimal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.TryReadbackExplicitProductionColor(in receipt, maxByteCount, sourceLayout, out color);
    }

    /// <summary>
    /// Reads a bounded GPU buffer range only after this exact, current receipt has completed.
    /// </summary>
    public bool TryReadbackProductionBuffer(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        XRDataBuffer sourceBuffer,
        uint sourceByteOffset,
        Span<byte> destination,
        out string route)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sourceBuffer);
        return _renderer.TryReadbackExplicitProductionBuffer(
            in receipt,
            sourceBuffer,
            sourceByteOffset,
            destination,
            out route);
    }

    /// <summary>Captures current native buffer identity from Vulkan's lifetime ledger.</summary>
    public bool TryDescribeCurrentNativeBuffer(
        XRDataBuffer sourceBuffer,
        out VulkanNativeBufferDiagnosticDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.TryDescribeCurrentNativeBuffer(sourceBuffer, out description);
    }

    /// <summary>Reads the last completed color output after the measured interval.</summary>
    public byte[] ReadbackLastSubmittedColor(
        int maxByteCount,
        ImageLayout sourceLayout = ImageLayout.TransferSrcOptimal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.ReadbackExplicitTargetColor(maxByteCount, sourceLayout);
    }

    /// <summary>Computes a SHA-256 hash through the target driver's bounded readback path.</summary>
    public string ComputeLastSubmittedColorHash(
        ImageLayout sourceLayout = ImageLayout.TransferSrcOptimal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.ComputeExplicitTargetColorHash(sourceLayout);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _renderer.CleanUp();
    }
}
