using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering.Vulkan;

namespace XREngine;

[Serializable]
[MemoryPackable]
public partial class OpenGLRenderSettings : XRBase
{
    private OpenGLContextSettings _context = new();
    private OpenGLShaderLinkingSettings _shaderLinking = new();
    private OpenGLTextureUploadSettings _textureUpload = new();
    private OpenGLDiagnosticsSettings _diagnostics = new();
    private bool _allowProgramPipelines = true;

    public OpenGLRenderSettings()
        => AttachSubSettings(_context, _shaderLinking, _textureUpload, _diagnostics);

    [Category("OpenGL")]
    [Description("OpenGL context creation policy. Debug context changes require window recreation.")]
    public OpenGLContextSettings Context
    {
        get => _context;
        set => SetField(ref _context, value ?? new OpenGLContextSettings());
    }

    [Category("OpenGL")]
    [Description("OpenGL shader compile/link, binary cache, and driver-parallel compile policy.")]
    public OpenGLShaderLinkingSettings ShaderLinking
    {
        get => _shaderLinking;
        set => SetField(ref _shaderLinking, value ?? new OpenGLShaderLinkingSettings());
    }

    [Category("OpenGL")]
    [Description("OpenGL texture upload and mip generation policy.")]
    public OpenGLTextureUploadSettings TextureUpload
    {
        get => _textureUpload;
        set => SetField(ref _textureUpload, value ?? new OpenGLTextureUploadSettings());
    }

    [Category("OpenGL")]
    [Description("OpenGL diagnostics and trace policy.")]
    public OpenGLDiagnosticsSettings Diagnostics
    {
        get => _diagnostics;
        set => SetField(ref _diagnostics, value ?? new OpenGLDiagnosticsSettings());
    }

    [Category("OpenGL")]
    [DisplayName("Allow Program Pipelines")]
    [Description("Allows OpenGL program-pipeline composition instead of requiring monolithic linked programs.")]
    public bool AllowProgramPipelines
    {
        get => _allowProgramPipelines;
        set => SetField(ref _allowProgramPipelines, value);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(Context))
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
        else if (propName == nameof(ShaderLinking))
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
        else if (propName == nameof(TextureUpload))
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
        else if (propName == nameof(Diagnostics))
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
    }

    private void AttachSubSettings(params IXRNotifyPropertyChanged?[] settings)
    {
        for (int i = 0; i < settings.Length; i++)
        {
            if (settings[i] is not null)
                settings[i]!.PropertyChanged += HandleSubSettingsChanged;
        }
    }

    private static void RefreshSubSettings<T>(T previous, T current, XRPropertyChangedEventHandler handler)
    {
        if (previous is IXRNotifyPropertyChanged previousNotify)
            previousNotify.PropertyChanged -= handler;

        if (current is IXRNotifyPropertyChanged currentNotify)
            currentNotify.PropertyChanged += handler;
    }

    private void HandleSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        => OnPropertyChanged(e.PropertyName, e.PreviousValue, e.NewValue);
}

[Serializable]
[MemoryPackable]
public partial class OpenGLContextSettings : XRBase
{
    private bool _debugContext;

    [Category("OpenGL")]
    [DisplayName("Debug Context")]
    [Description("Requests an OpenGL debug context on newly-created windows. Requires window recreation.")]
    public bool DebugContext
    {
        get => _debugContext;
        set => SetField(ref _debugContext, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class OpenGLShaderLinkingSettings : XRBase
{
    private bool _allowBinaryProgramCaching = true;
    private bool _asyncProgramBinaryUpload = true;
    private bool _asyncProgramCompilation = true;
    private int _programCompileLinkWorkerCount = 1;
    private int _maxAsyncShaderProgramsPerFrame = 16;
    private EOpenGLShaderLinkStrategy _strategy = EOpenGLShaderLinkStrategy.Auto;
    private int _driverCompilerThreadCount = 1;
    private bool _driverParallelProbeEnabled = true;
    private int _driverParallelProbeTimeoutMs = 25;

    [Category("OpenGL Shader Linking")]
    [Description("Allows linked OpenGL shader programs to be cached as driver binaries.")]
    public bool AllowBinaryProgramCaching
    {
        get => _allowBinaryProgramCaching;
        set => SetField(ref _allowBinaryProgramCaching, value);
    }

    [Category("OpenGL Shader Linking")]
    [Description("Uploads cached OpenGL program binaries on a shared GL context thread when possible.")]
    public bool AsyncProgramBinaryUpload
    {
        get => _asyncProgramBinaryUpload;
        set => SetField(ref _asyncProgramBinaryUpload, value);
    }

    [Category("OpenGL Shader Linking")]
    [Description("Compiles and links uncached OpenGL shader programs asynchronously when the selected strategy supports it.")]
    public bool AsyncProgramCompilation
    {
        get => _asyncProgramCompilation;
        set => SetField(ref _asyncProgramCompilation, value);
    }

    [Category("OpenGL Shader Linking")]
    [Description("Number of shared-context worker threads used to compile and link uncached OpenGL shader programs.")]
    public int ProgramCompileLinkWorkerCount
    {
        get => _programCompileLinkWorkerCount;
        set => SetField(ref _programCompileLinkWorkerCount, Math.Clamp(value, 1, 16));
    }

    [Category("OpenGL Shader Linking")]
    [Description("Maximum number of pending async OpenGL shader programs to advance per render frame.")]
    public int MaxAsyncShaderProgramsPerFrame
    {
        get => _maxAsyncShaderProgramsPerFrame;
        set => SetField(ref _maxAsyncShaderProgramsPerFrame, Math.Max(1, value));
    }

    [Category("OpenGL Shader Linking")]
    [Description("Selects the OpenGL shader compile/link path used by startup and runtime shaders.")]
    public EOpenGLShaderLinkStrategy Strategy
    {
        get => _strategy;
        set => SetField(ref _strategy, value);
    }

    [Category("OpenGL Shader Linking")]
    [Description("Worker-thread count requested from GL_ARB/KHR_parallel_shader_compile. -1 requests the driver maximum.")]
    public int DriverCompilerThreadCount
    {
        get => _driverCompilerThreadCount;
        set => SetField(ref _driverCompilerThreadCount, Math.Max(-1, value));
    }

    [Category("OpenGL Shader Linking")]
    [Description("Runs a small startup probe before using the explicit DriverParallel OpenGL link path.")]
    public bool DriverParallelProbeEnabled
    {
        get => _driverParallelProbeEnabled;
        set => SetField(ref _driverParallelProbeEnabled, value);
    }

    [Category("OpenGL Shader Linking")]
    [Description("Maximum time spent polling the startup driver-parallel OpenGL shader-link probe.")]
    public int DriverParallelProbeTimeoutMs
    {
        get => _driverParallelProbeTimeoutMs;
        set => SetField(ref _driverParallelProbeTimeoutMs, Math.Max(0, value));
    }
}

[Serializable]
[MemoryPackable]
public partial class OpenGLTextureUploadSettings : XRBase
{
    private bool _useDetailPreservingComputeMipmaps = true;

    [Category("OpenGL Texture Upload")]
    [Description("Eligible 2D OpenGL textures generate mipmaps with a detail-preserving compute shader instead of glGenerateTextureMipmap.")]
    public bool UseDetailPreservingComputeMipmaps
    {
        get => _useDetailPreservingComputeMipmaps;
        set => SetField(ref _useDetailPreservingComputeMipmaps, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class OpenGLDiagnosticsSettings : XRBase
{
    private int _submitTraceLevel;
    private bool _crashBreadcrumbs;

    [Category("OpenGL Diagnostics")]
    [Description("Write-through log level for OpenGL submit tracing. 0 disables tracing, 1 records basic calls, 2 records verbose row/chunk detail.")]
    public int SubmitTraceLevel
    {
        get => _submitTraceLevel;
        set => SetField(ref _submitTraceLevel, Math.Clamp(value, 0, 2));
    }

    [Category("OpenGL Diagnostics")]
    [Description("Emits OpenGL crash breadcrumbs around high-risk submit and texture paths.")]
    public bool CrashBreadcrumbs
    {
        get => _crashBreadcrumbs;
        set => SetField(ref _crashBreadcrumbs, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class VulkanRenderSettings : XRBase
{
    private VulkanStartupSettings _startup = new();
    private VulkanTargetModeSettings _targetMode = new();
    private VulkanGpuDrivenSettings _gpuDriven = new();
    private VulkanDescriptorSettings _descriptors = new();
    private VulkanSynchronizationSettings _synchronization = new();
    private VulkanMemorySettings _memory = new();
    private VulkanRobustnessSettings _robustness = new();
    private VulkanDiagnosticsSettings _diagnostics = new();

    public VulkanRenderSettings()
        => AttachSubSettings(_startup, _targetMode, _gpuDriven, _descriptors, _synchronization, _memory, _robustness, _diagnostics);

    [Category("Vulkan")]
    [Description("Vulkan startup and backend creation policy.")]
    public VulkanStartupSettings Startup
    {
        get => _startup;
        set => SetField(ref _startup, value ?? new VulkanStartupSettings());
    }

    [Category("Vulkan")]
    [Description("Vulkan render-target representation policy.")]
    public VulkanTargetModeSettings TargetMode
    {
        get => _targetMode;
        set => SetField(ref _targetMode, value ?? new VulkanTargetModeSettings());
    }

    [Category("Vulkan")]
    [Description("Vulkan GPU-driven rendering policy.")]
    public VulkanGpuDrivenSettings GpuDriven
    {
        get => _gpuDriven;
        set => SetField(ref _gpuDriven, value ?? new VulkanGpuDrivenSettings());
    }

    [Category("Vulkan")]
    [Description("Vulkan descriptor indexing, bindless material table, and contract validation policy.")]
    public VulkanDescriptorSettings Descriptors
    {
        get => _descriptors;
        set => SetField(ref _descriptors, value ?? new VulkanDescriptorSettings());
    }

    [Category("Vulkan")]
    [Description("Vulkan synchronization ownership and queue-overlap policy.")]
    public VulkanSynchronizationSettings Synchronization
    {
        get => _synchronization;
        set => SetField(ref _synchronization, value ?? new VulkanSynchronizationSettings());
    }

    [Category("Vulkan")]
    [Description("Vulkan memory and upload policy.")]
    public VulkanMemorySettings Memory
    {
        get => _memory;
        set => SetField(ref _memory, value ?? new VulkanMemorySettings());
    }

    [Category("Vulkan")]
    [Description("Vulkan allocator, synchronization, descriptor-update, and dynamic uniform migration policy.")]
    public VulkanRobustnessSettings Robustness
    {
        get => _robustness;
        set => SetField(ref _robustness, value ?? new VulkanRobustnessSettings());
    }

    [Category("Vulkan")]
    [Description("Vulkan diagnostics and isolation toggles.")]
    public VulkanDiagnosticsSettings Diagnostics
    {
        get => _diagnostics;
        set => SetField(ref _diagnostics, value ?? new VulkanDiagnosticsSettings());
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(Startup)
            || propName == nameof(TargetMode)
            || propName == nameof(GpuDriven)
            || propName == nameof(Descriptors)
            || propName == nameof(Synchronization)
            || propName == nameof(Memory)
            || propName == nameof(Robustness)
            || propName == nameof(Diagnostics))
        {
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
        }
    }

    private void AttachSubSettings(params IXRNotifyPropertyChanged?[] settings)
    {
        for (int i = 0; i < settings.Length; i++)
        {
            if (settings[i] is not null)
                settings[i]!.PropertyChanged += HandleSubSettingsChanged;
        }
    }

    private static void RefreshSubSettings<T>(T previous, T current, XRPropertyChangedEventHandler handler)
    {
        if (previous is IXRNotifyPropertyChanged previousNotify)
            previousNotify.PropertyChanged -= handler;

        if (current is IXRNotifyPropertyChanged currentNotify)
            currentNotify.PropertyChanged += handler;
    }

    private void HandleSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        => OnPropertyChanged(e.PropertyName, e.PreviousValue, e.NewValue);
}

[Serializable]
[MemoryPackable]
public partial class VulkanStartupSettings : XRBase
{
    private RenderBackendFallbackPolicy _fallbackPolicy = RenderBackendFallbackPolicy.RequireRequested;

    [Category("Vulkan Startup")]
    [Description("Controls whether startup may fall back from a requested Vulkan backend to OpenGL.")]
    public RenderBackendFallbackPolicy FallbackPolicy
    {
        get => _fallbackPolicy;
        set => SetField(ref _fallbackPolicy, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class VulkanTargetModeSettings : XRBase
{
    private EVulkanRenderTargetMode _renderTargetMode = EVulkanRenderTargetMode.Auto;

    [Category("Vulkan Target Mode")]
    [Description("Selects dynamic rendering or legacy render-pass render target ownership. Environment variable XRE_VK_RENDER_TARGET_MODE has highest priority.")]
    public EVulkanRenderTargetMode RenderTargetMode
    {
        get => _renderTargetMode;
        set => SetField(ref _renderTargetMode, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class VulkanGpuDrivenSettings : XRBase
{
    private EVulkanGpuDrivenProfile _profile = EVulkanGpuDrivenProfile.Auto;
    private EVulkanGeometryFetchMode _geometryFetchMode = EVulkanGeometryFetchMode.Atlas;

    [Category("Vulkan GPU Driven")]
    [Description("Selects the Vulkan GPU-driven runtime profile used to gate feature policy.")]
    public EVulkanGpuDrivenProfile Profile
    {
        get => _profile;
        set => SetField(ref _profile, value);
    }

    [Category("Vulkan GPU Driven")]
    [Description("Selects optional Vulkan geometry fetch strategy.")]
    public EVulkanGeometryFetchMode GeometryFetchMode
    {
        get => _geometryFetchMode;
        set => SetField(ref _geometryFetchMode, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class VulkanDescriptorSettings : XRBase
{
    private bool _enableDescriptorIndexing = true;
    private bool _enableBindlessMaterialTable = true;
    private EVulkanBindlessMaterialMode _bindlessMaterialMode = EVulkanBindlessMaterialMode.Auto;
    private bool _validateContracts = true;

    [Category("Vulkan Descriptors")]
    [Description("Enables Vulkan descriptor indexing for large runtime descriptor arrays when supported.")]
    public bool EnableDescriptorIndexing
    {
        get => _enableDescriptorIndexing;
        set => SetField(ref _enableDescriptorIndexing, value);
    }

    [Category("Vulkan Descriptors")]
    [Description("Enables global material-table population path for GPU-driven rendering.")]
    public bool EnableBindlessMaterialTable
    {
        get => _enableBindlessMaterialTable;
        set => SetField(ref _enableBindlessMaterialTable, value);
    }

    [Category("Vulkan Descriptors")]
    [Description("Selects Vulkan bindless material-table policy.")]
    public EVulkanBindlessMaterialMode BindlessMaterialMode
    {
        get => _bindlessMaterialMode;
        set => SetField(ref _bindlessMaterialMode, value);
    }

    [Category("Vulkan Descriptors")]
    [Description("Validates descriptor contract tiers against reflected shader bindings.")]
    public bool ValidateContracts
    {
        get => _validateContracts;
        set => SetField(ref _validateContracts, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class VulkanSynchronizationSettings : XRBase
{
    private EVulkanQueueOverlapMode _queueOverlapMode = EVulkanQueueOverlapMode.Auto;

    [Category("Vulkan Synchronization")]
    [Description("Selects Vulkan queue overlap policy for queue-family ownership transitions.")]
    public EVulkanQueueOverlapMode QueueOverlapMode
    {
        get => _queueOverlapMode;
        set => SetField(ref _queueOverlapMode, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class VulkanMemorySettings : XRBase
{
    private bool _preferDeviceLocalUploads = true;

    [Category("Vulkan Memory")]
    [Description("Keeps Vulkan texture and buffer uploads on device-local memory paths when supported.")]
    public bool PreferDeviceLocalUploads
    {
        get => _preferDeviceLocalUploads;
        set => SetField(ref _preferDeviceLocalUploads, value);
    }
}

[Serializable]
[MemoryPackable]
public partial class VulkanDiagnosticsSettings : XRBase
{
    private bool _pipelineCreationTrace;

    [Category("Vulkan Diagnostics")]
    [Description("Traces Vulkan pipeline creation details. Heavy; intended for pipeline bring-up only.")]
    public bool PipelineCreationTrace
    {
        get => _pipelineCreationTrace;
        set => SetField(ref _pipelineCreationTrace, value);
    }
}
