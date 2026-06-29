using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private void OnSwapchainExtentChanged(Extent2D extent)
    {
        ActiveState.SetSwapchainExtent(extent);
        if (_boundDrawFrameBuffer is null)
            ActiveState.SetCurrentTargetExtent(extent);
        MarkCommandBuffersDirty();
    }

    private void UpdateResourcePlannerFromPipeline()
    {
        UpdateResourcePlannerFromContext(CaptureFrameOpContext());
    }

    internal readonly record struct FrameOpContext(
        int PipelineIdentity,
        int ViewportIdentity,
        XRRenderPipelineInstance? PipelineInstance,
        RenderResourceRegistry? ResourceRegistry,
        IReadOnlyCollection<RenderPassMetadata>? PassMetadata,
        uint DisplayWidth = 1u,
        uint DisplayHeight = 1u,
        uint InternalWidth = 1u,
        uint InternalHeight = 1u)
    {
        public int SchedulingIdentity => HashCode.Combine(PipelineIdentity, ViewportIdentity);
    }

    internal FrameOpContext CaptureFrameOpContext()
    {
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingViewport;
        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        uint displayWidth = ResolvePositiveDimension(
            pipeline?.ResourceDisplayWidth,
            viewport?.Width,
            fallbackExtent.Width,
            1u);
        uint displayHeight = ResolvePositiveDimension(
            pipeline?.ResourceDisplayHeight,
            viewport?.Height,
            fallbackExtent.Height,
            1u);
        uint internalWidth = ResolvePositiveDimension(
            pipeline?.ResourceInternalWidth,
            viewport?.InternalWidth,
            displayWidth,
            1u);
        uint internalHeight = ResolvePositiveDimension(
            pipeline?.ResourceInternalHeight,
            viewport?.InternalHeight,
            displayHeight,
            1u);

        FrameOpContext context = new(
            pipeline?.GetHashCode() ?? 0,
            viewport?.GetHashCode() ?? 0,
            pipeline,
            pipeline?.Resources,
            pipeline?.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);
        context = ApplyInteractiveResizePlannerFreeze(context);

        if (pipeline is not null)
            ActiveLastActiveFrameOpContext = context;

        return context;
    }

    private FrameOpContext ApplyInteractiveResizePlannerFreeze(in FrameOpContext context)
    {
        if (!XRWindow.IsInteractiveResizeInProgress)
        {
            ResetInteractiveResizePlannerFreeze();
            return context;
        }

        if (!_interactiveResizePlannerFrozen)
        {
            CaptureInteractiveResizePlannerExtents(context);

            Debug.Vulkan(
                "[VulkanResourcePlanner] Freezing render-resource extents during interactive resize at {0}x{1}/{2}x{3}.",
                _interactiveResizeFrozenDisplayWidth,
                _interactiveResizeFrozenDisplayHeight,
                _interactiveResizeFrozenInternalWidth,
                _interactiveResizeFrozenInternalHeight);
        }
        return context with
        {
            DisplayWidth = _interactiveResizeFrozenDisplayWidth,
            DisplayHeight = _interactiveResizeFrozenDisplayHeight,
            InternalWidth = _interactiveResizeFrozenInternalWidth,
            InternalHeight = _interactiveResizeFrozenInternalHeight
        };
    }

    private void CaptureInteractiveResizePlannerExtents(in FrameOpContext context)
    {
        _interactiveResizeFrozenDisplayWidth = context.DisplayWidth;
        _interactiveResizeFrozenDisplayHeight = context.DisplayHeight;
        _interactiveResizeFrozenInternalWidth = context.InternalWidth;
        _interactiveResizeFrozenInternalHeight = context.InternalHeight;
        _interactiveResizePlannerFrozen = true;
    }

    private void ResetInteractiveResizePlannerFreeze()
    {
        _interactiveResizePlannerFrozen = false;
        _interactiveResizeFrozenDisplayWidth = 0;
        _interactiveResizeFrozenDisplayHeight = 0;
        _interactiveResizeFrozenInternalWidth = 0;
        _interactiveResizeFrozenInternalHeight = 0;
    }

    internal FrameOpContext CaptureFrameOpContextOrLastActive()
    {
        FrameOpContext context = CaptureFrameOpContext();
        if (context.PipelineInstance is not null || context.PassMetadata is { Count: > 0 })
            return context;

        return ActiveLastActiveFrameOpContext ?? context;
    }

    public IDisposable EnterPipelineResourcePlannerReadbackScope(
        XRRenderPipelineInstance pipeline,
        XRViewport? viewport)
    {
        if (pipeline is null)
            throw new ArgumentNullException(nameof(pipeline));

        FrameOpContext context = CreateFrameOpContext(pipeline, viewport);
        return new ExternalResourcePlannerReadbackScope(this, context);
    }

    internal IDisposable EnterFrameOpResourcePlannerReadbackScope(in FrameOpContext context)
        => new ExternalResourcePlannerReadbackScope(this, context);

    private FrameOpContext CreateFrameOpContext(
        XRRenderPipelineInstance pipeline,
        XRViewport? viewport)
    {
        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        uint displayWidth = ResolvePositiveDimension(
            pipeline.ResourceDisplayWidth,
            viewport?.Width,
            fallbackExtent.Width,
            1u);
        uint displayHeight = ResolvePositiveDimension(
            pipeline.ResourceDisplayHeight,
            viewport?.Height,
            fallbackExtent.Height,
            1u);
        uint internalWidth = ResolvePositiveDimension(
            pipeline.ResourceInternalWidth,
            viewport?.InternalWidth,
            displayWidth,
            1u);
        uint internalHeight = ResolvePositiveDimension(
            pipeline.ResourceInternalHeight,
            viewport?.InternalHeight,
            displayHeight,
            1u);

        FrameOpContext context = new(
            pipeline.GetHashCode(),
            viewport?.GetHashCode() ?? pipeline.LastWindowViewport?.GetHashCode() ?? 0,
            pipeline,
            pipeline.Resources,
            pipeline.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);

        return ApplyInteractiveResizePlannerFreeze(context);
    }

    private readonly struct ExternalResourcePlannerReadbackScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly FrameOpPlannerStateKey _key;
        private readonly bool _active;

        public ExternalResourcePlannerReadbackScope(
            VulkanRenderer renderer,
            in FrameOpContext context)
        {
            _renderer = renderer;
            _previousState = renderer.CaptureResourcePlannerRuntimeState();
            _key = FrameOpContextHasPlannerResources(context)
                ? BuildFrameOpPlannerStateKey(context)
                : default;
            _active = FrameOpContextHasPlannerResources(context);

            if (!_active)
                return;

            FrameOpResourcePlannerSwitchingState switchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            if (switchingState.States.TryGetValue(_key, out ResourcePlannerRuntimeState state))
            {
                renderer.RestoreResourcePlannerRuntimeState(state);
                return;
            }

            renderer.UpdateResourcePlannerFromContext(context);
            switchingState.States[_key] = renderer.CaptureResourcePlannerRuntimeState();
        }

        public void Dispose()
        {
            if (_active)
                _renderer.ActiveFrameOpResourcePlannerSwitchingState.States[_key] =
                    _renderer.CaptureResourcePlannerRuntimeState();

            _renderer.RestoreResourcePlannerRuntimeState(_previousState);
        }
    }

    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group)
    {
        group = null;
        if (string.IsNullOrWhiteSpace(resourceName))
            return false;

        if (ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group?.IsAllocated == true)
        {
            return true;
        }

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.ContainsKey(resourceName))
        {
            group = null;
            return false;
        }

        if (_isRecordingCommandBuffer)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringRecord.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Deferring lazy physical-image plan rebuild for '{0}' during command-buffer recording.",
                resourceName);
            group = null;
            return false;
        }

        if (IsCommandChainResourcePlanFrozen)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringFrozenCommandChainPlan.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Refusing lazy physical-image plan rebuild for '{0}' while command-chain readers are using frozen plan revision {1}.",
                resourceName,
                _commandChainFrozenResourcePlanRevision);
            group = null;
            return false;
        }

        UpdateResourcePlannerFromContext(context);

        if (ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group is not null)
        {
            group.EnsureAllocated(this);
            if (group.LastKnownLayout == ImageLayout.Undefined)
                TransitionNewPhysicalImagesToInitialLayout();
            return group.IsAllocated;
        }

        group = null;
        return false;
    }

    private FrameOpContext PrepareResourcePlannerForFrameOps(FrameOp[] ops)
    {
        if (ops.Length == 0)
        {
            FrameOpContext context = CaptureFrameOpContext();
            if (context.ResourceRegistry is null && context.PassMetadata is null)
                return context;

            UpdateResourcePlannerFromContext(context);
            return context;
        }

        HashSet<int>? activePassIndices = BuildActiveFrameOpPassSet(ops);
        HashSet<string>? activeFrameBufferNames = BuildActiveFrameOpFrameBufferSet(ops);
        int activeResourceSetSignature = ComputeActiveFrameBufferSetSignature(activeFrameBufferNames);
        FrameOpContext primary = SelectPrimaryPlannerContext(ops);
        RenderResourceRegistry? mergedRegistry = BuildMergedFrameOpRegistry(ops, primary.ResourceRegistry);
        FrameOpContext plannerContext = mergedRegistry is null
            ? primary
            : primary with { ResourceRegistry = mergedRegistry };

        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops);
        UpdateResourcePlannerFromContext(
            plannerContext,
            activePassIndices,
            activeFrameBufferNames,
            activeResourceSetSignature);
        return plannerContext;
    }

    private ulong PrepareFrameOpResourcePlannerStatesForFrameOps(FrameOp[] ops)
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        switchingState.HasActiveKey = false;
        switchingState.ActiveKeys.Clear();

        if (ops.Length == 0)
            return ResourcePlannerRevision;

        List<FrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        CollectFrameOpPlannerStateKeys(ops, keys);
        if (keys.Count <= 1)
            return ResourcePlannerRevision;

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        try
        {
            for (int i = 0; i < keys.Count; i++)
            {
                FrameOpPlannerStateKey key = keys[i];
                ResourcePlannerRuntimeState state = switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState existingState)
                    ? existingState
                    : ResourcePlannerRuntimeState.CreateEmpty();

                RestoreResourcePlannerRuntimeState(state);
                _ = PrepareResourcePlannerForFrameOps(ops, key);
                switchingState.States[key] = CaptureResourcePlannerRuntimeState();
                switchingState.ActiveKeys.Add(key);
            }
        }
        finally
        {
            RestoreResourcePlannerRuntimeState(previousState);
        }

        keys.Clear();
        switchingState.SwitchingActive = switchingState.ActiveKeys.Count > 1;
        if (!switchingState.SwitchingActive)
            return ResourcePlannerRevision;

        ulong signature = ComputeActiveFrameOpResourcePlannerStatesSignature();
        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.FrameOpContextStates.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Prepared {0} frame-op context resource planner states. Signature=0x{1:X16}.",
            switchingState.ActiveKeys.Count,
            signature);
        return signature;
    }

    private FrameOpContext PrepareResourcePlannerForFrameOps(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        HashSet<int>? activePassIndices = BuildActiveFrameOpPassSet(ops, key);
        HashSet<string>? activeFrameBufferNames = BuildActiveFrameOpFrameBufferSet(ops, key);
        int activeResourceSetSignature = ComputeActiveFrameBufferSetSignature(activeFrameBufferNames);
        FrameOpContext plannerContext = SelectPrimaryPlannerContext(ops, key);
        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops, filterByPlannerKey: true, plannerKey: key);
        UpdateResourcePlannerFromContext(
            plannerContext,
            activePassIndices,
            activeFrameBufferNames,
            activeResourceSetSignature);
        return plannerContext;
    }

    private void CollectFrameOpPlannerStateKeys(FrameOp[] ops, List<FrameOpPlannerStateKey> keys)
    {
        for (int i = 0; i < ops.Length; i++)
        {
            FrameOpContext context = ops[i].Context;
            if (!FrameOpContextHasPlannerResources(context))
                continue;

            FrameOpPlannerStateKey key = BuildFrameOpPlannerStateKey(context);
            if (!keys.Contains(key))
                keys.Add(key);
        }

        keys.Sort(static (left, right) =>
        {
            int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
            if (compare != 0)
                return compare;

            compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
            if (compare != 0)
                return compare;

            compare = left.ResourceRegistryIdentity.CompareTo(right.ResourceRegistryIdentity);
            if (compare != 0)
                return compare;

            return left.PassMetadataIdentity.CompareTo(right.PassMetadataIdentity);
        });
    }

    private ulong ComputeActiveFrameOpResourcePlannerStatesSignature()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        FrameOpSignatureHasher hash = new();
        List<FrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        foreach (FrameOpPlannerStateKey key in switchingState.ActiveKeys)
            keys.Add(key);
        keys.Sort(static (left, right) =>
        {
            int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
            if (compare != 0)
                return compare;

            compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
            if (compare != 0)
                return compare;

            compare = left.ResourceRegistryIdentity.CompareTo(right.ResourceRegistryIdentity);
            if (compare != 0)
                return compare;

            return left.PassMetadataIdentity.CompareTo(right.PassMetadataIdentity);
        });

        hash.Add(keys.Count);

        for (int i = 0; i < keys.Count; i++)
        {
            FrameOpPlannerStateKey key = keys[i];
            hash.Add(key.PipelineIdentity);
            hash.Add(key.ViewportIdentity);
            hash.Add(key.ResourceRegistryIdentity);
            hash.Add(key.PassMetadataIdentity);

            if (!switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state))
            {
                hash.Add(0);
                continue;
            }

            hash.Add(state.ResourcePlannerRevision);
            hash.Add(state.ResourcePlannerSignature);
            hash.Add(state.ResourceAllocationSignature);
        }

        keys.Clear();
        return hash.ToHash();
    }

    private FrameOpResourcePlannerRecordingScope EnterFrameOpResourcePlannerRecordingScope()
        => new(this);

    private bool TryActivateFrameOpResourcePlannerState(in FrameOpContext context)
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (!switchingState.SwitchingActive ||
            !FrameOpContextHasPlannerResources(context))
        {
            return false;
        }

        FrameOpPlannerStateKey key = BuildFrameOpPlannerStateKey(context);
        if (!switchingState.ActiveKeys.Contains(key))
            return false;

        if (switchingState.HasActiveKey &&
            key.Equals(switchingState.ActiveKey))
        {
            return true;
        }

        SaveActiveFrameOpResourcePlannerState();

        if (!switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state))
            return false;

        RestoreResourcePlannerRuntimeState(state);
        switchingState.ActiveKey = key;
        switchingState.HasActiveKey = true;
        return true;
    }

    private void SaveActiveFrameOpResourcePlannerState()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (!switchingState.RecordingScopeActive ||
            !switchingState.HasActiveKey)
        {
            return;
        }

        switchingState.States[switchingState.ActiveKey] = CaptureResourcePlannerRuntimeState();
    }

    private void DestroyFrameOpResourcePlannerStates()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (switchingState.States.Count == 0)
            return;

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        WaitForAllInFlightWork();
        foreach (KeyValuePair<FrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            RestoreResourcePlannerRuntimeState(pair.Value);
            ReleaseDescriptorReferencesForPhysicalResourceDestruction(
                $"FrameOpResourcePlannerStateDestroy.pipe{pair.Key.PipelineIdentity}.vp{pair.Key.ViewportIdentity}");
            DrainAllRetiredDescriptorPools();
            ResourceAllocator.DestroyPhysicalImages(this);
            ResourceAllocator.DestroyPhysicalBuffers(this);
        }

        switchingState.States.Clear();
        switchingState.ActiveKeys.Clear();
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        switchingState.HasActiveKey = false;
        RestoreResourcePlannerRuntimeState(previousState);
    }

    private static HashSet<int>? BuildActiveFrameOpPassSet(FrameOp[] ops)
    {
        HashSet<int> passIndices = [];
        foreach (FrameOp op in ops)
        {
            if (op.PassIndex != int.MinValue)
                passIndices.Add(op.PassIndex);
        }

        return passIndices.Count > 0 ? passIndices : null;
    }

    private static HashSet<int>? BuildActiveFrameOpPassSet(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        HashSet<int> passIndices = [];
        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            if (op.PassIndex != int.MinValue)
                passIndices.Add(op.PassIndex);
        }

        return passIndices.Count > 0 ? passIndices : null;
    }

    private static HashSet<string>? BuildActiveFrameOpFrameBufferSet(FrameOp[] ops)
    {
        HashSet<string> frameBufferNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (FrameOp op in ops)
        {
            AddFrameBufferName(frameBufferNames, op.Target);

            if (op is BlitOp blit)
            {
                AddFrameBufferName(frameBufferNames, blit.InFbo);
                AddFrameBufferName(frameBufferNames, blit.OutFbo);
            }
        }

        return frameBufferNames.Count > 0 ? frameBufferNames : null;
    }

    private static HashSet<string>? BuildActiveFrameOpFrameBufferSet(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        HashSet<string> frameBufferNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            AddFrameBufferName(frameBufferNames, op.Target);

            if (op is BlitOp blit)
            {
                AddFrameBufferName(frameBufferNames, blit.InFbo);
                AddFrameBufferName(frameBufferNames, blit.OutFbo);
            }
        }

        return frameBufferNames.Count > 0 ? frameBufferNames : null;
    }

    private static void AddFrameBufferName(HashSet<string> frameBufferNames, XRFrameBuffer? frameBuffer)
    {
        string? name = frameBuffer?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            frameBufferNames.Add(name);
    }

    private static bool FrameOpContextHasPlannerResources(in FrameOpContext context)
        => context.ResourceRegistry is not null ||
            context.PassMetadata is { Count: > 0 };

    private static FrameOpPlannerStateKey BuildFrameOpPlannerStateKey(in FrameOpContext context)
        => new(
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.ResourceRegistry is null ? 0 : RuntimeHelpers.GetHashCode(context.ResourceRegistry),
            context.PassMetadata is null ? 0 : RuntimeHelpers.GetHashCode(context.PassMetadata));

    private static bool FrameOpMatchesPlannerStateKey(FrameOp op, in FrameOpPlannerStateKey key)
        => FrameOpContextHasPlannerResources(op.Context) &&
            BuildFrameOpPlannerStateKey(op.Context).Equals(key);

    private FrameOpContext RefreshPlannerExtentsFromLiveContext(FrameOpContext context, FrameOp[] ops)
    {
        FrameOpPlannerStateKey ignoredKey = default;
        return RefreshPlannerExtentsFromLiveContext(context, ops, filterByPlannerKey: false, ignoredKey);
    }

    private FrameOpContext RefreshPlannerExtentsFromLiveContext(
        FrameOpContext context,
        FrameOp[] ops,
        bool filterByPlannerKey,
        in FrameOpPlannerStateKey plannerKey)
    {
        if (XRWindow.IsInteractiveResizeInProgress)
            return ApplyInteractiveResizePlannerFreeze(context);

        if (IsRenderingExternalSwapchainTarget)
            return context;

        FrameOpContext live = CaptureFrameOpContextOrLastActive();
        bool refreshExtents =
            ReferenceEquals(context.PipelineInstance, live.PipelineInstance) ||
            ReferenceEquals(context.ResourceRegistry, live.ResourceRegistry);

        if (!refreshExtents)
        {
            foreach (FrameOp op in ops)
            {
                if (filterByPlannerKey && !FrameOpMatchesPlannerStateKey(op, plannerKey))
                    continue;

                if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                {
                    refreshExtents = true;
                    break;
                }
            }
        }

        if (!refreshExtents)
            return context;

        uint displayWidth = live.DisplayWidth > 0 ? live.DisplayWidth : context.DisplayWidth;
        uint displayHeight = live.DisplayHeight > 0 ? live.DisplayHeight : context.DisplayHeight;
        uint internalWidth = live.InternalWidth > 0 ? live.InternalWidth : context.InternalWidth;
        uint internalHeight = live.InternalHeight > 0 ? live.InternalHeight : context.InternalHeight;

        if (displayWidth == context.DisplayWidth &&
            displayHeight == context.DisplayHeight &&
            internalWidth == context.InternalWidth &&
            internalHeight == context.InternalHeight)
        {
            return context;
        }

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.RefreshFrameOpExtents.{context.PipelineIdentity}.{context.ViewportIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Refreshing frame-op planner extents from live viewport. Old={0}x{1}/{2}x{3} Live={4}x{5}/{6}x{7}.",
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);

        return context with
        {
            DisplayWidth = displayWidth,
            DisplayHeight = displayHeight,
            InternalWidth = internalWidth,
            InternalHeight = internalHeight
        };
    }

    private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops)
    {
        FrameOpContext fallback = ops[0].Context;
        FrameOpContext best = fallback;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            FrameOpContext context = op.Context;
            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                score += 16;

            foreach (XRFrameBuffer target in EnumerateFrameOpFrameBuffers(op))
            {
                if (!string.IsNullOrWhiteSpace(target.Name) &&
                    context.ResourceRegistry.FrameBufferRecords.ContainsKey(target.Name))
                {
                    score += 256;
                }
                else
                {
                    score += 32;
                }
            }

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return best;
    }

    private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        FrameOpContext best = default;
        bool hasBest = false;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            FrameOpContext context = op.Context;
            if (!hasBest)
            {
                best = context;
                hasBest = true;
            }

            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                score += 16;

            foreach (XRFrameBuffer target in EnumerateFrameOpFrameBuffers(op))
            {
                if (!string.IsNullOrWhiteSpace(target.Name) &&
                    context.ResourceRegistry.FrameBufferRecords.ContainsKey(target.Name))
                {
                    score += 256;
                }
                else
                {
                    score += 32;
                }
            }

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return hasBest ? best : SelectPrimaryPlannerContext(ops);
    }

    private static int ComparePlannerContextTieBreak(in FrameOpContext left, in FrameOpContext right)
    {
        int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
        if (compare != 0)
            return compare;

        compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
        if (compare != 0)
            return compare;

        compare = (left.ResourceRegistry?.DescriptorSignature ?? 0)
            .CompareTo(right.ResourceRegistry?.DescriptorSignature ?? 0);
        if (compare != 0)
            return compare;

        return (left.PassMetadata?.Count ?? 0).CompareTo(right.PassMetadata?.Count ?? 0);
    }

    private static uint ResolvePositiveDimension(uint? primary, int? secondary, uint tertiary, uint fallback)
    {
        if (primary.HasValue && primary.Value > 0)
            return primary.Value;

        if (secondary.HasValue && secondary.Value > 0)
            return (uint)secondary.Value;

        return tertiary > 0 ? tertiary : fallback;
    }

    private Extent2D ResolveFrameOpContextFallbackExtent()
    {
        if (TryGetExternalSwapchainTargetRegion(out BoundingRectangle region) &&
            region.Width > 0 &&
            region.Height > 0)
        {
            return new Extent2D(
                (uint)region.Width,
                (uint)region.Height);
        }

        return swapChainExtent;
    }

    private VulkanResourceExtentContext BuildResourceExtentContext(in FrameOpContext context)
    {
        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        uint displayWidth = context.DisplayWidth > 0
            ? context.DisplayWidth
            : Math.Max(fallbackExtent.Width, 1u);
        uint displayHeight = context.DisplayHeight > 0
            ? context.DisplayHeight
            : Math.Max(fallbackExtent.Height, 1u);
        uint internalWidth = context.InternalWidth > 0
            ? context.InternalWidth
            : displayWidth;
        uint internalHeight = context.InternalHeight > 0
            ? context.InternalHeight
            : displayHeight;

        return new VulkanResourceExtentContext(
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);
    }

    private RenderResourceRegistry? BuildMergedFrameOpRegistry(
        FrameOp[] ops,
        RenderResourceRegistry? primaryRegistry)
    {
        RenderResourceRegistry[] registries = CollectUniqueFrameOpRegistries(ops);

        if (registries.Length == 0)
            return primaryRegistry;

        if (registries.Length == 1)
            return registries[0];

        if (primaryRegistry is not null && RegistriesCoveredByPrimary(registries, primaryRegistry))
            return primaryRegistry;

        FrameOpRegistryCacheSource[] cacheSources = BuildFrameOpRegistryCacheSources(ops, registries);
        if (TryGetCachedMergedFrameOpRegistry(primaryRegistry, cacheSources, out RenderResourceRegistry? cachedRegistry))
            return cachedRegistry;

        RenderResourceRegistry merged = new();
        if (primaryRegistry is not null)
            AddRegistryDescriptors(merged, primaryRegistry, overwrite: true);

        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            AddRegistryDescriptors(merged, registry, overwrite: false);
        }

        RememberMergedFrameOpRegistry(primaryRegistry, cacheSources, merged);
        return merged;
    }

    private static RenderResourceRegistry[] CollectUniqueFrameOpRegistries(FrameOp[] ops)
    {
        List<RenderResourceRegistry>? registries = null;
        foreach (FrameOp op in ops)
        {
            if (op.Context.ResourceRegistry is not { } registry)
                continue;

            registries ??= new();
            bool exists = false;
            for (int i = 0; i < registries.Count; i++)
            {
                if (ReferenceEquals(registries[i], registry))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                registries.Add(registry);
        }

        if (registries is null || registries.Count == 0)
            return [];

        registries.Sort(static (left, right) =>
            RuntimeHelpers.GetHashCode(left).CompareTo(RuntimeHelpers.GetHashCode(right)));
        return registries.ToArray();
    }

    private static FrameOpRegistryCacheSource[] BuildFrameOpRegistryCacheSources(
        FrameOp[] ops,
        RenderResourceRegistry[] registries)
    {
        FrameOpRegistryCacheSource[] sources = new FrameOpRegistryCacheSource[registries.Length];
        for (int i = 0; i < registries.Length; i++)
        {
            RenderResourceRegistry registry = registries[i];
            sources[i] = new FrameOpRegistryCacheSource(
                registry,
                ComputeResourceRegistrySignature(registry),
                FindRegistryGenerationStamp(ops, registry));
        }

        return sources;
    }

    private static int FindRegistryGenerationStamp(FrameOp[] ops, RenderResourceRegistry registry)
    {
        int stamp = 0;
        foreach (FrameOp op in ops)
        {
            if (ReferenceEquals(op.Context.ResourceRegistry, registry) &&
                op.Context.PipelineInstance is { } pipeline)
            {
                stamp = Math.Max(stamp, pipeline.ResourceGeneration);
            }
        }

        return stamp;
    }

    private bool TryGetCachedMergedFrameOpRegistry(
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        out RenderResourceRegistry? mergedRegistry)
    {
        for (int i = 0; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            MergedFrameOpRegistryCacheEntry entry = _mergedFrameOpRegistryCache[i];
            if (!ReferenceEquals(entry.PrimaryRegistry, primaryRegistry) ||
                !FrameOpRegistryCacheSourcesMatch(entry.Sources, sources))
            {
                continue;
            }

            entry.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            mergedRegistry = entry.MergedRegistry;
            return true;
        }

        mergedRegistry = null;
        return false;
    }

    private void RememberMergedFrameOpRegistry(
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        RenderResourceRegistry mergedRegistry)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        _mergedFrameOpRegistryCache.Add(new MergedFrameOpRegistryCacheEntry(
            primaryRegistry,
            sources,
            mergedRegistry,
            frameId));

        if (_mergedFrameOpRegistryCache.Count <= MaxMergedFrameOpRegistryCacheEntries)
            return;

        int oldestIndex = 0;
        ulong oldestFrameId = _mergedFrameOpRegistryCache[0].LastUsedFrameId;
        for (int i = 1; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            ulong candidateFrameId = _mergedFrameOpRegistryCache[i].LastUsedFrameId;
            if (candidateFrameId < oldestFrameId)
            {
                oldestIndex = i;
                oldestFrameId = candidateFrameId;
            }
        }

        _mergedFrameOpRegistryCache.RemoveAt(oldestIndex);
    }

    private static bool FrameOpRegistryCacheSourcesMatch(
        FrameOpRegistryCacheSource[] cached,
        FrameOpRegistryCacheSource[] current)
    {
        if (cached.Length != current.Length)
            return false;

        for (int i = 0; i < cached.Length; i++)
        {
            if (!ReferenceEquals(cached[i].Registry, current[i].Registry) ||
                cached[i].DescriptorSignature != current[i].DescriptorSignature ||
                cached[i].ResourceGenerationStamp != current[i].ResourceGenerationStamp)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RegistriesCoveredByPrimary(
        IEnumerable<RenderResourceRegistry> registries,
        RenderResourceRegistry primaryRegistry)
    {
        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            if (!TextureDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !FrameBufferDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !BufferDescriptorsCoveredByPrimary(registry, primaryRegistry))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TextureDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
        {
            if (!primary.TextureRecords.TryGetValue(pair.Key, out RenderTextureResource? primaryRecord) ||
                !EqualityComparer<TextureResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FrameBufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
        {
            if (!primary.FrameBufferRecords.TryGetValue(pair.Key, out RenderFrameBufferResource? primaryRecord) ||
                !FrameBufferDescriptorsEquivalent(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
        {
            if (!primary.BufferRecords.TryGetValue(pair.Key, out RenderBufferResource? primaryRecord) ||
                !EqualityComparer<BufferResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FrameBufferDescriptorsEquivalent(
        FrameBufferResourceDescriptor left,
        FrameBufferResourceDescriptor right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
            left.Lifetime != right.Lifetime ||
            left.SizePolicy != right.SizePolicy ||
            left.Attachments.Count != right.Attachments.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddRegistryDescriptors(
        RenderResourceRegistry destination,
        RenderResourceRegistry source,
        bool overwrite)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
        {
            if (overwrite || !destination.TextureRecords.ContainsKey(pair.Key))
                destination.RegisterTextureDescriptor(pair.Value.Descriptor);
        }

        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
        {
            if (overwrite || !destination.FrameBufferRecords.ContainsKey(pair.Key))
                destination.RegisterFrameBufferDescriptor(pair.Value.Descriptor);
        }

        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
        {
            if (overwrite || !destination.BufferRecords.ContainsKey(pair.Key))
                destination.RegisterBufferDescriptor(pair.Value.Descriptor);
        }
    }

    private static IEnumerable<XRFrameBuffer> EnumerateFrameOpFrameBuffers(FrameOp op)
    {
        if (op.Target is not null)
            yield return op.Target;

        if (op is BlitOp blit)
        {
            if (blit.InFbo is not null)
                yield return blit.InFbo;
            if (blit.OutFbo is not null)
                yield return blit.OutFbo;
        }
    }

    internal static bool RequiresResourcePlannerRebuild(in FrameOpContext previous, in FrameOpContext next)
    {
        if (!ReferenceEquals(previous.PipelineInstance, next.PipelineInstance))
            return true;

        if (!ReferenceEquals(previous.ResourceRegistry, next.ResourceRegistry))
            return true;

        if (!ReferenceEquals(previous.PassMetadata, next.PassMetadata))
            return true;

        return false;
    }

    private void UpdateResourcePlannerFromContext(
        in FrameOpContext context,
        HashSet<int>? activePassIndices = null,
        HashSet<string>? activeFrameBufferNames = null,
        int activeResourceSetSignature = 0)
    {
        if (IsCommandChainResourcePlanFrozen)
        {
            throw new InvalidOperationException(
                $"Resource planner cannot be replaced while command-chain readers are using frozen plan revision {_commandChainFrozenResourcePlanRevision}.");
        }

        int activePassSetSignature = ComputeActivePassSetSignature(activePassIndices);
        ResourcePlanningInputs planningInputs = PrepareResourcePlanningInputs(
            context,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature);

        if (CanReuseResourcePlannerFastPath(planningInputs.FastPathKey))
            return;

        ulong plannerSignature = ComputeResourcePlannerSignature(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        if (plannerSignature == ActiveResourcePlannerSignature)
        {
            RememberResourcePlannerFastPath(planningInputs.FastPathKey);
            return;
        }

        ResourcePlannerSignatureBreakdown signatureBreakdown = ComputeResourcePlannerSignatureBreakdown(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.SignatureChange.{context.PipelineIdentity}.{context.ViewportIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Signature changed. Revision={0} Old=0x{1:X16} New=0x{2:X16} ChangedFields=[{3}] OldComponents=[{4}] NewComponents=[{5}]",
            ActiveResourcePlannerRevision,
            ActiveResourcePlannerSignature,
            plannerSignature,
            signatureBreakdown.DescribeDelta(ActiveResourcePlannerSignatureBreakdown),
            ActiveResourcePlannerSignatureBreakdown,
            signatureBreakdown);

        VulkanResourcePlanner pendingPlanner = BuildResourceDescriptorPlan(context, planningInputs.ActivePassMetadata);
        PhysicalAllocationPlan allocationPlan = BuildPhysicalAllocationPlan(
            context,
            pendingPlanner,
            planningInputs.ActivePassMetadata);
        LogPhysicalAllocationPlanStatus(context, pendingPlanner, allocationPlan, planningInputs.ActivePassMetadata);

        VulkanResourceAllocator oldAllocator = ResourceAllocator;
        VulkanResourceAllocator? pendingAllocator = null;
        int retiredImageCount = 0;
        int retiredBufferCount = 0;
        if (allocationPlan.Changed)
        {
            if (ShouldDeferFailedResourceAllocationRetry(plannerSignature, allocationPlan.Signature))
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourcePlanner.DeferFailedAllocationRetry.{context.PipelineIdentity}.{context.ViewportIdentity}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Deferring retry for previously failed physical resource plan. Planner=0x{0:X16} Allocation=0x{1:X16}.",
                    plannerSignature,
                    allocationPlan.Signature);
                return;
            }

            if (!TryBuildPhysicalAllocator(
                context,
                pendingPlanner,
                allocationPlan.ExtentContext,
                planningInputs.ActivePassMetadata,
                out pendingAllocator,
                out retiredImageCount,
                out retiredBufferCount))
            {
                RecordResourceAllocationPlanFailure(plannerSignature, allocationPlan.Signature);
                return;
            }

            ClearResourceAllocationPlanFailure(plannerSignature, allocationPlan.Signature);
        }

        ActiveResourcePlanner = pendingPlanner;
        if (pendingAllocator is not null)
            ActiveResourceAllocator = pendingAllocator;

        CommitPhysicalAllocatorPlan(allocationPlan.Changed, oldAllocator, retiredImageCount, retiredBufferCount);
        RebuildRenderGraphAndBarriers(planningInputs, allocationPlan.Signature);

        ActiveResourcePlannerSignature = plannerSignature;
        ActiveResourceAllocationSignature = allocationPlan.Signature;
        ActiveResourcePlannerSignatureBreakdown = signatureBreakdown;
        ActiveResourcePlannerRevision++;
        RememberResourcePlannerFastPath(planningInputs.FastPathKey);
    }

    private ResourcePlanningInputs PrepareResourcePlanningInputs(
        in FrameOpContext context,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature)
    {
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata = FilterActivePassMetadata(
            context.PassMetadata,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature);
        VulkanCompiledRenderGraph compiledGraph = _renderGraphCompiler.Compile(activePassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = BuildQueueOwnershipConfig(activePassMetadata);
        ResourcePlannerFastPathKey fastPathKey = new(
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassMetadata,
            ComputePassMetadataRevisionStamp(activePassMetadata),
            activePassSetSignature,
            activeResourceSetSignature,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            queueOwnership,
            SupportsTransformFeedback);

        return new ResourcePlanningInputs(activePassMetadata, compiledGraph, queueOwnership, fastPathKey);
    }

    private bool CanReuseResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
        => ActiveHasResourcePlannerFastPathKey
            && ActiveResourcePlannerSignature != ulong.MaxValue
            && key.Matches(ActiveResourcePlannerFastPathKey);

    private void RememberResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
    {
        ActiveResourcePlannerFastPathKey = key;
        ActiveHasResourcePlannerFastPathKey = true;
    }

    private static VulkanResourcePlanner BuildResourceDescriptorPlan(
        in FrameOpContext context,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        VulkanResourcePlanner pendingPlanner = new();
        pendingPlanner.Sync(context.ResourceRegistry);
        ValidateVulkanResourcePlanMetadata(activePassMetadata, pendingPlanner);
        return pendingPlanner;
    }

    private PhysicalAllocationPlan BuildPhysicalAllocationPlan(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        VulkanResourceExtentContext extentContext = BuildResourceExtentContext(context);
        ulong allocationSignature = ComputeResourceAllocationSignature(
            context,
            pendingPlanner,
            passMetadata,
            extentContext,
            SupportsTransformFeedback);
        return new PhysicalAllocationPlan(
            extentContext,
            allocationSignature,
            allocationSignature != ActiveResourceAllocationSignature);
    }

    private void LogPhysicalAllocationPlanStatus(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        in PhysicalAllocationPlan allocationPlan,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        if (allocationPlan.Changed)
        {
            ResourceAllocationSignatureBreakdown allocationBreakdown = ComputeResourceAllocationSignatureBreakdown(
                context,
                pendingPlanner,
                activePassMetadata,
                allocationPlan.ExtentContext,
                SupportsTransformFeedback);
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.PhysicalPlanChange.{context.PipelineIdentity}.{context.ViewportIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Physical resource plan changed. Revision={0} Old=0x{1:X16} New=0x{2:X16} Components=[{3}]",
                ActiveResourcePlannerRevision,
                ActiveResourceAllocationSignature,
                allocationPlan.Signature,
                allocationBreakdown);
            return;
        }

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.PhysicalPlanReuse.{context.PipelineIdentity}.{context.ViewportIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Reusing physical resource plan for metadata-only graph change. Revision={0} AllocationSignature=0x{1:X16}",
            ActiveResourcePlannerRevision,
            allocationPlan.Signature);
    }

    private bool ShouldDeferFailedResourceAllocationRetry(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (ActiveFailedResourcePlannerSignature != plannerSignature ||
            ActiveFailedResourceAllocationSignature != allocationSignature ||
            ActiveFailedResourceAllocationTimestamp == 0)
        {
            return false;
        }

        return Stopwatch.GetElapsedTime(ActiveFailedResourceAllocationTimestamp) <
            ResourceAllocationFailureRetryDelay;
    }

    private void RecordResourceAllocationPlanFailure(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        ActiveFailedResourcePlannerSignature = plannerSignature;
        ActiveFailedResourceAllocationSignature = allocationSignature;
        ActiveFailedResourceAllocationTimestamp = Stopwatch.GetTimestamp();
    }

    private void ClearResourceAllocationPlanFailure(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (ActiveFailedResourcePlannerSignature != plannerSignature ||
            ActiveFailedResourceAllocationSignature != allocationSignature)
        {
            return;
        }

        ActiveFailedResourcePlannerSignature = ulong.MaxValue;
        ActiveFailedResourceAllocationSignature = ulong.MaxValue;
        ActiveFailedResourceAllocationTimestamp = 0;
    }

    private bool TryBuildPhysicalAllocator(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        VulkanResourceExtentContext extentContext,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata,
        out VulkanResourceAllocator? pendingAllocator,
        out int retiredImageCount,
        out int retiredBufferCount)
    {
        pendingAllocator = new();
        retiredImageCount = 0;
        retiredBufferCount = 0;

        try
        {
            pendingAllocator.UpdatePlan(pendingPlanner.CurrentPlan);
            pendingAllocator.RebuildPhysicalPlan(
                this,
                activePassMetadata,
                pendingPlanner,
                extentContext);
            pendingAllocator.AllocatePhysicalImages(this);
            pendingAllocator.AllocatePhysicalBuffers(this);
        }
        catch (Exception ex)
        {
            pendingAllocator.DestroyPhysicalImages(this);
            pendingAllocator.DestroyPhysicalBuffers(this);
            pendingAllocator = null;
            Debug.VulkanWarning(
                "[VulkanResourcePlanner] Pending physical resource plan failed. Keeping active plan revision={0}. Reason={1}",
                ActiveResourcePlannerRevision,
                ex.Message);
            return false;
        }

        retiredImageCount = ResourceAllocator.EnumeratePhysicalGroups().Count(static g => g.IsAllocated);
        retiredBufferCount = ResourceAllocator.EnumeratePhysicalBufferGroups().Count(static g => g.IsAllocated);
        return true;
    }

    private void CommitPhysicalAllocatorPlan(
        bool physicalPlanChanged,
        VulkanResourceAllocator oldAllocator,
        int retiredImageCount,
        int retiredBufferCount)
    {
        if (!physicalPlanChanged)
            return;

        // Transition every newly-allocated physical image from VK_IMAGE_LAYOUT_UNDEFINED
        // to a usable initial layout so that the first render pass that references them
        // does not hit a validation error (images stuck in UNDEFINED). Depth/stencil
        // images go to DEPTH_STENCIL_ATTACHMENT_OPTIMAL; colour images go to GENERAL
        // which is compatible with attachment, sampled, and storage usage.
        TransitionNewPhysicalImagesToInitialLayout();

        if (retiredImageCount > 0 || retiredBufferCount > 0)
        {
            LogDeferredResourcePlanReplacementRetirement(retiredImageCount, retiredBufferCount);
            ReleaseDescriptorReferencesForPhysicalResourceDestruction("ResourcePlanReplacement");
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourcePlanReplacement(retiredImageCount, retiredBufferCount);
        }

        if (IsDeviceLost)
            return;

        PreserveAutoExposureHistory(oldAllocator);

        oldAllocator.DestroyPhysicalImages(this);
        oldAllocator.DestroyPhysicalBuffers(this);
    }

    private void PreserveAutoExposureHistory(VulkanResourceAllocator oldAllocator)
    {
        if (ShouldSkipAutoExposureHistoryPreserve())
            return;

        if (!oldAllocator.TryGetPhysicalGroupForResource(DefaultRenderPipeline.AutoExposureTextureName, out VulkanPhysicalImageGroup? oldGroup) ||
            !ResourceAllocator.TryGetPhysicalGroupForResource(DefaultRenderPipeline.AutoExposureTextureName, out VulkanPhysicalImageGroup? newGroup) ||
            oldGroup is null ||
            newGroup is null ||
            ReferenceEquals(oldGroup, newGroup) ||
            !oldGroup.IsAllocated ||
            !newGroup.IsAllocated ||
            oldGroup.Image.Handle == 0 ||
            newGroup.Image.Handle == 0 ||
            oldGroup.LastKnownLayout == ImageLayout.Undefined ||
            oldGroup.Format != newGroup.Format ||
            oldGroup.ResolvedExtent.Width != newGroup.ResolvedExtent.Width ||
            oldGroup.ResolvedExtent.Height != newGroup.ResolvedExtent.Height ||
            oldGroup.ResolvedExtent.Depth != newGroup.ResolvedExtent.Depth)
        {
            return;
        }

        ImageLayout oldLayout = oldGroup.LastKnownLayout;
        ImageLayout newLayout = newGroup.LastKnownLayout == ImageLayout.Undefined
            ? ResolveInitialPhysicalGroupLayout(newGroup.Usage, VulkanResourceAllocator.IsDepthStencilFormat(newGroup.Format))
            : newGroup.LastKnownLayout;

        using var scope = NewCommandScope();

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            oldGroup,
            oldLayout,
            ImageLayout.TransferSrcOptimal,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            AccessFlags.TransferReadBit,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.TransferBit);

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            newGroup,
            newLayout,
            ImageLayout.TransferDstOptimal,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            AccessFlags.TransferWriteBit,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.TransferBit);

        ImageCopy copy = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            Extent = new Extent3D(
                Math.Max(1u, oldGroup.ResolvedExtent.Width),
                Math.Max(1u, oldGroup.ResolvedExtent.Height),
                Math.Max(1u, oldGroup.ResolvedExtent.Depth))
        };

        Api!.CmdCopyImage(
            scope.CommandBuffer,
            oldGroup.Image,
            ImageLayout.TransferSrcOptimal,
            newGroup.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            newGroup,
            ImageLayout.TransferDstOptimal,
            newLayout,
            AccessFlags.TransferWriteBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.AllCommandsBit);

        oldGroup.LastKnownLayout = ImageLayout.TransferSrcOptimal;
        newGroup.LastKnownLayout = newLayout;
    }

    private bool ShouldSkipAutoExposureHistoryPreserve()
        => IsDeviceLost ||
           ActiveResourcePlannerRevision == 0 ||
           RuntimeRenderingHostServices.Current.IsInVR;

    private void TransitionPhysicalGroupForCopy(
        CommandBuffer commandBuffer,
        VulkanPhysicalImageGroup group,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
    {
        if (oldLayout == newLayout)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = group.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = Math.Max(1u, group.MipLevels),
                BaseArrayLayer = 0,
                LayerCount = Math.Max(1u, group.Template.Layers),
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

    private void RebuildRenderGraphAndBarriers(
        in ResourcePlanningInputs planningInputs,
        ulong resourceAllocationSignature)
    {
        ActiveCompiledRenderGraph = planningInputs.CompiledGraph;

        BarrierPlanFastPathKey barrierKey = new(
            planningInputs.CompiledGraph,
            resourceAllocationSignature,
            planningInputs.QueueOwnership);
        if (ActiveHasBarrierPlanFastPathKey && barrierKey.Matches(ActiveBarrierPlanFastPathKey))
            return;

        BarrierPlanner.Rebuild(
            planningInputs.ActivePassMetadata,
            ResourcePlanner,
            ResourceAllocator,
            CompiledRenderGraph.Synchronization,
            planningInputs.QueueOwnership);
        ActiveBarrierPlanFastPathKey = barrierKey;
        ActiveHasBarrierPlanFastPathKey = true;
    }

    private IReadOnlyCollection<RenderPassMetadata>? FilterActivePassMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature)
    {
        if (passMetadata is null || passMetadata.Count == 0 || activePassIndices is not { Count: > 0 })
            return passMetadata;

        if (ReferenceEquals(passMetadata, _lastActiveFilterSourcePassMetadata) &&
            activePassSetSignature == _lastActiveFilterPassSetSignature &&
            activeResourceSetSignature == _lastActiveFilterResourceSetSignature)
        {
            return _lastActiveFilterResult;
        }

        List<RenderPassMetadata> filtered = new(Math.Min(passMetadata.Count, activePassIndices.Count));
        bool removedResourceUsages = false;
        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (!activePassIndices.Contains(pass.PassIndex))
                continue;

            RenderPassMetadata activePass = FilterActivePassResourceUsages(
                pass,
                activePassIndices,
                activeFrameBufferNames,
                ref removedResourceUsages);
            filtered.Add(activePass);
        }

        IReadOnlyCollection<RenderPassMetadata> result;
        if (filtered.Count == passMetadata.Count && !removedResourceUsages)
        {
            result = passMetadata;
        }
        else if (filtered.Count == 0)
        {
            result = Array.Empty<RenderPassMetadata>();
        }
        else
        {
            filtered.Sort(static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
            result = filtered.ToArray();
        }

        _lastActiveFilterSourcePassMetadata = passMetadata;
        _lastActiveFilterPassSetSignature = activePassSetSignature;
        _lastActiveFilterResourceSetSignature = activeResourceSetSignature;
        _lastActiveFilterResult = result;
        return result;
    }

    private static RenderPassMetadata FilterActivePassResourceUsages(
        RenderPassMetadata pass,
        HashSet<int> activePassIndices,
        HashSet<string>? activeFrameBufferNames,
        ref bool removedResourceUsages)
    {
        if (activeFrameBufferNames is not { Count: > 0 })
            return pass;

        List<RenderPassResourceUsage>? activeUsages = null;
        for (int i = 0; i < pass.ResourceUsages.Count; i++)
        {
            RenderPassResourceUsage usage = pass.ResourceUsages[i];
            if (IsInactiveFrameBufferUsage(usage, activeFrameBufferNames))
            {
                removedResourceUsages = true;
                if (activeUsages is null)
                {
                    activeUsages = new List<RenderPassResourceUsage>(pass.ResourceUsages.Count);
                    for (int previous = 0; previous < i; previous++)
                        activeUsages.Add(pass.ResourceUsages[previous]);
                }
                continue;
            }

            activeUsages?.Add(usage);
        }

        if (activeUsages is null)
            return pass;

        RenderPassMetadata filtered = new(pass.PassIndex, pass.Name, pass.Stage, pass.DeclarationOrder);
        foreach (RenderPassResourceUsage usage in activeUsages)
            filtered.AddUsage(usage);

        foreach (int dependency in pass.ExplicitDependencies)
            if (activePassIndices.Contains(dependency))
                filtered.AddDependency(dependency);

        foreach (string schema in pass.DescriptorSchemas)
            filtered.AddDescriptorSchema(schema);

        return filtered;
    }

    private static bool IsInactiveFrameBufferUsage(
        RenderPassResourceUsage usage,
        HashSet<string> activeFrameBufferNames)
    {
        if (!usage.ResourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = usage.ResourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        string frameBufferName = segments[1];
        return !IsVulkanExternalOutputName(frameBufferName) &&
            !activeFrameBufferNames.Contains(frameBufferName);
    }

    private static int ComputeActivePassSetSignature(HashSet<int>? activePassIndices)
    {
        if (activePassIndices is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activePassIndices.Count);
        long sum = 0;
        long squaredSum = 0;
        int xor = 0;
        foreach (int passIndex in activePassIndices)
        {
            sum += passIndex;
            squaredSum += (long)passIndex * passIndex;
            xor ^= HashCode.Combine(passIndex);
        }

        hash.Add(sum);
        hash.Add(squaredSum);
        hash.Add(xor);
        return hash.ToHashCode();
    }

    private static int ComputeActiveFrameBufferSetSignature(HashSet<string>? activeFrameBufferNames)
    {
        if (activeFrameBufferNames is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activeFrameBufferNames.Count);
        foreach (string frameBufferName in activeFrameBufferNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            hash.Add(frameBufferName, StringComparer.OrdinalIgnoreCase);

        return hash.ToHashCode();
    }

    private void LogDeferredResourcePlanReplacementRetirement(int imageCount, int bufferCount)
    {
        if (IsDeviceLost)
            return;

        Debug.VulkanEvery(
            "Vulkan.ResourcePlanner.PlanReplacementDeferredRetirement",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Deferring replaced physical resource plan retirement through frame-slot queues. images={0} buffers={1}",
            imageCount,
            bufferCount);
    }

    private static void ValidateVulkanResourcePlanMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner,
        HashSet<int>? activePassIndices = null)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return;

        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (activePassIndices is { Count: > 0 } && !activePassIndices.Contains(pass.PassIndex))
                continue;

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                string resourceName = usage.ResourceName;
                if (string.IsNullOrWhiteSpace(resourceName)
                    || IsVulkanExternalOutputResourceBinding(resourceName))
                {
                    continue;
                }

                if (resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateVulkanFrameBufferBinding(pass, usage, resourceName, planner);
                    continue;
                }

                if (resourceName.StartsWith("tex::", StringComparison.OrdinalIgnoreCase))
                {
                    string textureName = resourceName["tex::".Length..];
                    if (!string.IsNullOrWhiteSpace(textureName)
                        && !IsVulkanPlannerOptionalResource(textureName)
                        && !planner.TryGetTextureDescriptor(textureName, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingTexture.{pass.PassIndex}.{textureName}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared texture '{1}'.",
                            pass.Name,
                            textureName);
                    }
                    continue;
                }

                if (resourceName.StartsWith("buf::", StringComparison.OrdinalIgnoreCase))
                {
                    string bufferName = resourceName["buf::".Length..];
                    if (!string.IsNullOrWhiteSpace(bufferName)
                        && !IsVulkanPlannerOptionalResource(bufferName)
                        && !planner.TryGetBufferDescriptor(bufferName, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingBuffer.{pass.PassIndex}.{bufferName}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared buffer '{1}'.",
                            pass.Name,
                            bufferName);
                    }
                }
            }
        }
    }

    private static void ValidateVulkanFrameBufferBinding(
        RenderPassMetadata pass,
        RenderPassResourceUsage usage,
        string resourceName,
        VulkanResourcePlanner planner)
    {
        string[] segments = resourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return;

        string frameBufferName = segments[1];
        if (IsVulkanExternalOutputName(frameBufferName) || IsVulkanPlannerOptionalResource(frameBufferName))
            return;

        string slot = segments.Length >= 3 ? segments[2] : "color";
        if (!planner.TryGetFrameBufferDescriptor(frameBufferName, out FrameBufferResourceDescriptor? descriptor)
            || descriptor is null)
        {
            Debug.VulkanWarningEvery(
                $"VulkanResourcePlanner.MissingFBO.{pass.PassIndex}.{frameBufferName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Pass '{0}' references missing declared framebuffer '{1}'.",
                pass.Name,
                frameBufferName);
            return;
        }

        foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
        {
            if (!MatchesVulkanFrameBufferSlot(attachment.Attachment, slot))
                continue;

            if (!planner.TryGetTextureDescriptor(attachment.ResourceName, out _))
            {
                if (IsVulkanPlannerOptionalResource(attachment.ResourceName))
                    return;

                Debug.VulkanWarningEvery(
                    $"VulkanResourcePlanner.MissingFBOAttachment.{pass.PassIndex}.{frameBufferName}.{attachment.ResourceName}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' references attachment '{2}' that is missing from declared textures.",
                    pass.Name,
                    frameBufferName,
                    attachment.ResourceName);
            }
            return;
        }

        Debug.VulkanWarningEvery(
            $"VulkanResourcePlanner.MissingFBOSlot.{pass.PassIndex}.{frameBufferName}.{slot}",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' has no attachment matching slot '{2}' for usage {3}.",
            pass.Name,
            frameBufferName,
            slot,
            usage.ResourceType);
    }

    private static bool IsVulkanExternalOutputResourceBinding(string resourceName)
    {
        if (resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = resourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && IsVulkanExternalOutputName(segments[1]);
    }

    private static bool IsVulkanExternalOutputName(string resourceName)
        => resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase);

    private static bool IsVulkanPlannerOptionalResource(string resourceName)
        => VulkanPlannerOptionalResourceNames.Contains(resourceName);

    private static bool MatchesVulkanFrameBufferSlot(EFrameBufferAttachment attachment, string slot)
    {
        if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase))
        {
            if (slot.Length > 5 && int.TryParse(slot.AsSpan(5), out int colorIndex))
            {
                EFrameBufferAttachment expected = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + colorIndex);
                return attachment == expected;
            }

            return attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment31;
        }

        if (slot.Equals("depth", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        if (slot.Equals("stencil", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        return false;
    }

    private static ulong ComputeResourceAllocationSignature(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
    {
        ResourceAllocationSignatureBreakdown breakdown = ComputeResourceAllocationSignatureBreakdown(
            context,
            planner,
            passMetadata,
            extentContext,
            supportsTransformFeedback);
        HashCode hash = new();
        hash.Add(breakdown.AllocationDescriptors);
        hash.Add(breakdown.DisplayWidth);
        hash.Add(breakdown.DisplayHeight);
        hash.Add(breakdown.InternalWidth);
        hash.Add(breakdown.InternalHeight);
        hash.Add(breakdown.PhysicalUsage);
        hash.Add(breakdown.SupportsTransformFeedback);
        return unchecked((ulong)hash.ToHashCode());
    }

    private static ResourceAllocationSignatureBreakdown ComputeResourceAllocationSignatureBreakdown(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
        => new(
            ComputePhysicalResourceDescriptorSignature(context.ResourceRegistry),
            extentContext.WindowWidth,
            extentContext.WindowHeight,
            extentContext.InternalWidth,
            extentContext.InternalHeight,
            VulkanResourceAllocator.ComputePhysicalPlanUsageSignature(planner, passMetadata),
            supportsTransformFeedback);

    private static ulong ComputeResourcePlannerSignature(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        HashCode hash = new();
        hash.Add(ComputeResourceRegistrySignature(context.ResourceRegistry));

        hash.Add(context.DisplayWidth);
        hash.Add(context.DisplayHeight);
        hash.Add(context.InternalWidth);
        hash.Add(context.InternalHeight);

        hash.Add(ComputePassMetadataSignature(passMetadata));

        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        hash.Add(queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

        return unchecked((ulong)hash.ToHashCode());
    }

    private static ResourcePlannerSignatureBreakdown ComputeResourcePlannerSignatureBreakdown(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        => new(
            ComputeResourceRegistrySignature(context.ResourceRegistry),
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            ComputePassMetadataSignature(passMetadata),
            ComputeCompiledGraphBatchSignature(compiledGraph),
            ComputeCompiledGraphEdgeSignature(compiledGraph),
            queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

    private static int ComputeCompiledGraphBatchSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        return hash.ToHashCode();
    }

    private static int ComputeCompiledGraphEdgeSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        return hash.ToHashCode();
    }

    private static int ComputeResourceRegistrySignature(RenderResourceRegistry? registry)
        => registry?.DescriptorSignature ?? 0;

    private static int ComputePhysicalResourceDescriptorSignature(RenderResourceRegistry? registry)
    {
        if (registry is null)
            return 0;

        HashCode hash = new();

        foreach (KeyValuePair<string, RenderTextureResource> pair in registry.TextureRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            TextureResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add((int)descriptor.SizePolicy.SizeClass);
            hash.Add(descriptor.SizePolicy.ScaleX);
            hash.Add(descriptor.SizePolicy.ScaleY);
            hash.Add(descriptor.SizePolicy.Width);
            hash.Add(descriptor.SizePolicy.Height);
            hash.Add(descriptor.FormatLabel, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.ArrayLayers);
            hash.Add(descriptor.StereoCompatible);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.RequiresStorageUsage);
            hash.Add((int)descriptor.Kind);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.InternalFormat.HasValue ? (int)descriptor.InternalFormat.Value : -1);
            hash.Add(descriptor.PixelFormat.HasValue ? (int)descriptor.PixelFormat.Value : -1);
            hash.Add(descriptor.PixelType.HasValue ? (int)descriptor.PixelType.Value : -1);
            hash.Add(descriptor.SizedInternalFormat.HasValue ? (int)descriptor.SizedInternalFormat.Value : -1);
            hash.Add(descriptor.Samples);
            hash.Add(descriptor.MipPolicy.BaseMipLevel);
            hash.Add(descriptor.MipPolicy.MipLevelCount);
            hash.Add(descriptor.MipPolicy.AutoGenerateMipmaps);
            hash.Add(descriptor.MipPolicy.RequireImmutableStorage);
            hash.Add(descriptor.SourceTextureName, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.BaseMipLevel);
            hash.Add(descriptor.MipLevelCount);
            hash.Add(descriptor.BaseLayer);
            hash.Add(descriptor.LayerCount);
            hash.Add((int)descriptor.DepthStencilAspect);
            hash.Add(descriptor.ArrayTarget);
            hash.Add(descriptor.Multisample);
        }

        foreach (KeyValuePair<string, RenderBufferResource> pair in registry.BufferRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            BufferResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add(descriptor.SizeInBytes);
            hash.Add((int)descriptor.Target);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.ElementStride);
            hash.Add(descriptor.ElementCount);
            hash.Add((int)descriptor.AccessPattern);
        }

        return hash.ToHashCode();
    }

    private static int ComputePassMetadataSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);

        foreach (RenderPassMetadata pass in passMetadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add(pass.DeclarationOrder);
            hash.Add((int)pass.Stage);
            hash.Add(pass.Name, StringComparer.Ordinal);
            hash.Add(pass.Revision);

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                hash.Add(usage.ResourceName, StringComparer.Ordinal);
                hash.Add((int)usage.ResourceType);
                hash.Add((int)usage.Access);
                hash.Add((int)usage.LoadOp);
                hash.Add((int)usage.StoreOp);
                AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
            }

            foreach (int dependency in pass.ExplicitDependencies)
                hash.Add(dependency);

            foreach (string schema in pass.DescriptorSchemas)
                hash.Add(schema, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static int ComputePassMetadataRevisionStamp(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);
        foreach (RenderPassMetadata pass in passMetadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add(pass.DeclarationOrder);
            hash.Add(pass.Revision);
        }

        return hash.ToHashCode();
    }

    private int ComputeResourcePlanningSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        HashCode hash = new();
        hash.Add(fallbackExtent.Width);
        hash.Add(fallbackExtent.Height);

        foreach (VulkanAllocationRequest request in ResourcePlanner.CurrentPlan.AllTextures())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        foreach (VulkanBufferAllocationRequest request in ResourcePlanner.CurrentPlan.AllBuffers())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        if (passMetadata is not null)
        {
            hash.Add(passMetadata.Count);
            foreach (RenderPassMetadata pass in passMetadata.OrderBy(static p => p.PassIndex))
            {
                hash.Add(pass.PassIndex);
                hash.Add(pass.DeclarationOrder);
                hash.Add((int)pass.Stage);
                hash.Add(pass.Name, StringComparer.Ordinal);

                foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
                {
                    hash.Add(usage.ResourceName, StringComparer.Ordinal);
                    hash.Add((int)usage.ResourceType);
                    hash.Add((int)usage.Access);
                    hash.Add((int)usage.LoadOp);
                    hash.Add((int)usage.StoreOp);
                    AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
                }
            }
        }

        return hash.ToHashCode();
    }

    private static void AddSubresourceRangeToHash(ref HashCode hash, RenderGraphSubresourceRange range)
    {
        hash.Add(range.BaseMipLevel);
        hash.Add(range.MipLevelCount);
        hash.Add(range.BaseArrayLayer);
        hash.Add(range.ArrayLayerCount);
    }
    internal int EnsureValidPassIndex(
        int passIndex,
        string opName,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
    {
        passMetadata ??= RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.PassMetadata;

        if (passIndex == VulkanBarrierPlanner.SwapchainPassIndex)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.InvalidPass.{opName}.SwapchainPseudoPass",
                TimeSpan.FromSeconds(1),
                "[Vulkan] '{0}' attempted to use the reserved swapchain pseudo-pass as a render-graph pass. Treating it as unresolved.",
                opName);
            passIndex = int.MinValue;
        }

        // Short-circuit: well-known EDefaultRenderPass values are always valid.
        // Metadata may lag behind runtime enqueues (conditional pipeline paths,
        // hot-reload) — accept standard passes without warning.
        if (passIndex != int.MinValue && Enum.IsDefined(typeof(EDefaultRenderPass), passIndex))
            return passIndex;

        bool hasMetadata = passMetadata is { Count: > 0 };
        bool passDefinedInMetadata = hasMetadata && passMetadata!.Any(m => m.PassIndex == passIndex);

        if (passIndex != int.MinValue && (!hasMetadata || passDefinedInMetadata))
            return passIndex;

        int currentPassIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex != int.MinValue && passIndex == currentPassIndex)
            return passIndex;

        if (passIndex == int.MinValue)
        {
            bool currentPassDefined = currentPassIndex != int.MinValue &&
                (!hasMetadata || passMetadata!.Any(m => m.PassIndex == currentPassIndex));

            if (currentPassDefined)
                return currentPassIndex;

            if (hasMetadata && !opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            {
                const int preRenderPass = (int)EDefaultRenderPass.PreRender;
                if (passMetadata!.Any(m => m.PassIndex == preRenderPass))
                    return preRenderPass;
            }
        }

        int fallback = ResolveFallbackPassIndex(opName, passMetadata);

        string reason = passIndex == int.MinValue
            ? "invalid sentinel value"
            : $"pass {passIndex} is missing from metadata";

        int? firstKnownBarrierPass = BarrierPlanner.GetFirstKnownPassIndex();

        Debug.VulkanWarningEvery(
            $"Vulkan.InvalidPass.{opName}.{passIndex}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] '{0}' emitted with invalid render-graph pass index ({1}). Falling back to pass {2}. " +
            "MetadataCount={3} BarrierPlannerFirstPass={4} CurrentPipeline={5}",
            opName,
            reason,
            fallback,
            passMetadata?.Count ?? -1,
            firstKnownBarrierPass?.ToString() ?? "none",
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetType().Name ?? "null");

        return fallback;
    }

    private static int ResolveFallbackPassIndex(string opName, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return int.MinValue;

        ERenderGraphPassStage? preferredStage = ResolvePreferredFallbackStage(opName, passMetadata);
        if (preferredStage.HasValue)
        {
            RenderPassMetadata? preferredPass = passMetadata
                .Where(m => m.Stage == preferredStage.Value)
                .OrderBy(m => m.PassIndex)
                .FirstOrDefault();

            if (preferredPass is not null)
                return preferredPass.PassIndex;
        }

        return passMetadata.OrderBy(m => m.PassIndex).First().PassIndex;
    }

    private static ERenderGraphPassStage? ResolvePreferredFallbackStage(string opName, IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        if (opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return ERenderGraphPassStage.Compute;

        if (opName.Contains("Blit", StringComparison.OrdinalIgnoreCase))
        {
            bool hasTransferPass = passMetadata.Any(m => m.Stage == ERenderGraphPassStage.Transfer);
            return hasTransferPass ? ERenderGraphPassStage.Transfer : ERenderGraphPassStage.Graphics;
        }

        return ERenderGraphPassStage.Graphics;
    }

}
