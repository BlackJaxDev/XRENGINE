using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using DxgiFormat = Silk.NET.DXGI.Format;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the DXGI flip-model swapchain of one window and the two fences that order
/// Vulkan rendering, the Direct3D copy, and presentation.
/// </summary>
/// <remarks>
/// <para>
/// One presenter lives for the whole output of a window. A window can have only one
/// flip-model swapchain, so a resize calls <c>ResizeBuffers</c> on the same swapchain.
/// Each generation owns a separate <see cref="VulkanDxgiImageRing"/>. All presenters
/// share one <see cref="VulkanDxgiDevice"/>.
/// </para>
/// <para>
/// Each fence has one producer, so each value sequence increases monotonically:
/// Vulkan signals the shared <c>vulkanDone</c> fence (imported as a Vulkan timeline
/// semaphore), and Direct3D signals the <c>copyDone</c> fence after it copies a shared
/// image into the back buffer. The render thread calls every member.
/// </para>
/// </remarks>
internal sealed unsafe class VulkanDxgiPresenter : IDisposable
{
    private const uint BufferCount = 3;
    private const uint MaximumFrameLatency = 2;
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const uint DxgiMwaNoAltEnter = 0x2;
    private const uint DxgiPresentAllowTearing = 0x200;
    private const uint ColorSpaceSupportPresent = 0x1;
    private const uint DisposeWaitMilliseconds = 1000;

    /// <summary>Auxiliary log that records the active present path, fallbacks, and faults in every build configuration.</summary>
    internal const string LogFileName = "desktop-present-path.log";

    private readonly Vk _api;
    private readonly Device _vkDevice;
    private readonly nint _hwnd;
    private readonly VulkanDxgiDevice _shared;
    private IDXGISwapChain1* _swapchain;
    private IDXGISwapChain3* _swapchain3;
    private nint _latencyWaitable;
    private bool _latencyWaitConsumed;
    private ID3D11Fence* _vulkanDoneFence;
    private ID3D11Fence* _copyDoneFence;
    private nint _copyDoneEvent;
    private VkSemaphore _vulkanDoneSemaphore;
    private ulong _lastVulkanDoneValue;
    private ulong _lastCopyDoneValue;
    private uint _swapchainFlags;
    private bool _disposed;

    private VulkanDxgiPresenter(Vk api, Device vkDevice, nint hwnd, VulkanDxgiDevice shared)
    {
        _api = api;
        _vkDevice = vkDevice;
        _hwnd = hwnd;
        _shared = shared;
    }

    /// <summary>True when the DXGI factory reports tearing support for variable refresh displays.</summary>
    internal bool TearingSupported => _shared.TearingSupported;

    /// <summary>The shared Direct3D 11 device. Other presenters can reuse it.</summary>
    internal VulkanDxgiDevice SharedDevice => _shared;

    /// <summary>True after a Direct3D or DXGI failure. The output then must stop using this presenter.</summary>
    internal bool Faulted { get; private set; }

    /// <summary>The reason for <see cref="Faulted"/>, or an empty string.</summary>
    internal string FaultReason { get; private set; } = string.Empty;

    /// <summary>True after <see cref="Dispose"/>.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>The output format that the swapchain currently uses.</summary>
    internal VulkanDxgiSurfaceFormat SurfaceFormat { get; private set; } = VulkanDxgiSurfaceFormat.Sdr;

    /// <summary>The Vulkan timeline semaphore imported from the shared <c>vulkanDone</c> fence.</summary>
    internal VkSemaphore VulkanDoneSemaphore => _vulkanDoneSemaphore;

    /// <summary>The Direct3D 11 device that owns the shared textures.</summary>
    internal ID3D11Device* D3DDevice => _shared.Device;

    /// <summary>
    /// Creates a presenter for a window. When this returns false, <paramref name="reason"/>
    /// tells why and no native object remains.
    /// </summary>
    /// <param name="sharedDevice">
    /// The device of another presenter to reuse, or null to create a new device.
    /// </param>
    internal static bool TryCreate(
        Vk api,
        VulkanDeviceContext device,
        nint hwnd,
        VulkanDxgiDevice? sharedDevice,
        out VulkanDxgiPresenter? presenter,
        out string reason)
    {
        presenter = null;
        if (!OperatingSystem.IsWindows())
        {
            reason = "the DXGI present path requires Windows";
            return false;
        }

        if (hwnd == 0)
        {
            reason = "the desktop window has no Win32 handle";
            return false;
        }

        if (!device.SupportsExternalMemoryWin32 || !device.SupportsExternalSemaphoreWin32)
        {
            reason = "VK_KHR_external_memory_win32 or VK_KHR_external_semaphore_win32 is not enabled";
            return false;
        }

        if (!device.MutableCapabilities._supportsTimelineSemaphores)
        {
            reason = "timeline semaphores are not enabled";
            return false;
        }

        VulkanDxgiDevice shared;
        if (sharedDevice is null)
        {
            if (!VulkanDxgiDevice.TryCreate(device, out VulkanDxgiDevice? createdDevice, out reason))
                return false;
            shared = createdDevice!;
        }
        else
        {
            if (sharedDevice.IsReleased)
            {
                reason = "the shared Direct3D device was already released";
                return false;
            }
            sharedDevice.AddReference();
            shared = sharedDevice;
        }

        // From here, the presenter owns one reference to the device; Dispose releases it.
        VulkanDxgiPresenter created = new(api, device.Device, hwnd, shared);
        try
        {
            if (!created.TryCreateFences(device, out reason))
            {
                created.Dispose();
                return false;
            }
        }
        catch (Exception exception)
        {
            created.Dispose();
            reason = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }

        presenter = created;
        return true;
    }

    private bool TryCreateFences(VulkanDeviceContext device, out string reason)
    {
        Guid fenceId = ID3D11Fence.Guid;
        ID3D11Fence* vulkanDone = null;
        int hr = _shared.Device5->CreateFence(0UL, FenceFlag.Shared, &fenceId, (void**)&vulkanDone);
        if (hr < 0)
        {
            reason = $"creating the shared Direct3D fence failed (0x{hr:X8})";
            return false;
        }
        _vulkanDoneFence = vulkanDone;

        ID3D11Fence* copyDone = null;
        hr = _shared.Device5->CreateFence(0UL, FenceFlag.None, &fenceId, (void**)&copyDone);
        if (hr < 0)
        {
            reason = $"creating the copy fence failed (0x{hr:X8})";
            return false;
        }
        _copyDoneFence = copyDone;

        _copyDoneEvent = VulkanDxgiWin32.CreateEvent(0, manualReset: false, initialState: false, 0);
        if (_copyDoneEvent == 0)
        {
            reason = "CreateEvent failed for the copy fence";
            return false;
        }

        void* sharedHandle = null;
        hr = _vulkanDoneFence->CreateSharedHandle(null, VulkanDxgiWin32.GenericAll, (char*)null, &sharedHandle);
        if (hr < 0)
        {
            reason = $"exporting the shared Direct3D fence failed (0x{hr:X8})";
            return false;
        }

        nint handle = (nint)sharedHandle;
        try
        {
            return TryImportVulkanDoneSemaphore(device, handle, out reason);
        }
        finally
        {
            // Importing a Win32 handle does not take ownership of it.
            VulkanDxgiWin32.CloseIfValid(ref handle);
        }
    }

    private bool TryImportVulkanDoneSemaphore(VulkanDeviceContext device, nint handle, out string reason)
    {
        KhrExternalSemaphoreWin32 semaphoreWin32 = device.ExtensionFunctions.KhrExternalSemaphoreWin32
            ?? throw new InvalidOperationException("VK_KHR_external_semaphore_win32 functions are not loaded.");

        SemaphoreTypeCreateInfo typeInfo = new()
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0UL,
        };
        SemaphoreCreateInfo createInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &typeInfo,
        };
        VkSemaphore semaphore;
        Result result = _api.CreateSemaphore(_vkDevice, &createInfo, null, &semaphore);
        if (result != Result.Success)
        {
            reason = $"creating the Vulkan timeline semaphore failed ({result})";
            return false;
        }
        _vulkanDoneSemaphore = semaphore;
        device.SetDebugObjectName(ObjectType.Semaphore, semaphore.Handle, "DxgiPresent.VulkanDone");

        ImportSemaphoreWin32HandleInfoKHR importInfo = new()
        {
            SType = StructureType.ImportSemaphoreWin32HandleInfoKhr,
            Semaphore = semaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.D3D12FenceBit,
            Handle = handle,
        };
        result = semaphoreWin32.ImportSemaphoreWin32Handle(_vkDevice, &importInfo);
        if (result != Result.Success)
        {
            reason = $"importing the shared Direct3D fence into Vulkan failed ({result})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Creates the swapchain on the first call and resizes it after that. The format
    /// follows the native Vulkan HDR preference order: scRGB, then HDR10, then SDR. An
    /// HDR format is used only when the display accepts its color space.
    /// </summary>
    internal bool TryConfigureBuffers(uint width, uint height, bool preferHdr, out string reason)
    {
        ReadOnlySpan<VulkanDxgiSurfaceFormat> candidates = preferHdr
            ? [VulkanDxgiSurfaceFormat.ScRgb, VulkanDxgiSurfaceFormat.Hdr10, VulkanDxgiSurfaceFormat.Sdr]
            : [VulkanDxgiSurfaceFormat.Sdr];
        reason = string.Empty;
        for (int index = 0; index < candidates.Length; index++)
        {
            VulkanDxgiSurfaceFormat candidate = candidates[index];
            if (!TrySetBufferFormat(width, height, candidate.BufferFormat, out reason))
                return false;

            uint support = 0;
            int hr = _swapchain3->CheckColorSpaceSupport(candidate.DxgiColorSpace, &support);
            if (hr < 0 || (support & ColorSpaceSupportPresent) == 0)
            {
                reason = $"the display does not accept {candidate.Name}";
                continue;
            }

            hr = _swapchain3->SetColorSpace1(candidate.DxgiColorSpace);
            if (hr < 0)
            {
                reason = $"SetColorSpace1 failed for {candidate.Name} (0x{hr:X8})";
                continue;
            }

            SurfaceFormat = candidate;
            reason = string.Empty;
            return true;
        }

        return false;
    }

    private bool TrySetBufferFormat(uint width, uint height, DxgiFormat format, out string reason)
    {
        int hr;
        if (_swapchain is null)
            return TryCreateSwapchain(width, height, format, out reason);

        // Flip-model buffers must have no outstanding references before a resize.
        _shared.Context->ClearState();
        _shared.Context->Flush();
        hr = _swapchain->ResizeBuffers(BufferCount, width, height, format, _swapchainFlags);
        if (hr < 0)
        {
            reason = $"ResizeBuffers to {width}x{height} {format} failed (0x{hr:X8})";
            MarkFaulted(reason);
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryCreateSwapchain(uint width, uint height, DxgiFormat format, out string reason)
    {
        _swapchainFlags = (uint)SwapChainFlag.FrameLatencyWaitableObject;
        if (TearingSupported)
            _swapchainFlags |= (uint)SwapChainFlag.AllowTearing;

        SwapChainDesc1 desc = new()
        {
            Width = width,
            Height = height,
            Format = format,
            Stereo = false,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            BufferUsage = DxgiUsageRenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = _swapchainFlags,
        };

        IDXGISwapChain1* swapchain = null;
        int hr = _shared.Factory->CreateSwapChainForHwnd((IUnknown*)_shared.Device, _hwnd, &desc, null, null, &swapchain);
        if (hr < 0)
        {
            reason = $"CreateSwapChainForHwnd failed (0x{hr:X8}); another swapchain may own the window";
            return false;
        }
        _swapchain = swapchain;
        _ = _shared.Factory->MakeWindowAssociation(_hwnd, DxgiMwaNoAltEnter);

        Guid swapchain3Id = IDXGISwapChain3.Guid;
        IDXGISwapChain3* swapchain3 = null;
        if (_swapchain->QueryInterface(&swapchain3Id, (void**)&swapchain3) < 0)
        {
            reason = "IDXGISwapChain3 is not available";
            return false;
        }
        _swapchain3 = swapchain3;

        _ = _swapchain3->SetMaximumFrameLatency(MaximumFrameLatency);
        _latencyWaitable = (nint)_swapchain3->GetFrameLatencyWaitableObject();
        reason = string.Empty;
        return true;
    }

    /// <summary>Returns the next value that a Vulkan present submission signals on the shared fence.</summary>
    internal ulong ReserveVulkanDoneValue()
        => ++_lastVulkanDoneValue;

    /// <summary>
    /// Waits until Direct3D has finished the copy that read a ring slot and until the
    /// swapchain accepts another frame. A zero timeout never blocks.
    /// </summary>
    internal Result WaitForFrameSlot(ulong slotCopyDoneValue, uint timeoutMilliseconds)
    {
        if (Faulted || _disposed)
            return Result.ErrorOutOfDateKhr;

        if (_copyDoneFence->GetCompletedValue() < slotCopyDoneValue)
        {
            if (timeoutMilliseconds == 0)
                return Result.NotReady;

            int hr = _copyDoneFence->SetEventOnCompletion(slotCopyDoneValue, (void*)_copyDoneEvent);
            if (hr < 0)
            {
                MarkFaulted($"SetEventOnCompletion failed (0x{hr:X8})");
                return Result.ErrorOutOfDateKhr;
            }

            uint wait = VulkanDxgiWin32.WaitForSingleObject(_copyDoneEvent, timeoutMilliseconds);
            if (wait != VulkanDxgiWin32.WaitObject0)
                return Result.Timeout;
        }

        // The latency object counts frames that the swapchain can accept. A wait that
        // was consumed by an acquire without a present is kept for the next acquire,
        // so that skipped presents do not reduce the count.
        if (_latencyWaitable != 0 && !_latencyWaitConsumed)
        {
            uint wait = VulkanDxgiWin32.WaitForSingleObject(_latencyWaitable, timeoutMilliseconds);
            if (wait == VulkanDxgiWin32.WaitTimeout)
                return timeoutMilliseconds == 0 ? Result.NotReady : Result.Timeout;
            if (wait != VulkanDxgiWin32.WaitObject0)
            {
                MarkFaulted("waiting for the frame-latency object failed");
                return Result.ErrorOutOfDateKhr;
            }

            _latencyWaitConsumed = true;
        }

        return Result.Success;
    }

    /// <summary>
    /// Queues the Direct3D side of one present: wait for Vulkan to finish the shared
    /// image, copy it into the back buffer, signal the copy fence, and present. The
    /// call does not block on the GPU.
    /// </summary>
    /// <returns>The DXGI result. A negative value marks the presenter as faulted.</returns>
    internal int Present(
        ID3D11Texture2D* sharedTexture,
        ulong vulkanDoneValue,
        uint syncInterval,
        bool allowTearing,
        out ulong copyDoneValue)
    {
        copyDoneValue = _lastCopyDoneValue;
        if (Faulted || _disposed)
            return unchecked((int)0x887A0005);

        int hr = _shared.Context4->Wait(_vulkanDoneFence, vulkanDoneValue);
        if (hr < 0)
        {
            MarkFaulted($"ID3D11DeviceContext4.Wait failed (0x{hr:X8})");
            return hr;
        }

        Guid textureId = ID3D11Texture2D.Guid;
        ID3D11Texture2D* backBuffer = null;
        hr = _swapchain->GetBuffer(0, &textureId, (void**)&backBuffer);
        if (hr < 0)
        {
            MarkFaulted($"IDXGISwapChain.GetBuffer failed (0x{hr:X8})");
            return hr;
        }

        _shared.Context->CopyResource((ID3D11Resource*)backBuffer, (ID3D11Resource*)sharedTexture);
        backBuffer->Release();

        copyDoneValue = ++_lastCopyDoneValue;
        hr = _shared.Context4->Signal(_copyDoneFence, copyDoneValue);
        if (hr < 0)
        {
            MarkFaulted($"ID3D11DeviceContext4.Signal failed (0x{hr:X8})");
            return hr;
        }

        uint flags = allowTearing && TearingSupported && syncInterval == 0
            ? DxgiPresentAllowTearing
            : 0u;
        hr = _swapchain->Present(syncInterval, flags);
        _latencyWaitConsumed = false;
        if (hr < 0)
            MarkFaulted($"IDXGISwapChain.Present failed (0x{hr:X8})");
        return hr;
    }

    /// <summary>Waits a bounded time until the Direct3D copy fence reaches a value.</summary>
    internal void WaitForCopyDone(ulong value, uint timeoutMilliseconds)
    {
        if (_disposed || _copyDoneFence is null || _copyDoneFence->GetCompletedValue() >= value)
            return;

        if (_copyDoneFence->SetEventOnCompletion(value, (void*)_copyDoneEvent) >= 0)
            _ = VulkanDxgiWin32.WaitForSingleObject(_copyDoneEvent, timeoutMilliseconds);
    }

    private void MarkFaulted(string reason)
    {
        if (Faulted)
            return;

        Faulted = true;
        FaultReason = reason;
        Debug.VulkanWarning("[Vulkan][DxgiPresent] The DXGI present path failed and will be replaced by the Vulkan swapchain. Reason={0}", reason);
        Debug.WriteAuxiliaryLog(LogFileName, $"fault: {reason}");
    }

    /// <summary>
    /// Releases every native object. It first waits a bounded time for the last
    /// Direct3D copy and for the last Vulkan signal of the shared fence.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        WaitForCopyDone(_lastCopyDoneValue, DisposeWaitMilliseconds);
        if (_vulkanDoneSemaphore.Handle != 0)
        {
            if (_lastVulkanDoneValue != 0)
            {
                VkSemaphore semaphore = _vulkanDoneSemaphore;
                ulong value = _lastVulkanDoneValue;
                SemaphoreWaitInfo waitInfo = new()
                {
                    SType = StructureType.SemaphoreWaitInfo,
                    SemaphoreCount = 1,
                    PSemaphores = &semaphore,
                    PValues = &value,
                };
                _ = _api.WaitSemaphores(_vkDevice, &waitInfo, DisposeWaitMilliseconds * 1_000_000UL);
            }

            _api.DestroySemaphore(_vkDevice, _vulkanDoneSemaphore, null);
            _vulkanDoneSemaphore = default;
        }

        _disposed = true;
        VulkanDxgiWin32.CloseIfValid(ref _copyDoneEvent);
        VulkanDxgiWin32.CloseIfValid(ref _latencyWaitable);
        if (_swapchain is not null && _shared.Context is not null)
        {
            // Flip-model buffers must have no outstanding references when the
            // swapchain goes, and other presenters keep using the same context.
            _shared.Context->ClearState();
            _shared.Context->Flush();
        }
        ReleaseCom(ref _swapchain3);
        ReleaseCom(ref _swapchain);
        ReleaseCom(ref _copyDoneFence);
        ReleaseCom(ref _vulkanDoneFence);
        _shared.Release();
    }

    private static void ReleaseCom<T>(ref T* pointer)
        where T : unmanaged
    {
        if (pointer is null)
            return;

        ((IUnknown*)pointer)->Release();
        pointer = null;
    }
}
