using System;
using System.Collections.Concurrent;
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

        private static readonly ConcurrentDictionary<XRViewport, float> _lastViewportScale = new();

        private static bool _probed;
        private static bool _cachedIsSupported;
        private static bool _lastIsIntel;
        private static bool _lastIsVulkan;
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
                _lastIsIntel == Engine.Rendering.State.IsIntel &&
                _lastIsVulkan == Engine.Rendering.State.IsVulkan)
                return;

            DetectSupport();
        }

        private static void DetectSupport()
        {
            _probed = true;
            _lastIsIntel = Engine.Rendering.State.IsIntel;
            _lastIsVulkan = Engine.Rendering.State.IsVulkan;
            _lastError = null;

            if (!_lastIsVulkan)
            {
                _lastError = "XeSS requires a Vulkan renderer. OpenGL is not supported.";
                _cachedIsSupported = false;
                return;
            }

            if (!_lastIsIntel)
            {
                _lastError = "No Intel GPU detected.";
                _cachedIsSupported = false;
                return;
            }

            _cachedIsSupported = TryProbeLibrary("libxess.dll") || TryProbeLibrary("xess.dll");
            if (!_cachedIsSupported && _lastError is null)
                _lastError = "Neither libxess.dll nor xess.dll was found on the probing path.";
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

        private static float ComputeScale(Engine.Rendering.EngineSettings settings)
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
                return Math.Clamp(settings.XessCustomScale, MinScale, MaxScale);

            return Math.Clamp(ScaleForMode(quality), MinScale, MaxScale);
        }

        private static float Lerp(float a, float b, float t)
            => a + ((b - a) * Math.Clamp(t, 0.0f, 1.0f));
    }
}
