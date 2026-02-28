using System;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.DLSS
{
    public static partial class NvidiaDlssManager
    {
        /// <summary>
        /// Thin P/Invoke surface for the Streamline DLSS exports so the engine can
        /// enqueue native upscaling when available.
        /// </summary>
        internal static class Native
        {
        private const string StreamlineLibrary = "sl.interposer.dll";
        private const uint kFeatureDLSS = 0;

        private static bool _initialized;
        private static IntPtr _libraryHandle;
        private static bool _hasOptimalSettings;
        private static SlDLSSSetOptionsDelegate? _setOptions;
        private static SlDLSSGetOptimalSettingsDelegate? _getOptimalSettings;
        private static SlGetFeatureFunctionDelegate? _getFeatureFunction;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SlDLSSSetOptionsDelegate(ref StreamlineViewportHandle viewport, ref StreamlineDlssOptions options);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SlDLSSGetOptimalSettingsDelegate(ref StreamlineDlssOptions options, out StreamlineDlssOptimalSettings settings);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SlGetFeatureFunctionDelegate(uint feature, [MarshalAs(UnmanagedType.LPStr)] string functionName, out IntPtr functionPtr);

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
            public uint OptimalRenderWidth;
            public uint OptimalRenderHeight;
            public float OptimalSharpness;
            public uint RenderWidthMin;
            public uint RenderHeightMin;
            public uint RenderWidthMax;
            public uint RenderHeightMax;
        }

        internal static bool IsAvailable
        {
            get
            {
                if (_initialized)
                    return _libraryHandle != IntPtr.Zero;

                _initialized = true;
                if (NativeLibrary.TryLoad(StreamlineLibrary, out _libraryHandle))
                {
                    // Core resolver for feature functions.
                    if (NativeLibrary.TryGetExport(_libraryHandle, "slGetFeatureFunction", out var getFeatureFunctionPtr))
                        _getFeatureFunction = Marshal.GetDelegateForFunctionPointer<SlGetFeatureFunctionDelegate>(getFeatureFunctionPtr);

                    // Required export for issuing DLSS work. Prefer direct export, fall back to feature resolver.
                    if (NativeLibrary.TryGetExport(_libraryHandle, "slDLSSSetOptions", out var setOptionsPtr))
                    {
                        _setOptions = Marshal.GetDelegateForFunctionPointer<SlDLSSSetOptionsDelegate>(setOptionsPtr);
                    }
                    else if (_getFeatureFunction is not null && _getFeatureFunction(kFeatureDLSS, "slDLSSSetOptions", out setOptionsPtr) == 0)
                    {
                        _setOptions = Marshal.GetDelegateForFunctionPointer<SlDLSSSetOptionsDelegate>(setOptionsPtr);
                    }

                    // Optional export; older Streamline builds omit it, so gate calls. Try both direct and resolved.
                    if (NativeLibrary.TryGetExport(_libraryHandle, "slDLSSGetOptimalSettings", out var getOptimalPtr))
                    {
                        _hasOptimalSettings = true;
                        _getOptimalSettings = Marshal.GetDelegateForFunctionPointer<SlDLSSGetOptimalSettingsDelegate>(getOptimalPtr);
                    }
                    else if (_getFeatureFunction is not null && _getFeatureFunction(kFeatureDLSS, "slDLSSGetOptimalSettings", out getOptimalPtr) == 0)
                    {
                        _hasOptimalSettings = true;
                        _getOptimalSettings = Marshal.GetDelegateForFunctionPointer<SlDLSSGetOptimalSettingsDelegate>(getOptimalPtr);
                    }

                    // We consider the integration available only if the core entry point loaded.
                    if (_setOptions != null)
                        return true;

                    NativeLibrary.Free(_libraryHandle);
                    _libraryHandle = IntPtr.Zero;
                    return false;
                }

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
                if (_hasOptimalSettings && _getOptimalSettings is not null && _getOptimalSettings(ref options, out var recommended) == 0)
                {
                    options.OutputWidth = Math.Max(options.OutputWidth, recommended.OptimalRenderWidth);
                    options.OutputHeight = Math.Max(options.OutputHeight, recommended.OptimalRenderHeight);
                    options.Sharpness = recommended.OptimalSharpness;
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

                if (_setOptions is null)
                    return false;

                errorCode = _setOptions(ref viewportHandle, ref options);
                return errorCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
    }
}
