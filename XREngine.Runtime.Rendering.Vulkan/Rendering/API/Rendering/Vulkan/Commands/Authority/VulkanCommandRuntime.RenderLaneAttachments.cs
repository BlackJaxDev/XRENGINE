using Silk.NET.Vulkan;
using XREngine.Execution;

namespace XREngine.Rendering.Vulkan;

/// <summary>Render-domain Vulkan command-pool attachment ownership.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private VulkanRenderLaneFrameAttachment[,]? _renderLaneAttachments;
    private RenderWorkDomain? _renderWorkDomain;

    internal int RenderLogicalLaneCount
        => GetRenderWorkDomain().LogicalLaneCount;

    internal int MaxSecondaryCommandBuffersPerScope
        => checked(2 * RenderLogicalLaneCount);

    internal RenderWorkDomain RenderDomain
        => GetRenderWorkDomain();

    internal int ResolveOpenXrEyeRenderLaneId(int eyeOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eyeOrdinal);
        return Math.Min(eyeOrdinal, RenderLogicalLaneCount - 1);
    }

    internal void InitializeRenderLaneCommandAttachments()
    {
        if (_renderLaneAttachments is not null)
            return;

        RenderWorkDomain domain = RuntimeRenderingHostServices.Work.RenderWork;
        QueueFamilyIndices queueFamilies = DeviceContext.QueueFamilies;
        uint graphicsFamily = queueFamilies.GraphicsFamilyIndex ??
            throw new InvalidOperationException("Graphics queue family is not available.");
        Span<uint> uniqueFamilies = stackalloc uint[3];
        int familyCount = 0;
        AddFamily(uniqueFamilies, ref familyCount, graphicsFamily);
        if (queueFamilies.ComputeFamilyIndex is uint computeFamily)
            AddFamily(uniqueFamilies, ref familyCount, computeFamily);
        if (queueFamilies.TransferFamilyIndex is uint transferFamily)
            AddFamily(uniqueFamilies, ref familyCount, transferFamily);

        VulkanRenderLaneFrameAttachment[,] attachments = new VulkanRenderLaneFrameAttachment[
            domain.LogicalLaneCount,
            domain.BackendAttachments.FrameSlotCount];
        try
        {
            for (int laneId = 0; laneId < attachments.GetLength(0); laneId++)
            for (int frameSlot = 0; frameSlot < attachments.GetLength(1); frameSlot++)
            {
                VulkanLaneCommandFamilyArena[] arenas = new VulkanLaneCommandFamilyArena[familyCount];
                try
                {
                    for (int familyIndex = 0; familyIndex < familyCount; familyIndex++)
                    {
                        uint queueFamily = uniqueFamilies[familyIndex];
                        CommandPool transientPool = CreateCommandPoolForFamily(
                            queueFamily,
                            transient: true,
                            $"RenderLane[{laneId}].FrameSlot[{frameSlot}].QueueFamily[{queueFamily}].Transient");
                        CommandPool retainedPool = default;
                        try
                        {
                            retainedPool = CreateCommandPoolForFamily(
                                queueFamily,
                                transient: false,
                                $"RenderLane[{laneId}].FrameSlot[{frameSlot}].QueueFamily[{queueFamily}].Retained");
                            arenas[familyIndex] = new VulkanLaneCommandFamilyArena(
                                laneId,
                                frameSlot,
                                queueFamily,
                                transientPool,
                                retainedPool);
                        }
                        catch
                        {
                            if (retainedPool.Handle != 0)
                                DestroyCommandPoolHostSynchronized(retainedPool);
                            DestroyCommandPoolHostSynchronized(transientPool);
                            throw;
                        }
                    }
                }
                catch
                {
                    for (int familyIndex = 0; familyIndex < arenas.Length; familyIndex++)
                    {
                        VulkanLaneCommandFamilyArena? arena = arenas[familyIndex];
                        if (arena is null)
                            continue;
                        DestroyCommandPoolHostSynchronized(arena.TransientPool);
                        DestroyCommandPoolHostSynchronized(arena.RetainedPool);
                    }
                    throw;
                }

                VulkanRenderLaneFrameAttachment attachment = new(
                    laneId,
                    frameSlot,
                    arenas,
                    graphicsFamily);
                object? previous = domain.BackendAttachments.Register(
                    laneId,
                    frameSlot,
                    attachment);
                if (previous is not null)
                {
                    domain.BackendAttachments.Register(laneId, frameSlot, previous);
                    throw new InvalidOperationException(
                        $"Render lane {laneId}, frame slot {frameSlot} already has a backend attachment.");
                }

                attachments[laneId, frameSlot] = attachment;
            }

            _renderWorkDomain = domain;
            _renderLaneAttachments = attachments;
        }
        catch
        {
            RetireRenderLaneCommandAttachments(domain, attachments, requireDetachedArtifacts: false);
            throw;
        }

        static void AddFamily(
            Span<uint> families,
            ref int count,
            uint queueFamily)
        {
            for (int index = 0; index < count; index++)
                if (families[index] == queueFamily)
                    return;

            families[count++] = queueFamily;
        }
    }

    internal int ResolveRenderLaneFrameSlot(uint backendFrameSlot)
    {
        RenderWorkDomain domain = GetRenderWorkDomain();
        return checked((int)(backendFrameSlot % (uint)domain.BackendAttachments.FrameSlotCount));
    }

    internal VulkanRenderLaneFrameAttachment GetRenderLaneAttachment(
        int laneId,
        int frameSlot)
    {
        InitializeRenderLaneCommandAttachments();
        VulkanRenderLaneFrameAttachment[,] attachments = _renderLaneAttachments ??
            throw new InvalidOperationException("Vulkan render-lane attachments are unavailable.");
        if ((uint)laneId >= (uint)attachments.GetLength(0))
            throw new ArgumentOutOfRangeException(nameof(laneId));
        if ((uint)frameSlot >= (uint)attachments.GetLength(1))
            throw new ArgumentOutOfRangeException(nameof(frameSlot));

        return attachments[laneId, frameSlot] ??
            throw new InvalidOperationException(
                $"Vulkan render-lane attachment {laneId}:{frameSlot} was not initialized.");
    }

    internal void PrepareRenderLaneTransientGraphicsPool(
        int laneId,
        int frameSlot,
        ulong preparationIdentity,
        bool priorUseCompletionProven)
        => GetRenderLaneAttachment(laneId, frameSlot).Graphics.PrepareTransientPool(
            this,
            preparationIdentity,
            priorUseCompletionProven);

    internal void ResetCompletedRenderLaneTransientPools(
        int frameSlot,
        ulong preparationIdentity,
        bool priorUseCompletionProven)
    {
        VulkanRenderLaneFrameAttachment[,]? attachments = _renderLaneAttachments;
        if (attachments is null || (uint)frameSlot >= (uint)attachments.GetLength(1))
            return;

        for (int laneId = 0; laneId < attachments.GetLength(0); laneId++)
        {
            VulkanRenderLaneFrameAttachment attachment = attachments[laneId, frameSlot];
            for (int familyIndex = 0; familyIndex < attachment.QueueFamilyCount; familyIndex++)
            {
                attachment.GetFamilyAt(familyIndex).PrepareTransientPool(
                    this,
                    preparationIdentity,
                    priorUseCompletionProven);
            }
        }
    }

    internal void DestroyRenderLaneCommandAttachments()
    {
        VulkanRenderLaneFrameAttachment[,]? attachments = _renderLaneAttachments;
        if (attachments is null)
            return;

        RenderWorkDomain domain = _renderWorkDomain ?? GetRenderWorkDomain();
        RetireRenderLaneCommandAttachments(domain, attachments, requireDetachedArtifacts: true);
        _renderLaneAttachments = null;
        _renderWorkDomain = null;
    }

    private void RetireRenderLaneCommandAttachments(
        RenderWorkDomain domain,
        VulkanRenderLaneFrameAttachment[,] attachments,
        bool requireDetachedArtifacts)
    {
        for (int laneId = 0; laneId < attachments.GetLength(0); laneId++)
        for (int frameSlot = 0; frameSlot < attachments.GetLength(1); frameSlot++)
        {
            VulkanRenderLaneFrameAttachment? attachment = attachments[laneId, frameSlot];
            if (attachment is null)
                continue;

            if (ReferenceEquals(domain.BackendAttachments.Get(laneId, frameSlot), attachment))
                domain.BackendAttachments.Register(laneId, frameSlot, null);

            for (int familyIndex = 0; familyIndex < attachment.QueueFamilyCount; familyIndex++)
            {
                VulkanLaneCommandFamilyArena arena = attachment.GetFamilyAt(familyIndex);
                CommandPool transientPool = arena.TransientPool;
                CommandPool retainedPool = arena.RetainedPool;
                if (requireDetachedArtifacts)
                    arena.ClearAfterRetirement();
                if (transientPool.Handle != 0)
                    DestroyCommandPoolHostSynchronized(transientPool);
                if (retainedPool.Handle != 0)
                    DestroyCommandPoolHostSynchronized(retainedPool);
            }
        }
    }

    private RenderWorkDomain GetRenderWorkDomain()
        => _renderWorkDomain ?? RuntimeRenderingHostServices.Work.RenderWork;
}
