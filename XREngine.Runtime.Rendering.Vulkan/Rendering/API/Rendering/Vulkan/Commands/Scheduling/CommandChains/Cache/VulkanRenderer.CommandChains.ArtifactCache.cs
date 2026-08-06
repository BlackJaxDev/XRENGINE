using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private Dictionary<CommandChainKey, CommandChain> GetCommandChainCache(
        uint imageIndex)
    {
        if (!TryGetIndexedCommandChainCacheSlot(imageIndex, out int index))
            return GetExternalCommandChainCache(imageIndex);

        EnsureIndexedCommandChainCaches();
        return _commandChainCaches![index];
    }

    private int ResolveIndexedCommandChainCacheCount()
        => Math.Max(_commandBuffers?.Length ?? 0, 1);

    private bool TryGetIndexedCommandChainCacheSlot(
        uint imageIndex,
        out int index)
    {
        int count = ResolveIndexedCommandChainCacheCount();
        if (imageIndex < (uint)count)
        {
            index = unchecked((int)imageIndex);
            return true;
        }

        index = -1;
        return false;
    }

    private void EnsureIndexedCommandChainCaches()
    {
        int count = ResolveIndexedCommandChainCacheCount();
        if (_commandChainCaches is not null &&
            _commandChainCaches.Length == count)
        {
            return;
        }

        if (_commandChainCaches is not null)
            DestroyIndexedCommandChainCaches();

        _commandChainCaches =
            new Dictionary<CommandChainKey, CommandChain>[count];
        for (int i = 0; i < count; i++)
            _commandChainCaches[i] = [];
    }

    internal void NotifyTextureDescriptorPublished(string reason)
        => InvalidateCommandChainScheduleForResourceChange(
            RenderResourceChangeKind.CompatibleContentPublication,
            reason);

    private void InvalidateCommandChainScheduleForResourceChange(string reason)
        => InvalidateCommandChainScheduleForResourceChange(
            RenderResourceChangeKind.BindingIdentity,
            reason);

    private void InvalidateCommandChainScheduleForResourceChange(
        RenderResourceChangeKind kind,
        string reason)
    {
        if (kind is RenderResourceChangeKind.FrameData or
            RenderResourceChangeKind.CompatibleContentPublication)
        {
            return;
        }

        bool commandChainsAvailable =
            _commandChainCaches is not null ||
            _externalCommandChainCaches is not null ||
            CommandChainsEnabledForCurrentRecording;
        if (!commandChainsAvailable)
        {
            // Primary variants validate immutable dependency signatures at
            // selection time. Do not turn a local resource mutation into a
            // renderer-wide dirty generation; only incompatible variants rerecord.
            return;
        }

        // Descriptor generations are validated while rebuilding the command-chain
        // schedule. Clearing the fast schedule cache prevents stale primary reuse
        // while still letting unchanged chains survive texture streaming.
        _commandRuntime.InvalidateScheduleCache();
    }

    private Dictionary<CommandChainKey, CommandChain>
        GetExternalCommandChainCache(uint imageIndex)
    {
        Dictionary<uint, Dictionary<CommandChainKey, CommandChain>> caches =
            _externalCommandChainCaches ??= [];
        if (!caches.TryGetValue(
                imageIndex,
                out Dictionary<CommandChainKey, CommandChain>? cache))
        {
            cache = [];
            caches.Add(imageIndex, cache);
        }

        return cache;
    }

    private int
        InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease()
        => InvalidateCommandChainSecondaryCommandBuffers(
            CommandChainDirtyReason.DescriptorGeneration);

    private int
        InvalidateCommandChainSecondaryCommandBuffersForFrameDataLayoutChange()
        => InvalidateCommandChainSecondaryCommandBuffers(
            CommandChainDirtyReason.Structure);

    private int InvalidateCommandChainSecondaryCommandBuffers(
        CommandChainDirtyReason dirtyReason)
    {
        using VulkanCpuStageScope cpuStage =
            new(_frameTelemetry, EVulkanCpuStage.CommandDirtyPropagation);
        int invalidated = 0;
        if (_commandChainCaches is not null)
        {
            for (int i = 0; i < _commandChainCaches.Length; i++)
            {
                Dictionary<CommandChainKey, CommandChain>? cache =
                    _commandChainCaches[i];
                if (cache is null)
                    continue;

                foreach (CommandChain chain in cache.Values)
                {
                    if (chain.SecondaryCommandBuffer.Handle == 0 ||
                        !chain.SecondaryCommandBufferExecutable)
                    {
                        continue;
                    }

                    MarkCommandChainSecondaryCommandBufferInvalid(
                        chain,
                        EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
                    chain.DirtyReason |= dirtyReason;
                    invalidated++;
                }
            }
        }

        if (_externalCommandChainCaches is not null)
        {
            foreach (Dictionary<CommandChainKey, CommandChain> cache in
                     _externalCommandChainCaches.Values)
            {
                foreach (CommandChain chain in cache.Values)
                {
                    if (chain.SecondaryCommandBuffer.Handle == 0 ||
                        !chain.SecondaryCommandBufferExecutable)
                    {
                        continue;
                    }

                    MarkCommandChainSecondaryCommandBufferInvalid(
                        chain,
                        EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
                    chain.DirtyReason |= dirtyReason;
                    invalidated++;
                }
            }
        }

        _commandRuntime.InvalidateScheduleCache();

        if (invalidated > 0)
            MarkOpenXrPrimaryCommandArtifactOwnersDirty();

        return invalidated;
    }

    private static CommandChain GetOrCreateCommandChain(
        Dictionary<CommandChainKey, CommandChain> cache,
        CommandChainKey key)
    {
        if (!cache.TryGetValue(key, out CommandChain? chain))
        {
            chain = new CommandChain(key);
            cache.Add(key, chain);
        }

        return chain;
    }

    private void TrimScheduledCommandChainCache(
        Dictionary<CommandChainKey, CommandChain> cache)
    {
        // Scheduled entries cannot outnumber the cache. Keep the stable frame
        // off the cache-wide scan entirely until capacity is actually exceeded.
        if (cache.Count <= MaxCachedScheduledCommandChainsPerFrameSlot)
            return;

        using VulkanCpuStageScope cpuStage =
            new(_frameTelemetry, EVulkanCpuStage.CommandCacheScanning);
        int scheduledCount = 0;
        foreach (CommandChain chain in cache.Values)
        {
            if (chain.ScheduledPacket)
                scheduledCount++;
        }

        while (scheduledCount > MaxCachedScheduledCommandChainsPerFrameSlot)
        {
            bool found = false;
            CommandChainKey oldestKey = default;
            CommandChain? oldest = null;
            foreach ((CommandChainKey key, CommandChain chain) in cache)
            {
                if (!chain.ScheduledPacket ||
                    (oldest is not null &&
                     chain.LastUsedScheduleGeneration >=
                     oldest.LastUsedScheduleGeneration))
                {
                    continue;
                }

                oldestKey = key;
                oldest = chain;
                found = true;
            }

            if (!found || oldest is null)
                break;

            DestroyCommandChainSecondaryCommandBuffer(oldest);
            oldest.ReleasePacketSnapshot();
            cache.Remove(oldestKey);
            scheduledCount--;
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                reusedClean: false,
                recorded: false,
                forcedDirty: false,
                frameOpSignatureDirty: false,
                plannerDirty: false,
                profilerDirty: false,
                dirtyReason: null,
                detailReasons: EVulkanCommandBufferDecisionReason.Evicted,
                structuralSignature: oldest.StructuralSignature,
                descriptorGeneration: oldest.DescriptorGeneration,
                swapchainSlot: oldest.Key.FrameSlot);
        }
    }
}
