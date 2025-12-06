using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Rendering;

namespace XREngine.Rendering.DLSS
{
    public enum EDlssQualityMode
    {
        UltraPerformance,
        Performance,
        Balanced,
        Quality,
        UltraQuality,
        Custom
    }

    /// <summary>
    /// Lightweight helper that wires engine viewport resolution to NVIDIA DLSS expectations.
    /// The manager only adjusts internal render resolution and leaves the actual DLSS execution
    /// to the native runtime present in the host environment.
    /// </summary>
    public static class NvidiaDlssManager
    {
        private const float MinScale = 0.25f;
        private const float MaxScale = 1.0f;

        private static readonly ConcurrentDictionary<XRViewport, float> _lastViewportScale = new();

        public static bool IsSupported { get; private set; }
        public static string? LastError { get; private set; }

        static NvidiaDlssManager()
        {
            DetectSupport();
        }

        private static void DetectSupport()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LastError = "DLSS requires Windows.";
                IsSupported = false;
                return;
            }

            if (!Engine.Rendering.State.IsNVIDIA)
            {
                LastError = "No NVIDIA GPU detected.";
                IsSupported = false;
                return;
            }

            // Probe for the Streamline/DLSS runtime without taking a hard dependency on it.
            IsSupported = TryProbeLibrary("sl.interposer.dll") || TryProbeLibrary("nvngx_dlss.dll");
            if (!IsSupported && LastError is null)
                LastError = "Neither sl.interposer.dll nor nvngx_dlss.dll was found on the probing path.";
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
                LastError = ex.Message;
            }

            return false;
        }

        public static void ApplyToViewport(XRViewport viewport, Engine.Rendering.EngineSettings settings)
        {
            if (viewport is null)
                return;

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
            if (!settings.EnableNvidiaDlss || !IsSupported)
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
