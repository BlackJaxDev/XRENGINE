using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned timeline completion receipt with no renderer backlink.</summary>
internal sealed class VulkanTimelineGpuFence : XRGpuFence
{
    private Vk? _api;
    private VulkanDeviceContext? _device;
    private VulkanCommandRuntime? _runtime;
    private VulkanResourceRuntime? _resources;
    private ulong _semaphore;
    private ulong _value;
    private int _state;

    internal void Reset(Vk api, VulkanDeviceContext device, VulkanCommandRuntime runtime, VulkanResourceRuntime resources)
    {
        ResetForReuse();
        _api = api; _device = device; _runtime = runtime; _resources = resources;
        _semaphore = 0; _value = 0; Volatile.Write(ref _state, 0);
    }
    internal void Bind(ulong semaphore, ulong value)
    {
        if (semaphore == 0 || value == 0) { Fail(); return; }
        _semaphore = semaphore; _value = value; Volatile.Write(ref _state, 1);
    }
    internal void Fail() => Volatile.Write(ref _state, 2);
    protected override unsafe EGpuFenceStatus PollCore()
    {
        if (Volatile.Read(ref _state) == 2) return EGpuFenceStatus.Failed;
        if (_api is not { } api || _device is not { } device || _runtime is not { } runtime || _resources is not { } resources || !device.StateMachine.IsOperational)
        { Fail(); return EGpuFenceStatus.Failed; }
        if (Volatile.Read(ref _state) == 0 || _semaphore == 0 || _value == 0) return EGpuFenceStatus.Pending;
        Result result = runtime.Synchronization.QueryTimelineCompletion(api, device, resources.Lifetime.Tracker, new Semaphore(_semaphore), _value, out bool completed);
        if (result != Result.Success) { Fail(); return EGpuFenceStatus.Failed; }
        return completed ? EGpuFenceStatus.Signaled : EGpuFenceStatus.Pending;
    }
    protected override void DisposeCore()
    {
        VulkanCommandRuntime? runtime = _runtime;
        bool reusable = Volatile.Read(ref _state) != 0;
        _api = null; _device = null; _runtime = null; _resources = null; _semaphore = 0; _value = 0; Fail();
        if (reusable && runtime is not null)
            lock (runtime.Synchronization._submissionMarkerLock) runtime.Synchronization._timelineGpuFencePool.Push(this);
    }
}
