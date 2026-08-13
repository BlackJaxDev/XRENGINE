using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns a production Vulkan renderer whose target supports deterministic,
/// presentation-independent frame submission.
/// </summary>
public sealed unsafe class VulkanExplicitTargetRendererHost : IRuntimeRendererHost, IDisposable
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
    public bool PresentationUsesDesktopCompositor => false;
    public IReadOnlyList<string> EnabledInstanceExtensions => _renderer.EnabledInstanceExtensions;
    public IReadOnlyList<string> EnabledDeviceExtensions => _renderer.EnabledDeviceExtensions;
    public VulkanRenderer Renderer => _renderer;

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
    public AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
        => _renderer.GetAdvancedRenderPipelineCapabilities();

    public bool TryDrawMeshTasksIndirectCount(
        XRDataBuffer indirectBuffer,
        XRDataBuffer countBuffer,
        uint maxDrawCount,
        uint stride,
        out string failureReason,
        nuint byteOffset = 0,
        nuint countByteOffset = 0)
        => _renderer.TryDrawMeshTasksIndirectCount(
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
