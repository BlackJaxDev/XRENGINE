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
    private const int MaxMergedFrameOpRegistryCacheEntries = 8;
    private const int MaxActivePassMetadataFilterCacheEntries = 32;

    private VulkanStateTracker _state => _commandRuntime.StateTracker;
    private VulkanFrameOperationQueue _frameOperationQueue => _framePlanner.Operations;

    private VulkanCommandThreadContext<
        VulkanStateTracker,
        ResourcePlannerRuntimeState,
        FrameOpResourcePlannerSwitchingState,
        XRFrameBuffer,
        EReadBufferMode> CommandThreadContext
        => _commandRuntime.GetThreadWorkspace<
            VulkanStateTracker,
            ResourcePlannerRuntimeState,
            FrameOpResourcePlannerSwitchingState,
            XRFrameBuffer,
            EReadBufferMode>().Current;

    internal VulkanFrameOpWorkspace GetCommandThreadFrameOpWorkspace()
        => CommandThreadContext.FrameOpWorkspace ??= new VulkanFrameOpWorkspace();

    internal T GetOrCreateCommandThreadBindingCaptureWorkspace<T>(Func<T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (ReferenceEquals(CommandThreadContext.BindingCaptureWorkspaceOwner, this) &&
            CommandThreadContext.BindingCaptureWorkspace is T workspace)
        {
            return workspace;
        }

        T created = factory();
        CommandThreadContext.BindingCaptureWorkspaceOwner = this;
        CommandThreadContext.BindingCaptureWorkspace = created;
        return created;
    }

    private void ReleaseCurrentThreadStateTrackingCaches()
    {
        if (ReferenceEquals(CommandThreadContext.RenderStateOwner, this))
        {
            CommandThreadContext.RenderStateOwner = null;
            CommandThreadContext.RenderState = null;
        }

        if (ReferenceEquals(CommandThreadContext.ResourcePlannerRuntimeStateOwner, this))
        {
            CommandThreadContext.ResourcePlannerRuntimeStateOwner = null;
            CommandThreadContext.ResourcePlannerRuntimeState = null;
        }

        if (ReferenceEquals(CommandThreadContext.FrameOpResourcePlannerSwitchingStateOwner, this))
        {
            CommandThreadContext.FrameOpResourcePlannerSwitchingStateOwner = null;
            CommandThreadContext.FrameOpResourcePlannerSwitchingState = null;
        }

        if (ReferenceEquals(CommandThreadContext.BindingCaptureWorkspaceOwner, this))
        {
            CommandThreadContext.BindingCaptureWorkspaceOwner = null;
            CommandThreadContext.BindingCaptureWorkspace = null;
        }

        CommandThreadContext.FrameOpWorkspace?.Reset();
        CommandThreadContext.FrameOpWorkspace = null;

        if (!ReferenceEquals(CommandThreadContext.FramebufferBindingOwner, this))
            return;

        CommandThreadContext.FramebufferBindingOwner = null;
        CommandThreadContext.BoundDrawFrameBuffer = null;
        CommandThreadContext.BoundReadFrameBuffer = null;
        CommandThreadContext.ReadBufferMode = default;
    }
    private ResourcePlannerRuntimeState PublishedResourcePlannerRuntimeState
        => _framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
            .State;
    private VulkanResourcePlanner _resourcePlanner => PublishedResourcePlannerRuntimeState.ResourcePlanner;
    private VulkanResourceAllocator _resourceAllocator => PublishedResourcePlannerRuntimeState.ResourceAllocator;
    private VulkanBarrierPlanner _barrierPlanner => PublishedResourcePlannerRuntimeState.BarrierPlanner;
    private VulkanCompiledRenderGraph _compiledRenderGraph => PublishedResourcePlannerRuntimeState.CompiledRenderGraph;
    private ulong _resourcePlannerSignature => PublishedResourcePlannerRuntimeState.ResourcePlannerSignature;
    private ulong _resourceAllocationSignature => PublishedResourcePlannerRuntimeState.ResourceAllocationSignature;
    private ResourcePlannerFastPathKey _resourcePlannerFastPathKey => PublishedResourcePlannerRuntimeState.ResourcePlannerFastPathKey;
    private bool _hasResourcePlannerFastPathKey => PublishedResourcePlannerRuntimeState.HasResourcePlannerFastPathKey;
    private BarrierPlanFastPathKey _barrierPlanFastPathKey => PublishedResourcePlannerRuntimeState.BarrierPlanFastPathKey;
    private bool _hasBarrierPlanFastPathKey => PublishedResourcePlannerRuntimeState.HasBarrierPlanFastPathKey;
    private ResourcePlannerSignatureBreakdown _resourcePlannerSignatureBreakdown => PublishedResourcePlannerRuntimeState.ResourcePlannerSignatureBreakdown;
    private ulong _resourcePlannerRevision => PublishedResourcePlannerRuntimeState.ResourcePlannerRevision;
    private VulkanFramePlannerMutableState<
        VulkanFrameOpPlannerStateKey,
        FrameOpResourcePlannerSwitchingState,
        QueueOwnershipConfigCacheEntry,
        MergedFrameOpRegistryCacheEntry,
        FrameOpRegistryCacheSource,
        ActivePassMetadataFilterCacheEntry> PlannerMutableState
        => _framePlanner.MutableState;
    private FrameOpResourcePlannerSwitchingState _frameOpResourcePlannerSwitchingState => PlannerMutableState.DefaultSwitchingState;
    private VulkanStateTracker ActiveState =>
        ReferenceEquals(CommandThreadContext.RenderStateOwner, this) &&
        CommandThreadContext.RenderState is not null
            ? CommandThreadContext.RenderState
            : _state;
    private bool HasThreadResourcePlannerRuntimeState =>
        ReferenceEquals(CommandThreadContext.ResourcePlannerRuntimeStateOwner, this) &&
        CommandThreadContext.ResourcePlannerRuntimeState.HasValue;
    private FrameOpResourcePlannerSwitchingState ActiveFrameOpResourcePlannerSwitchingState =>
        ReferenceEquals(CommandThreadContext.FrameOpResourcePlannerSwitchingStateOwner, this) &&
        CommandThreadContext.FrameOpResourcePlannerSwitchingState is not null
            ? CommandThreadContext.FrameOpResourcePlannerSwitchingState
            : PublishedResourcePlannerRuntimeState.FrameOpResourcePlannerSwitchingState ??
              _frameOpResourcePlannerSwitchingState;

    private void ThrowIfPersistentResourceAllocationDuringRecording(string operation)
    {
        if (!ActiveFrameOpResourcePlannerSwitchingState.RecordingScopeActive)
            return;

        throw new InvalidOperationException(
            $"Persistent Vulkan resource allocation '{operation}' is forbidden while command recording is active. " +
            "Allocate persistent resources during planning or upload preparation.");
    }
    private bool HasThreadFramebufferBindingState
        => ReferenceEquals(CommandThreadContext.FramebufferBindingOwner, this);
    private XRFrameBuffer? ActiveBoundDrawFrameBuffer
    {
        get => HasThreadFramebufferBindingState
            ? CommandThreadContext.BoundDrawFrameBuffer
            : _boundDrawFrameBuffer;
        set
        {
            if (HasThreadFramebufferBindingState)
            {
                CommandThreadContext.BoundDrawFrameBuffer = value;
                return;
            }

            _boundDrawFrameBuffer = value;
        }
    }
    private XRFrameBuffer? ActiveBoundReadFrameBuffer
    {
        get => HasThreadFramebufferBindingState
            ? CommandThreadContext.BoundReadFrameBuffer
            : _boundReadFrameBuffer;
        set
        {
            if (HasThreadFramebufferBindingState)
            {
                CommandThreadContext.BoundReadFrameBuffer = value;
                return;
            }

            _boundReadFrameBuffer = value;
        }
    }
    private EReadBufferMode ActiveReadBufferMode
    {
        get => HasThreadFramebufferBindingState
            ? CommandThreadContext.ReadBufferMode
            : _readBufferMode;
        set
        {
            if (HasThreadFramebufferBindingState)
            {
                CommandThreadContext.ReadBufferMode = value;
                return;
            }

            _readBufferMode = value;
        }
    }
    internal VulkanResourcePlanner ResourcePlanner =>
        HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.ResourcePlanner
            : _resourcePlanner;
    internal VulkanResourcePlan ResourcePlan => ResourcePlanner.CurrentPlan;
    internal VulkanResourceAllocator ResourceAllocator =>
        HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.ResourceAllocator
            : _resourceAllocator;
    internal int ResourceAllocatorIdentity => RuntimeHelpers.GetHashCode(ResourceAllocator);
    internal VulkanBarrierPlanner BarrierPlanner =>
        HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.BarrierPlanner
            : _barrierPlanner;
    internal VulkanCompiledRenderGraph CompiledRenderGraph =>
        HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.CompiledRenderGraph
            : _compiledRenderGraph;
    internal ulong ResourcePlannerRevision =>
        HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.ResourcePlannerRevision
            : _resourcePlannerRevision;
    private VulkanResourcePlanner ActiveResourcePlanner
    {
        get => ResourcePlanner;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.ResourcePlanner = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.ResourcePlanner = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private VulkanResourceAllocator ActiveResourceAllocator
    {
        get => ResourceAllocator;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.ResourceAllocator = value;
                threadState.AllocatorOwnershipId = value.OwnershipId;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.ResourceAllocator = value;
            publishedState.AllocatorOwnershipId = value.OwnershipId;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private VulkanBarrierPlanner ActiveBarrierPlanner
    {
        get => BarrierPlanner;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.BarrierPlanner = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.BarrierPlanner = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private VulkanCompiledRenderGraph ActiveCompiledRenderGraph
    {
        get => CompiledRenderGraph;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.CompiledRenderGraph = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.CompiledRenderGraph = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    internal FrameOpContext? ActiveLastActiveFrameOpContext
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.LastActiveFrameOpContext
            : PublishedResourcePlannerRuntimeState.LastActiveFrameOpContext;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.LastActiveFrameOpContext = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.LastActiveFrameOpContext = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private ulong ActiveResourcePlannerSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.ResourcePlannerSignature
            : _resourcePlannerSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.ResourcePlannerSignature = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.ResourcePlannerSignature = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private ulong ActiveResourceAllocationSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.ResourceAllocationSignature
            : _resourceAllocationSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.ResourceAllocationSignature = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.ResourceAllocationSignature = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private ulong ActiveFailedResourcePlannerSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.FailedResourcePlannerSignature
            : PublishedResourcePlannerRuntimeState.FailedResourcePlannerSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.FailedResourcePlannerSignature = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.FailedResourcePlannerSignature = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private ulong ActiveFailedResourceAllocationSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.FailedResourceAllocationSignature
            : PublishedResourcePlannerRuntimeState.FailedResourceAllocationSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.FailedResourceAllocationSignature = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.FailedResourceAllocationSignature = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private long ActiveFailedResourceAllocationTimestamp
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.FailedResourceAllocationTimestamp
            : PublishedResourcePlannerRuntimeState.FailedResourceAllocationTimestamp;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.FailedResourceAllocationTimestamp = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.FailedResourceAllocationTimestamp = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private ResourcePlannerFastPathKey ActiveResourcePlannerFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.ResourcePlannerFastPathKey
            : _resourcePlannerFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.ResourcePlannerFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.ResourcePlannerFastPathKey = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private bool ActiveHasResourcePlannerFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.HasResourcePlannerFastPathKey
            : _hasResourcePlannerFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.HasResourcePlannerFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.HasResourcePlannerFastPathKey = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private BarrierPlanFastPathKey ActiveBarrierPlanFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.BarrierPlanFastPathKey
            : _barrierPlanFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.BarrierPlanFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.BarrierPlanFastPathKey = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private bool ActiveHasBarrierPlanFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.HasBarrierPlanFastPathKey
            : _hasBarrierPlanFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.HasBarrierPlanFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.HasBarrierPlanFastPathKey = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private ResourcePlannerSignatureBreakdown ActiveResourcePlannerSignatureBreakdown
    {
        get => HasThreadResourcePlannerRuntimeState
            ? CommandThreadContext.ResourcePlannerRuntimeState!.Value.ResourcePlannerSignatureBreakdown
            : _resourcePlannerSignatureBreakdown;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.ResourcePlannerSignatureBreakdown = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.ResourcePlannerSignatureBreakdown = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private ulong ActiveResourcePlannerRevision
    {
        get => ResourcePlannerRevision;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState threadState))
            {
                threadState.ResourcePlannerRevision = value;
                StoreThreadResourcePlannerRuntimeState(in threadState);
                return;
            }

            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            publishedState.ResourcePlannerRevision = value;
            PublishResourcePlannerRuntimeState(publishedState, commitReusedImageMetadata: false);
        }
    }
    private bool IsCommandChainResourcePlanFrozen => _framePlanner.IsResourcePlanFrozen;
    private ref bool[]? _commandBufferDirtyFlags => ref _commandRuntime.CommandBuffers.DirtyFlags;
    private object _commandBufferDirtyReasonLock => _commandRuntime.CommandBuffers.DirtyReasonGate;
    private Dictionary<string, int> _commandBufferDirtyReasons => _commandRuntime.CommandBuffers.DirtyReasons;
    private ref long _lastCommandBufferDirtyReasonLogTimestamp => ref _commandRuntime.CommandBuffers.LastDirtyReasonLogTimestamp;
    private ref XRFrameBuffer? _boundDrawFrameBuffer => ref _commandRuntime.CommandBuffers.BoundDrawFrameBuffer;
    private ref XRFrameBuffer? _boundReadFrameBuffer => ref _commandRuntime.CommandBuffers.BoundReadFrameBuffer;
    private ref XRTexture? _lastWindowPresentColorTexture => ref OutputRuntime.PresentationSource.ColorTexture;
    private ref XRFrameBuffer? _lastWindowPresentFrameBuffer => ref OutputRuntime.PresentationSource.FrameBuffer;
    private ref XRTexture? _lastWindowPresentFallbackFrameBufferTexture => ref OutputRuntime.PresentationSource.FallbackFrameBufferTexture;
    private ref XRFrameBuffer? _lastWindowPresentFallbackFrameBuffer => ref OutputRuntime.PresentationSource.FallbackFrameBuffer;
    private ref FrameOpContext? _lastWindowPresentFrameOpContext => ref OutputRuntime.PresentationSource.FrameOpContext;
    private VulkanPresentationSourcePublication _windowPresentSource => OutputRuntime.PresentationSource.Publication;
    private ref VulkanPhysicalImageGroup? _retainedAutoExposureHistoryGroup => ref ResourceRuntime.RetainedAutoExposureHistoryGroup;
    private ref ulong _lastResourcePlanReplacementRevision => ref _framePlanner.LastResourcePlanReplacementRevision;
    private ref ulong _lastResourcePlanReplacementSignature => ref _framePlanner.LastResourcePlanReplacementSignature;
    private ref ulong _lastResourcePlanReplacementAllocationSignature => ref _framePlanner.LastResourcePlanReplacementAllocationSignature;
    private ref int _lastResourcePlanReplacementRetiredImageCount => ref _framePlanner.LastResourcePlanReplacementRetiredImageCount;
    private ref int _lastResourcePlanReplacementRetiredBufferCount => ref _framePlanner.LastResourcePlanReplacementRetiredBufferCount;
    private ref EReadBufferMode _readBufferMode => ref _commandRuntime.CommandBuffers.ReadBufferMode;
    private ref EVulkanQueueOverlapMode _autoQueueOverlapMode => ref _framePlanner.AutoQueueOverlapMode;
    private ref EVulkanQueueOverlapMode _lastResolvedQueueOverlapMode => ref _framePlanner.LastResolvedQueueOverlapMode;
    private ref int _queueOverlapPromotionStabilityFrames => ref _framePlanner.QueueOverlapPromotionStabilityFrames;
    private ref int _queueOverlapFramesInMode => ref _framePlanner.QueueOverlapFramesInMode;
    private ref long _lastQueueOverlapSampleTimestamp => ref _framePlanner.LastQueueOverlapSampleTimestamp;
    private ref ulong _lastQueueOverlapSampleFrameId => ref _framePlanner.LastQueueOverlapSampleFrameId;
    private ref ulong _lastQueueOverlapPolicyFrameId => ref _framePlanner.LastQueueOverlapPolicyFrameId;
    private ref double _queueOverlapFrameDeltaEmaMs => ref _framePlanner.QueueOverlapFrameDeltaEmaMilliseconds;
    private ref double _queueOverlapModeStartFrameDeltaMs => ref _framePlanner.QueueOverlapModeStartFrameDeltaMilliseconds;
    private ref ulong _queueOwnershipConfigCacheFrameId => ref _framePlanner.QueueOwnershipConfigCacheFrameId;
    private List<QueueOwnershipConfigCacheEntry> _queueOwnershipConfigCache => PlannerMutableState.QueueOwnershipCache;
    private List<MergedFrameOpRegistryCacheEntry> _mergedFrameOpRegistryCache => PlannerMutableState.MergedRegistryCache;
    private List<VulkanFrameOpPlannerStateKey> _frameOpPlannerStateKeyScratch => PlannerMutableState.PlannerStateKeyScratch;
    private List<VulkanFrameOpPlannerStateKey> _frameOpPlannerStateEvictionScratch => PlannerMutableState.PlannerStateEvictionScratch;
    private List<RenderResourceRegistry> _frameOpRegistryScratch => PlannerMutableState.RegistryScratch;
    private List<FrameOpRegistryCacheSource> _frameOpRegistryCacheSourceScratch => PlannerMutableState.RegistryCacheSourceScratch;
    private List<XRFrameBuffer> _frameOpFrameBufferScratch => PlannerMutableState.FrameBufferScratch;
    private List<ActivePassMetadataFilterCacheEntry> _activePassMetadataFilterCache => PlannerMutableState.ActivePassMetadataFilterCache;
    private ref int _activePassMetadataFilterCacheReplacementIndex => ref PlannerMutableState.ActivePassMetadataFilterCacheReplacementIndex;
    private IReadOnlyCollection<RenderPassMetadata>? _lastActiveFilterSourcePassMetadata
    {
        get => PlannerMutableState.LastActiveFilterSourcePassMetadata;
        set => PlannerMutableState.LastActiveFilterSourcePassMetadata = value;
    }
    private IReadOnlyCollection<RenderPassMetadata>? _lastActiveFilterResult
    {
        get => PlannerMutableState.LastActiveFilterResult;
        set => PlannerMutableState.LastActiveFilterResult = value;
    }
    private RenderResourceRegistry? _lastActiveFilterResourceRegistry
    {
        get => PlannerMutableState.LastActiveFilterResourceRegistry;
        set => PlannerMutableState.LastActiveFilterResourceRegistry = value;
    }
    private ref int _lastActiveFilterResourceRegistryRevision => ref PlannerMutableState.LastActiveFilterResourceRegistryRevision;
    private ref int _lastActiveFilterPassSetSignature => ref PlannerMutableState.LastActiveFilterPassSetSignature;
    private ref int _lastActiveFilterResourceSetSignature => ref PlannerMutableState.LastActiveFilterResourceSetSignature;
    private ref bool _lastActiveFilterConstrainToActivePassSet => ref PlannerMutableState.LastActiveFilterConstrainToActivePassSet;
    private static readonly TimeSpan ResourceAllocationFailureRetryDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan OpenXrResourceAllocationFailureRetryDelay = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> VulkanPlannerOptionalResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LightProbeIrradianceArray",
        "LightProbePrefilterArray",
        "LightProbePositions",
        "LightProbeTetrahedra",
        "LightProbeParameters",
        "LightProbeGridCells",
        "LightProbeGridIndices",
        "AtmosphereColor",
        "VolumetricFogColor"
    };

    private readonly record struct QueueOverlapMetrics(
        int ComputePassCount,
        int TransferUsageCount,
        int OverlapCandidatePassCount,
        int TransferCost,
        int QueueOwnershipTransfers,
        int BarrierStageFlushes,
        TimeSpan FrameDelta);

    internal readonly record struct FrameOpRegistryCacheSource(
        RenderResourceRegistry Registry,
        int DescriptorSignature);

    internal readonly record struct ActivePassMetadataFilterCacheEntry(
        IReadOnlyCollection<RenderPassMetadata> SourcePassMetadata,
        RenderResourceRegistry? ResourceRegistry,
        int ResourceRegistryRevision,
        int ActivePassSetSignature,
        int ActiveResourceSetSignature,
        bool ConstrainToActivePassSet,
        IReadOnlyCollection<RenderPassMetadata> Result)
    {
        public bool Matches(
            IReadOnlyCollection<RenderPassMetadata> sourcePassMetadata,
            RenderResourceRegistry? resourceRegistry,
            int resourceRegistryRevision,
            int activePassSetSignature,
            int activeResourceSetSignature,
            bool constrainToActivePassSet)
            => ReferenceEquals(SourcePassMetadata, sourcePassMetadata)
                && ReferenceEquals(ResourceRegistry, resourceRegistry)
                && ResourceRegistryRevision == resourceRegistryRevision
                && ActivePassSetSignature == activePassSetSignature
                && ActiveResourceSetSignature == activeResourceSetSignature
                && ConstrainToActivePassSet == constrainToActivePassSet;
    }

    internal sealed class MergedFrameOpRegistryCacheEntry(
        VulkanFrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        int frameBufferDescriptorSignature,
        ulong frameOpsSignature,
        RenderResourceRegistry mergedRegistry,
        ulong lastUsedFrameId)
    {
        public VulkanFrameOpPlannerStateKey OwnerKey { get; } = ownerKey;
        public RenderResourceRegistry? PrimaryRegistry { get; } = primaryRegistry;
        public int PrimaryDescriptorSignature { get; set; } = primaryRegistry?.DescriptorSignature ?? 0;
        public FrameOpRegistryCacheSource[] Sources { get; set; } = sources;
        public int FrameBufferDescriptorSignature { get; set; } = frameBufferDescriptorSignature;
        public ulong FrameOpsSignature { get; set; } = frameOpsSignature;
        public RenderResourceRegistry MergedRegistry { get; set; } = mergedRegistry;
        public ulong LastUsedFrameId { get; set; } = lastUsedFrameId;
    }

    internal sealed class FrameOpResourcePlannerSwitchingState
    {
        public Dictionary<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> States { get; } =
            new(VulkanFrameOpPlannerStateKeyComparer.Instance);
        public Dictionary<VulkanFrameOpPlannerStateKey, ulong> LastUsedSerials { get; } =
            new(VulkanFrameOpPlannerStateKeyComparer.Instance);
        public HashSet<VulkanFrameOpPlannerStateKey> ActiveKeys { get; } =
            new(VulkanFrameOpPlannerStateKeyComparer.Instance);
        public ulong UsageSerial;
        public bool SwitchingActive;
        public bool MergedPlanActive;
        public bool RecordingScopeActive;
        public bool HasActiveKey;
        public VulkanFrameOpPlannerStateKey ActiveKey;
        public bool HasActiveContext;
        public FrameOpContext ActiveContext;
        public ResourcePlannerRuntimeState PreparationState;
        public bool HasPreparationState;
        public ulong PreparedFrameOpsSignature;
        public ulong PreparedPlanRevision;
        public bool HasPreparedPlan;
    }

    private Dictionary<VulkanFrameOpPlannerStateKey, FrameOp[]> _frameOpPlannerPartitionCache => PlannerMutableState.PartitionCache;
    private VulkanFrameOpPlannerStateKey[] _frameOpPlannerPartitionKeyBuffer => PlannerMutableState.PartitionKeyBuffer;
    private ref ulong _frameOpPlannerPartitionSignature => ref PlannerMutableState.PartitionSignature;

    private readonly struct ThreadRenderStateScope : IDisposable
    {
        private readonly VulkanCommandThreadContext<VulkanStateTracker, ResourcePlannerRuntimeState, FrameOpResourcePlannerSwitchingState, XRFrameBuffer, EReadBufferMode> _threadContext;
        private readonly VulkanRenderer? _previousOwner;
        private readonly VulkanStateTracker? _previousState;
        private readonly VulkanRenderer? _previousFramebufferBindingOwner;
        private readonly XRFrameBuffer? _previousThreadBoundDrawFrameBuffer;
        private readonly XRFrameBuffer? _previousThreadBoundReadFrameBuffer;
        private readonly EReadBufferMode _previousThreadReadBufferMode;
        private readonly IDisposable _currentRendererScope;

        public ThreadRenderStateScope(VulkanRenderer renderer, VulkanStateTracker state)
        {
            _threadContext = renderer.CommandThreadContext;
            _previousOwner = _threadContext.RenderStateOwner as VulkanRenderer;
            _previousState = _threadContext.RenderState;
            _previousFramebufferBindingOwner = _threadContext.FramebufferBindingOwner as VulkanRenderer;
            _previousThreadBoundDrawFrameBuffer = _threadContext.BoundDrawFrameBuffer;
            _previousThreadBoundReadFrameBuffer = _threadContext.BoundReadFrameBuffer;
            _previousThreadReadBufferMode = _threadContext.ReadBufferMode;
            _threadContext.RenderStateOwner = renderer;
            _threadContext.RenderState = state;
            _threadContext.FramebufferBindingOwner = renderer;
            _threadContext.BoundDrawFrameBuffer = null;
            _threadContext.BoundReadFrameBuffer = null;
            _threadContext.ReadBufferMode = EReadBufferMode.ColorAttachment0;
            _currentRendererScope = AbstractRenderer.PushThreadCurrent(renderer);
        }

        public void Dispose()
        {
            _currentRendererScope.Dispose();
            _threadContext.RenderStateOwner = _previousOwner;
            _threadContext.RenderState = _previousState;
            _threadContext.FramebufferBindingOwner = _previousFramebufferBindingOwner;
            _threadContext.BoundDrawFrameBuffer = _previousThreadBoundDrawFrameBuffer;
            _threadContext.BoundReadFrameBuffer = _previousThreadBoundReadFrameBuffer;
            _threadContext.ReadBufferMode = _previousThreadReadBufferMode;
        }
    }

    private ThreadRenderStateScope EnterThreadRenderStateScope(VulkanStateTracker state)
        => new(this, state);

    private bool TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state)
    {
        if (HasThreadResourcePlannerRuntimeState)
        {
            state = CommandThreadContext.ResourcePlannerRuntimeState!.Value;
            return true;
        }

        state = default;
        return false;
    }

    private void StoreThreadResourcePlannerRuntimeState(in ResourcePlannerRuntimeState state)
    {
        if (CommandThreadContext.PreparedCommandChainEncodingActive)
        {
            throw new InvalidOperationException(
                "Prepared Vulkan command-chain encoding cannot publish resource-planner state.");
        }

        ResourcePlannerRuntimeState next = state;
        next.FrameOpResourcePlannerSwitchingState =
            CommandThreadContext.FrameOpResourcePlannerSwitchingState ??
            next.FrameOpResourcePlannerSwitchingState;
        CommandThreadContext.ResourcePlannerRuntimeState = next;
    }

    internal readonly struct ThreadResourcePlannerRuntimeStateScope : IDisposable
    {
        private readonly VulkanCommandThreadContext<VulkanStateTracker, ResourcePlannerRuntimeState, FrameOpResourcePlannerSwitchingState, XRFrameBuffer, EReadBufferMode> _threadContext;
        private readonly VulkanRenderer? _previousOwner;
        private readonly ResourcePlannerRuntimeState? _previousState;

        public ThreadResourcePlannerRuntimeStateScope(
            VulkanRenderer renderer,
            in ResourcePlannerRuntimeState state)
        {
            _threadContext = renderer.CommandThreadContext;
            ResourcePlannerRuntimeState scopedState = state;
            scopedState.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();
            _previousOwner = _threadContext.ResourcePlannerRuntimeStateOwner as VulkanRenderer;
            _previousState = _threadContext.ResourcePlannerRuntimeState;
            _threadContext.ResourcePlannerRuntimeStateOwner = renderer;
            _threadContext.ResourcePlannerRuntimeState = scopedState;
        }

        public ResourcePlannerRuntimeState CaptureCurrent(VulkanRenderer renderer)
        {
            if (!ReferenceEquals(_threadContext.ResourcePlannerRuntimeStateOwner, renderer) ||
                !_threadContext.ResourcePlannerRuntimeState.HasValue)
            {
                return renderer.CaptureResourcePlannerRuntimeState();
            }

            ResourcePlannerRuntimeState state = _threadContext.ResourcePlannerRuntimeState.Value;
            state.FrameOpResourcePlannerSwitchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            return state;
        }

        public void Dispose()
        {
            _threadContext.ResourcePlannerRuntimeStateOwner = _previousOwner;
            _threadContext.ResourcePlannerRuntimeState = _previousState;
        }
    }

    private ThreadResourcePlannerRuntimeStateScope EnterThreadResourcePlannerRuntimeStateScope(
        in ResourcePlannerRuntimeState state)
    {
        if (CommandThreadContext.PreparedCommandChainEncodingActive)
        {
            throw new InvalidOperationException(
                "Prepared Vulkan command-chain encoding cannot enter a resource-planner scope.");
        }

        return new(this, state);
    }

    internal readonly struct ThreadFrameOpResourcePlannerSwitchingStateScope : IDisposable
    {
        private readonly VulkanCommandThreadContext<VulkanStateTracker, ResourcePlannerRuntimeState, FrameOpResourcePlannerSwitchingState, XRFrameBuffer, EReadBufferMode> _threadContext;
        private readonly VulkanRenderer? _previousOwner;
        private readonly FrameOpResourcePlannerSwitchingState? _previousState;

        public ThreadFrameOpResourcePlannerSwitchingStateScope(
            VulkanRenderer renderer,
            FrameOpResourcePlannerSwitchingState state)
        {
            _threadContext = renderer.CommandThreadContext;
            _previousOwner = _threadContext.FrameOpResourcePlannerSwitchingStateOwner as VulkanRenderer;
            _previousState = _threadContext.FrameOpResourcePlannerSwitchingState;
            _threadContext.FrameOpResourcePlannerSwitchingStateOwner = renderer;
            _threadContext.FrameOpResourcePlannerSwitchingState = state;
        }

        public FrameOpResourcePlannerSwitchingState CaptureCurrent(VulkanRenderer renderer)
        {
            if (!ReferenceEquals(
                    _threadContext.FrameOpResourcePlannerSwitchingStateOwner,
                    renderer) ||
                _threadContext.FrameOpResourcePlannerSwitchingState is null)
            {
                return renderer.ActiveFrameOpResourcePlannerSwitchingState;
            }

            return _threadContext.FrameOpResourcePlannerSwitchingState;
        }

        public void Dispose()
        {
            _threadContext.FrameOpResourcePlannerSwitchingStateOwner = _previousOwner;
            _threadContext.FrameOpResourcePlannerSwitchingState = _previousState;
        }
    }

    private ThreadFrameOpResourcePlannerSwitchingStateScope EnterThreadFrameOpResourcePlannerSwitchingStateScope(
        FrameOpResourcePlannerSwitchingState state)
        => new(this, state);


    internal readonly record struct ResourcePlannerSignatureBreakdown(
        EVulkanFrameOpContextKind ContextKind,
        ulong ContextId,
        ulong CompatibilityFingerprint,
        int Registry,
        int OutputFrameBuffer,
        int OutputTarget,
        uint DisplayWidth,
        uint DisplayHeight,
        uint InternalWidth,
        uint InternalHeight,
        int PassMetadata,
        int GraphBatches,
        int GraphEdges,
        ulong ResourceGeneration,
        ulong DescriptorGeneration,
        uint SubmissionQueueFamily,
        uint GraphicsQueueFamily,
        uint ComputeQueueFamily,
        uint TransferQueueFamily)
    {
        public override string ToString()
            => $"kind={ContextKind} contextId={ContextId} plan=0x{CompatibilityFingerprint:X16} registry=0x{Registry:X8} outputFbo=0x{OutputFrameBuffer:X8} outputTarget=0x{OutputTarget:X8} dims={DisplayWidth}x{DisplayHeight}/{InternalWidth}x{InternalHeight} " +
               $"passes=0x{PassMetadata:X8} batches=0x{GraphBatches:X8} edges=0x{GraphEdges:X8} resourceGen={ResourceGeneration} descriptorGen={DescriptorGeneration} submitQ={SubmissionQueueFamily} " +
               $"queues=g{GraphicsQueueFamily}/c{ComputeQueueFamily}/t{TransferQueueFamily}";

        public string DescribeDelta(in ResourcePlannerSignatureBreakdown previous)
        {
            StringBuilder builder = new();
            AppendDelta(builder, "context-kind", (int)previous.ContextKind, (int)ContextKind);
            AppendDelta(builder, "plan-fingerprint", previous.CompatibilityFingerprint, CompatibilityFingerprint, hexadecimal: true);
            AppendDelta(builder, "resource-registry", previous.Registry, Registry, hexadecimal: true);
            AppendDelta(builder, "output-fbo", previous.OutputFrameBuffer, OutputFrameBuffer, hexadecimal: true);
            AppendDelta(builder, "output-target", previous.OutputTarget, OutputTarget, hexadecimal: true);
            AppendDelta(builder, "display-width", previous.DisplayWidth, DisplayWidth);
            AppendDelta(builder, "display-height", previous.DisplayHeight, DisplayHeight);
            AppendDelta(builder, "internal-width", previous.InternalWidth, InternalWidth);
            AppendDelta(builder, "internal-height", previous.InternalHeight, InternalHeight);
            AppendDelta(builder, "pass-metadata", previous.PassMetadata, PassMetadata, hexadecimal: true);
            AppendDelta(builder, "graph-batches", previous.GraphBatches, GraphBatches, hexadecimal: true);
            AppendDelta(builder, "graph-edges", previous.GraphEdges, GraphEdges, hexadecimal: true);
            AppendDelta(builder, "resource-generation", previous.ResourceGeneration, ResourceGeneration);
            AppendDelta(builder, "descriptor-generation", previous.DescriptorGeneration, DescriptorGeneration);
            AppendDelta(builder, "submission-queue-family", previous.SubmissionQueueFamily, SubmissionQueueFamily);
            AppendDelta(builder, "graphics-queue-family", previous.GraphicsQueueFamily, GraphicsQueueFamily);
            AppendDelta(builder, "compute-queue-family", previous.ComputeQueueFamily, ComputeQueueFamily);
            AppendDelta(builder, "transfer-queue-family", previous.TransferQueueFamily, TransferQueueFamily);
            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private static void AppendDelta(StringBuilder builder, string name, int oldValue, int newValue, bool hexadecimal = false)
        {
            if (oldValue == newValue)
                return;

            AppendDeltaPrefix(builder);
            if (hexadecimal)
                builder.Append(name).Append("=0x").Append(oldValue.ToString("X8")).Append("->0x").Append(newValue.ToString("X8"));
            else
                builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
        }

        private static void AppendDelta(StringBuilder builder, string name, uint oldValue, uint newValue)
        {
            if (oldValue == newValue)
                return;

            AppendDeltaPrefix(builder);
            builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
        }

        private static void AppendDelta(StringBuilder builder, string name, ulong oldValue, ulong newValue, bool hexadecimal = false)
        {
            if (oldValue == newValue)
                return;

            AppendDeltaPrefix(builder);
            if (hexadecimal)
                builder.Append(name).Append("=0x").Append(oldValue.ToString("X16")).Append("->0x").Append(newValue.ToString("X16"));
            else
                builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
        }

        private static void AppendDeltaPrefix(StringBuilder builder)
        {
            if (builder.Length > 0)
                builder.Append(", ");
        }
    }

    private readonly record struct ResourceAllocationSignatureBreakdown(
        int AllocationDescriptors,
        uint DisplayWidth,
        uint DisplayHeight,
        uint InternalWidth,
        uint InternalHeight,
        int PhysicalUsage,
        bool SupportsTransformFeedback)
    {
        public override string ToString()
            => $"allocDescriptors=0x{AllocationDescriptors:X8} dims={DisplayWidth}x{DisplayHeight}/{InternalWidth}x{InternalHeight} " +
               $"physicalUsage=0x{PhysicalUsage:X8} xfb={SupportsTransformFeedback}";
    }

    internal readonly record struct ResourcePlannerFastPathKey(
        RenderResourceRegistry? Registry,
        int RegistryDescriptorRevision,
        IReadOnlyCollection<RenderPassMetadata>? ActivePassMetadata,
        int ActivePassMetadataRevision,
        int ActivePassSetSignature,
        int ActiveResourceSetSignature,
        int OutputFrameBufferIdentity,
        int OutputTargetIdentity,
        uint DisplayWidth,
        uint DisplayHeight,
        uint InternalWidth,
        uint InternalHeight,
        VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership,
        bool SupportsTransformFeedback)
    {
        public bool Matches(in ResourcePlannerFastPathKey other)
            => ReferenceEquals(Registry, other.Registry)
                && RegistryDescriptorRevision == other.RegistryDescriptorRevision
                && ReferenceEquals(ActivePassMetadata, other.ActivePassMetadata)
                && ActivePassMetadataRevision == other.ActivePassMetadataRevision
                && ActivePassSetSignature == other.ActivePassSetSignature
                && ActiveResourceSetSignature == other.ActiveResourceSetSignature
                && OutputFrameBufferIdentity == other.OutputFrameBufferIdentity
                && OutputTargetIdentity == other.OutputTargetIdentity
                && DisplayWidth == other.DisplayWidth
                && DisplayHeight == other.DisplayHeight
                && InternalWidth == other.InternalWidth
                && InternalHeight == other.InternalHeight
                && QueueOwnership.Equals(other.QueueOwnership)
                && SupportsTransformFeedback == other.SupportsTransformFeedback;
    }

    private readonly record struct ResourcePlanningInputs(
        IReadOnlyCollection<RenderPassMetadata>? ActivePassMetadata,
        VulkanCompiledRenderGraph CompiledGraph,
        VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership,
        ResourcePlannerFastPathKey FastPathKey);

    private readonly struct CommandChainResourcePlanReadScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;

        public CommandChainResourcePlanReadScope(VulkanRenderer renderer, ulong resourcePlanRevision)
        {
            _renderer = renderer;
            _renderer._framePlanner.AddFrozenPlanReader(resourcePlanRevision);
        }

        public void Dispose()
        {
            _renderer._framePlanner.RemoveFrozenPlanReader();
        }
    }

    private readonly struct FrameOpResourcePlannerPreparationScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly FrameOpResourcePlannerSwitchingState? _switchingState;
        private readonly FrameOp[]? _operations;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly VulkanFrameOpPlannerStateKey _initialKey;
        private readonly bool _usesSingleKeyState;
        private readonly bool _active;

        public FrameOpResourcePlannerPreparationScope(VulkanRenderer renderer, FrameOp[] ops)
        {
            _renderer = renderer;
            _switchingState = null;
            _operations = null;
            _previousState = default;
            _initialKey = default;
            _usesSingleKeyState = false;
            _active = false;

            if (!renderer.DeviceContext.IsOperational ||
                !FrameOpResourcePlannerSwitchingEnabled ||
                ops.Length == 0)
                return;

            bool found = false;
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOpContext context = ops[i].Context;
                if (!FrameOpContextHasPlannerResources(context))
                    continue;

                found = true;
                break;
            }

            if (!found)
                return;

            FrameOpResourcePlannerSwitchingState switchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            _switchingState = switchingState;
            _operations = ops;
            _previousState = renderer.CaptureResourcePlannerRuntimeState();
            _active = true;

            bool usesSingleKeyState = TryGetSingleFrameOpPlannerStateKey(ops, out VulkanFrameOpPlannerStateKey initialKey);
            if (!usesSingleKeyState)
            {
                // Incompatible contexts are prepared independently by the frame-plan
                // path. Never restore or publish the historical merged preparation
                // state: it carries the first context's extents and allocator into
                // unrelated outputs.
                return;
            }

            switchingState.MergedPlanActive = false;
            _usesSingleKeyState = usesSingleKeyState;
            _initialKey = initialKey;
            ResourcePlannerRuntimeState keyedState = default;
            bool hasCachedState = usesSingleKeyState &&
                switchingState.States.TryGetValue(initialKey, out keyedState) &&
                IsFrameOpPlannerAllocatorExclusivelyOwnedByKey(switchingState, initialKey, keyedState.ResourceAllocator) &&
                IsReusableFrameOpResourcePlannerState(keyedState);
            if (usesSingleKeyState &&
                !hasCachedState &&
                TryFindBestPhysicalOwnerFrameOpPlannerState(
                    initialKey,
                    switchingState,
                    out VulkanFrameOpPlannerStateKey compatibleKey,
                    out keyedState))
            {
                // Registry descriptor revisions are publication metadata, not
                // physical-resource ownership. Start the current plan update from
                // the compatible allocator and move its cache entry to the current
                // key so obsolete revisions cannot crowd the bounded state arena.
                RekeyFrameOpResourcePlannerState(
                    switchingState,
                    compatibleKey,
                    initialKey,
                    keyedState);
                hasCachedState = true;
            }
            ResourcePlannerRuntimeState state = usesSingleKeyState
                ? hasCachedState
                    ? keyedState
                    : ResourcePlannerRuntimeState.CreateEmpty()
                : switchingState.HasPreparationState
                    ? switchingState.PreparationState
                    : ResourcePlannerRuntimeState.CreateEmpty();

            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourcePlanner.PreparationState.{renderer.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Restoring {0} preparation state cached={1} owner={2} revision={3} signature=0x{4:X16}.",
                    usesSingleKeyState ? "single-context" : "merged",
                    usesSingleKeyState ? hasCachedState : switchingState.HasPreparationState,
                    state.AllocatorOwnershipId,
                    state.ResourcePlannerRevision,
                    state.ResourcePlannerSignature);
            }

            renderer.RestoreResourcePlannerRuntimeState(state);
        }

        public void Dispose()
        {
            if (!_active || _switchingState is null)
                return;


            _switchingState.MergedPlanActive = false;
            if (!_renderer.DeviceContext.IsOperational)
            {
                ResourcePlannerRuntimeState terminalRestoreState =
                    _previousState.ResourceAllocator is not null && _previousState.ResourceAllocator.IsRetired
                        ? ResourcePlannerRuntimeState.CreateEmpty()
                        : _previousState;
                _renderer.RestoreResourcePlannerRuntimeState(terminalRestoreState);
                return;
            }

            if (!_usesSingleKeyState)
            {
                // Mixed-context plans are published exclusively through their
                // independently keyed partition scopes. The outer lifecycle
                // scope does not own the runtime state left behind by that
                // process and must never create a second merged alias for it.
                _renderer.RestoreUsableFrameOpPlannerState(_previousState);
                _renderer.AssertFrameOpPlannerAllocatorOwnership(_switchingState);
                return;
            }

            ResourcePlannerRuntimeState state = PublishCurrentState();

            ResourcePlannerRuntimeState restoreState =
                _previousState.ResourceAllocator is not null && _previousState.ResourceAllocator.IsRetired
                    ? state
                    : _previousState;
            _renderer.RestoreResourcePlannerRuntimeState(restoreState);
            _renderer.AssertFrameOpPlannerAllocatorOwnership(_switchingState);
        }

        public ResourcePlannerRuntimeState PublishCurrentState()
        {
            if (!_active || _switchingState is null || !_renderer.DeviceContext.IsOperational)
                return default;

            ResourcePlannerRuntimeState state = _renderer.CaptureResourcePlannerRuntimeState();
            if (!_usesSingleKeyState)
                return state;

            if (_usesSingleKeyState &&
                _operations is not null &&
                TryGetSingleFrameOpPlannerStateKey(_operations, out VulkanFrameOpPlannerStateKey currentKey))
            {
                if (!_initialKey.Equals(currentKey) &&
                    _switchingState.States.TryGetValue(_initialKey, out ResourcePlannerRuntimeState initialState) &&
                    ReferenceEquals(initialState.ResourceAllocator, state.ResourceAllocator))
                {
                    _switchingState.States.Remove(_initialKey);
                    _switchingState.LastUsedSerials.Remove(_initialKey);
                    _switchingState.ActiveKeys.Remove(_initialKey);
                }

                _switchingState.States[currentKey] = state;
                if (_switchingState.HasPreparationState &&
                    ReferenceEquals(
                        _switchingState.PreparationState.ResourceAllocator,
                        state.ResourceAllocator))
                {
                    // Ownership has moved from the legacy merged preparation
                    // slot into this exact context key. Keeping both aliases
                    // would let later cleanup or restore mutate one allocator
                    // through two independent ownership records.
                    _switchingState.PreparationState = default;
                    _switchingState.HasPreparationState = false;
                }
                _renderer.MarkFrameOpResourcePlannerStateUsed(_switchingState, currentKey);
                return state;
            }

            _switchingState.PreparationState = state;
            _switchingState.HasPreparationState = true;

            return state;
        }
    }

    private readonly struct FrameOpResourcePlannerRecordingScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly bool _active;

        public FrameOpResourcePlannerRecordingScope(VulkanRenderer renderer)
        {
            _renderer = renderer;
            FrameOpResourcePlannerSwitchingState switchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            _active = renderer.DeviceContext.IsOperational && switchingState.ActiveKeys.Count > 0;
            _previousState = _active
                ? renderer.CaptureResourcePlannerRuntimeState()
                : default;

            if (_active)
            {
                switchingState.RecordingScopeActive = true;
                switchingState.HasActiveKey = false;
                switchingState.HasActiveContext = false;
            }
        }

        public void Dispose()
        {
            if (!_active)
                return;

            FrameOpResourcePlannerSwitchingState switchingState = _renderer.ActiveFrameOpResourcePlannerSwitchingState;
            if (_renderer.DeviceContext.IsOperational)
                _renderer.SaveActiveFrameOpResourcePlannerState();
            switchingState.RecordingScopeActive = false;
            switchingState.HasActiveKey = false;
            switchingState.HasActiveContext = false;
            ResourcePlannerRuntimeState restoreState =
                _previousState.ResourceAllocator is not null && _previousState.ResourceAllocator.IsRetired
                    ? ResourcePlannerRuntimeState.CreateEmpty()
                    : _previousState;
            _renderer.RestoreResourcePlannerRuntimeState(restoreState);
        }
    }

    private ResourcePlannerRuntimeState CaptureResourcePlannerRuntimeState()
    {
        if (HasThreadResourcePlannerRuntimeState)
        {
            ResourcePlannerRuntimeState threadState =
                CommandThreadContext.ResourcePlannerRuntimeState!.Value;
            threadState.FrameOpResourcePlannerSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;
            return threadState;
        }

        ResourcePlannerRuntimeState state = PublishedResourcePlannerRuntimeState;
        state.FrameOpResourcePlannerSwitchingState ??= _frameOpResourcePlannerSwitchingState;
        return state;
    }

    private void RestoreResourcePlannerRuntimeState(in ResourcePlannerRuntimeState state)
    {
        AssertResourcePlannerRuntimeStateCanBeRestored(state);
        ResourcePlannerRuntimeState next = state;
        next.FrameOpResourcePlannerSwitchingState =
            ActiveFrameOpResourcePlannerSwitchingState;
        if (HasThreadResourcePlannerRuntimeState)
        {
            CommandThreadContext.ResourcePlannerRuntimeState = next;
            return;
        }

        lock (_framePlanner.PlannerReadbackGate)
            RestoreResourcePlannerRuntimeStateCore(in next);
    }

    private static FrameOpResourcePlannerSwitchingState CloneFrameOpResourcePlannerSwitchingState(
        FrameOpResourcePlannerSwitchingState source)
    {
        FrameOpResourcePlannerSwitchingState clone = new()
        {
            UsageSerial = source.UsageSerial,
            SwitchingActive = source.SwitchingActive,
            MergedPlanActive = source.MergedPlanActive,
            RecordingScopeActive = source.RecordingScopeActive,
            HasActiveKey = source.HasActiveKey,
            ActiveKey = source.ActiveKey,
            HasActiveContext = source.HasActiveContext,
            ActiveContext = source.ActiveContext,
            PreparationState = source.PreparationState,
            HasPreparationState = source.HasPreparationState,
            PreparedFrameOpsSignature = source.PreparedFrameOpsSignature,
            PreparedPlanRevision = source.PreparedPlanRevision,
            HasPreparedPlan = source.HasPreparedPlan,
        };
        foreach ((VulkanFrameOpPlannerStateKey key, ResourcePlannerRuntimeState state) in source.States)
            clone.States[key] = state;
        foreach ((VulkanFrameOpPlannerStateKey key, ulong serial) in source.LastUsedSerials)
            clone.LastUsedSerials[key] = serial;
        foreach (VulkanFrameOpPlannerStateKey key in source.ActiveKeys)
            clone.ActiveKeys.Add(key);
        return clone;
    }

    /// <summary>
    /// Publishes every member of a planner generation under one gate. Reused image
    /// metadata belongs to that same generation, so it is committed in the same
    /// critical section rather than mutating the currently published generation
    /// before its replacement is visible.
    /// </summary>
    private void PublishResourcePlannerRuntimeState(
        in ResourcePlannerRuntimeState state,
        bool commitReusedImageMetadata)
    {
        if (!_deviceContext.IsOperational)
        {
            throw new InvalidOperationException(
                $"Cannot publish Vulkan resource-planner generation while device state is {_deviceContext.State}.");
        }

        AssertResourcePlannerRuntimeStateCanBeRestored(state);
        if (HasThreadResourcePlannerRuntimeState)
        {
            ResourcePlannerRuntimeState next = state;
            next.FrameOpResourcePlannerSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;
            CommandThreadContext.ResourcePlannerRuntimeState = next;
            return;
        }

        lock (_framePlanner.PlannerReadbackGate)
        {
            if (commitReusedImageMetadata)
                state.ResourceAllocator.CommitReusedPhysicalImageMetadata();

            RestoreResourcePlannerRuntimeStateCore(in state);
        }
    }

    /// <summary>
    /// Atomically merges a generation prepared on a worker thread into the
    /// renderer-wide context-key set and publishes it. This deliberately bypasses
    /// thread-local publication: a completed preparation transaction is global
    /// renderer state, not another temporary worker scope.
    /// </summary>
    private FrameOpResourcePlannerSwitchingState PublishPreparedResourcePlannerRuntimeState(
        ref ResourcePlannerRuntimeState state,
        in VulkanFrameOpPlannerStateKey key,
        VulkanPreparedResourceGenerationManifest preparedManifest)
    {
        if (!_deviceContext.IsOperational)
        {
            throw new InvalidOperationException(
                $"Cannot publish Vulkan resource-planner generation while device state is {_deviceContext.State}.");
        }

        AssertResourcePlannerRuntimeStateCanBeRestored(state);
        lock (_framePlanner.PlannerReadbackGate)
        {
            ResourcePlannerRuntimeState publishedState = PublishedResourcePlannerRuntimeState;
            FrameOpResourcePlannerSwitchingState switchingState =
                CloneFrameOpResourcePlannerSwitchingState(
                    publishedState.FrameOpResourcePlannerSwitchingState ??
                    _frameOpResourcePlannerSwitchingState);
            state.FrameOpResourcePlannerSwitchingState = switchingState;
            state.PreparedGenerationManifest = preparedManifest;
            switchingState.States[key] = state;
            MarkFrameOpResourcePlannerStateUsed(switchingState, key);
            PruneFrameOpResourcePlannerStatesToCapacity(switchingState);

            state.ResourceAllocator.CommitReusedPhysicalImageMetadata();
            RestoreResourcePlannerRuntimeStateCore(in state);
            return switchingState;
        }
    }

    private void RestoreResourcePlannerRuntimeStateCore(in ResourcePlannerRuntimeState state)
    {
        ResourcePlannerRuntimeState publishedState = state;
        publishedState.FrameOpResourcePlannerSwitchingState ??= _frameOpResourcePlannerSwitchingState;
        _framePlanner.PublishResourcePlannerGeneration(
            new ResourcePlannerRuntimeGeneration(publishedState));
    }

    private readonly record struct PhysicalAllocationPlan(
        VulkanResourceExtentContext ExtentContext,
        ulong Signature,
        bool Changed);

    internal readonly record struct BarrierPlanFastPathKey(
        VulkanCompiledRenderGraph CompiledGraph,
        ulong ResourcePlannerSignature,
        ulong ResourceAllocationSignature,
        VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership)
    {
        public bool Matches(in BarrierPlanFastPathKey other)
            => ReferenceEquals(CompiledGraph, other.CompiledGraph)
                && ResourcePlannerSignature == other.ResourcePlannerSignature
                && ResourceAllocationSignature == other.ResourceAllocationSignature
                && QueueOwnership.Equals(other.QueueOwnership);
    }

    internal Viewport GetCurrentViewport()
    {
        Extent2D targetExtent = ResolveCurrentDrawTargetExtent();
        BoundingRectangle region = RuntimeEngine.Rendering.State.RenderingPipelineState?.CurrentRenderRegion ?? default;
        return region.Width > 0 && region.Height > 0
            ? VulkanStateTracker.GetViewport(region, targetExtent)
            : ActiveState.GetViewport(targetExtent);
    }

    internal Rect2D GetCurrentScissor()
    {
        Extent2D targetExtent = ResolveCurrentDrawTargetExtent();
        BoundingRectangle region = RuntimeEngine.Rendering.State.RenderingPipelineState?.CurrentCropRegion ?? default;
        return region.Width > 0 && region.Height > 0
            ? VulkanStateTracker.GetScissor(region, targetExtent)
            : VulkanStateTracker.GetDefaultScissor(targetExtent);
    }

    internal IndexedViewportScissorSnapshot GetCurrentIndexedViewportScissorSnapshot()
        => ActiveState.GetIndexedViewportScissorSnapshot(ResolveCurrentDrawTargetExtent());

    /// <summary>
    /// Extent of the draw target that is actually bound right now. Pipeline commands
    /// publish their logical render target through the render-state binding stack,
    /// while quad-blit helpers bind FBOs directly through <see cref="XRFrameBuffer.BindForWriting"/>.
    /// The backend tracker's last-bound extent can be stale in both cases, so prefer
    /// the live engine-side binding before falling back to the tracker.
    /// </summary>
    internal Extent2D ResolveCurrentDrawTargetExtent()
    {
        XRFrameBuffer? fbo = GetCurrentDrawFrameBuffer();
        if (fbo is not null)
            return ResolveFrameBufferDrawExtent(fbo);

        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
            return externalExtent;

        return ActiveState.GetCurrentTargetExtent();
    }

    internal XRFrameBuffer? GetCurrentDrawFrameBuffer()
    {
        // A direct BindForWritingState scope is the innermost physical binding used
        // by quad, bloom-mip, cubemap, and shadow-atlas helpers. It must win over an
        // enclosing logical render-graph binding when draw state is snapshotted.
        if (XRFrameBuffer.BoundForWriting is { } directlyBoundTarget)
            return directlyBoundTarget;

        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRRenderPipelineInstance.RenderingState.ScopedRenderTargetBinding? binding =
            pipeline?.RenderState.CurrentRenderTargetBinding;
        if (binding is { Write: true, FrameBuffer: { } scopedTarget })
            return scopedTarget;

        return ActiveBoundDrawFrameBuffer;
    }

    internal uint CurrentDrawViewMask
    {
        get
        {
            XRFrameBuffer? frameBuffer = GetCurrentDrawFrameBuffer();
            return frameBuffer is null
                ? 0u
                : GenericToAPI<VkFrameBuffer>(frameBuffer)?.MultiviewViewMask ?? 0u;
        }
    }

    internal bool HasActiveMultiviewDrawTarget
        => System.Numerics.BitOperations.PopCount(CurrentDrawViewMask) > 1;

    internal XRFrameBuffer? ResolveCurrentFrameOpDrawTarget()
    {
        return GetCurrentDrawFrameBuffer();
    }

    internal static Extent2D ResolveFrameBufferDrawExtent(XRFrameBuffer fbo)
    {
        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
            return new Extent2D(Math.Max(fbo.Width, 1u), Math.Max(fbo.Height, 1u));

        uint minWidth = uint.MaxValue;
        uint minHeight = uint.MaxValue;
        bool found = false;

        foreach (var (target, _, mip, _) in targets)
        {
            if (target is null)
                continue;

            uint width = Math.Max(target.Width, 1u);
            uint height = Math.Max(target.Height, 1u);
            int mipLevel = Math.Max(mip, 0);
            if (mipLevel > 0)
            {
                width = Math.Max(width >> mipLevel, 1u);
                height = Math.Max(height >> mipLevel, 1u);
            }

            minWidth = Math.Min(minWidth, width);
            minHeight = Math.Min(minHeight, height);
            found = true;
        }

        return found
            ? new Extent2D(minWidth, minHeight)
            : new Extent2D(Math.Max(fbo.Width, 1u), Math.Max(fbo.Height, 1u));
    }

    internal XRFrameBuffer? GetCurrentReadFrameBuffer()
        => ActiveBoundReadFrameBuffer;

    internal EReadBufferMode GetReadBufferMode()
        => ActiveReadBufferMode;

    internal bool GetDepthTestEnabled()
        => ActiveState.GetDepthTestEnabled();

    internal bool GetDepthWriteEnabled()
        => ActiveState.GetDepthWriteEnabled();

    internal CompareOp GetDepthCompareOp()
        => ActiveState.GetDepthCompareOp();

    internal uint GetStencilWriteMask()
        => ActiveState.GetStencilWriteMask();

    internal ColorComponentFlags GetColorWriteMask()
        => ActiveState.GetColorWriteMask();

    internal CullModeFlags GetCullMode()
        => ActiveState.GetCullMode();

    internal FrontFace GetFrontFace()
        => ActiveState.GetFrontFace();

    internal bool GetBlendEnabled()
        => ActiveState.GetBlendEnabled();

    internal bool GetAlphaToCoverageEnabled()
        => ActiveState.GetAlphaToCoverageEnabled();

    internal BlendOp GetColorBlendOp()
        => ActiveState.GetColorBlendOp();

    internal BlendOp GetAlphaBlendOp()
        => ActiveState.GetAlphaBlendOp();

    internal BlendFactor GetSrcColorBlendFactor()
        => ActiveState.GetSrcColorBlendFactor();

    internal BlendFactor GetDstColorBlendFactor()
        => ActiveState.GetDstColorBlendFactor();

    internal BlendFactor GetSrcAlphaBlendFactor()
        => ActiveState.GetSrcAlphaBlendFactor();

    internal BlendFactor GetDstAlphaBlendFactor()
        => ActiveState.GetDstAlphaBlendFactor();

    internal bool GetStencilTestEnabled()
        => ActiveState.GetStencilTestEnabled();

    internal StencilOpState GetFrontStencilState()
        => ActiveState.GetFrontStencilState();

    internal StencilOpState GetBackStencilState()
        => ActiveState.GetBackStencilState();

    internal VulkanFixedFunctionStateSnapshot CaptureFixedFunctionState()
        => ActiveState.CaptureFixedFunctionState();

    internal void RestoreFixedFunctionState(in VulkanFixedFunctionStateSnapshot snapshot)
        => ActiveState.RestoreFixedFunctionState(snapshot);

    internal bool GetCroppingEnabled()
        => ActiveState.GetCroppingEnabled();

    internal ColorF4 GetClearColorValue()
        => ActiveState.GetClearColorValue();

    internal float GetClearDepthValue()
        => ActiveState.GetClearDepthValue();

    internal uint GetClearStencilValue()
        => ActiveState.GetClearStencilValue();

    internal Extent2D GetCurrentTargetExtent()
        => ActiveState.GetCurrentTargetExtent();

    private static ERenderClipDepthRange ResolveEffectiveVulkanClipDepthRange()
    {
        ERenderClipDepthRange requested = RuntimeEngine.Rendering.Settings.ClipDepthRange;
        if (requested != ERenderClipDepthRange.NegativeOneToOne)
            return requested;

        if (RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl)
        {
            if (!VulkanFramePlanner.ReportedNativeNegativeOneToOneDepth)
            {
                VulkanFramePlanner.ReportedNativeNegativeOneToOneDepth = true;
                Debug.Vulkan(
                    "[Vulkan] ClipDepthRange=NegativeOneToOne is using {0}.",
                    VulkanDepthClipControlExt.ExtensionName);
            }

            return requested;
        }

        if (!VulkanFramePlanner.ReportedShaderRemappedNegativeOneToOneDepth)
        {
            VulkanFramePlanner.ReportedShaderRemappedNegativeOneToOneDepth = true;
            Debug.VulkanWarning(
                "[Vulkan] ClipDepthRange=NegativeOneToOne was requested, but {0} is unavailable. " +
                "Keeping the engine's -1..1 clip-depth policy and remapping vertex shader gl_Position.z to Vulkan 0..w clip depth.",
                VulkanDepthClipControlExt.ExtensionName);
        }

        return requested;
    }

    internal static Viewport CreateVulkanViewport(Extent2D extent)
    {
        _ = ResolveEffectiveVulkanClipDepthRange();
        return RuntimeEngine.Rendering.Settings.ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown
            ? new Viewport
            {
                X = 0f,
                Y = 0f,
                Width = extent.Width,
                Height = extent.Height,
                MinDepth = 0f,
                MaxDepth = 1f
            }
            : new Viewport
            {
                X = 0f,
                Y = extent.Height,
                Width = extent.Width,
                Height = -(float)extent.Height,
                MinDepth = 0f,
                MaxDepth = 1f
            };
    }

    internal static Viewport CreateVulkanViewport(BoundingRectangle region, Extent2D targetExtent)
    {
        _ = ResolveEffectiveVulkanClipDepthRange();
        if (RuntimeEngine.Rendering.Settings.ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown)
        {
            return new Viewport
            {
                X = region.X,
                Y = targetExtent.Height - (region.Y + region.Height),
                Width = region.Width,
                Height = region.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
        }

        return new Viewport
        {
            X = region.X,
            Y = targetExtent.Height - region.Y,
            Width = region.Width,
            Height = -region.Height,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };
    }

}
