using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private Semaphore[]? acquireBridgeSemaphores;
    private Semaphore[]? presentBridgeSemaphores;
    private Semaphore _graphicsTimelineSemaphore;
    private Semaphore _presentTimelineSemaphore;
    private Semaphore _transferTimelineSemaphore;
    private ulong[]? _frameSlotTimelineValues;
    private ulong[]? _swapchainImageTimelineValues;
    private ulong _acquireTimelineValue;
    private ulong _graphicsTimelineValue;
    private ulong _presentTimelineValue;
    private ulong _transferTimelineValue;

    /// <summary>
    /// Set to <c>true</c> when <c>VK_ERROR_DEVICE_LOST</c> is detected. Once the Vulkan
    /// logical device is lost it cannot be recovered — all subsequent API calls will fail.
    /// The render loop checks this flag to short-circuit immediately instead of looping
    /// forever with cascading failures.
    /// </summary>
    private volatile bool _deviceLost;

    private void EnsureSwapchainTimelineState()
    {
        if (swapChainImages is null)
        {
            _swapchainImageTimelineValues = null;
            return;
        }

        if (_swapchainImageTimelineValues is null || _swapchainImageTimelineValues.Length != swapChainImages.Length)
            _swapchainImageTimelineValues = new ulong[swapChainImages.Length];
        else
            Array.Clear(_swapchainImageTimelineValues, 0, _swapchainImageTimelineValues.Length);
    }

    private void WaitForTimelineValue(Semaphore semaphore, ulong value)
    {
        if (semaphore.Handle == 0 || value == 0)
            return;

        SemaphoreWaitInfo waitInfo = new()
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
        };

        Semaphore* semaphorePtr = stackalloc Semaphore[1];
        ulong* valuePtr = stackalloc ulong[1];
        semaphorePtr[0] = semaphore;
        valuePtr[0] = value;
        waitInfo.PSemaphores = semaphorePtr;
        waitInfo.PValues = valuePtr;

        Result waitResult = Api!.WaitSemaphores(device, &waitInfo, ulong.MaxValue);
        if (waitResult == Result.ErrorDeviceLost)
        {
            _deviceLost = true;

            // Device is irrecoverably lost. Reset all timeline values so subsequent
            // frames don't block forever waiting for a semaphore the GPU will never signal.
            if (_frameSlotTimelineValues is not null)
                Array.Clear(_frameSlotTimelineValues);
            if (_swapchainImageTimelineValues is not null)
                Array.Clear(_swapchainImageTimelineValues);
            _acquireTimelineValue = 0;
            _graphicsTimelineValue = 0;
            _presentTimelineValue = 0;
            _transferTimelineValue = 0;

            throw new InvalidOperationException(
                $"Vulkan device lost while waiting for timeline value {value}. Timeline state has been reset.");
        }

        if (waitResult != Result.Success)
            throw new InvalidOperationException($"Failed to wait for timeline semaphore value {value}. Result={waitResult}.");
    }

    private void DestroySyncObjects()
    {
        if (acquireBridgeSemaphores is not null)
        {
            for (int i = 0; i < acquireBridgeSemaphores.Length; i++)
            {
                if (presentBridgeSemaphores is not null && i < presentBridgeSemaphores.Length)
                    Api!.DestroySemaphore(device, presentBridgeSemaphores[i], null);

                Api!.DestroySemaphore(device, acquireBridgeSemaphores[i], null);
            }
        }

        if (_graphicsTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(device, _graphicsTimelineSemaphore, null);
        if (_presentTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(device, _presentTimelineSemaphore, null);
        if (_transferTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(device, _transferTimelineSemaphore, null);

        acquireBridgeSemaphores = null;
        presentBridgeSemaphores = null;
        _graphicsTimelineSemaphore = default;
        _presentTimelineSemaphore = default;
        _transferTimelineSemaphore = default;
        _frameSlotTimelineValues = null;
        _swapchainImageTimelineValues = null;
        _acquireTimelineValue = 0;
        _graphicsTimelineValue = 0;
        _presentTimelineValue = 0;
        _transferTimelineValue = 0;
    }

    private void CreateSyncObjects()
    {
        acquireBridgeSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        presentBridgeSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        _frameSlotTimelineValues = new ulong[MAX_FRAMES_IN_FLIGHT];
        EnsureSwapchainTimelineState();

        SemaphoreCreateInfo semaphoreInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
        };

        SemaphoreTypeCreateInfo timelineTypeInfo = new()
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };

        SemaphoreCreateInfo timelineSemaphoreInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &timelineTypeInfo,
        };

        if (Api!.CreateSemaphore(device, ref timelineSemaphoreInfo, null, out _graphicsTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(device, ref timelineSemaphoreInfo, null, out _presentTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(device, ref timelineSemaphoreInfo, null, out _transferTimelineSemaphore) != Result.Success)
        {
            throw new Exception("failed to create timeline synchronization semaphores.");
        }

        for (var i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            if (Api!.CreateSemaphore(device, ref semaphoreInfo, null, out acquireBridgeSemaphores[i]) != Result.Success ||
                Api.CreateSemaphore(device, ref semaphoreInfo, null, out presentBridgeSemaphores[i]) != Result.Success)
            {
                throw new Exception("failed to create frame bridge synchronization semaphores.");
            }
        }
    }
}