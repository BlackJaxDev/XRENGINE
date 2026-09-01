using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private VulkanDesktopSwapchainService? _desktopSwapchainService;
    private VulkanTargetOutputContext? _targetOutputSession;
    private VulkanImGuiOverlayAdmission? _imguiOverlayAdmission;
    private VulkanImGuiFontAtlasResources? _imguiFontAtlasResources;
    private VulkanImGuiDrawBufferResources? _imguiDrawBufferResources;
    private VulkanImGuiOutputPipelineService? _imguiOutputPipelineService;
    private VulkanImGuiTextureOutputResources? _imguiTextureOutputResources;
    private VulkanImGuiTextureRegistryService? _imguiTextureRegistryService;
    private VulkanOpenXrOutputResourceService? _openXrOutputResourceService;
    private VulkanReadbackOutputResourceService? _readbackOutputResourceService;

    internal VulkanDesktopSwapchainService DesktopSwapchainService
        => _desktopSwapchainService ?? throw OutputServicesNotAttached();
    internal VulkanTargetOutputContext TargetOutputSession
        => _targetOutputSession ?? throw OutputServicesNotAttached();
    internal VulkanImGuiOverlayAdmission ImGuiOverlayAdmission
        => _imguiOverlayAdmission ?? throw OutputServicesNotAttached();
    internal VulkanImGuiFontAtlasResources ImGuiFontAtlasResources
        => _imguiFontAtlasResources ?? throw OutputServicesNotAttached();
    internal VulkanImGuiDrawBufferResources ImGuiDrawBufferResources
        => _imguiDrawBufferResources ?? throw OutputServicesNotAttached();
    internal VulkanImGuiOutputPipelineService ImGuiOutputPipelineService
        => _imguiOutputPipelineService ?? throw OutputServicesNotAttached();
    internal VulkanImGuiTextureOutputResources ImGuiTextureOutputResources
        => _imguiTextureOutputResources ?? throw OutputServicesNotAttached();
    internal VulkanImGuiTextureRegistryService ImGuiTextureRegistryService
        => _imguiTextureRegistryService ?? throw OutputServicesNotAttached();

    internal bool TryGetTexturePreviewHandle(
        XRTexture texture,
        in RenderTexturePreviewOptions options,
        out nint handle,
        out bool requiresVerticalFlip,
        out string? failureReason)
    {
        IntPtr textureId = ImGuiTextureRegistryService.RegisterImGuiTexture(texture);
        handle = (nint)textureId;
        requiresVerticalFlip = false;
        failureReason = textureId == IntPtr.Zero
            ? "Texture has not been uploaded to the GPU yet."
            : null;
        return textureId != IntPtr.Zero;
    }
    internal VulkanOpenXrOutputResourceService OpenXrOutputResourceService
        => _openXrOutputResourceService ?? throw OutputServicesNotAttached();
    internal VulkanReadbackOutputResourceService ReadbackOutputResourceService
        => _readbackOutputResourceService ?? throw OutputServicesNotAttached();

    internal void AttachOutputServices(Vk api)
    {
        if (_targetOutputSession is not null)
            throw new InvalidOperationException("Vulkan output services are already attached.");

        VulkanTargetOutputContext target = new(this);
        VulkanImGuiOverlayAdmission admission = new(_outputRuntime, _resourceRuntime, _deviceContext);
        VulkanImGuiFontAtlasResources fontAtlas = new(
            _outputRuntime._imguiResources,
            _outputRuntime._imguiTextureRegistry,
            _resourceRuntime,
            _commandRuntime,
            _deviceContext,
            target);
        VulkanImGuiDrawBufferResources drawBuffers = new(_resourceRuntime, target);
        VulkanImGuiOutputPipelineService pipeline = new(_outputRuntime, _resourceRuntime, _deviceContext);
        VulkanImGuiTextureOutputResources textureOutput = new(_deviceContext, _resourceRuntime);
        VulkanImGuiTextureRegistryService textureRegistry = new(
            _outputRuntime._imguiResources,
            _outputRuntime._imguiTextureRegistry,
            _resourceRuntime,
            _commandRuntime,
            _deviceContext,
            textureOutput,
            fontAtlas);

        _targetOutputSession = target;
        _imguiOverlayAdmission = admission;
        _imguiFontAtlasResources = fontAtlas;
        _imguiDrawBufferResources = drawBuffers;
        _imguiOutputPipelineService = pipeline;
        _imguiTextureOutputResources = textureOutput;
        _imguiTextureRegistryService = textureRegistry;
        _desktopSwapchainService = new VulkanDesktopSwapchainService(
            _outputRuntime,
            api,
            _deviceContext,
            _resourceRuntime,
            _telemetry,
            pipeline,
            _targetDriver as VulkanDesktopWsiTargetDriver,
            this,
            FrameSlotCount);
        _openXrOutputResourceService = new VulkanOpenXrOutputResourceService(
            _outputRuntime.OpenXrBackend,
            api,
            _deviceContext,
            _commandRuntime,
            _resourceRuntime,
            _telemetry,
            this);
        _readbackOutputResourceService = new VulkanReadbackOutputResourceService(
            _deviceContext,
            _resourceRuntime,
            _commandRuntime);
    }

    internal void InitializeTargetFinalOutput()
        => _targetDriver.InitializeFinalOutput(TargetOutputSession);

    /// <summary>
    /// Initializes target-compatible terminal UI work once an ImGui context has
    /// built its font atlas. Scene composition variants are intentionally sealed
    /// and made mandatory by PresentNow readiness before acquire because their
    /// pass/material/target identity does not exist at output initialization.
    /// Empty-terminal clear and failure reporting remain pipeline-free so a
    /// shader compiler failure cannot disable the diagnostic path itself.
    /// </summary>
    internal void InitializeMandatoryDesktopPresentNowPipelines()
    {
        if (_targetDriver is not VulkanDesktopWsiTargetDriver ||
            _outputRuntime.Desktop.Swapchain.Handle == 0)
        {
            return;
        }

        ImGuiFontAtlasResources.EnsureCreated();
        ImGuiOutputPipelineService.EnsureMandatoryPresentNowPipeline();
    }

    internal void DestroyTargetFinalOutput()
    {
        if (_targetOutputSession is not { } target)
            return;

        try
        {
            _targetDriver.DestroyFinalOutput(target);
        }
        finally
        {
            _targetOutputSession = null;
        }
    }

    internal void DetachOutputServices()
    {
        _readbackOutputResourceService = null;
        _openXrOutputResourceService = null;
        _desktopSwapchainService = null;
        _imguiTextureRegistryService = null;
        _imguiTextureOutputResources = null;
        _imguiOutputPipelineService = null;
        _imguiDrawBufferResources = null;
        _imguiFontAtlasResources = null;
        _imguiOverlayAdmission = null;
        _targetOutputSession = null;
    }

    internal void CreateTargetInstanceResources(Vk api, Silk.NET.Windowing.IWindow? window)
        => _targetDriver.CreateInstanceResources(
            new VulkanTargetSurfaceAuthority(api, _deviceContext, _outputRuntime, window));

    internal void DestroyTargetInstanceResources(Vk api, Silk.NET.Windowing.IWindow? window)
        => _targetDriver.DestroyInstanceResources(
            new VulkanTargetSurfaceAuthority(api, _deviceContext, _outputRuntime, window));

    internal void DisposeImGuiResources()
    {
        _imguiBackend?.Dispose();
        _imguiBackend = null;
        ImGuiOutputPipelineService.Dispose();
        ImGuiFontAtlasResources.RetireAll();
        ImGuiDrawBufferResources.RetireAll();
        _outputRuntime._imguiDrawData.Clear();
    }

    internal void CreateDesktopWsiGeneration(SwapchainKHR oldSwapchain = default)
        => DesktopSwapchainService.CreateSwapchain(oldSwapchain);
    internal void CreateInitialDesktopSwapchainGeneration()
        => DesktopSwapchainService.CreateInitialGeneration();
    internal void DestroyDesktopWsiGeneration()
        => DesktopSwapchainService.DestroySwapchain();
    internal void DestroyDesktopSwapchainGenerationForShutdown()
        => DesktopSwapchainService.DestroyLiveGenerationForShutdown();
    internal void CreateDesktopPresentBridgeSemaphores(int imageCount)
        => DesktopSwapchainService.CreateLivePresentBridgeSemaphores(imageCount);
    internal void DestroyDesktopPresentBridgeSemaphores()
        => DesktopSwapchainService.DestroyLivePresentBridgeSemaphores();
    internal void CreateDesktopSwapchainImageViews()
        => DesktopSwapchainService.CreateImageViews();
    internal void DestroyDesktopSwapchainImageViews()
        => DesktopSwapchainService.DestroyImageViews();
    internal void CreateDesktopDepthResources()
        => DesktopSwapchainService.CreateDepthResources();
    internal VulkanSwapchainDepthResources? DetachDesktopDepthResources()
        => DesktopSwapchainService.DetachDepthResources();
    internal bool IsDesktopSurfacePresentable(out string reason)
        => DesktopSwapchainService.IsSurfacePresentable(out reason);
    internal SwapChainSupportDetails QueryDesktopSwapchainSupport(PhysicalDevice physicalDevice)
        => DesktopSwapchainService.QuerySupport(physicalDevice);
    internal void DisableDesktopStreamlineFrameGenerationForMutation(string reason)
        => DesktopSwapchainService.DisableStreamlineFrameGenerationBeforeMutation(reason);
    internal void DrainDesktopStreamlineFrameGenerationDisableBeforePresent()
        => DesktopSwapchainService.DrainStreamlineFrameGenerationDisableBeforePresent();
    internal void DrainRetiredDesktopSwapchainGenerations(bool force = false)
        => DesktopSwapchainService.DrainRetiredGenerations(force);
    internal void QueueRetiredDesktopSwapchainGeneration(RetiredSwapchainGeneration generation)
        => DesktopSwapchainService.QueueRetiredGeneration(generation);
    internal bool TryRecreateDesktopSwapchain()
        => DesktopSwapchainService.TryRecreateGeneration();

    private static InvalidOperationException OutputServicesNotAttached()
        => new("Vulkan frame-loop output services are not attached.");
}
