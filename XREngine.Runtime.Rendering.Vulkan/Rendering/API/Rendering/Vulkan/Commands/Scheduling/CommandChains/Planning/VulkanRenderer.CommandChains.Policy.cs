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

internal sealed unsafe partial class VulkanCommandRuntime
{
    internal const string CommandChainsEnvVar = XREngineEnvironmentVariables.VulkanCommandChains;
    internal const string CommandChainsSingleThreadEnvVar = XREngineEnvironmentVariables.VulkanCommandChainsSingleThread;
    internal const string CommandChainValidateEnvVar = XREngineEnvironmentVariables.VulkanCommandChainValidate;
    internal const string CommandChainTraceEnvVar = XREngineEnvironmentVariables.VulkanCommandChainTrace;
    internal const string DisableParallelChainRecordingEnvVar = XREngineEnvironmentVariables.VulkanDisableParallelChainRecording;
    internal const string CommandChainMultiQueueEnvVar = XREngineEnvironmentVariables.VulkanCommandChainMultiQueue;
    internal const string CommandChainStabilityGuardEnvVar = XREngineEnvironmentVariables.VulkanCommandChainStabilityGuard;
    internal const string CommandChainsAllowIndependentDesktopEnvVar =
        XREngineEnvironmentVariables.VulkanCommandChainsAllowIndependentDesktop;
    internal const string CommandChainBenchmarkForceRerecordEnvVar =
        XREngineEnvironmentVariables.VulkanCommandChainBenchmarkForceRerecord;
    internal const int CommandChainLeftEyeViewIndex = 0;
    internal const int CommandChainRightEyeViewIndex = 1;
    internal const int CommandChainStereoMultiviewViewIndex = -1;

    private ref Dictionary<CommandChainKey, CommandChain>[]? _commandChainCaches => ref _commandRuntime.CommandChains.Caches;
    private ref Dictionary<uint, Dictionary<CommandChainKey, CommandChain>>? _externalCommandChainCaches => ref _commandRuntime.CommandChains.ExternalCaches;
    private List<RenderPacket> _commandChainPacketScratch => _commandRuntime.CommandChains.PacketScratch;
    private List<RenderPacket> _commandChainPacketPool => _commandRuntime.CommandChains.PacketPool;
    // Packets hold only numeric range headers. A publication selects one arena;
    // retained packet leases keep that arena immutable until every prepared
    // command chain and worker has retired it.
    private List<RenderPacketPayloadArena> _commandChainPacketPayloadArenas => _commandRuntime.CommandChains.PacketPayloadArenas;
    private ref RenderPacketPayloadArena? _activeCommandChainPacketPayloadArena => ref _commandRuntime.CommandChains.ActivePacketPayloadArena;
    private DrawPacket[] _commandChainDrawPacketScratch => _commandRuntime.CommandChains.DrawPacketScratch;
    private ref int _commandChainPacketPoolCursor => ref _commandRuntime.CommandChains.PacketPoolCursor;
    private List<RenderPassChainGroup> _commandChainGroupScratch => _commandRuntime.CommandChains.GroupScratch;
    private List<CommandChainKey> _commandChainGroupKeyScratch => _commandRuntime.CommandChains.GroupKeyScratch;
    private Dictionary<ulong, int> _commandChainStructuralOccurrenceScratch => _commandRuntime.CommandChains.StructuralOccurrenceScratch;
    private HashSet<RenderViewKey> _commandChainViewKeyScratch => _commandRuntime.CommandChains.ViewKeyScratch;
    private Dictionary<uint, CommandChainStabilityGuardState> _commandChainStabilityGuardStates => _commandRuntime.CommandChains.StabilityGuardStates;
    private ref int _commandChainTraceDumped => ref _commandRuntime.CommandChains.TraceDumped;
    private ref long _commandChainTraceLastDumpTimestamp => ref _commandRuntime.CommandChains.TraceLastDumpTimestamp;
    private const int CommandChainZeroReuseBackoffThreshold = 1;
    private const int CommandChainZeroReuseProbeInterval = 120;
    // Correct program-scoped descriptor identity can split a large imported scene into
    // thousands of compatible packets. The Sponza desktop view reaches roughly 2.2K
    // packets while camera motion refreshes directional shadows. A 1K ceiling left
    // more than half of those draws inline in the primary, then the 2K cache ceiling
    // evicted the working set on every frame. Keep the bound finite, but large enough
    // for the traced desktop + grouped-cascade workload.
    private const int MaxCommandChainsPerSchedule = 4096;
    // Camera and occlusion changes can alternate between two valid schedules. Retain
    // both working sets so the bounded LRU does not destroy one while recording the
    // other, then immediately rebuild the evicted secondary command buffers.
    private const int MaxCachedScheduledCommandChainsPerFrameSlot = MaxCommandChainsPerSchedule * 2;
    internal const int MinMeshDrawsPerRenderPacket = 10;
    internal const int MaxMeshDrawsPerRenderPacket = 64;
    // Directional-cascade membership changes continuously while the camera moves.
    // Stable buckets limit that churn to one portion of the caster set, while this
    // deliberately smaller packet cap bounds how many otherwise-stable draws a
    // membership change can re-record. Per-caster packets made scheduling and
    // dependency publication O(casters) and were substantially slower in motion.
    internal const int MaxShadowMeshDrawsPerRenderPacket = 16;
    private const int ShadowCommandChainBucketCount = 8;

    /// <summary>
    /// Assigns a shadow caster to a stable runtime bucket. Membership changes can
    /// only disturb packet boundaries inside that bucket instead of shifting the
    /// source ranges and duplicate ordinals of every later caster.
    /// </summary>
    internal static int ResolveShadowCommandChainBucket(MeshDrawOp draw)
    {
        XRMaterial? material =
            draw.Draw.MaterialOverride ??
            draw.Draw.Renderer.MeshRenderer.Material;
        return ResolveShadowCommandChainBucket(
            draw.Draw.Renderer.GetHashCode(),
            material?.GetHashCode() ?? 0);
    }

    internal static int ResolveShadowCommandChainBucket(
        int rendererIdentity,
        int materialIdentity)
        => (HashCode.Combine(rendererIdentity, materialIdentity) & int.MaxValue) %
           ShadowCommandChainBucketCount;

    private static bool? CommandChainsEnvironmentOverride
        => XREnvironment.GetBooleanOverride(CommandChainsEnvVar);
    private static bool CommandChainsSingleThread
        => IsCommandChainFlagEnabled(CommandChainsSingleThreadEnvVar);
    private static bool CommandChainValidationEnabled
        => IsCommandChainFlagEnabled(CommandChainValidateEnvVar);
    private static bool CommandChainTraceEnabled
        => IsCommandChainFlagEnabled(CommandChainTraceEnvVar);
    private static bool ParallelCommandChainRecordingDisabled
        => IsCommandChainFlagEnabled(DisableParallelChainRecordingEnvVar);
    private static bool CommandChainMultiQueueEnabled
        => IsCommandChainFlagEnabled(CommandChainMultiQueueEnvVar);
    private static bool CommandChainStabilityGuardEnabled
        => ResolveCommandChainStabilityGuardEnabled(
            CommandChainTraceEnabled,
            CommandChainValidationEnabled,
            CommandChainBenchmarkForceRerecord,
            IsCommandChainFlagDisabled(CommandChainStabilityGuardEnvVar));
    private static bool AllowIndependentDesktopCommandChains
        => IsCommandChainFlagEnabled(CommandChainsAllowIndependentDesktopEnvVar);
    private static bool CommandChainBenchmarkForceRerecord
        => IsCommandChainFlagEnabled(CommandChainBenchmarkForceRerecordEnvVar);

    internal static bool ResolveCommandChainNeedsRecording(
        bool benchmarkForcedRerecord,
        bool secondaryNeedsRecording,
        bool uniformSlotMappingChanged)
        => benchmarkForcedRerecord ||
           secondaryNeedsRecording ||
           uniformSlotMappingChanged;

    internal static bool ResolveCommandChainStabilityGuardEnabled(
        bool traceEnabled,
        bool validationEnabled,
        bool benchmarkForcedRerecord,
        bool explicitlyDisabled)
        => !traceEnabled &&
           !validationEnabled &&
           !benchmarkForcedRerecord &&
           !explicitlyDisabled;
    private bool CommandChainsRequested =>
        !FreshSerialRecordingEnabled &&
        ResolveCommandChainsRequested(
            RuntimeRenderingHostServices.Settings.VulkanCommandRecordingMode,
            CommandChainsEnvironmentOverride);
    private bool FreshSerialRecordingEnabled =>
        RuntimeRenderingHostServices.Settings.VulkanCommandRecordingMode ==
        EVulkanCommandRecordingMode.FreshSerial;
    private static bool CommandChainsExplicitlyRequested =>
        CommandChainsEnvironmentOverride == true;
    private bool CommandChainsEnabledForCurrentRecording =>
        !FreshSerialRecordingEnabled &&
        ((CommandChainsRequested && !ShouldBypassCommandChainsForOpenXrIndependentDesktop) ||
         ShouldUseCommandChainsForOpenXrIndependentDesktop);

    private static bool ShouldBypassCommandChainsForOpenXrIndependentDesktop =>
        ShouldUseOpenXrIndependentDesktopCommandChainPolicy &&
        !AllowIndependentDesktopCommandChains;

    private static bool ShouldUseCommandChainsForOpenXrIndependentDesktop
    {
        get
        {
            return ShouldUseOpenXrIndependentDesktopCommandChainPolicy &&
                   AllowIndependentDesktopCommandChains;
        }
    }

    private static bool ShouldUseOpenXrIndependentDesktopCommandChainPolicy
    {
        get
        {
            IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
            IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
            return frameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan &&
                   presentation.IsInVR &&
                   presentation.IsOpenXRActive &&
                   presentation.RenderWindowsWhileInVR &&
                   presentation.VrMirrorMode == EVrMirrorMode.FullIndependentRender &&
                   !presentation.VrMirrorComposeFromEyeTextures;
        }
    }


    private static bool IsCommandChainFlagEnabled(string name)
        => XREnvironment.IsEnabled(name);

    internal static bool ResolveCommandChainsRequested(
        EVulkanCommandRecordingMode mode,
        bool? environmentOverride)
    {
        if (environmentOverride.HasValue)
            return environmentOverride.Value;

        return mode is EVulkanCommandRecordingMode.Auto or EVulkanCommandRecordingMode.Hybrid;
    }

    private static bool IsCommandChainFlagDisabled(string name)
        => XREnvironment.GetBooleanOverride(name) == false;

    internal static int ResolveCommandChainRecordingWorkerIndex(
        in CommandChainKey chainKey,
        int workerCount)
    {
        if (workerCount <= 1)
            return 0;

        // Preparation has already reduced the chain to immutable, backend-ready
        // records. Hash the stable chain key rather than the VkMeshRenderer owner
        // so independent chains from one renderer may record concurrently while
        // a chain retains the same worker-owned pool across dirty subsets.
        FrameOpSignatureHasher hash = new();
        hash.Add(chainKey.FrameSlot);
        hash.Add(chainKey.ViewKey.PipelineIdentity);
        hash.Add(chainKey.ViewKey.ViewportIdentity);
        hash.Add(chainKey.ViewKey.ViewIndex);
        hash.Add((int)chainKey.ViewKey.Kind);
        hash.Add(chainKey.ViewKey.LightIdentity);
        hash.Add(chainKey.ViewKey.CascadeIndex);
        hash.Add(chainKey.PassIndex);
        hash.Add(chainKey.TargetIdentity);
        hash.Add(chainKey.DynamicOverlay);
        hash.Add(chainKey.ChainOrdinal);
        return unchecked((int)(hash.ToHash() % (uint)workerCount));
    }

    internal static bool TryResolveCommandChainRecordingRendererFamily(
        FrameOp[] ops,
        CommandChain chain,
        int frameDataSlot,
        EVulkanMeshFrameDataStreamKind streamKind,
        out VulkanMeshFrameDataRendererFamilyKey rendererFamily)
    {
        rendererFamily = default;
        if (chain.SourceStartIndex < 0 ||
            chain.SourceCount <= 0 ||
            chain.SourceStartIndex > ops.Length - chain.SourceCount ||
            ops[chain.SourceStartIndex] is not MeshDrawOp firstDraw)
        {
            return false;
        }

        VulkanMeshFrameDataFamilyKey firstFamily = VulkanMeshFrameDataFamilyKey.From(
            frameDataSlot,
            streamKind,
            firstDraw.Context,
            firstDraw.Draw);
        rendererFamily = new VulkanMeshFrameDataRendererFamilyKey(firstDraw.Draw.Renderer, firstFamily);

        VulkanMeshFrameDataRendererFamilyKeyComparer comparer =
            VulkanMeshFrameDataRendererFamilyKeyComparer.Instance;
        for (int drawIndex = 1; drawIndex < chain.SourceCount; drawIndex++)
        {
            if (ops[chain.SourceStartIndex + drawIndex] is not MeshDrawOp draw)
                return false;

            VulkanMeshFrameDataFamilyKey family = VulkanMeshFrameDataFamilyKey.From(
                frameDataSlot,
                streamKind,
                draw.Context,
                draw.Draw);
            VulkanMeshFrameDataRendererFamilyKey candidate = new(draw.Draw.Renderer, family);
            if (!comparer.Equals(rendererFamily, candidate))
                return false;
        }

        return true;
    }

    private static bool ContainsQueryFrameOp(FrameOperationStream ops)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops.GetHeader(i).OpCode == EVulkanPrimaryPlanNodeKind.Query)
                return true;
        }

        return false;
    }

}
