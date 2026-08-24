using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Preallocated frame-slot retirement ownership for canonical publications and
/// resident native-template uses referenced by submitted command buffers.
/// Slots are released only after the frame loop proves their prior timeline
/// value complete.
/// </summary>
internal sealed class VulkanResidentTemplateFrameSlotLifetimes
{
    private sealed class Slot
    {
        internal readonly AdvancedSharedGpuSceneDatabase?[] Databases =
            new AdvancedSharedGpuSceneDatabase[VulkanMeshOperationRequestQueue.Capacity];
        internal readonly AdvancedGpuScenePublicationReference[] Publications =
            new AdvancedGpuScenePublicationReference[VulkanMeshOperationRequestQueue.Capacity];
        internal readonly AdvancedGpuScenePublicationLease[] PublicationLeases =
            new AdvancedGpuScenePublicationLease[VulkanMeshOperationRequestQueue.Capacity];
        internal readonly VulkanResidentDrawTemplate?[] Templates =
            new VulkanResidentDrawTemplate[VulkanMeshOperationRequestQueue.Capacity];
        internal int PublicationCount;
        internal int TemplateCount;
    }

    private readonly Slot[] _slots;

    internal VulkanResidentTemplateFrameSlotLifetimes(int frameSlotCount)
    {
        if (frameSlotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSlotCount));

        _slots = new Slot[frameSlotCount];
        for (int index = 0; index < _slots.Length; ++index)
            _slots[index] = new Slot();
    }

    internal bool TryAdoptCanonicalPublication(
        int frameSlot,
        AdvancedSharedGpuSceneDatabase database,
        in AdvancedGpuScenePublicationReference publication,
        ref AdvancedGpuScenePublicationLease lease)
    {
        Slot slot = GetSlot(frameSlot);
        for (int index = 0; index < slot.PublicationCount; ++index)
        {
            if (!ReferenceEquals(slot.Databases[index], database) ||
                slot.Publications[index] != publication)
                continue;

            lease.Dispose();
            lease = default;
            return true;
        }
        if (slot.PublicationCount == slot.PublicationLeases.Length)
            return false;

        int target = slot.PublicationCount++;
        slot.Databases[target] = database;
        slot.Publications[target] = publication;
        slot.PublicationLeases[target] = lease;
        lease = default;
        return true;
    }

    internal bool TryAdoptResidentTemplate(
        int frameSlot,
        VulkanResidentDrawTemplate template)
    {
        Slot slot = GetSlot(frameSlot);
        for (int index = 0; index < slot.TemplateCount; ++index)
        {
            if (!ReferenceEquals(slot.Templates[index], template))
                continue;

            template.ReleaseUse();
            return true;
        }
        if (slot.TemplateCount == slot.Templates.Length)
            return false;

        slot.Templates[slot.TemplateCount++] = template;
        return true;
    }

    internal void ReleaseFrameSlot(int frameSlot)
    {
        Slot slot = GetSlot(frameSlot);
        for (int index = 0; index < slot.TemplateCount; ++index)
        {
            slot.Templates[index]?.ReleaseUse();
            slot.Templates[index] = null;
        }
        slot.TemplateCount = 0;

        for (int index = 0; index < slot.PublicationCount; ++index)
        {
            slot.PublicationLeases[index].Dispose();
            slot.PublicationLeases[index] = default;
            slot.Databases[index] = null;
            slot.Publications[index] = default;
        }
        slot.PublicationCount = 0;
    }

    internal void ReleaseAll()
    {
        for (int frameSlot = 0; frameSlot < _slots.Length; ++frameSlot)
            ReleaseFrameSlot(frameSlot);
    }

    private Slot GetSlot(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)_slots.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        return _slots[frameSlot];
    }
}
