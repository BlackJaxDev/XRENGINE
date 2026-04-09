using Silk.NET.Vulkan;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.DLSS
{
    public static partial class NvidiaDlssManager
    {
        internal static class Native
        {
            private const string StreamlineLibrary = "sl.interposer.dll";
            private const ulong StreamlineSdkVersion = 0x0002000A0003FEDC;
            private const uint FeatureDlss = 0;
            private const uint BufferTypeDepth = 0;
            private const uint BufferTypeMotionVectors = 1;
            private const uint BufferTypeScalingInputColor = 3;
            private const uint BufferTypeScalingOutputColor = 4;
            private const uint BufferTypeExposure = 13;

            private static readonly object Sync = new();

            private static bool _initialized;
            private static bool _runtimeInitialized;
            private static bool _featureFunctionsResolved;
            private static int _activeBridgeSessions;
            private static IntPtr _libraryHandle;
            private static string? _lastError;

            private static SlInitDelegate? _init;
            private static SlShutdownDelegate? _shutdown;
            private static SlSetVulkanInfoDelegate? _setVulkanInfo;
            private static SlEvaluateFeatureDelegate? _evaluateFeature;
            private static SlAllocateResourcesDelegate? _allocateResources;
            private static SlFreeResourcesDelegate? _freeResources;
            private static SlSetTagForFrameDelegate? _setTagForFrame;
            private static SlSetConstantsDelegate? _setConstants;
            private static SlGetFeatureFunctionDelegate? _getFeatureFunction;
            private static SlGetNewFrameTokenDelegate? _getNewFrameToken;
            private static SlDlssSetOptionsDelegate? _setOptions;
            private static SlDlssGetOptimalSettingsDelegate? _getOptimalSettings;

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

            internal static bool TryDispatchUpscale(
                XRViewport viewport,
                XRQuadFrameBuffer source,
                XRFrameBuffer? target,
                XRTexture? depth,
                XRTexture? motion,
                out int errorCode)
            {
                errorCode = 0;
                return false;
            }

            internal static bool TryCreateBridgeSession(
                VulkanUpscaleBridgeSidecar sidecar,
                uint viewportId,
                out BridgeSession? session,
                out string failureReason)
            {
                session = null;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureBridgeRuntime(sidecar, out failureReason))
                        return false;

                    _activeBridgeSessions++;
                }

                session = new BridgeSession(sidecar, viewportId);
                return true;
            }

            private static unsafe bool EnsureBridgeRuntime(VulkanUpscaleBridgeSidecar sidecar, out string failureReason)
            {
                failureReason = string.Empty;

                if (!EnsureLibraryLoaded())
                {
                    failureReason = _lastError ?? "Streamline could not be loaded.";
                    return false;
                }

                if (!_runtimeInitialized)
                {
                    uint feature = FeatureDlss;
                    StreamlinePreferences preferences = CreatePreferences(&feature, 1);
                    try
                    {
                        StreamlineResult initResult = _init!(ref preferences, StreamlineSdkVersion);
                        if (initResult != StreamlineResult.Ok)
                        {
                            failureReason = $"slInit failed with {initResult}.";
                            _lastError = failureReason;
                            return false;
                        }

                        _runtimeInitialized = true;
                    }
                    finally
                    {
                        if (preferences.EngineVersion != IntPtr.Zero)
                            Marshal.FreeHGlobal(preferences.EngineVersion);
                        if (preferences.ProjectId != IntPtr.Zero)
                            Marshal.FreeHGlobal(preferences.ProjectId);
                    }
                }

                StreamlineVulkanInfo info = CreateVulkanInfo(sidecar);
                StreamlineResult setInfoResult = _setVulkanInfo!(ref info);
                if (setInfoResult != StreamlineResult.Ok)
                {
                    failureReason = $"slSetVulkanInfo failed with {setInfoResult}.";
                    _lastError = failureReason;
                    return false;
                }

                if (!_featureFunctionsResolved && !ResolveFeatureFunctions(out failureReason))
                {
                    _lastError = failureReason;
                    return false;
                }

                return true;
            }

            private static bool EnsureLibraryLoaded()
            {
                if (_initialized)
                    return _libraryHandle != IntPtr.Zero;

                _initialized = true;

                try
                {
                    if (!NativeLibrary.TryLoad(StreamlineLibrary, out _libraryHandle) || _libraryHandle == IntPtr.Zero)
                    {
                        _lastError = $"{StreamlineLibrary} was not found on the probing path.";
                        _libraryHandle = IntPtr.Zero;
                        return false;
                    }

                    if (!TryLoadExport("slInit", out _init)
                        || !TryLoadExport("slShutdown", out _shutdown)
                        || !TryLoadExport("slSetVulkanInfo", out _setVulkanInfo)
                        || !TryLoadExport("slEvaluateFeature", out _evaluateFeature)
                        || !TryLoadExport("slAllocateResources", out _allocateResources)
                        || !TryLoadExport("slFreeResources", out _freeResources)
                        || !TryLoadExport("slSetTagForFrame", out _setTagForFrame)
                        || !TryLoadExport("slSetConstants", out _setConstants)
                        || !TryLoadExport("slGetFeatureFunction", out _getFeatureFunction)
                        || !TryLoadExport("slGetNewFrameToken", out _getNewFrameToken))
                    {
                        UnloadLibrary();
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    UnloadLibrary();
                    return false;
                }
            }

            private static bool ResolveFeatureFunctions(out string failureReason)
            {
                failureReason = string.Empty;

                if (_featureFunctionsResolved)
                    return true;

                if (!TryResolveFeatureFunction("slDLSSSetOptions", out _setOptions)
                    || !TryResolveFeatureFunction("slDLSSGetOptimalSettings", out _getOptimalSettings, required: false))
                {
                    failureReason = _lastError ?? "Failed to resolve one or more Streamline DLSS feature exports.";
                    return false;
                }

                _featureFunctionsResolved = true;
                return true;
            }

            private static bool TryResolveFeatureFunction<T>(string name, out T? del, bool required = true) where T : Delegate
            {
                del = null;

                if (_libraryHandle == IntPtr.Zero)
                {
                    _lastError = $"Cannot resolve Streamline feature export '{name}' before the library is loaded.";
                    return false;
                }

                if (NativeLibrary.TryGetExport(_libraryHandle, name, out IntPtr directExport) && directExport != IntPtr.Zero)
                {
                    del = Marshal.GetDelegateForFunctionPointer<T>(directExport);
                    return true;
                }

                if (_getFeatureFunction is not null
                    && _getFeatureFunction(FeatureDlss, name, out IntPtr functionPtr) == StreamlineResult.Ok
                    && functionPtr != IntPtr.Zero)
                {
                    del = Marshal.GetDelegateForFunctionPointer<T>(functionPtr);
                    return true;
                }

                if (!required)
                    return true;

                _lastError = $"Failed to resolve Streamline feature export '{name}'.";
                return false;
            }

            private static unsafe StreamlinePreferences CreatePreferences(uint* featuresToLoad, uint featureCount)
            {
                return new StreamlinePreferences
                {
                    Base = CreateBase(PreferencesStructType, 1),
                    ShowConsole = 0,
                    LogLevel = StreamlineLogLevel.Default,
                    PathsToPlugins = IntPtr.Zero,
                    NumPathsToPlugins = 0,
                    PathToLogsAndData = IntPtr.Zero,
                    AllocateCallback = IntPtr.Zero,
                    ReleaseCallback = IntPtr.Zero,
                    LogMessageCallback = IntPtr.Zero,
                    Flags = StreamlinePreferenceFlags.DisableCommandListStateTracking
                        | StreamlinePreferenceFlags.DisableDebugText
                        | StreamlinePreferenceFlags.UseManualHooking
                        | StreamlinePreferenceFlags.UseFrameBasedResourceTagging,
                    FeaturesToLoad = (IntPtr)featuresToLoad,
                    NumFeaturesToLoad = featureCount,
                    ApplicationId = 0,
                    Engine = StreamlineEngineType.Custom,
                    EngineVersion = Marshal.StringToHGlobalAnsi("XRENGINE"),
                    ProjectId = Marshal.StringToHGlobalAnsi("xrengine-vulkan-upscale-bridge"),
                    RenderApi = StreamlineRenderApi.Vulkan,
                };
            }

            private static StreamlineVulkanInfo CreateVulkanInfo(VulkanUpscaleBridgeSidecar sidecar)
            {
                return new StreamlineVulkanInfo
                {
                    Base = CreateBase(VulkanInfoStructType, 3),
                    Device = sidecar.Device,
                    Instance = sidecar.Instance,
                    PhysicalDevice = sidecar.PhysicalDevice,
                    ComputeQueueIndex = sidecar.GraphicsQueueIndex,
                    ComputeQueueFamily = sidecar.GraphicsQueueFamilyIndex,
                    GraphicsQueueIndex = sidecar.GraphicsQueueIndex,
                    GraphicsQueueFamily = sidecar.GraphicsQueueFamilyIndex,
                    OpticalFlowQueueIndex = 0,
                    OpticalFlowQueueFamily = 0,
                    UseNativeOpticalFlowMode = 0,
                    ComputeQueueCreateFlags = 0,
                    GraphicsQueueCreateFlags = 0,
                    OpticalFlowQueueCreateFlags = 0,
                };
            }

            private static void ReleaseBridgeRuntime()
            {
                lock (Sync)
                {
                    if (_activeBridgeSessions > 0)
                        _activeBridgeSessions--;

                    if (_activeBridgeSessions != 0)
                        return;

                    if (_runtimeInitialized && _shutdown is not null)
                        _shutdown();

                    _runtimeInitialized = false;
                    _featureFunctionsResolved = false;
                }
            }

            private static bool TryLoadExport<T>(string exportName, out T? del) where T : Delegate
            {
                del = null;

                if (_libraryHandle == IntPtr.Zero || !NativeLibrary.TryGetExport(_libraryHandle, exportName, out IntPtr exportPtr) || exportPtr == IntPtr.Zero)
                {
                    _lastError = $"Failed to load Streamline export '{exportName}'.";
                    return false;
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

                _init = null;
                _shutdown = null;
                _setVulkanInfo = null;
                _evaluateFeature = null;
                _allocateResources = null;
                _freeResources = null;
                _setTagForFrame = null;
                _setConstants = null;
                _getFeatureFunction = null;
                _getNewFrameToken = null;
                _setOptions = null;
                _getOptimalSettings = null;
            }

            private static StreamlineBaseStructure CreateBase(StreamlineStructType type, nuint version)
                => new()
                {
                    Next = IntPtr.Zero,
                    StructType = type,
                    StructVersion = version,
                };

            private static StreamlineStructType CreateStructType(uint data1, ushort data2, ushort data3, byte d4, byte d5, byte d6, byte d7, byte d8, byte d9, byte d10, byte d11)
                => new()
                {
                    Data1 = data1,
                    Data2 = data2,
                    Data3 = data3,
                    Data40 = d4,
                    Data41 = d5,
                    Data42 = d6,
                    Data43 = d7,
                    Data44 = d8,
                    Data45 = d9,
                    Data46 = d10,
                    Data47 = d11,
                };

            private static readonly StreamlineStructType PreferencesStructType = CreateStructType(0x1CA10965, 0xBF8E, 0x432B, 0x8D, 0xA1, 0x67, 0x16, 0xD8, 0x79, 0xFB, 0x14);
            private static readonly StreamlineStructType ViewportStructType = CreateStructType(0x171B6435, 0x9B3C, 0x4FC8, 0x99, 0x94, 0xFB, 0xE5, 0x25, 0x69, 0xAA, 0xA4);
            private static readonly StreamlineStructType ResourceStructType = CreateStructType(0x3A9D70CF, 0x2418, 0x4B72, 0x83, 0x91, 0x13, 0xF8, 0x72, 0x1C, 0x72, 0x61);
            private static readonly StreamlineStructType ResourceTagStructType = CreateStructType(0x4C6A5AAD, 0xB445, 0x496C, 0x87, 0xFF, 0x1A, 0xF3, 0x84, 0x5B, 0xE6, 0x53);
            private static readonly StreamlineStructType ConstantsStructType = CreateStructType(0xDCD35AD7, 0x4E4A, 0x4BAD, 0xA9, 0x0C, 0xE0, 0xC4, 0x9E, 0xB2, 0x3A, 0xFE);
            private static readonly StreamlineStructType VulkanInfoStructType = CreateStructType(0x0EED6FD5, 0x82CD, 0x43A9, 0xBD, 0xB5, 0x47, 0xA5, 0xBA, 0x2F, 0x45, 0xD6);
            private static readonly StreamlineStructType DlssOptionsStructType = CreateStructType(0x6AC826E4, 0x4C61, 0x4101, 0xA9, 0x2D, 0x63, 0x8D, 0x42, 0x10, 0x57, 0xB8);

            internal sealed class BridgeSession : IDisposable
            {
                private readonly VulkanUpscaleBridgeSidecar _sidecar;
                private readonly StreamlineViewportHandle _viewport;
                private bool _disposed;
                private bool _resourcesAllocated;
                private bool _firstDispatch = true;

                public BridgeSession(VulkanUpscaleBridgeSidecar sidecar, uint viewportId)
                {
                    _sidecar = sidecar;
                    _viewport = new StreamlineViewportHandle
                    {
                        Base = CreateBase(ViewportStructType, 1),
                        Value = viewportId,
                    };
                }

                public unsafe bool Record(VulkanUpscaleBridgeFrameSlot slot, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
                {
                    failureReason = string.Empty;

                    if (_disposed)
                    {
                        failureReason = "Streamline bridge session was already disposed.";
                        return false;
                    }

                    if (_allocateResources is null
                        || _freeResources is null
                        || _setTagForFrame is null
                        || _setConstants is null
                        || _evaluateFeature is null
                        || _getNewFrameToken is null
                        || _setOptions is null)
                    {
                        failureReason = "Streamline core exports are not fully initialized.";
                        return false;
                    }

                    IntPtr commandBuffer = ToIntPtr(slot.CommandBuffer.Handle);
                    StreamlineViewportHandle viewport = _viewport;

                    if (!_resourcesAllocated)
                    {
                        StreamlineResult allocateResult = _allocateResources(commandBuffer, FeatureDlss, ref viewport);
                        if (allocateResult != StreamlineResult.Ok)
                        {
                            failureReason = $"slAllocateResources failed with {allocateResult}.";
                            return false;
                        }

                        _resourcesAllocated = true;
                    }

                    StreamlineDlssOptions options = CreateDlssOptions(parameters);
                    StreamlineResult setOptionsResult = _setOptions(ref viewport, ref options);
                    if (setOptionsResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slDLSSSetOptions failed with {setOptionsResult}.";
                        return false;
                    }

                    StreamlineConstants constants = CreateConstants(parameters, _firstDispatch);
                    uint frameIndex = parameters.FrameIndex;
                    StreamlineResult frameTokenResult = _getNewFrameToken(out IntPtr frameToken, ref frameIndex);
                    if (frameTokenResult != StreamlineResult.Ok || frameToken == IntPtr.Zero)
                    {
                        failureReason = $"slGetNewFrameToken failed with {frameTokenResult}.";
                        return false;
                    }

                    StreamlineResource colorInput = CreateResource(slot.SourceColor, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource colorOutput = CreateResource(slot.OutputColor, parameters.OutputWidth, parameters.OutputHeight);
                    StreamlineResource depth = CreateResource(slot.SourceDepth, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource motion = CreateResource(slot.SourceMotion, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource exposure = parameters.HasExposureTexture
                        ? CreateResource(slot.Exposure, 1, 1)
                        : default;

                    StreamlineExtent inputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.InputWidth,
                        Height = parameters.InputHeight,
                    };
                    StreamlineExtent outputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.OutputWidth,
                        Height = parameters.OutputHeight,
                    };
                    StreamlineExtent exposureExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = 1,
                        Height = 1,
                    };

                    StreamlineResourceTag* tags = stackalloc StreamlineResourceTag[5];
                    tags[0] = CreateResourceTag(&colorInput, BufferTypeScalingInputColor, StreamlineResourceLifecycle.OnlyValidNow, inputExtent);
                    tags[1] = CreateResourceTag(&colorOutput, BufferTypeScalingOutputColor, StreamlineResourceLifecycle.OnlyValidNow, outputExtent);
                    tags[2] = CreateResourceTag(&depth, BufferTypeDepth, StreamlineResourceLifecycle.OnlyValidNow, inputExtent);
                    tags[3] = CreateResourceTag(&motion, BufferTypeMotionVectors, StreamlineResourceLifecycle.OnlyValidNow, inputExtent);
                    uint tagCount = 4;
                    if (parameters.HasExposureTexture)
                        tags[tagCount++] = CreateResourceTag(&exposure, BufferTypeExposure, StreamlineResourceLifecycle.OnlyValidNow, exposureExtent);

                    StreamlineResult tagResult = _setTagForFrame(frameToken, ref viewport, (IntPtr)tags, tagCount, commandBuffer);
                    if (tagResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slSetTagForFrame failed with {tagResult}.";
                        return false;
                    }

                    StreamlineResult constantsResult = _setConstants(ref constants, frameToken, ref viewport);
                    if (constantsResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slSetConstants failed with {constantsResult}.";
                        return false;
                    }

                    StreamlineViewportHandle viewportInput = viewport;
                    IntPtr* inputs = stackalloc IntPtr[1];
                    inputs[0] = (IntPtr)(&viewportInput);

                    StreamlineResult evaluateResult = _evaluateFeature(FeatureDlss, frameToken, (IntPtr)inputs, 1, commandBuffer);
                    if (evaluateResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slEvaluateFeature failed with {evaluateResult}.";
                        return false;
                    }

                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceColor, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceDepth, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceMotion, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    if (parameters.HasExposureTexture)
                        _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.Exposure, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.OutputColor, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

                    _firstDispatch = false;
                    return true;
                }

                public void Dispose()
                {
                    if (_disposed)
                        return;

                    _disposed = true;

                    if (_resourcesAllocated && _freeResources is not null)
                    {
                        StreamlineViewportHandle viewport = _viewport;
                        _freeResources(FeatureDlss, ref viewport);
                        _resourcesAllocated = false;
                    }

                    ReleaseBridgeRuntime();
                }

                private static StreamlineDlssOptions CreateDlssOptions(in VulkanUpscaleBridgeDispatchParameters parameters)
                {
                    float preExposure = parameters.HasExposureTexture
                        ? 1.0f
                        : MathF.Max(parameters.ExposureScale, 0.0001f);

                    return new StreamlineDlssOptions
                    {
                        Base = CreateBase(DlssOptionsStructType, 3),
                        Mode = ResolveDlssMode(parameters),
                        OutputWidth = parameters.OutputWidth,
                        OutputHeight = parameters.OutputHeight,
                        Sharpness = parameters.DlssSharpness,
                        PreExposure = preExposure,
                        ExposureScale = 1.0f,
                        ColorBuffersHdr = parameters.OutputHdr ? StreamlineBoolean.True : StreamlineBoolean.False,
                        IndicatorInvertAxisX = StreamlineBoolean.False,
                        IndicatorInvertAxisY = StreamlineBoolean.False,
                        DlaaPreset = StreamlineDlssPreset.Default,
                        QualityPreset = StreamlineDlssPreset.Default,
                        BalancedPreset = StreamlineDlssPreset.Default,
                        PerformancePreset = StreamlineDlssPreset.Default,
                        UltraPerformancePreset = StreamlineDlssPreset.Default,
                        UltraQualityPreset = StreamlineDlssPreset.Default,
                        UseAutoExposure = StreamlineBoolean.False,
                        AlphaUpscalingEnabled = StreamlineBoolean.False,
                    };
                }

                private static StreamlineDlssMode ResolveDlssMode(in VulkanUpscaleBridgeDispatchParameters parameters)
                {
                    if (parameters.DlssQuality != EDlssQualityMode.Custom)
                    {
                        return parameters.DlssQuality switch
                        {
                            EDlssQualityMode.UltraPerformance => StreamlineDlssMode.UltraPerformance,
                            EDlssQualityMode.Performance => StreamlineDlssMode.MaxPerformance,
                            EDlssQualityMode.Balanced => StreamlineDlssMode.Balanced,
                            EDlssQualityMode.Quality => StreamlineDlssMode.MaxQuality,
                            EDlssQualityMode.UltraQuality => StreamlineDlssMode.UltraQuality,
                            _ => StreamlineDlssMode.MaxQuality,
                        };
                    }

                    float inputScale = parameters.OutputWidth == 0 ? 1.0f : parameters.InputWidth / (float)parameters.OutputWidth;
                    return inputScale switch
                    {
                        <= 0.40f => StreamlineDlssMode.UltraPerformance,
                        <= 0.54f => StreamlineDlssMode.MaxPerformance,
                        <= 0.62f => StreamlineDlssMode.Balanced,
                        <= 0.72f => StreamlineDlssMode.MaxQuality,
                        <= 0.86f => StreamlineDlssMode.UltraQuality,
                        _ => StreamlineDlssMode.MaxQuality,
                    };
                }

                private static StreamlineConstants CreateConstants(in VulkanUpscaleBridgeDispatchParameters parameters, bool firstDispatch)
                {
                    return new StreamlineConstants
                    {
                        Base = CreateBase(ConstantsStructType, 2),
                        CameraViewToClip = ToFloat4x4(parameters.CameraViewToClip),
                        ClipToCameraView = ToFloat4x4(parameters.ClipToCameraView),
                        ClipToLensClip = ToFloat4x4(Matrix4x4.Identity),
                        ClipToPrevClip = ToFloat4x4(parameters.ClipToPrevClip),
                        PrevClipToClip = ToFloat4x4(parameters.PrevClipToClip),
                        JitterOffset = new StreamlineFloat2(parameters.JitterOffsetX, parameters.JitterOffsetY),
                        MotionVectorScale = new StreamlineFloat2(parameters.MotionVectorScaleX, parameters.MotionVectorScaleY),
                        CameraPinholeOffset = new StreamlineFloat2(float.MaxValue, float.MaxValue),
                        CameraPosition = new StreamlineFloat3(parameters.CameraPosition.X, parameters.CameraPosition.Y, parameters.CameraPosition.Z),
                        CameraUp = new StreamlineFloat3(parameters.CameraUp.X, parameters.CameraUp.Y, parameters.CameraUp.Z),
                        CameraRight = new StreamlineFloat3(parameters.CameraRight.X, parameters.CameraRight.Y, parameters.CameraRight.Z),
                        CameraForward = new StreamlineFloat3(parameters.CameraForward.X, parameters.CameraForward.Y, parameters.CameraForward.Z),
                        CameraNear = parameters.CameraNear,
                        CameraFar = parameters.CameraFar,
                        CameraFov = parameters.CameraFovRadians,
                        CameraAspectRatio = parameters.CameraAspectRatio,
                        MotionVectorsInvalidValue = float.MaxValue,
                        DepthInverted = parameters.ReverseDepth ? StreamlineBoolean.True : StreamlineBoolean.False,
                        CameraMotionIncluded = StreamlineBoolean.True,
                        MotionVectors3D = StreamlineBoolean.False,
                        Reset = parameters.ResetHistory || firstDispatch ? StreamlineBoolean.True : StreamlineBoolean.False,
                        OrthographicProjection = parameters.IsOrthographic ? StreamlineBoolean.True : StreamlineBoolean.False,
                        MotionVectorsDilated = StreamlineBoolean.False,
                        MotionVectorsJittered = StreamlineBoolean.False,
                        MinRelativeLinearDepthObjectSeparation = 40.0f,
                    };
                }

                private static StreamlineResource CreateResource(VulkanUpscaleBridgeSharedImage image, uint width, uint height)
                {
                    return new StreamlineResource
                    {
                        Base = CreateBase(ResourceStructType, 1),
                        Type = StreamlineResourceType.Texture2D,
                        Native = ToIntPtr(image.VulkanImage.Handle),
                        Memory = ToIntPtr(image.VulkanMemory.Handle),
                        View = ToIntPtr(image.VulkanImageView.Handle),
                        State = (uint)image.CurrentLayout,
                        Width = width,
                        Height = height,
                        NativeFormat = (uint)image.VulkanFormat,
                        MipLevels = 1,
                        ArrayLayers = 1,
                        GpuVirtualAddress = 0,
                        Flags = 0,
                        Usage = (uint)image.Usage,
                        Reserved = 0,
                    };
                }

                private static unsafe StreamlineResourceTag CreateResourceTag(StreamlineResource* resource, uint bufferType, StreamlineResourceLifecycle lifecycle, StreamlineExtent extent)
                {
                    return new StreamlineResourceTag
                    {
                        Base = CreateBase(ResourceTagStructType, 1),
                        Resource = (IntPtr)resource,
                        Type = bufferType,
                        Lifecycle = lifecycle,
                        Extent = extent,
                    };
                }

                private static StreamlineFloat4x4 ToFloat4x4(Matrix4x4 matrix)
                {
                    return new StreamlineFloat4x4
                    {
                        Row0 = new StreamlineFloat4(matrix.M11, matrix.M12, matrix.M13, matrix.M14),
                        Row1 = new StreamlineFloat4(matrix.M21, matrix.M22, matrix.M23, matrix.M24),
                        Row2 = new StreamlineFloat4(matrix.M31, matrix.M32, matrix.M33, matrix.M34),
                        Row3 = new StreamlineFloat4(matrix.M41, matrix.M42, matrix.M43, matrix.M44),
                    };
                }
            }

            private static IntPtr ToIntPtr(ulong handle)
                => unchecked((IntPtr)(nint)handle);

            private static IntPtr ToIntPtr(nint handle)
                => (IntPtr)handle;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlInitDelegate(ref StreamlinePreferences preferences, ulong sdkVersion);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlShutdownDelegate();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlSetVulkanInfoDelegate(ref StreamlineVulkanInfo info);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlEvaluateFeatureDelegate(uint feature, IntPtr frameToken, IntPtr inputs, uint numInputs, IntPtr commandBuffer);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlAllocateResourcesDelegate(IntPtr commandBuffer, uint feature, ref StreamlineViewportHandle viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlFreeResourcesDelegate(uint feature, ref StreamlineViewportHandle viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlSetTagForFrameDelegate(IntPtr frameToken, ref StreamlineViewportHandle viewport, IntPtr resourceTags, uint resourceTagCount, IntPtr commandBuffer);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlSetConstantsDelegate(ref StreamlineConstants values, IntPtr frameToken, ref StreamlineViewportHandle viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlGetFeatureFunctionDelegate(uint feature, [MarshalAs(UnmanagedType.LPStr)] string functionName, out IntPtr function);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlGetNewFrameTokenDelegate(out IntPtr token, ref uint frameIndex);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlDlssSetOptionsDelegate(ref StreamlineViewportHandle viewport, ref StreamlineDlssOptions options);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlDlssGetOptimalSettingsDelegate(ref StreamlineDlssOptions options, out IntPtr settings);

            private enum StreamlineResult
            {
                Ok = 0,
            }

            private enum StreamlineLogLevel : uint
            {
                Off = 0,
                Default = 1,
                Verbose = 2,
            }

            [Flags]
            private enum StreamlinePreferenceFlags : ulong
            {
                DisableCommandListStateTracking = 1UL << 0,
                DisableDebugText = 1UL << 1,
                UseManualHooking = 1UL << 2,
                UseFrameBasedResourceTagging = 1UL << 7,
            }

            private enum StreamlineEngineType : uint
            {
                Custom = 0,
            }

            private enum StreamlineRenderApi : uint
            {
                D3D11 = 0,
                D3D12 = 1,
                Vulkan = 2,
            }

            private enum StreamlineBoolean : byte
            {
                False = 0,
                True = 1,
                Invalid = 2,
            }

            private enum StreamlineDlssMode : uint
            {
                Off = 0,
                MaxPerformance = 1,
                Balanced = 2,
                MaxQuality = 3,
                UltraPerformance = 4,
                UltraQuality = 5,
                Dlaa = 6,
            }

            private enum StreamlineDlssPreset : uint
            {
                Default = 0,
            }

            private enum StreamlineResourceType : byte
            {
                Texture2D = 0,
            }

            private enum StreamlineResourceLifecycle
            {
                OnlyValidNow = 0,
                ValidUntilPresent = 1,
                ValidUntilEvaluate = 2,
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineStructType
            {
                public uint Data1;
                public ushort Data2;
                public ushort Data3;
                public byte Data40;
                public byte Data41;
                public byte Data42;
                public byte Data43;
                public byte Data44;
                public byte Data45;
                public byte Data46;
                public byte Data47;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineBaseStructure
            {
                public IntPtr Next;
                public StreamlineStructType StructType;
                public nuint StructVersion;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlinePreferences
            {
                public StreamlineBaseStructure Base;
                public byte ShowConsole;
                public StreamlineLogLevel LogLevel;
                public IntPtr PathsToPlugins;
                public uint NumPathsToPlugins;
                public IntPtr PathToLogsAndData;
                public IntPtr AllocateCallback;
                public IntPtr ReleaseCallback;
                public IntPtr LogMessageCallback;
                public StreamlinePreferenceFlags Flags;
                public IntPtr FeaturesToLoad;
                public uint NumFeaturesToLoad;
                public uint ApplicationId;
                public StreamlineEngineType Engine;
                public IntPtr EngineVersion;
                public IntPtr ProjectId;
                public StreamlineRenderApi RenderApi;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineVulkanInfo
            {
                public StreamlineBaseStructure Base;
                public Device Device;
                public Instance Instance;
                public PhysicalDevice PhysicalDevice;
                public uint ComputeQueueIndex;
                public uint ComputeQueueFamily;
                public uint GraphicsQueueIndex;
                public uint GraphicsQueueFamily;
                public uint OpticalFlowQueueIndex;
                public uint OpticalFlowQueueFamily;
                public byte UseNativeOpticalFlowMode;
                public uint ComputeQueueCreateFlags;
                public uint GraphicsQueueCreateFlags;
                public uint OpticalFlowQueueCreateFlags;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineViewportHandle
            {
                public StreamlineBaseStructure Base;
                public uint Value;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineExtent
            {
                public uint Top;
                public uint Left;
                public uint Width;
                public uint Height;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineResource
            {
                public StreamlineBaseStructure Base;
                public StreamlineResourceType Type;
                public IntPtr Native;
                public IntPtr Memory;
                public IntPtr View;
                public uint State;
                public uint Width;
                public uint Height;
                public uint NativeFormat;
                public uint MipLevels;
                public uint ArrayLayers;
                public ulong GpuVirtualAddress;
                public uint Flags;
                public uint Usage;
                public uint Reserved;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineResourceTag
            {
                public StreamlineBaseStructure Base;
                public IntPtr Resource;
                public uint Type;
                public StreamlineResourceLifecycle Lifecycle;
                public StreamlineExtent Extent;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat2
            {
                public StreamlineFloat2(float x, float y)
                {
                    X = x;
                    Y = y;
                }

                public float X;
                public float Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat3
            {
                public StreamlineFloat3(float x, float y, float z)
                {
                    X = x;
                    Y = y;
                    Z = z;
                }

                public float X;
                public float Y;
                public float Z;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat4
            {
                public StreamlineFloat4(float x, float y, float z, float w)
                {
                    X = x;
                    Y = y;
                    Z = z;
                    W = w;
                }

                public float X;
                public float Y;
                public float Z;
                public float W;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat4x4
            {
                public StreamlineFloat4 Row0;
                public StreamlineFloat4 Row1;
                public StreamlineFloat4 Row2;
                public StreamlineFloat4 Row3;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineConstants
            {
                public StreamlineBaseStructure Base;
                public StreamlineFloat4x4 CameraViewToClip;
                public StreamlineFloat4x4 ClipToCameraView;
                public StreamlineFloat4x4 ClipToLensClip;
                public StreamlineFloat4x4 ClipToPrevClip;
                public StreamlineFloat4x4 PrevClipToClip;
                public StreamlineFloat2 JitterOffset;
                public StreamlineFloat2 MotionVectorScale;
                public StreamlineFloat2 CameraPinholeOffset;
                public StreamlineFloat3 CameraPosition;
                public StreamlineFloat3 CameraUp;
                public StreamlineFloat3 CameraRight;
                public StreamlineFloat3 CameraForward;
                public float CameraNear;
                public float CameraFar;
                public float CameraFov;
                public float CameraAspectRatio;
                public float MotionVectorsInvalidValue;
                public StreamlineBoolean DepthInverted;
                public StreamlineBoolean CameraMotionIncluded;
                public StreamlineBoolean MotionVectors3D;
                public StreamlineBoolean Reset;
                public StreamlineBoolean OrthographicProjection;
                public StreamlineBoolean MotionVectorsDilated;
                public StreamlineBoolean MotionVectorsJittered;
                public float MinRelativeLinearDepthObjectSeparation;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineDlssOptions
            {
                public StreamlineBaseStructure Base;
                public StreamlineDlssMode Mode;
                public uint OutputWidth;
                public uint OutputHeight;
                public float Sharpness;
                public float PreExposure;
                public float ExposureScale;
                public StreamlineBoolean ColorBuffersHdr;
                public StreamlineBoolean IndicatorInvertAxisX;
                public StreamlineBoolean IndicatorInvertAxisY;
                private byte _padding;
                public StreamlineDlssPreset DlaaPreset;
                public StreamlineDlssPreset QualityPreset;
                public StreamlineDlssPreset BalancedPreset;
                public StreamlineDlssPreset PerformancePreset;
                public StreamlineDlssPreset UltraPerformancePreset;
                public StreamlineDlssPreset UltraQualityPreset;
                public StreamlineBoolean UseAutoExposure;
                public StreamlineBoolean AlphaUpscalingEnabled;
            }
        }
    }
}
