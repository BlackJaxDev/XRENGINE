using System;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.XeSS
{
    /// <summary>
    /// Thin P/Invoke surface for the Intel XeSS exports so the engine can enqueue native upscaling and frame generation when available.
    /// The current implementation intentionally keeps the invocation surface conservative to avoid hard dependencies when the runtime is absent.
    /// </summary>
    internal static class IntelXessNative
    {
        private const string XessLibrary = "xess.dll";
        private const string XessFrameGenLibrary = "libxess_fg.dll";

        private static bool _initialized;
        private static IntPtr _libraryHandle;
        private static bool _frameGenInitialized;
        private static IntPtr _frameGenLibraryHandle;
        private static string? _frameGenLastError;

        internal static bool IsAvailable
        {
            get
            {
                if (_initialized)
                    return _libraryHandle != IntPtr.Zero;

                _initialized = true;
                if (NativeLibrary.TryLoad(XessLibrary, out _libraryHandle))
                    return true;

                _libraryHandle = IntPtr.Zero;
                return false;
            }
        }

        internal static bool TryDispatchUpscale(
            XRViewport viewport,
            XRQuadFrameBuffer source,
            XRFrameBuffer? target,
            XRTexture? depth,
            XRTexture? motion,
            float sharpness,
            out int errorCode)
        {
            errorCode = -1;

            // Without the XeSS runtime present we cannot submit any work.
            if (!IsAvailable)
                return false;

            // Placeholder: a full XeSS submission requires the SDK context and dispatch plumbing.
            // Until the native runtime is linked, we simply report availability so callers can fall back gracefully.
            return false;
        }

        internal static bool IsFrameGenerationAvailable(out string? error)
        {
            error = null;
            if (_frameGenInitialized)
            {
                if (_frameGenLibraryHandle != IntPtr.Zero)
                    return true;

                error = _frameGenLastError;
                return false;
            }

            _frameGenInitialized = true;

            if (!OperatingSystem.IsWindows())
            {
                _frameGenLastError = "XeSS frame generation requires Windows and a DirectX 12 swap chain.";
                error = _frameGenLastError;
                return false;
            }

            if (NativeLibrary.TryLoad(XessFrameGenLibrary, out _frameGenLibraryHandle))
                return true;

            _frameGenLibraryHandle = IntPtr.Zero;
            _frameGenLastError = $"Missing {XessFrameGenLibrary}. XeSS frame generation is unavailable.";
            error = _frameGenLastError;
            return false;
        }

        internal static bool TryDispatchFrameGeneration(
            XRViewport viewport,
            XRQuadFrameBuffer source,
            XRTexture? motion,
            out int errorCode,
            out string? errorMessage)
        {
            errorCode = -1;
            errorMessage = null;

            if (!IsFrameGenerationAvailable(out errorMessage))
                return false;

            // Placeholder: frame generation requires a DirectX 12 swap chain path with tagged resources per the XeSS-FG SDK.
            errorMessage ??= "XeSS frame generation dispatch is not wired to the DirectX 12 swap chain path.";
            return false;
        }
    }
}
