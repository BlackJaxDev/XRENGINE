using System;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.DLSS
{
    /// <summary>
    /// Thin P/Invoke surface for the Streamline DLSS exports so the engine can
    /// enqueue native upscaling when available.
    /// </summary>
    internal static class StreamlineNative
    {
        private const string StreamlineLibrary = "sl.interposer.dll";

        private static bool _initialized;
        private static IntPtr _libraryHandle;

        [StructLayout(LayoutKind.Sequential)]
        internal struct StreamlineViewportHandle
        {
            public ulong Identifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StreamlineDlssOptions
        {
            public uint Version;
            public uint Flags;
            public uint InputWidth;
            public uint InputHeight;
            public uint OutputWidth;
            public uint OutputHeight;
            public float Sharpness;
            public float JitterOffsetX;
            public float JitterOffsetY;
            public float MotionVectorScaleX;
            public float MotionVectorScaleY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StreamlineDlssOptimalSettings
        {
            public uint OutputWidth;
            public uint OutputHeight;
            public float RenderRatio;
            public float Sharpness;
        }

        [DllImport(StreamlineLibrary, EntryPoint = "slDLSSSetOptions", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SlDLSSSetOptions(ref StreamlineViewportHandle viewport, ref StreamlineDlssOptions options);

        [DllImport(StreamlineLibrary, EntryPoint = "slDLSSGetOptimalSettings", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SlDLSSGetOptimalSettings(ref StreamlineDlssOptions options, out StreamlineDlssOptimalSettings settings);

        internal static bool IsAvailable
        {
            get
            {
                if (_initialized)
                    return _libraryHandle != IntPtr.Zero;

                _initialized = true;
                if (NativeLibrary.TryLoad(StreamlineLibrary, out _libraryHandle))
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
            out int errorCode)
        {
            errorCode = 0;

            if (!IsAvailable)
                return false;

            // Build a lightweight option payload. Streamline will clamp values or
            // fail gracefully if we do not supply the full structure.
            StreamlineDlssOptions options = new()
            {
                Version = 1,
                InputWidth = (uint)source.Width,
                InputHeight = (uint)source.Height,
                OutputWidth = (uint)viewport.Width,
                OutputHeight = (uint)viewport.Height,
                Sharpness = Engine.Rendering.Settings.DlssSharpness,
                MotionVectorScaleX = 1.0f,
                MotionVectorScaleY = 1.0f,
            };

            // Optional: ask Streamline what it thinks the optimal setup is so we can
            // trace failures. We fall back silently if this fails.
            try
            {
                if (SlDLSSGetOptimalSettings(ref options, out var recommended) == 0)
                {
                    options.OutputWidth = Math.Max(options.OutputWidth, recommended.OutputWidth);
                    options.OutputHeight = Math.Max(options.OutputHeight, recommended.OutputHeight);
                    options.Sharpness = recommended.Sharpness;
                }
            }
            catch
            {
                // Ignore failures; we will try to set options with the requested values.
            }

            try
            {
                StreamlineViewportHandle viewportHandle = new()
                {
                    Identifier = (ulong)viewport.GetHashCode()
                };

                errorCode = SlDLSSSetOptions(ref viewportHandle, ref options);
                return errorCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
