using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private sealed class RetirementSlot
    {
        public readonly ulong[] Handles = new ulong[RenderFrameViewSet.MaxViewCount];
        public long ReservationGeneration;
        public int Count;
        public int State;
    }

    private readonly RetirementSlot[] _retirementSlots = CreateRetirementSlots();
    private long _nativeInstanceGeneration;

    private static RetirementSlot[] CreateRetirementSlots()
    {
        RetirementSlot[] slots = new RetirementSlot[32];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = new RetirementSlot();
        return slots;
    }

    private bool TryReserveRetirement(int swapchainCount, out OpenXrRetirementToken token)
    {
        lock (_nativeGraphicsBorrowLock)
        {
            if (_nativeGraphicsBorrowClosing || _instance.Handle == 0 ||
                swapchainCount < 1 || swapchainCount > RenderFrameViewSet.MaxViewCount)
            {
                token = default;
                return false;
            }

            for (int i = 0; i < _retirementSlots.Length; i++)
            {
                RetirementSlot slot = _retirementSlots[i];
                if (slot.State != 0)
                    continue;
                slot.State = 1;
                slot.Count = swapchainCount;
                slot.ReservationGeneration++;
                _nativeGraphicsBorrowCount++;
                token = new OpenXrRetirementToken(_nativeInstanceGeneration, i, slot.ReservationGeneration);
                return true;
            }
        }

        token = default;
        return false;
    }

    private void CommitRetirement(OpenXrRetirementToken token, ReadOnlySpan<ulong> swapchains)
    {
        lock (_nativeGraphicsBorrowLock)
        {
            RetirementSlot slot = GetRetirementSlot(token, expectedState: 1);
            if (swapchains.Length != slot.Count)
                throw new ArgumentException("Retirement handle count changed after reservation.", nameof(swapchains));

            for (int i = 0; i < swapchains.Length; i++)
            {
                ulong handle = swapchains[i];
                slot.Handles[i] = handle;
                if (handle == 0)
                    continue;
                for (int view = 0; view < _viewCount; view++)
                {
                    if (_swapchains[view].Handle != handle)
                        continue;
                    _swapchains[view] = default;
                    _neutralSwapchains[view] = 0;
                    _swapchainImageCounts[view] = 0;
                    break;
                }
            }
            slot.State = 2;
        }
    }

    private void CancelRetirement(OpenXrRetirementToken token)
    {
        lock (_nativeGraphicsBorrowLock)
        {
            RetirementSlot slot = GetRetirementSlot(token, expectedState: 1);
            slot.State = 0;
            slot.Count = 0;
            _nativeGraphicsBorrowCount--;
            Monitor.PulseAll(_nativeGraphicsBorrowLock);
        }
    }

    private int DestroyRetiredSwapchain(OpenXrRetirementToken token, ulong swapchainHandle)
    {
        lock (_nativeGraphicsBorrowLock)
        {
            RetirementSlot slot = GetRetirementSlot(token, expectedState: 2);
            int index = Array.IndexOf(slot.Handles, swapchainHandle, 0, slot.Count);
            if (swapchainHandle == 0 || index < 0)
                throw new InvalidOperationException("Retired swapchain is not owned by this reservation.");
            if (HasAcquiredImage(swapchainHandle))
                throw new InvalidOperationException("Retired OpenXR swapchain still owns an acquired runtime image.");

            int result = (int)Api.DestroySwapchain(new Swapchain(swapchainHandle));
            if (result == OpenXrResultCodes.Success)
                slot.Handles[index] = 0;
            return result;
        }
    }

    private void ReleaseRetirement(OpenXrRetirementToken token)
    {
        lock (_nativeGraphicsBorrowLock)
        {
            RetirementSlot slot = GetRetirementSlot(token, expectedState: 2);
            for (int i = 0; i < slot.Count; i++)
                if (slot.Handles[i] != 0)
                    throw new InvalidOperationException("Retired native swapchains remain live.");
            slot.State = 0;
            slot.Count = 0;
            _nativeGraphicsBorrowCount--;
            Monitor.PulseAll(_nativeGraphicsBorrowLock);
        }
    }

    private void AbandonRetirement(OpenXrRetirementToken token)
    {
        lock (_nativeGraphicsBorrowLock)
        {
            RetirementSlot slot = GetRetirementSlot(token, expectedState: 0, allowReservedOrCommitted: true);
            Array.Clear(slot.Handles, 0, slot.Count);
            slot.State = 0;
            slot.Count = 0;
            _nativeGraphicsBorrowCount--;
            Monitor.PulseAll(_nativeGraphicsBorrowLock);
        }
    }

    private RetirementSlot GetRetirementSlot(OpenXrRetirementToken token, int expectedState, bool allowReservedOrCommitted = false)
    {
        if (token.InstanceGeneration != _nativeInstanceGeneration ||
            token.Slot < 0 || token.Slot >= _retirementSlots.Length)
            throw new InvalidOperationException("Retirement token belongs to a different OpenXR instance generation.");
        RetirementSlot slot = _retirementSlots[token.Slot];
        if (slot.ReservationGeneration != token.ReservationGeneration ||
            (allowReservedOrCommitted ? slot.State is not (1 or 2) : slot.State != expectedState))
            throw new InvalidOperationException("Retirement reservation is no longer active.");
        return slot;
    }
}
