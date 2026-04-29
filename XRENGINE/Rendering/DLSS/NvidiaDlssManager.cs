using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private static readonly ConcurrentDictionary<XRViewport, float> _lastViewportScale = new();
        private static readonly string[] RequiredRuntimeLibraryNames = ["sl.interposer.dll", "nvngx_dlss.dll"];

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

        private static void EnsureDetected()
        {
            // Vulkan sets IsNVIDIA during adapter selection; OpenGL sets it when the context is created.
            // The original static ctor probe could run before either of those, permanently caching false.
            int nativeFailureGeneration = Native.BridgeFailureGeneration;
            if (_probed &&
                _lastIsNvidia == Engine.Rendering.State.IsNVIDIA &&
                _lastIsVulkan == Engine.Rendering.State.IsVulkan &&
                _lastNativeFailureGeneration == nativeFailureGeneration &&
                string.Equals(_lastBridgeFingerprint, Engine.Rendering.VulkanUpscaleBridgeSnapshot.Fingerprint, StringComparison.Ordinal))
                return;

            DetectSupport();
        }

        private static void DetectSupport()
        {
            _probed = true;
            _lastIsNvidia = Engine.Rendering.State.IsNVIDIA;
            _lastIsVulkan = Engine.Rendering.State.IsVulkan;
            _lastNativeFailureGeneration = Native.BridgeFailureGeneration;
            _lastBridgeFingerprint = Engine.Rendering.VulkanUpscaleBridgeSnapshot.Fingerprint;
            _lastError = null;

            bool usingVulkan = Engine.Rendering.State.IsVulkan;
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

            if (!Engine.Rendering.State.IsNVIDIA)
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
            var snapshot = Engine.Rendering.VulkanUpscaleBridgeSnapshot;
            failureReason = null;

            if (!Engine.Rendering.VulkanUpscaleBridgeRequested || !snapshot.EnvironmentEnabled)
            {
                failureReason = $"{Engine.Rendering.VulkanUpscaleBridgeEnvVar}=0 disabled the OpenGL->Vulkan upscale bridge (clear it or set it to 1 to re-enable)";
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
            try
            {
                if (NativeLibrary.TryLoad(libraryName, out nint handle))
                {
                    NativeLibrary.Free(handle);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        public static void ApplyToViewport(XRViewport viewport, Engine.Rendering.EngineSettings settings)
        {
            if (viewport is null)
                return;

            EnsureDetected();

            float targetScale = ComputeScale(settings);
            float currentScale = _lastViewportScale.GetOrAdd(viewport, MaxScale);

            if (settings.DlssEnableFrameSmoothing)
            {
                targetScale = Lerp(currentScale, targetScale, settings.DlssFrameSmoothingStrength);
            }

            targetScale = Math.Clamp(targetScale, MinScale, MaxScale);
            _lastViewportScale[viewport] = targetScale;

            int targetWidth = (int)(viewport.Width * targetScale);
            int targetHeight = (int)(viewport.Height * targetScale);

            viewport.SetInternalResolution(targetWidth, targetHeight, true);
        }

        public static void ResetViewport(XRViewport viewport)
        {
            if (viewport is null)
                return;

            _lastViewportScale.TryRemove(viewport, out _);
            viewport.SetInternalResolution(viewport.Width, viewport.Height, true);
        }

        internal static float GetRecommendedRenderScale(Engine.Rendering.EngineSettings settings)
            => ComputeScale(settings);

        private static float ComputeScale(Engine.Rendering.EngineSettings settings)
        {
            EnsureDetected();

            if (!Engine.EffectiveSettings.EnableNvidiaDlss || !_cachedIsSupported)
                return MaxScale;

            static float ScaleForMode(EDlssQualityMode mode)
            {
                return mode switch
                {
                    EDlssQualityMode.UltraPerformance => 0.33f,
                    EDlssQualityMode.Performance => 0.5f,
                    EDlssQualityMode.Balanced => 0.58f,
                    EDlssQualityMode.Quality => 0.66f,
                    EDlssQualityMode.UltraQuality => 0.77f,
                    _ => MaxScale
                };
            }

            if (settings.DlssQuality == EDlssQualityMode.Custom)
                return Math.Clamp(settings.DlssCustomScale, MinScale, MaxScale);

            return Math.Clamp(ScaleForMode(settings.DlssQuality), MinScale, MaxScale);
        }

        private static float Lerp(float a, float b, float t)
            => a + ((b - a) * Math.Clamp(t, 0.0f, 1.0f));
    }
}
