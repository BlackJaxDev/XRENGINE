using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Rendering;

namespace XREngine.Rendering.DLSS
{
    // Note: EDlssQualityMode is now defined in XREngine namespace (XREngine.Data project)
    // for use in the cascading settings system.

    /// <summary>
    /// Lightweight helper that wires engine viewport resolution to NVIDIA DLSS expectations.
    /// The manager only adjusts internal render resolution and leaves the actual DLSS execution
    /// to the native runtime present in the host environment.
    /// </summary>
    public static partial class NvidiaDlssManager
    {
        private const float MinScale = 0.25f;
        private const float MaxScale = 1.0f;
        private const string MissingRuntimeRecoveryMessage =
            "Download the official NVIDIA Streamline/DLSS SDK, copy the production x64 sl.*.dll and nvngx_*.dll files " +
            "plus any *.license.txt files into ThirdParty/NVIDIA/SDK/win-x64/, then rebuild so they are copied next to the editor executable. " +
            "Do not download NVIDIA runtime DLLs from third-party DLL sites or commit NVIDIA SDK binaries.";
        private const string MissingRuntimeMessage = "DLSS runtime is incomplete: required NVIDIA Streamline/DLSS DLLs are missing. " + MissingRuntimeRecoveryMessage;
        private const float UltraPerformanceScale = 0.33f;
        private const float PerformanceScale = 0.5f;
        private const float BalancedScale = 0.58f;
        private const float QualityScale = 0.66f;
        private const float UltraQualityScale = 0.77f;
        private static readonly string[] RequiredRuntimeLibraryNames = ["sl.interposer.dll", "sl.common.dll", "sl.dlss.dll", "nvngx_dlss.dll"];
        private static readonly string[] RequiredFrameGenerationRuntimeLibraryNames =
        [
            "sl.interposer.dll",
            "sl.common.dll",
            "sl.dlss_g.dll",
            "sl.reflex.dll",
            "sl.pcl.dll",
            "nvngx_dlssg.dll"
        ];

        private static bool _probed;
        private static bool _cachedIsSupported;
        private static bool _lastIsNvidia;
        private static bool _lastIsVulkan;
        private static int _lastNativeFailureGeneration;
        private static string? _lastBridgeFingerprint;
        private static string? _lastError;
        private static bool _runtimeDllsProbed;
        private static bool _cachedRuntimeDllsAvailable;
        private static string? _runtimeDllsUnavailableReason;
        private static bool _frameGenerationRuntimeDllsProbed;
        private static bool _cachedFrameGenerationRuntimeDllsAvailable;
        private static string? _frameGenerationRuntimeDllsUnavailableReason;

        public static bool IsSupported
        {
            get
            {
                EnsureDetected();
                return _cachedIsSupported;
            }
        }

        public static string? LastError
        {
            get
            {
                EnsureDetected();
                return _lastError;
            }
        }

        public static bool RequiredRuntimeDllsAvailable
        {
            get
            {
                EnsureRuntimeDllsProbed();
                return _cachedRuntimeDllsAvailable;
            }
        }

        public static string RequiredRuntimeDllsUnavailableReason
        {
            get
            {
                EnsureRuntimeDllsProbed();
                return _runtimeDllsUnavailableReason ?? MissingRuntimeMessage;
            }
        }

        public static bool FrameGenerationRuntimeDllsAvailable
        {
            get
            {
                EnsureFrameGenerationRuntimeDllsProbed();
                return _cachedFrameGenerationRuntimeDllsAvailable;
            }
        }

        public static string FrameGenerationRuntimeDllsUnavailableReason
        {
            get
            {
                EnsureFrameGenerationRuntimeDllsProbed();
                return _frameGenerationRuntimeDllsUnavailableReason
                    ?? "DLSS frame generation runtime is incomplete: required NVIDIA Streamline/DLSS-G DLLs are missing. " + MissingRuntimeRecoveryMessage;
            }
        }

        public static bool FrameGenerationAvailable
        {
            get
            {
                EnsureDetected();
                if (!_cachedIsSupported)
                    return false;

                return Native.IsFrameGenerationAvailable(out _);
            }
        }

        public static string FrameGenerationUnavailableReason
        {
            get
            {
                EnsureDetected();
                if (!_cachedIsSupported)
                    return _lastError ?? "NVIDIA DLSS is not available.";

                return Native.IsFrameGenerationAvailable(out string? reason)
                    ? string.Empty
                    : reason ?? Native.LastError ?? "NVIDIA DLSS frame generation is not available.";
            }
        }

        /// <summary>
        /// Maximum number of interpolated frames the active Streamline runtime reports it can
        /// generate between engine-rendered frames. Zero means no active state has been reported yet.
        /// </summary>
        public static uint FrameGenerationMaximumFramesToGenerate
            => Native.FrameGenerationMaximumFramesToGenerate;

        /// <summary>
        /// Number of interpolated frames Streamline reports as actually presented since its previous
        /// state query. This is presentation telemetry, not an engine render-frame counter.
        /// </summary>
        public static uint FrameGenerationFramesActuallyPresented
            => Native.FrameGenerationFramesActuallyPresented;

        /// <summary>
        /// Running total of interpolated frames reported as actually presented by Streamline.
        /// </summary>
        public static ulong FrameGenerationFramesActuallyPresentedTotal
            => Native.FrameGenerationFramesActuallyPresentedTotal;

        private static void EnsureRuntimeDllsProbed()
        {
            if (_runtimeDllsProbed)
                return;

            List<string>? missingLibraries = null;
            for (int i = 0; i < RequiredRuntimeLibraryNames.Length; i++)
            {
                string libraryName = RequiredRuntimeLibraryNames[i];
                if (TryProbeRuntimeLibrary(libraryName))
                    continue;

                missingLibraries ??= [];
                missingLibraries.Add(libraryName);
            }

            if (missingLibraries is null)
            {
                _cachedRuntimeDllsAvailable = true;
                _runtimeDllsUnavailableReason = null;
            }
            else
            {
                _cachedRuntimeDllsAvailable = false;
                _runtimeDllsUnavailableReason = BuildRuntimeDllsUnavailableReason(missingLibraries);
            }

            _runtimeDllsProbed = true;
        }

        private static string BuildRuntimeDllsUnavailableReason(IReadOnlyList<string> missingLibraries)
            => $"DLSS runtime is incomplete: missing {string.Join(", ", missingLibraries)}. {MissingRuntimeRecoveryMessage}";

        private static void EnsureFrameGenerationRuntimeDllsProbed()
        {
            if (_frameGenerationRuntimeDllsProbed)
                return;

            List<string>? missingLibraries = null;
            for (int i = 0; i < RequiredFrameGenerationRuntimeLibraryNames.Length; i++)
            {
                string libraryName = RequiredFrameGenerationRuntimeLibraryNames[i];
                if (TryProbeRuntimeLibrary(libraryName))
                    continue;

                missingLibraries ??= [];
                missingLibraries.Add(libraryName);
            }

            if (missingLibraries is null)
            {
                _cachedFrameGenerationRuntimeDllsAvailable = true;
                _frameGenerationRuntimeDllsUnavailableReason = null;
            }
            else
            {
                _cachedFrameGenerationRuntimeDllsAvailable = false;
                _frameGenerationRuntimeDllsUnavailableReason =
                    $"DLSS frame generation runtime is incomplete: missing {string.Join(", ", missingLibraries)}. {MissingRuntimeRecoveryMessage}";
            }

            _frameGenerationRuntimeDllsProbed = true;
        }

        private static void EnsureDetected()
        {
            // Vulkan sets IsNVIDIA during adapter selection; OpenGL sets it when the context is created.
            // The original static ctor probe could run before either of those, permanently caching false.
            int nativeFailureGeneration = Native.BridgeFailureGeneration;
            if (_probed &&
                _lastIsNvidia == RuntimeEngine.Rendering.State.IsNVIDIA &&
                _lastIsVulkan == RuntimeEngine.Rendering.State.IsVulkan &&
                _lastNativeFailureGeneration == nativeFailureGeneration &&
                string.Equals(_lastBridgeFingerprint, RuntimeEngine.Rendering.VulkanUpscaleBridgeSnapshot.Fingerprint, StringComparison.Ordinal))
                return;

            DetectSupport();
        }

        private static void DetectSupport()
        {
            _probed = true;
            _lastIsNvidia = RuntimeEngine.Rendering.State.IsNVIDIA;
            _lastIsVulkan = RuntimeEngine.Rendering.State.IsVulkan;
            _lastNativeFailureGeneration = Native.BridgeFailureGeneration;
            _lastBridgeFingerprint = RuntimeEngine.Rendering.VulkanUpscaleBridgeSnapshot.Fingerprint;
            _lastError = null;

            bool usingVulkan = RuntimeEngine.Rendering.State.IsVulkan;
            string? bridgeFailure = null;
            bool usingBridge = !usingVulkan && TryIsBridgeCapable(out bridgeFailure);
            if (!usingVulkan && !usingBridge)
            {
                _lastError = bridgeFailure ?? "DLSS requires a Vulkan renderer or the OpenGL->Vulkan bridge.";
                _cachedIsSupported = false;
                return;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _lastError = "DLSS requires Windows.";
                _cachedIsSupported = false;
                return;
            }

            if (!RuntimeEngine.Rendering.State.IsNVIDIA)
            {
                _lastError = "No NVIDIA GPU detected.";
                _cachedIsSupported = false;
                return;
            }

            if (usingBridge && Native.HasTerminalBridgeFailure)
            {
                _lastError = Native.LastError ?? "Streamline bridge runtime is unavailable.";
                _cachedIsSupported = false;
                return;
            }

            // Probe for the Streamline/DLSS runtime without taking a hard dependency on it.
            _cachedIsSupported = RequiredRuntimeDllsAvailable;
            if (!_cachedIsSupported && _lastError is null)
                _lastError = RequiredRuntimeDllsUnavailableReason;
        }

        private static bool TryIsBridgeCapable(out string? failureReason)
        {
            var snapshot = RuntimeEngine.Rendering.VulkanUpscaleBridgeSnapshot;
            failureReason = null;

            if (!RuntimeEngine.Rendering.VulkanUpscaleBridgeRequested || !snapshot.EnvironmentEnabled)
            {
                failureReason = $"{RuntimeEngine.Rendering.VulkanUpscaleBridgeEnvVar}=0 disabled the OpenGL->Vulkan upscale bridge (clear it or set it to 1 to re-enable)";
                return false;
            }

            if (!snapshot.HasRequiredOpenGlInterop)
            {
                failureReason = "required OpenGL bridge interop extensions are unavailable";
                return false;
            }

            if (!snapshot.VulkanProbeSucceeded)
            {
                failureReason = snapshot.ProbeFailureReason ?? "Vulkan bridge probe failed";
                return false;
            }

            if (!snapshot.HasRequiredVulkanInterop)
            {
                failureReason = "required Vulkan bridge interop extensions are unavailable";
                return false;
            }

            if (snapshot.SamePhysicalGpu == false)
            {
                failureReason = snapshot.GpuIdentityReason ?? "OpenGL and Vulkan resolved to different physical GPUs";
                return false;
            }

            return true;
        }

        private static bool TryProbeRuntimeLibrary(string libraryName)
        {
            if (!TryLoadRuntimeLibrary(libraryName, out nint handle))
                return false;

            NativeLibrary.Free(handle);
            return true;
        }

        private static bool TryLoadRuntimeLibrary(string libraryName, out nint handle)
        {
            string runtimePath = Path.Combine(AppContext.BaseDirectory, libraryName);
            if (File.Exists(runtimePath) && TryLoadNativeLibrary(runtimePath, out handle))
                return true;

            return TryLoadNativeLibrary(libraryName, out handle);
        }

        private static bool TryLoadNativeLibrary(string pathOrLibraryName, out nint handle)
        {
            try
            {
                if (NativeLibrary.TryLoad(pathOrLibraryName, out handle) && handle != IntPtr.Zero)
                    return true;
            }
            catch
            {
            }

            handle = IntPtr.Zero;
            return false;
        }

        public static void ApplyToViewport(XRViewport viewport, object? settings = null)
        {
            if (viewport is null)
                return;

            EnsureDetected();

            float targetScale = Math.Clamp(ComputeScale(), MinScale, MaxScale);

            int targetWidth = (int)(viewport.Width * targetScale);
            int targetHeight = (int)(viewport.Height * targetScale);

            viewport.SetInternalResolution(targetWidth, targetHeight, true);
        }

        public static void ResetViewport(XRViewport viewport)
        {
            if (viewport is null)
                return;

            viewport.SetInternalResolution(viewport.Width, viewport.Height, true);
        }

        internal static float GetRecommendedRenderScale(object? settings = null)
            => ComputeScale();

        internal static bool IsFrameGenerationRequested
            => RuntimeEngine.EffectiveSettings.EnableNvidiaDlssFrameGeneration
            && RuntimeEngine.EffectiveSettings.NvidiaDlssFrameGenerationMode != ENvidiaDlssFrameGenerationMode.Off;

        internal static ENvidiaDlssFrameGenerationMode ResolveFrameGenerationMode()
            => !IsFrameGenerationRequested
                ? ENvidiaDlssFrameGenerationMode.Off
                : RuntimeEngine.EffectiveSettings.NvidiaDlssFrameGenerationMode switch
            {
                ENvidiaDlssFrameGenerationMode.OneX => ENvidiaDlssFrameGenerationMode.OneX,
                ENvidiaDlssFrameGenerationMode.TwoX => ENvidiaDlssFrameGenerationMode.TwoX,
                ENvidiaDlssFrameGenerationMode.ThreeX => ENvidiaDlssFrameGenerationMode.ThreeX,
                _ => ENvidiaDlssFrameGenerationMode.Off,
            };

        private static float ComputeScale()
        {
            EnsureDetected();

            if (RuntimeEngine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Dlaa)
                return MaxScale;

            if (!RuntimeEngine.EffectiveSettings.EnableNvidiaDlss || !_cachedIsSupported)
                return MaxScale;

            static float ScaleForMode(EDlssQualityMode mode)
            {
                return mode switch
                {
                    EDlssQualityMode.UltraPerformance => UltraPerformanceScale,
                    EDlssQualityMode.Performance => PerformanceScale,
                    EDlssQualityMode.Balanced => BalancedScale,
                    EDlssQualityMode.Quality => QualityScale,
                    EDlssQualityMode.UltraQuality => UltraQualityScale,
                    _ => MaxScale
                };
            }

            return RuntimeEngine.Rendering.Settings.DlssQuality == EDlssQualityMode.Custom
                ? Math.Clamp(RuntimeEngine.EffectiveSettings.DlssCustomScale, MinScale, MaxScale)
                : Math.Clamp(ScaleForMode(RuntimeEngine.Rendering.Settings.DlssQuality), MinScale, MaxScale);
        }
    }
}
