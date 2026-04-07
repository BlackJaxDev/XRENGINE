using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.XeSS
{
    public static partial class IntelXessManager
    {
        internal static class Native
        {
            private const string PrimaryXessLibrary = "libxess.dll";
            private const string SecondaryXessLibrary = "xess.dll";
            private const string XessFrameGenLibrary = "libxess_fg.dll";

            private static readonly object Sync = new();

            private static bool _initialized;
            private static IntPtr _libraryHandle;
            private static string? _loadedLibraryName;
            private static string? _lastError;

            private static bool _frameGenInitialized;
            private static IntPtr _frameGenLibraryHandle;
            private static string? _frameGenLastError;

            private static XessVkGetRequiredInstanceExtensionsDelegate? _getRequiredInstanceExtensions;
            private static XessVkGetRequiredDeviceExtensionsDelegate? _getRequiredDeviceExtensions;
            private static XessVkGetRequiredDeviceFeaturesDelegate? _getRequiredDeviceFeatures;
            private static XessVkCreateContextDelegate? _createContext;
            private static XessVkBuildPipelinesDelegate? _buildPipelines;
            private static XessVkInitDelegate? _initialize;
            private static XessVkExecuteDelegate? _execute;
            private static XessDestroyContextDelegate? _destroyContext;

            internal static bool IsAvailable
            {
                get
                {
                    lock (Sync)
                        return EnsureLibraryLoaded();
                }
            }

            internal static string? LastError
            {
                get
                {
                    lock (Sync)
                    {
                        EnsureLibraryLoaded();
                        return _lastError;
                    }
                }
            }

            internal static bool TryGetRequiredInstanceExtensions(out string[] extensions, out uint minApiVersion, out string failureReason)
            {
                extensions = [];
                minApiVersion = Vk.Version11;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureLibraryLoaded())
                    {
                        failureReason = _lastError ?? "XeSS could not be loaded.";
                        return false;
                    }

                    XessResult result = _getRequiredInstanceExtensions!(out uint count, out IntPtr extensionNames, out uint requiredApiVersion);
                    if (result != XessResult.Success)
                    {
                        failureReason = $"xessVKGetRequiredInstanceExtensions failed with {result}.";
                        _lastError = failureReason;
                        return false;
                    }

                    extensions = MarshalStringArray(extensionNames, count);
                    minApiVersion = requiredApiVersion == 0 ? Vk.Version11 : requiredApiVersion;
                    return true;
                }
            }

            internal static bool TryGetRequiredDeviceRequirements(Instance instance, PhysicalDevice physicalDevice, out string[] extensions, out IntPtr featureChain, out string failureReason)
            {
                extensions = [];
                featureChain = IntPtr.Zero;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureLibraryLoaded())
                    {
                        failureReason = _lastError ?? "XeSS could not be loaded.";
                        return false;
                    }

                    XessResult extResult = _getRequiredDeviceExtensions!(instance, physicalDevice, out uint count, out IntPtr extensionNames);
                    if (extResult != XessResult.Success)
                    {
                        failureReason = $"xessVKGetRequiredDeviceExtensions failed with {extResult}.";
                        _lastError = failureReason;
                        return false;
                    }

                    XessResult featureResult = _getRequiredDeviceFeatures!(instance, physicalDevice, ref featureChain);
                    if (featureResult != XessResult.Success)
                    {
                        failureReason = $"xessVKGetRequiredDeviceFeatures failed with {featureResult}.";
                        _lastError = failureReason;
                        featureChain = IntPtr.Zero;
                        return false;
                    }

                    extensions = MarshalStringArray(extensionNames, count);
                    return true;
                }
            }

            internal static bool TryCreateBridgeSession(
                VulkanUpscaleBridgeSidecar sidecar,
                in VulkanUpscaleBridgeDispatchParameters parameters,
                out BridgeSession? session,
                out string failureReason)
            {
                session = null;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureLibraryLoaded())
                    {
                        failureReason = _lastError ?? "XeSS could not be loaded.";
                        return false;
                    }

                    XessResult createResult = _createContext!(sidecar.Instance, sidecar.PhysicalDevice, sidecar.Device, out IntPtr context);
                    if (createResult != XessResult.Success || context == IntPtr.Zero)
                    {
                        failureReason = $"xessVKCreateContext failed with {createResult}.";
                        _lastError = failureReason;
                        return false;
                    }

                    XessQualitySetting quality = ResolveQualitySetting(parameters);
                    uint initFlags = ResolveInitFlags(parameters);

                    if (_buildPipelines is not null)
                    {
                        _buildPipelines(context, default, false, initFlags);
                    }

                    XessVkInitParams initParams = new()
                    {
                        OutputResolution = new Xess2D(parameters.OutputWidth, parameters.OutputHeight),
                        QualitySetting = quality,
                        InitFlags = initFlags,
                        CreationNodeMask = 0,
                        VisibleNodeMask = 0,
                        TempBufferHeap = default,
                        BufferHeapOffset = 0,
                        TempTextureHeap = default,
                        TextureHeapOffset = 0,
                        PipelineCache = default,
                    };

                    XessResult initResult = _initialize!(context, ref initParams);
                    if (initResult != XessResult.Success)
                    {
                        _destroyContext!(context);
                        failureReason = $"xessVKInit failed with {initResult}.";
                        _lastError = failureReason;
                        return false;
                    }

                    session = new BridgeSession(sidecar, context, quality, initFlags, parameters.OutputWidth, parameters.OutputHeight);
                    return true;
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
                errorCode = (int)XessResult.ErrorNotImplemented;
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

                errorMessage ??= "XeSS frame generation dispatch is not wired to the DirectX 12 swap chain path.";
                return false;
            }

            private static bool EnsureLibraryLoaded()
            {
                if (_initialized)
                    return _libraryHandle != IntPtr.Zero;

                _initialized = true;

                if (!TryLoadLibrary(PrimaryXessLibrary) && !TryLoadLibrary(SecondaryXessLibrary))
                {
                    _lastError = $"Neither {PrimaryXessLibrary} nor {SecondaryXessLibrary} was found on the probing path.";
                    return false;
                }

                if (!TryLoadExport("xessVKGetRequiredInstanceExtensions", out _getRequiredInstanceExtensions)
                    || !TryLoadExport("xessVKGetRequiredDeviceExtensions", out _getRequiredDeviceExtensions)
                    || !TryLoadExport("xessVKGetRequiredDeviceFeatures", out _getRequiredDeviceFeatures)
                    || !TryLoadExport("xessVKCreateContext", out _createContext)
                    || !TryLoadExport("xessVKInit", out _initialize)
                    || !TryLoadExport("xessVKExecute", out _execute)
                    || !TryLoadExport("xessDestroyContext", out _destroyContext))
                {
                    UnloadLibrary();
                    return false;
                }

                TryLoadExport("xessVKBuildPipelines", out _buildPipelines, required: false);
                return true;
            }

            private static bool TryLoadLibrary(string libraryName)
            {
                try
                {
                    if (NativeLibrary.TryLoad(libraryName, out _libraryHandle) && _libraryHandle != IntPtr.Zero)
                    {
                        _loadedLibraryName = libraryName;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }

                _libraryHandle = IntPtr.Zero;
                return false;
            }

            private static bool TryLoadExport<T>(string exportName, out T? del, bool required = true) where T : Delegate
            {
                del = null;

                if (_libraryHandle == IntPtr.Zero || !NativeLibrary.TryGetExport(_libraryHandle, exportName, out IntPtr exportPtr) || exportPtr == IntPtr.Zero)
                {
                    if (required)
                        _lastError = $"Failed to load XeSS export '{exportName}' from '{_loadedLibraryName ?? "<unknown>"}'.";
                    return !required;
                }

                del = Marshal.GetDelegateForFunctionPointer<T>(exportPtr);
                return true;
            }

            private static void UnloadLibrary()
            {
                if (_libraryHandle != IntPtr.Zero)
                {
                    NativeLibrary.Free(_libraryHandle);
                    _libraryHandle = IntPtr.Zero;
                }

                _loadedLibraryName = null;
                _getRequiredInstanceExtensions = null;
                _getRequiredDeviceExtensions = null;
                _getRequiredDeviceFeatures = null;
                _createContext = null;
                _buildPipelines = null;
                _initialize = null;
                _execute = null;
                _destroyContext = null;
            }

            private static string[] MarshalStringArray(IntPtr names, uint count)
            {
                if (names == IntPtr.Zero || count == 0)
                    return [];

                string[] results = new string[count];
                for (int index = 0; index < count; index++)
                {
                    IntPtr entry = Marshal.ReadIntPtr(names, index * IntPtr.Size);
                    results[index] = Marshal.PtrToStringAnsi(entry) ?? string.Empty;
                }

                return results;
            }

            private static uint ResolveInitFlags(in VulkanUpscaleBridgeDispatchParameters parameters)
            {
                    uint flags = (uint)XessInitFlags.UseNdcVelocity | (uint)XessInitFlags.LdrInputColor | (uint)XessInitFlags.EnableAutoExposure;
                if (parameters.ReverseDepth)
                    flags |= (uint)XessInitFlags.InvertedDepth;
                return flags;
            }

            private static XessQualitySetting ResolveQualitySetting(in VulkanUpscaleBridgeDispatchParameters parameters)
            {
                if (parameters.XessQuality != EXessQualityMode.Custom)
                {
                    return parameters.XessQuality switch
                    {
                        EXessQualityMode.UltraPerformance => XessQualitySetting.UltraPerformance,
                        EXessQualityMode.Performance => XessQualitySetting.Performance,
                        EXessQualityMode.Balanced => XessQualitySetting.Balanced,
                        EXessQualityMode.Quality => XessQualitySetting.Quality,
                        EXessQualityMode.UltraQuality => XessQualitySetting.UltraQuality,
                        _ => XessQualitySetting.Quality,
                    };
                }

                float inputScale = parameters.OutputWidth == 0 ? 1.0f : parameters.InputWidth / (float)parameters.OutputWidth;
                return inputScale switch
                {
                    <= 0.36f => XessQualitySetting.UltraPerformance,
                    <= 0.46f => XessQualitySetting.Performance,
                    <= 0.54f => XessQualitySetting.Balanced,
                    <= 0.66f => XessQualitySetting.Quality,
                    <= 0.80f => XessQualitySetting.UltraQuality,
                    _ => XessQualitySetting.AntiAliasing,
                };
            }

            internal sealed class BridgeSession : IDisposable
            {
                private readonly VulkanUpscaleBridgeSidecar _sidecar;
                private readonly IntPtr _context;
                private readonly XessQualitySetting _quality;
                private readonly uint _initFlags;
                private readonly uint _outputWidth;
                private readonly uint _outputHeight;
                private bool _disposed;
                private bool _firstDispatch = true;

                internal BridgeSession(VulkanUpscaleBridgeSidecar sidecar, IntPtr context, XessQualitySetting quality, uint initFlags, uint outputWidth, uint outputHeight)
                {
                    _sidecar = sidecar;
                    _context = context;
                    _quality = quality;
                    _initFlags = initFlags;
                    _outputWidth = outputWidth;
                    _outputHeight = outputHeight;
                }

                public bool Record(VulkanUpscaleBridgeFrameSlot slot, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
                {
                    failureReason = string.Empty;

                    if (_disposed)
                    {
                        failureReason = "XeSS bridge session was already disposed.";
                        return false;
                    }

                    if (_execute is null)
                    {
                        failureReason = "XeSS execute export is unavailable.";
                        return false;
                    }

                    if (_quality != ResolveQualitySetting(parameters) || _initFlags != ResolveInitFlags(parameters) || _outputWidth != parameters.OutputWidth || _outputHeight != parameters.OutputHeight)
                    {
                        failureReason = "XeSS bridge session configuration changed and requires bridge recreation.";
                        return false;
                    }

                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceColor, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceMotion, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceDepth, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.OutputColor, ImageLayout.General, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

                    XessVkExecuteParams executeParams = new()
                    {
                        ColorTexture = CreateImageViewInfo(slot.SourceColor),
                        VelocityTexture = CreateImageViewInfo(slot.SourceMotion),
                        DepthTexture = CreateImageViewInfo(slot.SourceDepth),
                        ExposureScaleTexture = default,
                        ResponsivePixelMaskTexture = default,
                        OutputTexture = CreateImageViewInfo(slot.OutputColor),
                        JitterOffsetX = parameters.JitterOffsetX,
                        JitterOffsetY = parameters.JitterOffsetY,
                        ExposureScale = 1.0f,
                        ResetHistory = _firstDispatch || parameters.ResetHistory ? 1u : 0u,
                        InputWidth = parameters.InputWidth,
                        InputHeight = parameters.InputHeight,
                        InputColorBase = default,
                        InputMotionVectorBase = default,
                        InputDepthBase = default,
                        InputResponsiveMaskBase = default,
                        Reserved0 = default,
                        OutputColorBase = default,
                    };

                    XessResult executeResult = _execute(_context, slot.CommandBuffer, ref executeParams);
                    if (executeResult != XessResult.Success)
                    {
                        failureReason = $"xessVKExecute failed with {executeResult}.";
                        return false;
                    }

                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceColor, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceMotion, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceDepth, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.OutputColor, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

                    _firstDispatch = false;
                    return true;
                }

                public void Dispose()
                {
                    if (_disposed)
                        return;

                    _disposed = true;

                    lock (Sync)
                    {
                        if (_context != IntPtr.Zero && _destroyContext is not null)
                            _destroyContext(_context);
                    }
                }

                private static XessVkImageViewInfo CreateImageViewInfo(VulkanUpscaleBridgeSharedImage image)
                {
                    return new XessVkImageViewInfo
                    {
                        ImageView = image.VulkanImageView,
                        Image = image.VulkanImage,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = image.ViewAspectMask,
                            BaseMipLevel = 0,
                            LevelCount = 1,
                            BaseArrayLayer = 0,
                            LayerCount = 1,
                        },
                        Format = image.VulkanFormat,
                        Width = image.Texture.Width,
                        Height = image.Texture.Height,
                    };
                }
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessVkGetRequiredInstanceExtensionsDelegate(out uint extensionCount, out IntPtr extensions, out uint minApiVersion);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessVkGetRequiredDeviceExtensionsDelegate(Instance instance, PhysicalDevice physicalDevice, out uint extensionCount, out IntPtr extensions);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessVkGetRequiredDeviceFeaturesDelegate(Instance instance, PhysicalDevice physicalDevice, ref IntPtr features);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessVkCreateContextDelegate(Instance instance, PhysicalDevice physicalDevice, Device device, out IntPtr context);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessVkBuildPipelinesDelegate(IntPtr context, PipelineCache pipelineCache, [MarshalAs(UnmanagedType.I1)] bool blocking, uint initFlags);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessVkInitDelegate(IntPtr context, ref XessVkInitParams initParams);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessVkExecuteDelegate(IntPtr context, CommandBuffer commandBuffer, ref XessVkExecuteParams executeParams);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate XessResult XessDestroyContextDelegate(IntPtr context);

            private enum XessResult : int
            {
                Success = 0,
                WarningOldDriver = 2,
                ErrorUnsupportedDevice = -1,
                ErrorUnsupportedDriver = -2,
                ErrorUninitialized = -3,
                ErrorInvalidArgument = -4,
                ErrorDeviceOutOfMemory = -5,
                ErrorDevice = -6,
                ErrorNotImplemented = -7,
                ErrorInvalidContext = -8,
                ErrorOperationInProgress = -9,
                ErrorUnsupported = -10,
                ErrorCantLoadLibrary = -11,
                ErrorWrongCallOrder = -12,
                ErrorUnknown = -1000,
            }

            internal enum XessQualitySetting : int
            {
                UltraPerformance = 100,
                Performance = 101,
                Balanced = 102,
                Quality = 103,
                UltraQuality = 104,
                UltraQualityPlus = 105,
                AntiAliasing = 106,
            }

            [Flags]
            private enum XessInitFlags : uint
            {
                None = 0,
                HighResMotionVectors = 1u << 0,
                InvertedDepth = 1u << 1,
                ExposureScaleTexture = 1u << 2,
                ResponsivePixelMask = 1u << 3,
                UseNdcVelocity = 1u << 4,
                ExternalDescriptorHeap = 1u << 5,
                LdrInputColor = 1u << 6,
                JitteredMotionVectors = 1u << 7,
                EnableAutoExposure = 1u << 8,
            }

            [StructLayout(LayoutKind.Sequential, Pack = 8)]
            private struct Xess2D
            {
                public Xess2D(uint x, uint y)
                {
                    X = x;
                    Y = y;
                }

                public uint X;
                public uint Y;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 8)]
            private struct XessVkImageViewInfo
            {
                public ImageView ImageView;
                public Image Image;
                public ImageSubresourceRange SubresourceRange;
                public Format Format;
                public uint Width;
                public uint Height;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 8)]
            private struct XessVkExecuteParams
            {
                public XessVkImageViewInfo ColorTexture;
                public XessVkImageViewInfo VelocityTexture;
                public XessVkImageViewInfo DepthTexture;
                public XessVkImageViewInfo ExposureScaleTexture;
                public XessVkImageViewInfo ResponsivePixelMaskTexture;
                public XessVkImageViewInfo OutputTexture;
                public float JitterOffsetX;
                public float JitterOffsetY;
                public float ExposureScale;
                public uint ResetHistory;
                public uint InputWidth;
                public uint InputHeight;
                public Xess2D InputColorBase;
                public Xess2D InputMotionVectorBase;
                public Xess2D InputDepthBase;
                public Xess2D InputResponsiveMaskBase;
                public Xess2D Reserved0;
                public Xess2D OutputColorBase;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 8)]
            private struct XessVkInitParams
            {
                public Xess2D OutputResolution;
                public XessQualitySetting QualitySetting;
                public uint InitFlags;
                public uint CreationNodeMask;
                public uint VisibleNodeMask;
                public DeviceMemory TempBufferHeap;
                public ulong BufferHeapOffset;
                public DeviceMemory TempTextureHeap;
                public ulong TextureHeapOffset;
                public PipelineCache PipelineCache;
            }
        }
    }
}
