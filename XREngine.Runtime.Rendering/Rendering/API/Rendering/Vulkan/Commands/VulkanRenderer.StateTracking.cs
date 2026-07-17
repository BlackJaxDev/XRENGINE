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

    private readonly VulkanStateTracker _state = new();
    [ThreadStatic]
    private static VulkanRenderer? _threadRenderStateOwner;
    [ThreadStatic]
    private static VulkanStateTracker? _threadRenderState;
    [ThreadStatic]
    private static VulkanRenderer? _threadResourcePlannerRuntimeStateOwner;
    [ThreadStatic]
    private static ResourcePlannerRuntimeState? _threadResourcePlannerRuntimeState;
    [ThreadStatic]
    private static VulkanRenderer? _threadFrameOpResourcePlannerSwitchingStateOwner;
    [ThreadStatic]
    private static FrameOpResourcePlannerSwitchingState? _threadFrameOpResourcePlannerSwitchingState;
    [ThreadStatic]
    private static VulkanRenderer? _threadFramebufferBindingOwner;
    [ThreadStatic]
    private static XRFrameBuffer? _threadBoundDrawFrameBuffer;
    [ThreadStatic]
    private static XRFrameBuffer? _threadBoundReadFrameBuffer;
    [ThreadStatic]
    private static EReadBufferMode _threadReadBufferMode;
    private VulkanResourcePlanner _resourcePlanner = new();
    private VulkanResourceAllocator _resourceAllocator = new();
    private VulkanBarrierPlanner _barrierPlanner = new();
    private VulkanCompiledRenderGraph _compiledRenderGraph = VulkanCompiledRenderGraph.Empty;
    private FrameOpContext? _lastActiveFrameOpContext;
    private ulong _resourcePlannerSignature = ulong.MaxValue;
    private ulong _resourceAllocationSignature = ulong.MaxValue;
    private bool _interactiveResizePlannerFrozen;
    private uint _interactiveResizeFrozenDisplayWidth;
    private uint _interactiveResizeFrozenDisplayHeight;
    private uint _interactiveResizeFrozenInternalWidth;
    private uint _interactiveResizeFrozenInternalHeight;
    private ulong _failedResourcePlannerSignature = ulong.MaxValue;
    private ulong _failedResourceAllocationSignature = ulong.MaxValue;
    private long _failedResourceAllocationTimestamp;
    private ResourcePlannerFastPathKey _resourcePlannerFastPathKey;
    private bool _hasResourcePlannerFastPathKey;
    private BarrierPlanFastPathKey _barrierPlanFastPathKey;
    private bool _hasBarrierPlanFastPathKey;
    private ResourcePlannerSignatureBreakdown _resourcePlannerSignatureBreakdown;
    private ulong _resourcePlannerRevision;
    private bool _isRecordingCommandBuffer;
    private int _commandChainFrozenPlanReaders;
    private ulong _commandChainFrozenResourcePlanRevision;
    private readonly Dictionary<string, XRDataBuffer> _trackedBuffersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrameOpResourcePlannerSwitchingState _frameOpResourcePlannerSwitchingState = new();
    private VulkanStateTracker ActiveState =>
        ReferenceEquals(_threadRenderStateOwner, this) && _threadRenderState is not null
            ? _threadRenderState
            : _state;
    private bool HasThreadResourcePlannerRuntimeState =>
        ReferenceEquals(_threadResourcePlannerRuntimeStateOwner, this) &&
        _threadResourcePlannerRuntimeState.HasValue;
    private FrameOpResourcePlannerSwitchingState ActiveFrameOpResourcePlannerSwitchingState =>
        ReferenceEquals(_threadFrameOpResourcePlannerSwitchingStateOwner, this) &&
        _threadFrameOpResourcePlannerSwitchingState is not null
            ? _threadFrameOpResourcePlannerSwitchingState
            : _frameOpResourcePlannerSwitchingState;
    private bool HasThreadFramebufferBindingState
        => ReferenceEquals(_threadFramebufferBindingOwner, this);
    private XRFrameBuffer? ActiveBoundDrawFrameBuffer
    {
        get => HasThreadFramebufferBindingState ? _threadBoundDrawFrameBuffer : _boundDrawFrameBuffer;
        set
        {
            if (HasThreadFramebufferBindingState)
            {
                _threadBoundDrawFrameBuffer = value;
                return;
            }

            _boundDrawFrameBuffer = value;
        }
    }
    private XRFrameBuffer? ActiveBoundReadFrameBuffer
    {
        get => HasThreadFramebufferBindingState ? _threadBoundReadFrameBuffer : _boundReadFrameBuffer;
        set
        {
            if (HasThreadFramebufferBindingState)
            {
                _threadBoundReadFrameBuffer = value;
                return;
            }

            _boundReadFrameBuffer = value;
        }
    }
    private EReadBufferMode ActiveReadBufferMode
    {
        get => HasThreadFramebufferBindingState ? _threadReadBufferMode : _readBufferMode;
        set
        {
            if (HasThreadFramebufferBindingState)
            {
                _threadReadBufferMode = value;
                return;
            }

            _readBufferMode = value;
        }
    }
    internal VulkanResourcePlanner ResourcePlanner =>
        HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.ResourcePlanner
            : _resourcePlanner;
    internal VulkanResourcePlan ResourcePlan => ResourcePlanner.CurrentPlan;
    internal VulkanResourceAllocator ResourceAllocator =>
        HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.ResourceAllocator
            : _resourceAllocator;
    internal int ResourceAllocatorIdentity => RuntimeHelpers.GetHashCode(ResourceAllocator);
    internal VulkanBarrierPlanner BarrierPlanner =>
        HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.BarrierPlanner
            : _barrierPlanner;
    internal VulkanCompiledRenderGraph CompiledRenderGraph =>
        HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.CompiledRenderGraph
            : _compiledRenderGraph;
    internal ulong ResourcePlannerRevision =>
        HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.ResourcePlannerRevision
            : _resourcePlannerRevision;
    private VulkanResourcePlanner ActiveResourcePlanner
    {
        get => ResourcePlanner;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.ResourcePlanner = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _resourcePlanner = value;
        }
    }
    private VulkanResourceAllocator ActiveResourceAllocator
    {
        get => ResourceAllocator;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.ResourceAllocator = value;
                state.AllocatorOwnershipId = value.OwnershipId;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _resourceAllocator = value;
        }
    }
    private VulkanBarrierPlanner ActiveBarrierPlanner
    {
        get => BarrierPlanner;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.BarrierPlanner = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _barrierPlanner = value;
        }
    }
    private VulkanCompiledRenderGraph ActiveCompiledRenderGraph
    {
        get => CompiledRenderGraph;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.CompiledRenderGraph = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _compiledRenderGraph = value;
        }
    }
    private FrameOpContext? ActiveLastActiveFrameOpContext
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.LastActiveFrameOpContext
            : _lastActiveFrameOpContext;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.LastActiveFrameOpContext = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _lastActiveFrameOpContext = value;
        }
    }
    private ulong ActiveResourcePlannerSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.ResourcePlannerSignature
            : _resourcePlannerSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.ResourcePlannerSignature = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _resourcePlannerSignature = value;
        }
    }
    private ulong ActiveResourceAllocationSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.ResourceAllocationSignature
            : _resourceAllocationSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.ResourceAllocationSignature = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _resourceAllocationSignature = value;
        }
    }
    private ulong ActiveFailedResourcePlannerSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.FailedResourcePlannerSignature
            : _failedResourcePlannerSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.FailedResourcePlannerSignature = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _failedResourcePlannerSignature = value;
        }
    }
    private ulong ActiveFailedResourceAllocationSignature
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.FailedResourceAllocationSignature
            : _failedResourceAllocationSignature;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.FailedResourceAllocationSignature = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _failedResourceAllocationSignature = value;
        }
    }
    private long ActiveFailedResourceAllocationTimestamp
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.FailedResourceAllocationTimestamp
            : _failedResourceAllocationTimestamp;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.FailedResourceAllocationTimestamp = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _failedResourceAllocationTimestamp = value;
        }
    }
    private ResourcePlannerFastPathKey ActiveResourcePlannerFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.ResourcePlannerFastPathKey
            : _resourcePlannerFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.ResourcePlannerFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _resourcePlannerFastPathKey = value;
        }
    }
    private bool ActiveHasResourcePlannerFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.HasResourcePlannerFastPathKey
            : _hasResourcePlannerFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.HasResourcePlannerFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _hasResourcePlannerFastPathKey = value;
        }
    }
    private BarrierPlanFastPathKey ActiveBarrierPlanFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.BarrierPlanFastPathKey
            : _barrierPlanFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.BarrierPlanFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _barrierPlanFastPathKey = value;
        }
    }
    private bool ActiveHasBarrierPlanFastPathKey
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.HasBarrierPlanFastPathKey
            : _hasBarrierPlanFastPathKey;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.HasBarrierPlanFastPathKey = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _hasBarrierPlanFastPathKey = value;
        }
    }
    private ResourcePlannerSignatureBreakdown ActiveResourcePlannerSignatureBreakdown
    {
        get => HasThreadResourcePlannerRuntimeState
            ? _threadResourcePlannerRuntimeState!.Value.ResourcePlannerSignatureBreakdown
            : _resourcePlannerSignatureBreakdown;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.ResourcePlannerSignatureBreakdown = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _resourcePlannerSignatureBreakdown = value;
        }
    }
    private ulong ActiveResourcePlannerRevision
    {
        get => ResourcePlannerRevision;
        set
        {
            if (TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state))
            {
                state.ResourcePlannerRevision = value;
                StoreThreadResourcePlannerRuntimeState(in state);
                return;
            }

            _resourcePlannerRevision = value;
        }
    }
    private bool IsCommandChainResourcePlanFrozen => Volatile.Read(ref _commandChainFrozenPlanReaders) > 0;
    private bool[]? _commandBufferDirtyFlags;
    private readonly object _commandBufferDirtyReasonLock = new();
    private readonly Dictionary<string, int> _commandBufferDirtyReasons = new(StringComparer.Ordinal);
    private long _lastCommandBufferDirtyReasonLogTimestamp;
    private XRFrameBuffer? _boundDrawFrameBuffer;
    private XRFrameBuffer? _boundReadFrameBuffer;
    private XRTexture? _lastWindowPresentColorTexture;
    private XRFrameBuffer? _lastWindowPresentFrameBuffer;
    private XRTexture? _lastWindowPresentFallbackFrameBufferTexture;
    private XRFrameBuffer? _lastWindowPresentFallbackFrameBuffer;
    private FrameOpContext? _lastWindowPresentFrameOpContext;
    private VulkanPhysicalImageGroup? _retainedAutoExposureHistoryGroup;
    private ulong _lastResourcePlanReplacementRevision;
    private ulong _lastResourcePlanReplacementSignature;
    private ulong _lastResourcePlanReplacementAllocationSignature;
    private int _lastResourcePlanReplacementRetiredImageCount;
    private int _lastResourcePlanReplacementRetiredBufferCount;
    private EReadBufferMode _readBufferMode = EReadBufferMode.ColorAttachment0;
    private EVulkanQueueOverlapMode _autoQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    private EVulkanQueueOverlapMode _lastResolvedQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    private int _queueOverlapPromotionStabilityFrames;
    private int _queueOverlapFramesInMode;
    private long _lastQueueOverlapSampleTimestamp;
    private ulong _lastQueueOverlapSampleFrameId = ulong.MaxValue;
    private double _queueOverlapFrameDeltaEmaMs = -1.0;
    private double _queueOverlapModeStartFrameDeltaMs = -1.0;
    private readonly List<MergedFrameOpRegistryCacheEntry> _mergedFrameOpRegistryCache = new(MaxMergedFrameOpRegistryCacheEntries);
    private readonly List<FrameOpPlannerStateKey> _frameOpPlannerStateKeyScratch = [];
    private readonly List<FrameOpPlannerStateKey> _frameOpPlannerStateEvictionScratch = [];
    private readonly List<RenderResourceRegistry> _frameOpRegistryScratch = [];
    private readonly List<FrameOpRegistryCacheSource> _frameOpRegistryCacheSourceScratch = [];
    private IReadOnlyCollection<RenderPassMetadata>? _lastActiveFilterSourcePassMetadata;
    private IReadOnlyCollection<RenderPassMetadata>? _lastActiveFilterResult;
    private RenderResourceRegistry? _lastActiveFilterResourceRegistry;
    private int _lastActiveFilterResourceRegistryRevision = int.MinValue;
    private int _lastActiveFilterPassSetSignature = int.MinValue;
    private int _lastActiveFilterResourceSetSignature = int.MinValue;
    private bool _lastActiveFilterConstrainToActivePassSet;
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

    private readonly record struct FrameOpRegistryCacheSource(
        RenderResourceRegistry Registry,
        int DescriptorSignature);

    private sealed class MergedFrameOpRegistryCacheEntry(
        FrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        int frameBufferDescriptorSignature,
        RenderResourceRegistry mergedRegistry,
        ulong lastUsedFrameId)
    {
        public FrameOpPlannerStateKey OwnerKey { get; } = ownerKey;
        public RenderResourceRegistry? PrimaryRegistry { get; } = primaryRegistry;
        public int PrimaryDescriptorSignature { get; set; } = primaryRegistry?.DescriptorSignature ?? 0;
        public FrameOpRegistryCacheSource[] Sources { get; set; } = sources;
        public int FrameBufferDescriptorSignature { get; set; } = frameBufferDescriptorSignature;
        public RenderResourceRegistry MergedRegistry { get; set; } = mergedRegistry;
        public ulong LastUsedFrameId { get; set; } = lastUsedFrameId;
    }

    private sealed class FrameOpResourcePlannerSwitchingState
    {
        public Dictionary<FrameOpPlannerStateKey, ResourcePlannerRuntimeState> States { get; } = new();
        public Dictionary<FrameOpPlannerStateKey, ulong> LastUsedSerials { get; } = new();
        public HashSet<FrameOpPlannerStateKey> ActiveKeys { get; } = new();
        public ulong UsageSerial;
        public bool SwitchingActive;
        public bool RecordingScopeActive;
        public bool HasActiveKey;
        public FrameOpPlannerStateKey ActiveKey;
        public bool HasActiveContext;
        public FrameOpContext ActiveContext;
        public ResourcePlannerRuntimeState PreparationState;
        public bool HasPreparationState;
    }

    private struct ResourcePlannerRuntimeState
    {
        public VulkanResourcePlanner ResourcePlanner;
        public VulkanResourceAllocator ResourceAllocator;
        public VulkanBarrierPlanner BarrierPlanner;
        public VulkanCompiledRenderGraph CompiledRenderGraph;
        public FrameOpContext? LastActiveFrameOpContext;
        public ulong ResourcePlannerSignature;
        public ulong ResourceAllocationSignature;
        public ulong FailedResourcePlannerSignature;
        public ulong FailedResourceAllocationSignature;
        public long FailedResourceAllocationTimestamp;
        public ResourcePlannerFastPathKey ResourcePlannerFastPathKey;
        public bool HasResourcePlannerFastPathKey;
        public BarrierPlanFastPathKey BarrierPlanFastPathKey;
        public bool HasBarrierPlanFastPathKey;
        public ResourcePlannerSignatureBreakdown ResourcePlannerSignatureBreakdown;
        public ulong ResourcePlannerRevision;
        public long AllocatorOwnershipId;
        public FrameOpResourcePlannerSwitchingState? FrameOpResourcePlannerSwitchingState;

        public static ResourcePlannerRuntimeState CreateEmpty()
        {
            VulkanResourceAllocator allocator = new();
            return new()
            {
                ResourcePlanner = new VulkanResourcePlanner(),
                ResourceAllocator = allocator,
                BarrierPlanner = new VulkanBarrierPlanner(),
                CompiledRenderGraph = VulkanCompiledRenderGraph.Empty,
                ResourcePlannerSignature = ulong.MaxValue,
                ResourceAllocationSignature = ulong.MaxValue,
                FailedResourcePlannerSignature = ulong.MaxValue,
                FailedResourceAllocationSignature = ulong.MaxValue,
                AllocatorOwnershipId = allocator.OwnershipId,
                FrameOpResourcePlannerSwitchingState = new FrameOpResourcePlannerSwitchingState(),
            };
        }
    }

    private readonly struct ThreadRenderStateScope : IDisposable
    {
        private readonly VulkanRenderer? _previousOwner;
        private readonly VulkanStateTracker? _previousState;
        private readonly VulkanRenderer? _previousFramebufferBindingOwner;
        private readonly XRFrameBuffer? _previousThreadBoundDrawFrameBuffer;
        private readonly XRFrameBuffer? _previousThreadBoundReadFrameBuffer;
        private readonly EReadBufferMode _previousThreadReadBufferMode;
        private readonly IDisposable _currentRendererScope;

        public ThreadRenderStateScope(VulkanRenderer renderer, VulkanStateTracker state)
        {
            _previousOwner = _threadRenderStateOwner;
            _previousState = _threadRenderState;
            _previousFramebufferBindingOwner = _threadFramebufferBindingOwner;
            _previousThreadBoundDrawFrameBuffer = _threadBoundDrawFrameBuffer;
            _previousThreadBoundReadFrameBuffer = _threadBoundReadFrameBuffer;
            _previousThreadReadBufferMode = _threadReadBufferMode;
            _threadRenderStateOwner = renderer;
            _threadRenderState = state;
            _threadFramebufferBindingOwner = renderer;
            _threadBoundDrawFrameBuffer = null;
            _threadBoundReadFrameBuffer = null;
            _threadReadBufferMode = EReadBufferMode.ColorAttachment0;
            _currentRendererScope = AbstractRenderer.PushThreadCurrent(renderer);
        }

        public void Dispose()
        {
            _currentRendererScope.Dispose();
            _threadRenderStateOwner = _previousOwner;
            _threadRenderState = _previousState;
            _threadFramebufferBindingOwner = _previousFramebufferBindingOwner;
            _threadBoundDrawFrameBuffer = _previousThreadBoundDrawFrameBuffer;
            _threadBoundReadFrameBuffer = _previousThreadBoundReadFrameBuffer;
            _threadReadBufferMode = _previousThreadReadBufferMode;
        }
    }

    private ThreadRenderStateScope EnterThreadRenderStateScope(VulkanStateTracker state)
        => new(this, state);

    private bool TryCaptureThreadResourcePlannerRuntimeState(out ResourcePlannerRuntimeState state)
    {
        if (HasThreadResourcePlannerRuntimeState)
        {
            state = _threadResourcePlannerRuntimeState!.Value;
            return true;
        }

        state = default;
        return false;
    }

    private static void StoreThreadResourcePlannerRuntimeState(in ResourcePlannerRuntimeState state)
    {
        ResourcePlannerRuntimeState next = state;
        next.FrameOpResourcePlannerSwitchingState = _threadFrameOpResourcePlannerSwitchingState ??
            next.FrameOpResourcePlannerSwitchingState;
        _threadResourcePlannerRuntimeState = next;
    }

    private readonly struct ThreadResourcePlannerRuntimeStateScope : IDisposable
    {
        private readonly VulkanRenderer? _previousOwner;
        private readonly ResourcePlannerRuntimeState? _previousState;

        public ThreadResourcePlannerRuntimeStateScope(
            VulkanRenderer renderer,
            in ResourcePlannerRuntimeState state)
        {
            ResourcePlannerRuntimeState scopedState = state;
            scopedState.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();
            _previousOwner = _threadResourcePlannerRuntimeStateOwner;
            _previousState = _threadResourcePlannerRuntimeState;
            _threadResourcePlannerRuntimeStateOwner = renderer;
            _threadResourcePlannerRuntimeState = scopedState;
        }

        public ResourcePlannerRuntimeState CaptureCurrent(VulkanRenderer renderer)
        {
            if (!ReferenceEquals(_threadResourcePlannerRuntimeStateOwner, renderer) ||
                !_threadResourcePlannerRuntimeState.HasValue)
            {
                return renderer.CaptureResourcePlannerRuntimeState();
            }

            ResourcePlannerRuntimeState state = _threadResourcePlannerRuntimeState.Value;
            state.FrameOpResourcePlannerSwitchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            return state;
        }

        public void Dispose()
        {
            _threadResourcePlannerRuntimeStateOwner = _previousOwner;
            _threadResourcePlannerRuntimeState = _previousState;
        }
    }

    private ThreadResourcePlannerRuntimeStateScope EnterThreadResourcePlannerRuntimeStateScope(
        in ResourcePlannerRuntimeState state)
        => new(this, state);

    private readonly struct ThreadFrameOpResourcePlannerSwitchingStateScope : IDisposable
    {
        private readonly VulkanRenderer? _previousOwner;
        private readonly FrameOpResourcePlannerSwitchingState? _previousState;

        public ThreadFrameOpResourcePlannerSwitchingStateScope(
            VulkanRenderer renderer,
            FrameOpResourcePlannerSwitchingState state)
        {
            _previousOwner = _threadFrameOpResourcePlannerSwitchingStateOwner;
            _previousState = _threadFrameOpResourcePlannerSwitchingState;
            _threadFrameOpResourcePlannerSwitchingStateOwner = renderer;
            _threadFrameOpResourcePlannerSwitchingState = state;
        }

        public FrameOpResourcePlannerSwitchingState CaptureCurrent(VulkanRenderer renderer)
        {
            if (!ReferenceEquals(_threadFrameOpResourcePlannerSwitchingStateOwner, renderer) ||
                _threadFrameOpResourcePlannerSwitchingState is null)
            {
                return renderer.ActiveFrameOpResourcePlannerSwitchingState;
            }

            return _threadFrameOpResourcePlannerSwitchingState;
        }

        public void Dispose()
        {
            _threadFrameOpResourcePlannerSwitchingStateOwner = _previousOwner;
            _threadFrameOpResourcePlannerSwitchingState = _previousState;
        }
    }

    private ThreadFrameOpResourcePlannerSwitchingStateScope EnterThreadFrameOpResourcePlannerSwitchingStateScope(
        FrameOpResourcePlannerSwitchingState state)
        => new(this, state);

    internal readonly record struct FrameOpPlannerStateKey(
        EVulkanFrameOpContextKind ContextKind,
        int PipelineIdentity,
        int ViewportIdentity,
        uint DisplayWidth,
        uint DisplayHeight,
        uint InternalWidth,
        uint InternalHeight,
        int OutputFrameBufferIdentity,
        int OutputTargetIdentity,
        int ResourceRegistrySignature,
        int PassMetadataSignature,
        ulong ResourceGeneration,
        uint SubmissionQueueFamily);

    private readonly record struct ResourcePlannerSignatureBreakdown(
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

    private readonly record struct ResourcePlannerFastPathKey(
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
            _renderer._commandChainFrozenResourcePlanRevision = resourcePlanRevision;
            Interlocked.Increment(ref _renderer._commandChainFrozenPlanReaders);
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _renderer._commandChainFrozenPlanReaders) == 0)
                _renderer._commandChainFrozenResourcePlanRevision = 0;
        }
    }

    private readonly struct FrameOpResourcePlannerPreparationScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly FrameOpResourcePlannerSwitchingState? _switchingState;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly bool _active;

        public FrameOpResourcePlannerPreparationScope(VulkanRenderer renderer, FrameOp[] ops)
        {
            _renderer = renderer;
            _switchingState = null;
            _previousState = default;
            _active = false;

            if (!renderer.IsDeviceOperational ||
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
            _previousState = renderer.CaptureResourcePlannerRuntimeState();
            _active = true;

            ResourcePlannerRuntimeState state = switchingState.HasPreparationState
                ? switchingState.PreparationState
                : ResourcePlannerRuntimeState.CreateEmpty();

            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourcePlanner.PreparationState.{renderer.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Restoring merged preparation state cached={0} owner={1} revision={2} signature=0x{3:X16}.",
                    switchingState.HasPreparationState,
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

            if (!_renderer.IsDeviceOperational)
            {
                ResourcePlannerRuntimeState terminalRestoreState =
                    _previousState.ResourceAllocator is not null && _previousState.ResourceAllocator.IsRetired
                        ? ResourcePlannerRuntimeState.CreateEmpty()
                        : _previousState;
                _renderer.RestoreResourcePlannerRuntimeState(terminalRestoreState);
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
            if (!_active || _switchingState is null || !_renderer.IsDeviceOperational)
                return default;

            ResourcePlannerRuntimeState state = _renderer.CaptureResourcePlannerRuntimeState();
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
            _active = renderer.IsDeviceOperational && switchingState.ActiveKeys.Count > 0;
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
            if (_renderer.IsDeviceOperational)
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
            ResourcePlannerRuntimeState state = _threadResourcePlannerRuntimeState!.Value;
            state.FrameOpResourcePlannerSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;
            return state;
        }

        return new()
        {
            ResourcePlanner = _resourcePlanner,
            ResourceAllocator = _resourceAllocator,
            BarrierPlanner = _barrierPlanner,
            CompiledRenderGraph = _compiledRenderGraph,
            LastActiveFrameOpContext = _lastActiveFrameOpContext,
            ResourcePlannerSignature = _resourcePlannerSignature,
            ResourceAllocationSignature = _resourceAllocationSignature,
            FailedResourcePlannerSignature = _failedResourcePlannerSignature,
            FailedResourceAllocationSignature = _failedResourceAllocationSignature,
            FailedResourceAllocationTimestamp = _failedResourceAllocationTimestamp,
            ResourcePlannerFastPathKey = _resourcePlannerFastPathKey,
            HasResourcePlannerFastPathKey = _hasResourcePlannerFastPathKey,
            BarrierPlanFastPathKey = _barrierPlanFastPathKey,
            HasBarrierPlanFastPathKey = _hasBarrierPlanFastPathKey,
            ResourcePlannerSignatureBreakdown = _resourcePlannerSignatureBreakdown,
            ResourcePlannerRevision = _resourcePlannerRevision,
            AllocatorOwnershipId = _resourceAllocator.OwnershipId,
            FrameOpResourcePlannerSwitchingState = _frameOpResourcePlannerSwitchingState,
        };
    }

    private void RestoreResourcePlannerRuntimeState(in ResourcePlannerRuntimeState state)
    {
        AssertResourcePlannerRuntimeStateCanBeRestored(state);
        if (HasThreadResourcePlannerRuntimeState)
        {
            ResourcePlannerRuntimeState next = state;
            next.FrameOpResourcePlannerSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;
            _threadResourcePlannerRuntimeState = next;
            return;
        }

        _resourcePlanner = state.ResourcePlanner;
        _resourceAllocator = state.ResourceAllocator;
        _barrierPlanner = state.BarrierPlanner;
        _compiledRenderGraph = state.CompiledRenderGraph;
        _lastActiveFrameOpContext = state.LastActiveFrameOpContext;
        _resourcePlannerSignature = state.ResourcePlannerSignature;
        _resourceAllocationSignature = state.ResourceAllocationSignature;
        _failedResourcePlannerSignature = state.FailedResourcePlannerSignature;
        _failedResourceAllocationSignature = state.FailedResourceAllocationSignature;
        _failedResourceAllocationTimestamp = state.FailedResourceAllocationTimestamp;
        _resourcePlannerFastPathKey = state.ResourcePlannerFastPathKey;
        _hasResourcePlannerFastPathKey = state.HasResourcePlannerFastPathKey;
        _barrierPlanFastPathKey = state.BarrierPlanFastPathKey;
        _hasBarrierPlanFastPathKey = state.HasBarrierPlanFastPathKey;
        _resourcePlannerSignatureBreakdown = state.ResourcePlannerSignatureBreakdown;
        _resourcePlannerRevision = state.ResourcePlannerRevision;
    }

    private readonly record struct PhysicalAllocationPlan(
        VulkanResourceExtentContext ExtentContext,
        ulong Signature,
        bool Changed);

    private readonly record struct BarrierPlanFastPathKey(
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
        => ActiveState.GetViewport(ResolveCurrentDrawTargetExtent());

    internal Rect2D GetCurrentScissor()
        => ActiveState.GetScissor(ResolveCurrentDrawTargetExtent());

    internal IndexedViewportScissorSnapshot GetCurrentIndexedViewportScissorSnapshot()
        => ActiveState.GetIndexedViewportScissorSnapshot(ResolveCurrentDrawTargetExtent());

    internal readonly record struct IndexedViewportScissorSnapshot(
        Viewport[]? Viewports,
        Rect2D[]? Scissors,
        uint Count);

    internal readonly record struct VulkanFixedFunctionStateSnapshot(
        bool DepthTestEnabled,
        bool DepthWriteEnabled,
        CompareOp DepthCompareOp,
        bool StencilTestEnabled,
        StencilOpState FrontStencilState,
        StencilOpState BackStencilState,
        uint StencilWriteMask,
        ColorComponentFlags ColorWriteMask,
        CullModeFlags CullMode,
        FrontFace FrontFace,
        bool BlendEnabled,
        bool AlphaToCoverageEnabled,
        BlendOp ColorBlendOp,
        BlendOp AlphaBlendOp,
        BlendFactor SrcColorBlendFactor,
        BlendFactor DstColorBlendFactor,
        BlendFactor SrcAlphaBlendFactor,
        BlendFactor DstAlphaBlendFactor);

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
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRRenderPipelineInstance.RenderingState.ScopedRenderTargetBinding? binding =
            pipeline?.RenderState.CurrentRenderTargetBinding;
        if (binding is { Write: true, FrameBuffer: { } scopedTarget })
            return scopedTarget;

        return XRFrameBuffer.BoundForWriting ?? ActiveBoundDrawFrameBuffer;
    }

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

    private static bool _reportedNativeNegativeOneToOneDepth;
    private static bool _reportedShaderRemappedNegativeOneToOneDepth;

    private static ERenderClipDepthRange ResolveEffectiveVulkanClipDepthRange()
    {
        ERenderClipDepthRange requested = RuntimeEngine.Rendering.Settings.ClipDepthRange;
        if (requested != ERenderClipDepthRange.NegativeOneToOne)
            return requested;

        if (RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl)
        {
            if (!_reportedNativeNegativeOneToOneDepth)
            {
                _reportedNativeNegativeOneToOneDepth = true;
                Debug.Vulkan(
                    "[Vulkan] ClipDepthRange=NegativeOneToOne is using {0}.",
                    VulkanDepthClipControlExt.ExtensionName);
            }

            return requested;
        }

        if (!_reportedShaderRemappedNegativeOneToOneDepth)
        {
            _reportedShaderRemappedNegativeOneToOneDepth = true;
            Debug.VulkanWarning(
                "[Vulkan] ClipDepthRange=NegativeOneToOne was requested, but {0} is unavailable. " +
                "Keeping the engine's -1..1 clip-depth policy and remapping vertex shader gl_Position.z to Vulkan 0..w clip depth.",
                VulkanDepthClipControlExt.ExtensionName);
        }

        return requested;
    }

    private static Viewport CreateVulkanViewport(Extent2D extent)
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

    private static Viewport CreateVulkanViewport(BoundingRectangle region, Extent2D targetExtent)
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
