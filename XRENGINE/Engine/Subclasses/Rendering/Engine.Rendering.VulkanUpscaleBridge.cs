using System;
using System.Collections.Generic;
using System.Linq;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine
{
    [Flags]
    public enum EVulkanUpscaleBridgeSurfaceSet
    {
        None = 0,
        SourceColor = 1 << 0,
        SourceDepth = 1 << 1,
        SourceMotion = 1 << 2,
        OutputColor = 1 << 3,
    }

    public enum EVulkanUpscaleBridgeOwnershipMode
    {
        PerViewport,
    }

    public enum EVulkanUpscaleBridgeInteropMode
    {
        CopyResolve,
    }

    public enum EVulkanUpscaleBridgeQueueModel
    {
        Graphics,
    }

    public sealed record class VulkanUpscaleBridgeCapabilitySnapshot
    {
        public bool EnvironmentEnabled { get; init; }
        public bool WindowsOnly { get; init; }
        public bool MonoViewportOnly { get; init; }
        public bool HdrSupported { get; init; }
        public bool DlssFirst { get; init; }
        public EVulkanUpscaleBridgeQueueModel QueueModel { get; init; }
        public EVulkanUpscaleBridgeOwnershipMode OwnershipMode { get; init; }
        public EVulkanUpscaleBridgeInteropMode InteropMode { get; init; }
        public EVulkanUpscaleBridgeSurfaceSet SurfaceSet { get; init; }
        public bool HasOpenGlExternalMemory { get; init; }
        public bool HasOpenGlExternalMemoryWin32 { get; init; }
        public bool HasOpenGlSemaphore { get; init; }
        public bool HasOpenGlSemaphoreWin32 { get; init; }
        public bool VulkanProbeSucceeded { get; init; }
        public bool HasVulkanExternalMemoryImport { get; init; }
        public bool HasVulkanExternalSemaphoreImport { get; init; }
        public string? OpenGlVendor { get; init; }
        public string? OpenGlRenderer { get; init; }
        public string? VulkanDeviceName { get; init; }
        public uint VulkanVendorId { get; init; }
        public uint VulkanDeviceId { get; init; }
        public bool? SamePhysicalGpu { get; init; }
        public string? GpuIdentityReason { get; init; }
        public string? ProbeFailureReason { get; init; }
        public string Fingerprint { get; init; } = string.Empty;

        public bool HasRequiredOpenGlInterop
            => HasOpenGlExternalMemory && HasOpenGlExternalMemoryWin32 && HasOpenGlSemaphore && HasOpenGlSemaphoreWin32;

        public bool HasRequiredVulkanInterop
            => VulkanProbeSucceeded && HasVulkanExternalMemoryImport && HasVulkanExternalSemaphoreImport;
    }

    public static partial class Engine
    {
        public static partial class Rendering
        {
            public const string VulkanUpscaleBridgeEnvVar = "XRE_ENABLE_VULKAN_UPSCALE_BRIDGE";

            private static readonly object _vulkanUpscaleBridgeSnapshotSync = new();
            private static readonly object _vulkanUpscaleBridgeRegistrySync = new();
            private static VulkanUpscaleBridgeCapabilitySnapshot _vulkanUpscaleBridgeSnapshot = new();
            private static readonly Dictionary<XRViewport, VulkanUpscaleBridge> _vulkanUpscaleBridges = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            private static string? _lastVulkanUpscaleBridgeProbeKey;
            private static string? _lastVulkanUpscaleBridgeFingerprint;

            public static bool VulkanUpscaleBridgeRequested => IsEnvFlagEnabled(VulkanUpscaleBridgeEnvVar);
            public static bool VulkanUpscaleBridgeWindowsOnly => true;
            public static bool VulkanUpscaleBridgeMonoViewportOnly => true;
            public static bool VulkanUpscaleBridgeHdrSupported => false;
            public static bool VulkanUpscaleBridgeDlssFirst => true;
            public static EVulkanUpscaleBridgeQueueModel VulkanUpscaleBridgeQueueModel => EVulkanUpscaleBridgeQueueModel.Graphics;
            public static EVulkanUpscaleBridgeOwnershipMode VulkanUpscaleBridgeOwnershipMode => EVulkanUpscaleBridgeOwnershipMode.PerViewport;
            public static EVulkanUpscaleBridgeInteropMode VulkanUpscaleBridgeInteropMode => EVulkanUpscaleBridgeInteropMode.CopyResolve;
            public static EVulkanUpscaleBridgeSurfaceSet VulkanUpscaleBridgeSurfaceSet =>
                EVulkanUpscaleBridgeSurfaceSet.SourceColor |
                EVulkanUpscaleBridgeSurfaceSet.SourceDepth |
                EVulkanUpscaleBridgeSurfaceSet.SourceMotion |
                EVulkanUpscaleBridgeSurfaceSet.OutputColor;

            public static VulkanUpscaleBridgeCapabilitySnapshot VulkanUpscaleBridgeSnapshot
            {
                get
                {
                    lock (_vulkanUpscaleBridgeSnapshotSync)
                        return _vulkanUpscaleBridgeSnapshot;
                }
            }

            public static VulkanUpscaleBridge? GetVulkanUpscaleBridge(XRViewport? viewport)
            {
                if (viewport is null)
                    return null;

                lock (_vulkanUpscaleBridgeRegistrySync)
                    return _vulkanUpscaleBridges.TryGetValue(viewport, out VulkanUpscaleBridge? bridge)
                        ? bridge
                        : null;
            }

            public static EVulkanUpscaleBridgeState PrepareVulkanUpscaleBridgeForFrame(XRViewport? viewport, XRRenderPipelineInstance? pipeline)
            {
                if (viewport is null || pipeline is null)
                    return EVulkanUpscaleBridgeState.Disabled;

                if (!VulkanUpscaleBridgeRequested)
                {
                    ReleaseVulkanUpscaleBridge(viewport, "experimental bridge disabled");
                    return EVulkanUpscaleBridgeState.Disabled;
                }

                VulkanUpscaleBridge bridge;
                lock (_vulkanUpscaleBridgeRegistrySync)
                {
                    if (!_vulkanUpscaleBridges.TryGetValue(viewport, out bridge!))
                    {
                        bridge = new VulkanUpscaleBridge(viewport);
                        _vulkanUpscaleBridges.Add(viewport, bridge);
                    }
                }

                return bridge.PrepareForFrame(pipeline, VulkanUpscaleBridgeSnapshot);
            }

            public static void ReleaseVulkanUpscaleBridge(XRViewport? viewport, string reason)
            {
                if (viewport is null)
                    return;

                VulkanUpscaleBridge? bridge;
                lock (_vulkanUpscaleBridgeRegistrySync)
                {
                    if (!_vulkanUpscaleBridges.Remove(viewport, out bridge))
                        return;
                }

                bridge.Destroy(reason);
            }

            public static void InvalidateAllVulkanUpscaleBridges(string reason)
            {
                VulkanUpscaleBridge[] bridges;
                lock (_vulkanUpscaleBridgeRegistrySync)
                    bridges = [.. _vulkanUpscaleBridges.Values];

                foreach (VulkanUpscaleBridge bridge in bridges)
                    bridge.MarkNeedsRecreate(reason);
            }

            public static void NotifyVulkanUpscaleBridgeVendorSelectionChanged(string reason)
            {
                VulkanUpscaleBridge[] bridges;
                lock (_vulkanUpscaleBridgeRegistrySync)
                    bridges = [.. _vulkanUpscaleBridges.Values];

                foreach (VulkanUpscaleBridge bridge in bridges)
                    bridge.NotifyVendorSelectionChanged(reason);
            }

            public static void NotifyVulkanUpscaleBridgeCapabilitySnapshotChanged(string reason)
            {
                VulkanUpscaleBridge[] bridges;
                lock (_vulkanUpscaleBridgeRegistrySync)
                    bridges = [.. _vulkanUpscaleBridges.Values];

                foreach (VulkanUpscaleBridge bridge in bridges)
                    bridge.NotifyCapabilitySnapshotChanged(reason);
            }

            private static void ReleaseAllVulkanUpscaleBridges(string reason)
            {
                KeyValuePair<XRViewport, VulkanUpscaleBridge>[] bridges;
                lock (_vulkanUpscaleBridgeRegistrySync)
                {
                    if (_vulkanUpscaleBridges.Count == 0)
                        return;

                    bridges = [.. _vulkanUpscaleBridges];
                    _vulkanUpscaleBridges.Clear();
                }

                foreach (KeyValuePair<XRViewport, VulkanUpscaleBridge> bridge in bridges)
                    bridge.Value.Destroy(reason);
            }

            internal static void RefreshVulkanUpscaleBridgeCapabilitySnapshot(OpenGLRenderer renderer)
            {
                if (renderer is null)
                    return;

                if (!VulkanUpscaleBridgeRequested)
                {
                    lock (_vulkanUpscaleBridgeSnapshotSync)
                    {
                        _vulkanUpscaleBridgeSnapshot = new();
                        _lastVulkanUpscaleBridgeProbeKey = null;
                    }

                    ReleaseAllVulkanUpscaleBridges("experimental bridge disabled");
                    return;
                }

                string probeKey = string.Join(
                    "|",
                    State.OpenGLVendor ?? string.Empty,
                    State.OpenGLRendererName ?? string.Empty,
                    renderer.EXTMemoryObject is not null ? "1" : "0",
                    renderer.EXTMemoryObjectWin32 is not null ? "1" : "0",
                    renderer.EXTSemaphore is not null ? "1" : "0",
                    renderer.EXTSemaphoreWin32 is not null ? "1" : "0");

                lock (_vulkanUpscaleBridgeSnapshotSync)
                {
                    if (string.Equals(_lastVulkanUpscaleBridgeProbeKey, probeKey, StringComparison.Ordinal))
                    {
                        LogVulkanUpscaleBridgeCapabilitySnapshot();
                        return;
                    }
                }

                VulkanUpscaleBridgeProbeResult probe = VulkanUpscaleBridgeProbe.Probe(
                    State.OpenGLVendor,
                    State.OpenGLRendererName);

                var snapshot = new VulkanUpscaleBridgeCapabilitySnapshot
                {
                    EnvironmentEnabled = VulkanUpscaleBridgeRequested,
                    WindowsOnly = VulkanUpscaleBridgeWindowsOnly,
                    MonoViewportOnly = VulkanUpscaleBridgeMonoViewportOnly,
                    HdrSupported = VulkanUpscaleBridgeHdrSupported,
                    DlssFirst = VulkanUpscaleBridgeDlssFirst,
                    QueueModel = VulkanUpscaleBridgeQueueModel,
                    OwnershipMode = VulkanUpscaleBridgeOwnershipMode,
                    InteropMode = VulkanUpscaleBridgeInteropMode,
                    SurfaceSet = VulkanUpscaleBridgeSurfaceSet,
                    HasOpenGlExternalMemory = renderer.EXTMemoryObject is not null,
                    HasOpenGlExternalMemoryWin32 = renderer.EXTMemoryObjectWin32 is not null,
                    HasOpenGlSemaphore = renderer.EXTSemaphore is not null,
                    HasOpenGlSemaphoreWin32 = renderer.EXTSemaphoreWin32 is not null,
                    VulkanProbeSucceeded = probe.ProbeSucceeded,
                    HasVulkanExternalMemoryImport = probe.HasVulkanExternalMemoryImport,
                    HasVulkanExternalSemaphoreImport = probe.HasVulkanExternalSemaphoreImport,
                    OpenGlVendor = State.OpenGLVendor,
                    OpenGlRenderer = State.OpenGLRendererName,
                    VulkanDeviceName = probe.SelectedDeviceName,
                    VulkanVendorId = probe.SelectedVendorId,
                    VulkanDeviceId = probe.SelectedDeviceId,
                    SamePhysicalGpu = probe.SamePhysicalGpu,
                    GpuIdentityReason = probe.GpuIdentityReason,
                    ProbeFailureReason = probe.ProbeFailureReason,
                };

                snapshot = snapshot with
                {
                    Fingerprint = BuildVulkanUpscaleBridgeFingerprint(snapshot)
                };

                lock (_vulkanUpscaleBridgeSnapshotSync)
                {
                    _lastVulkanUpscaleBridgeProbeKey = probeKey;
                    _vulkanUpscaleBridgeSnapshot = snapshot;
                }

                LogVulkanUpscaleBridgeCapabilitySnapshot(force: true);
                NotifyVulkanUpscaleBridgeCapabilitySnapshotChanged("bridge capability snapshot changed");
            }

            public static void LogVulkanUpscaleBridgeCapabilitySnapshot(bool force = false)
            {
                VulkanUpscaleBridgeCapabilitySnapshot snapshot = VulkanUpscaleBridgeSnapshot;
                if (string.IsNullOrWhiteSpace(snapshot.Fingerprint))
                    return;

                lock (_vulkanUpscaleBridgeSnapshotSync)
                {
                    if (!force && string.Equals(_lastVulkanUpscaleBridgeFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
                        return;

                    _lastVulkanUpscaleBridgeFingerprint = snapshot.Fingerprint;
                }

                XREngine.Debug.Rendering(
                    "[RenderDiag] VulkanUpscaleBridge Snapshot: env={0} scope=Windows:{1},MonoViewport:{2},HDR:{3},VendorPriority={4} queue={5} ownership={6} interop={7} surfaces={8} GL(extMem={9},extMemWin32={10},sem={11},semWin32={12},vendor='{13}',renderer='{14}') Vulkan(probe={15},extMemImport={16},semImport={17},device='{18}',vendorId=0x{19:X4},deviceId=0x{20:X4},sameGpu={21},reason='{22}')",
                    snapshot.EnvironmentEnabled ? 1 : 0,
                    snapshot.WindowsOnly ? 1 : 0,
                    snapshot.MonoViewportOnly ? 1 : 0,
                    snapshot.HdrSupported ? 1 : 0,
                    snapshot.DlssFirst ? "DLSS-first" : "XeSS-first",
                    snapshot.QueueModel,
                    snapshot.OwnershipMode,
                    snapshot.InteropMode,
                    snapshot.SurfaceSet,
                    snapshot.HasOpenGlExternalMemory ? 1 : 0,
                    snapshot.HasOpenGlExternalMemoryWin32 ? 1 : 0,
                    snapshot.HasOpenGlSemaphore ? 1 : 0,
                    snapshot.HasOpenGlSemaphoreWin32 ? 1 : 0,
                    snapshot.OpenGlVendor ?? "<unknown>",
                    snapshot.OpenGlRenderer ?? "<unknown>",
                    snapshot.VulkanProbeSucceeded ? 1 : 0,
                    snapshot.HasVulkanExternalMemoryImport ? 1 : 0,
                    snapshot.HasVulkanExternalSemaphoreImport ? 1 : 0,
                    snapshot.VulkanDeviceName ?? "<unknown>",
                    snapshot.VulkanVendorId,
                    snapshot.VulkanDeviceId,
                    snapshot.SamePhysicalGpu is null ? "unknown" : snapshot.SamePhysicalGpu.Value ? "yes" : "no",
                    snapshot.GpuIdentityReason ?? snapshot.ProbeFailureReason ?? "<none>");
            }

            public static string DescribeVulkanUpscaleBridgeUnavailability(XRViewport? viewport, bool hdrRequested)
            {
                VulkanUpscaleBridgeCapabilitySnapshot snapshot = VulkanUpscaleBridgeSnapshot;
                List<string> reasons = new(12);

                if (!snapshot.EnvironmentEnabled)
                    reasons.Add($"experimental bridge disabled (set {VulkanUpscaleBridgeEnvVar}=1 to opt in)");

                if (snapshot.WindowsOnly && !OperatingSystem.IsWindows())
                    reasons.Add("bridge MVP is Windows only");

                if (viewport?.Window?.Renderer is not OpenGLRenderer)
                    reasons.Add("bridge MVP only applies to OpenGL windows");

                if (snapshot.MonoViewportOnly && CountWindowViewports(viewport?.Window) != 1)
                    reasons.Add("bridge MVP only supports a single active viewport in the target window");

                if (IsStereoPipeline(viewport?.RenderPipeline))
                    reasons.Add("bridge MVP excludes stereo/XR render pipelines");

                if (!snapshot.HdrSupported && hdrRequested)
                    reasons.Add("bridge MVP is SDR only");

                if (!snapshot.HasOpenGlExternalMemory)
                    reasons.Add("GL_EXT_memory_object is unavailable");
                if (!snapshot.HasOpenGlExternalMemoryWin32)
                    reasons.Add("GL_EXT_memory_object_win32 is unavailable");
                if (!snapshot.HasOpenGlSemaphore)
                    reasons.Add("GL_EXT_semaphore is unavailable");
                if (!snapshot.HasOpenGlSemaphoreWin32)
                    reasons.Add("GL_EXT_semaphore_win32 is unavailable");

                if (!snapshot.VulkanProbeSucceeded)
                {
                    reasons.Add(snapshot.ProbeFailureReason ?? "Vulkan bridge probe failed");
                }
                else
                {
                    if (!snapshot.HasVulkanExternalMemoryImport)
                        reasons.Add("Vulkan external-memory image import extensions are unavailable");
                    if (!snapshot.HasVulkanExternalSemaphoreImport)
                        reasons.Add("Vulkan external-semaphore import extensions are unavailable");
                    if (snapshot.SamePhysicalGpu == false)
                        reasons.Add(snapshot.GpuIdentityReason ?? "OpenGL and Vulkan would land on different physical GPUs");
                }

                if (GetVulkanUpscaleBridge(viewport) is VulkanUpscaleBridge bridge)
                {
                    if (bridge.State is EVulkanUpscaleBridgeState.Initializing or EVulkanUpscaleBridgeState.NeedsRecreate)
                    {
                        reasons.Add(
                            !string.IsNullOrWhiteSpace(bridge.PendingRecreateReason)
                                ? $"bridge {bridge.State.ToString().ToLowerInvariant()}: {bridge.PendingRecreateReason}"
                                : $"bridge state is {bridge.State}");
                    }
                    else if (bridge.State == EVulkanUpscaleBridgeState.Faulted &&
                        !string.IsNullOrWhiteSpace(bridge.LastStateReason))
                    {
                        reasons.Add($"bridge faulted: {bridge.LastStateReason}");
                    }
                }

                reasons.Add("bridge vendor dispatch also requires a compatible DLSS or XeSS runtime plus per-vendor support checks");
                return string.Join("; ", reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason)));
            }

            private static bool IsEnvFlagEnabled(string name)
            {
                string? raw = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(raw))
                    return false;

                return string.Equals(raw, "1", StringComparison.Ordinal) ||
                    bool.TryParse(raw, out bool enabled) && enabled;
            }

            private static string BuildVulkanUpscaleBridgeFingerprint(VulkanUpscaleBridgeCapabilitySnapshot snapshot)
                => string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "BridgePhase4|env={0}|scope={1}/{2}/{3}/{4}|queue={5}|ownership={6}|interop={7}|surfaces={8}|gl={9}/{10}/{11}/{12}|vk={13}/{14}/{15}|gpu={16}|glVendor={17}|glRenderer={18}|vkDevice={19}|vkIds={20:X4}:{21:X4}",
                    snapshot.EnvironmentEnabled ? 1 : 0,
                    snapshot.WindowsOnly ? 1 : 0,
                    snapshot.MonoViewportOnly ? 1 : 0,
                    snapshot.HdrSupported ? 1 : 0,
                    snapshot.DlssFirst ? 1 : 0,
                    snapshot.QueueModel,
                    snapshot.OwnershipMode,
                    snapshot.InteropMode,
                    snapshot.SurfaceSet,
                    snapshot.HasOpenGlExternalMemory ? 1 : 0,
                    snapshot.HasOpenGlExternalMemoryWin32 ? 1 : 0,
                    snapshot.HasOpenGlSemaphore ? 1 : 0,
                    snapshot.HasOpenGlSemaphoreWin32 ? 1 : 0,
                    snapshot.VulkanProbeSucceeded ? 1 : 0,
                    snapshot.HasVulkanExternalMemoryImport ? 1 : 0,
                    snapshot.HasVulkanExternalSemaphoreImport ? 1 : 0,
                    snapshot.SamePhysicalGpu is null ? "unknown" : snapshot.SamePhysicalGpu.Value ? "yes" : "no",
                    snapshot.OpenGlVendor ?? "<unknown>",
                    snapshot.OpenGlRenderer ?? "<unknown>",
                    snapshot.VulkanDeviceName ?? "<unknown>",
                    snapshot.VulkanVendorId,
                    snapshot.VulkanDeviceId);

            private static bool IsStereoPipeline(RenderPipeline? pipeline)
                => pipeline switch
                {
                    DefaultRenderPipeline { Stereo: true } => true,
                    DefaultRenderPipeline2 { Stereo: true } => true,
                    _ => false,
                };

            private static int CountWindowViewports(XRWindow? window)
            {
                if (window is null)
                    return 0;

                int count = 0;
                foreach (var _ in Engine.EnumerateActiveViewports(window))
                    count++;

                return count;
            }
        }
    }
}