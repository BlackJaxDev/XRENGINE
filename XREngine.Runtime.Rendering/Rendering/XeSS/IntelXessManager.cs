using System;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.XeSS
{
    /// <summary>
    /// Lightweight helper that wires engine viewport resolution to Intel XeSS expectations.
    /// The manager only adjusts internal render resolution and leaves the actual XeSS execution
    /// to the native runtime present in the host environment.
    /// </summary>
    public static partial class IntelXessManager
    {
        private const float MinScale = 0.25f;
        private const float MaxScale = 1.0f;

        private static bool _probed;
        private static bool _cachedIsSupported;
        private static bool _lastIsVulkan;
        private static string? _lastBridgeFingerprint;
        private static string? _lastError;

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

        private static void EnsureDetected()
        {
            if (_probed &&
                _lastIsVulkan == Engine.Rendering.State.IsVulkan &&
                string.Equals(_lastBridgeFingerprint, Engine.Rendering.VulkanUpscaleBridgeSnapshot.Fingerprint, StringComparison.Ordinal))
                return;

            DetectSupport();
        }

        private static void DetectSupport()
        {
            _probed = true;
            _lastIsVulkan = Engine.Rendering.State.IsVulkan;
            _lastBridgeFingerprint = Engine.Rendering.VulkanUpscaleBridgeSnapshot.Fingerprint;
            _lastError = null;

            bool usingVulkan = _lastIsVulkan;
            string? bridgeFailure = null;
            bool usingBridge = !usingVulkan && TryIsBridgeCapable(out bridgeFailure);
            if (!usingVulkan && !usingBridge)
            {
                _lastError = bridgeFailure ?? "XeSS requires a Vulkan renderer or the OpenGL->Vulkan bridge.";
                _cachedIsSupported = false;
                return;
            }

            _cachedIsSupported = TryProbeLibrary("libxess.dll") || TryProbeLibrary("xess.dll");
            if (!_cachedIsSupported && _lastError is null)
                _lastError = "Neither libxess.dll nor xess.dll was found on the probing path.";
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

        private static bool TryProbeLibrary(string libraryName)
        {
            try
            {
                if (NativeLibrary.TryLoad(libraryName, out nint handle))
                {
                    NativeLibrary.Free(handle);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }

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

        private static float ComputeScale()
        {
            EnsureDetected();

            if (!Engine.EffectiveSettings.EnableIntelXess || !_cachedIsSupported)
                return MaxScale;

            static float ScaleForMode(EXessQualityMode mode)
            {
                return mode switch
                {
                    EXessQualityMode.UltraPerformance => 1.0f / 3.0f,   // 3.0x scaling
                    EXessQualityMode.Performance => 1.0f / 2.3f,       // 2.3x scaling
                    EXessQualityMode.Balanced => 0.5f,                // 2.0x scaling
                    EXessQualityMode.Quality => 1.0f / 1.7f,          // 1.7x scaling
                    EXessQualityMode.UltraQuality => 1.0f / 1.5f,     // 1.5x scaling
                    _ => MaxScale
                };
            }

            var quality = Engine.EffectiveSettings.XessQuality;

            if (quality == EXessQualityMode.Custom)
                return Math.Clamp(Engine.Rendering.Settings.XessCustomScale, MinScale, MaxScale);

            return Math.Clamp(ScaleForMode(quality), MinScale, MaxScale);
        }
    }
}
