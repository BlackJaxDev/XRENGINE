using Silk.NET.Vulkan;
using System.Threading;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    internal sealed class VulkanTimelineGpuFence : XRGpuFence
    {
        private VulkanRenderer? _renderer;
        private ulong _semaphoreHandle;
        private ulong _timelineValue;
        private int _state;

        internal void Reset(VulkanRenderer renderer)
        {
            ResetForReuse();
            _renderer = renderer;
            _semaphoreHandle = 0;
            _timelineValue = 0;
            Volatile.Write(ref _state, 0);
        }

        internal void Bind(ulong semaphoreHandle, ulong timelineValue)
        {
            if (semaphoreHandle == 0 || timelineValue == 0)
            {
                Fail();
                return;
            }

            _semaphoreHandle = semaphoreHandle;
            _timelineValue = timelineValue;
            Volatile.Write(ref _state, 1);
        }

        internal void Fail()
            => Volatile.Write(ref _state, 2);

        protected override EGpuFenceStatus PollCore()
        {
            if (Volatile.Read(ref _state) == 2)
                return EGpuFenceStatus.Failed;

            VulkanRenderer? currentRenderer = _renderer;
            if (currentRenderer is null || !currentRenderer.DeviceContext.IsOperational)
            {
                Fail();
                return EGpuFenceStatus.Failed;
            }

            ulong semaphoreHandle = _semaphoreHandle;
            ulong timelineValue = _timelineValue;
            if (Volatile.Read(ref _state) == 0 || semaphoreHandle == 0 || timelineValue == 0)
                return EGpuFenceStatus.Pending;

            try
            {
                return currentRenderer.HasTimelineValueCompleted(new Semaphore(semaphoreHandle), timelineValue)
                    ? EGpuFenceStatus.Signaled
                    : EGpuFenceStatus.Pending;
            }
            catch
            {
                Fail();
                return EGpuFenceStatus.Failed;
            }
        }

        protected override void DisposeCore()
        {
            VulkanRenderer? owner = _renderer;
            bool reusable = Volatile.Read(ref _state) != 0;
            _renderer = null;
            _semaphoreHandle = 0;
            _timelineValue = 0;
            Fail();
            if (reusable && owner is not null)
                owner.ReturnTimelineGpuFence(this);
        }
    }

}
