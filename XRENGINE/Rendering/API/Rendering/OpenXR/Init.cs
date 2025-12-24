using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

/// <summary>
/// Provides an implementation of XR functionality using the OpenXR standard.
/// Handles initialization, session management, swapchain creation, and frame rendering.
/// </summary>
public unsafe partial class OpenXRAPI : XRBase
{
    public OpenXRAPI()
    {
        Api = XR.GetApi();
    }

    ~OpenXRAPI()
    {
        CleanUp();
        Api.Dispose();
    }

    /// <summary>
    /// The system ID used to identify the XR system.
    /// </summary>
    private ulong _systemId;

    /// <summary>
    /// The OpenXR session handle.
    /// </summary>
    private Session _session;

    /// <summary>
    /// The associated window for rendering.
    /// </summary>
    private XRWindow? _window;

    /// <summary>
    /// The number of views (1 for AR phone rendering, 2 for stereo rendering, or 4 for fovated rendering).
    /// </summary>
    private uint _viewCount;

    private Space _appSpace;
    private View[] _views = new View[2];
    private FrameState _frameState;
    private GL? _gl;

    /// <summary>
    /// Gets the OpenXR API instance.
    /// </summary>
    public XR Api { get; private set; }

    /// <summary>
    /// Gets or sets the window associated with this XR session.
    /// Setting a new window triggers initialization or cleanup as appropriate.
    /// </summary>
    public XRWindow? Window
    {
        get => _window;
        set => SetField(ref _window, value);
    }

    /// <summary>
    /// Called before a property changes to perform any necessary cleanup.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="propName">The name of the property.</param>
    /// <param name="field">The current value of the property.</param>
    /// <param name="new">The new value of the property.</param>
    /// <returns>Whether the property change should proceed.</returns>
    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(Window):
                    if (field is not null)
                        CleanUp();
                    break;
            }
        }
        return change;
    }

    /// <summary>
    /// Called after a property changes to perform any necessary initialization.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="propName">The name of the property.</param>
    /// <param name="prev">The previous value of the property.</param>
    /// <param name="field">The new value of the property.</param>
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(Window):
                if (field is not null)
                    Initialize();
                break;
        }
    }

    /// <summary>
    /// Creates an OpenXR session using Vulkan graphics binding.
    /// </summary>
    /// <exception cref="Exception">Thrown when session creation fails.</exception>
    private void CreateVulkanSession()
    {
        if (Window is null)
            throw new Exception("Window is null");

        var requirements = new GraphicsRequirementsVulkanKHR
        {
            Type = StructureType.GraphicsRequirementsVulkanKhr
        };

        if (!Api.TryGetInstanceExtension<KhrVulkanEnable>("", _instance, out var vulkanExtension))
            throw new Exception("Failed to get Vulkan extension");

        if (vulkanExtension.GetVulkanGraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
            throw new Exception("Failed to get Vulkan graphics requirements");

        Debug.Out($"Vulkan requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

        if (Window.Renderer is not VulkanRenderer renderer)
            throw new Exception("Renderer is not a VulkanRenderer.");

        // Get the primary graphics queue family index
        var graphicsFamilyIndex = renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;

        // Check if multiple graphics queues are supported
        bool supportsMultiQueue = renderer.SupportsMultipleGraphicsQueues();
        if (supportsMultiQueue)
        {
            Debug.Out("Multiple graphics queues are supported - enabling parallel eye rendering");
            _parallelRenderingEnabled = true;

            // Store secondary queue for right eye rendering
            // Note: This assumes VulkanRenderer has been modified to expose this functionality
            //_secondaryQueue = renderer.GetSecondaryGraphicsQueue();
        }
        else
        {
            Debug.Out("Multiple graphics queues not supported - using single queue rendering");
            _parallelRenderingEnabled = false;
        }

        var vkBinding = new GraphicsBindingVulkanKHR
        {
            Type = StructureType.GraphicsBindingVulkanKhr,
            Instance = new(renderer.Instance.Handle),
            PhysicalDevice = new(renderer.PhysicalDevice.Handle),
            Device = new(renderer.Device.Handle),
            QueueFamilyIndex = graphicsFamilyIndex,
            QueueIndex = 0 // Main queue for session
        };
        var createInfo = new SessionCreateInfo
        {
            Type = StructureType.SessionCreateInfo,
            SystemId = _systemId,
            Next = &vkBinding
        };
        var result = Api.CreateSession(_instance, ref createInfo, ref _session);
        if (result != Result.Success)
            throw new Exception($"Failed to create session: {result}");
    }

    /// <summary>
    /// Flag indicating if parallel rendering is enabled
    /// </summary>
    private bool _parallelRenderingEnabled = false;

    /// <summary>
    /// Secondary queue for right eye rendering when parallel rendering is enabled
    /// </summary>
    private object? _secondaryQueue = null;

    /// <summary>
    /// Renders a frame for both eyes in the XR device.
    /// This implementation supports parallel rendering of eyes when available.
    /// </summary>
    /// <param name="renderCallback">Callback function to render content to each eye's texture.</param>
    public void RenderFrame(DelRenderToFBO? renderCallback)
    {
        PollEvents();

        if (_sessionState != SessionState.Focused)
            return;

        if (!WaitFrame(out _frameState))
            return;

        if (!BeginFrame())
            return;

        if (!LocateViews())
            return;

        var projectionViews = stackalloc CompositionLayerProjectionView[2];
        var layer = new CompositionLayerProjection
        {
            Type = StructureType.CompositionLayerProjection,
            Views = projectionViews
        };

        var layers = stackalloc CompositionLayerBaseHeader*[1];
        layers[0] = (CompositionLayerBaseHeader*)&layer;
        var frameEndInfo = new FrameEndInfo
        {
            Type = StructureType.FrameEndInfo,
            DisplayTime = _frameState.PredictedDisplayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
            LayerCount = 1,
            Layers = layers
        };

        renderCallback ??= RenderViewportsToSwapchain;

        if (_parallelRenderingEnabled && Window?.Renderer is VulkanRenderer)
        {
            // Parallel rendering path for Vulkan
            RenderEyesInParallel(renderCallback, projectionViews);
        }
        else
        {
            // Sequential rendering path
            for (uint i = 0; i < _viewCount; i++)
                RenderEye(i, renderCallback, projectionViews);
        }

        Api.EndFrame(_session, in frameEndInfo);
    }

    /// <summary>
    /// Renders a single eye (view)
    /// </summary>
    private void RenderEye(uint viewIndex, DelRenderToFBO renderCallback, CompositionLayerProjectionView* projectionViews)
    {
        // Acquire swapchain image
        uint imageIndex = 0;
        Api.AcquireSwapchainImage(_swapchains[viewIndex], null, ref imageIndex);

        // Wait for image ready
        var waitInfo = new SwapchainImageWaitInfo
        {
            Type = StructureType.SwapchainImageWaitInfo,
            Timeout = 1000 // 1 second
        };

        if (Api.WaitSwapchainImage(_swapchains[viewIndex], in waitInfo) != Result.Success)
            return;

        // Render to the texture
        if (_gl is not null)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _swapchainFramebuffers[viewIndex][imageIndex]);
            _gl.Viewport(0, 0, _viewConfigViews[viewIndex].RecommendedImageRectWidth, _viewConfigViews[viewIndex].RecommendedImageRectHeight);
        }

        renderCallback(_swapchainImagesGL[viewIndex][imageIndex].Image, viewIndex);

        // Release the image
        var releaseInfo = new SwapchainImageReleaseInfo
        {
            Type = StructureType.SwapchainImageReleaseInfo
        };
        Api.ReleaseSwapchainImage(_swapchains[viewIndex], in releaseInfo);

        _gl?.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Setup projection view
        projectionViews[viewIndex].Type = StructureType.View;
        projectionViews[viewIndex].Fov = _views[viewIndex].Fov;
        projectionViews[viewIndex].Pose = _views[viewIndex].Pose;
        // Set the swapchain image
        projectionViews[viewIndex].SubImage.Swapchain = _swapchains[viewIndex];
        projectionViews[viewIndex].SubImage.ImageRect = new Rect2Di
        {
            Offset = new Offset2Di
            {
                X = 0,
                Y = 0
            },
            Extent = new Extent2Di
            {
                Width = (int)_viewConfigViews[viewIndex].RecommendedImageRectWidth,
                Height = (int)_viewConfigViews[viewIndex].RecommendedImageRectHeight
            }
        };
    }

    /// <summary>
    /// Renders both eyes in parallel using multiple graphics queues
    /// </summary>
    private void RenderEyesInParallel(DelRenderToFBO renderCallback, CompositionLayerProjectionView* projectionViews)
    {
        // For Vulkan renderer, start two parallel rendering tasks
        Task leftEyeTask = Task.Run(() => RenderEye(0, renderCallback, projectionViews));
        Task rightEyeTask = Task.Run(() => RenderEye(1, renderCallback, projectionViews));

        // Wait for both eyes to complete rendering
        Task.WaitAll(leftEyeTask, rightEyeTask);
    }

    /// <summary>
    /// Initializes Vulkan swapchains for stereo rendering
    /// </summary>
    private unsafe void InitializeVulkanSwapchains(VulkanRenderer renderer)
    {
        // Get view configuration
        var viewConfigType = ViewConfigurationType.PrimaryStereo;
        _viewCount = 0;
        Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);

        if (_viewCount != 2)
        {
            throw new Exception($"Expected 2 views, got {_viewCount}");
        }

        _views = new View[_viewCount];

        fixed (ViewConfigurationView* viewConfigViewsPtr = _viewConfigViews)
        {
            Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, _viewCount, ref _viewCount, viewConfigViewsPtr);
        }

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            var swapchainCreateInfo = new SwapchainCreateInfo
            {
                Type = StructureType.SwapchainCreateInfo,
                UsageFlags = SwapchainUsageFlags.ColorAttachmentBit,
                Format = 37 /* VK_FORMAT_R8G8B8A8_SRGB */,
                SampleCount = 1,
                Width = (uint)_viewConfigViews[i].RecommendedImageRectWidth,
                Height = (uint)_viewConfigViews[i].RecommendedImageRectHeight,
                FaceCount = 1,
                ArraySize = 1,
                MipCount = 1
            };

            fixed (Swapchain* swapchainPtr = &_swapchains[i])
            {
                if (Api.CreateSwapchain(_session, in swapchainCreateInfo, swapchainPtr) != Result.Success)
                {
                    throw new Exception($"Failed to create swapchain for view {i}");
                }
            }

            // Get swapchain images
            uint imageCount = 0;
            Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null);

            _swapchainImagesVK[i] = (SwapchainImageVulkan2KHR*)Marshal.AllocHGlobal((int)imageCount * sizeof(SwapchainImageVulkan2KHR));

            for (uint j = 0; j < imageCount; j++)
                _swapchainImagesVK[i][j].Type = StructureType.SwapchainImageVulkan2Khr;

            Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)_swapchainImagesVK[i]);

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({swapchainCreateInfo.Width}x{swapchainCreateInfo.Height})");
        }
    }
    /// <summary>
    /// Initializes the OpenXR session and associated resources.
    /// </summary>
    protected void Initialize()
    {
        CreateInstance();
        CreateSystem();
        switch (Window?.Renderer)
        {
            case OpenGLRenderer renderer:
                CreateOpenGLSession(renderer);
                CreateReferenceSpace();
                InitializeOpenGLSwapchains(renderer);
                Window.RenderViewportsCallback += Window_RenderViewportsCallback;
                break;
            case VulkanRenderer renderer:
                CreateVulkanSession();
                CreateReferenceSpace();
                InitializeVulkanSwapchains(renderer);
                Window.RenderViewportsCallback += Window_RenderViewportsCallback;
                break;
            //case D3D12Renderer renderer:
            //    throw new NotImplementedException("DirectX 12 renderer not implemented");
            default:
                throw new Exception("Unsupported renderer");
        }
        SetupDebugMessenger();
    }

    private void Window_RenderViewportsCallback()
    {
        RenderFrame(null);
    }

    /// <summary>
    /// Creates an OpenXR system for the specified form factor.
    /// </summary>
    private void CreateSystem()
    {
        var systemGetInfo = new SystemGetInfo
        {
            Type = StructureType.SystemGetInfo,
            FormFactor = FormFactor.HeadMountedDisplay
        };
        var result = Api.GetSystem(_instance, in systemGetInfo, ref _systemId);
        if (result != Result.Success)
        {
            throw new Exception($"Failed to get system: {result}");
        }
    }

    private void CreateReferenceSpace()
    {
        var spaceCreateInfo = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = ReferenceSpaceType.Local,
            PoseInReferenceSpace = new Posef
            {
                Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
                Position = new Vector3f { X = 0, Y = 0, Z = 0 }
            }
        };

        Space space = default;
        if (Api.CreateReferenceSpace(_session, in spaceCreateInfo, ref space) != Result.Success)
            throw new Exception("Failed to create reference space");

        _appSpace = space;
    }

    ///// <summary>
    ///// Creates an OpenXR session using Vulkan graphics binding.
    ///// </summary>
    ///// <exception cref="Exception">Thrown when session creation fails.</exception>
    //private void CreateVulkanSession()
    //{
    //    if (Window is null)
    //        throw new Exception("Window is null");

    //    var requirements = new GraphicsRequirementsVulkanKHR
    //    {
    //        Type = StructureType.GraphicsRequirementsVulkanKhr
    //    };

    //    if (!Api.TryGetInstanceExtension<KhrVulkanEnable>("", instance, out var vulkanExtension))
    //        throw new Exception("Failed to get Vulkan extension");

    //    if (vulkanExtension.GetVulkanGraphicsRequirements(instance, _systemId, ref requirements) != Result.Success)
    //        throw new Exception("Failed to get Vulkan graphics requirements");

    //    Debug.Out($"Vulkan requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

    //    if (Window.Renderer is not VulkanRenderer renderer)
    //        throw new Exception("Renderer is not a VulkanRenderer.");

    //    var graphicsFamilyIndex = renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;

    //    var vkBinding = new GraphicsBindingVulkanKHR
    //    {
    //        Type = StructureType.GraphicsBindingVulkanKhr,
    //        Instance = new(renderer.Instance.Handle),
    //        PhysicalDevice = new(renderer.PhysicalDevice.Handle),
    //        Device = new(renderer.Device.Handle),
    //        QueueFamilyIndex = graphicsFamilyIndex,
    //        QueueIndex = 0
    //    };
    //    var createInfo = new SessionCreateInfo
    //    {
    //        Type = StructureType.SessionCreateInfo,
    //        SystemId = _systemId,
    //        Next = &vkBinding
    //    };
    //    var result = Api.CreateSession(instance, ref createInfo, ref _session);
    //    if (result != Result.Success)
    //        throw new Exception($"Failed to create session: {result}");
    //}

    /// <summary>
    /// Creates an OpenXR session using OpenGL graphics binding.
    /// </summary>
    /// <exception cref="Exception">Thrown when session creation fails.</exception>
    private void CreateOpenGLSession(OpenGLRenderer renderer)
    {
        if (Window is null)
            throw new Exception("Window is null");

        _gl = renderer.RawGL;

        var requirements = new GraphicsRequirementsOpenGLKHR
        {
            Type = StructureType.GraphicsRequirementsOpenglKhr
        };

        if (!Api.TryGetInstanceExtension<KhrOpenglEnable>("", _instance, out var openglExtension))
            throw new Exception("Failed to get OpenGL extension");

        if (openglExtension.GetOpenGlgraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
            throw new Exception("Failed to get OpenGL graphics requirements");

        Debug.Out($"OpenGL requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

        var w = Window.Window;
        var glBinding = new GraphicsBindingOpenGLWin32KHR
        {
            Type = StructureType.GraphicsBindingOpenglWin32Khr,
            HDC = w.Native?.Win32?.HDC ?? 0,
            HGlrc = w.GLContext?.Handle ?? 0
        };
        var createInfo = new SessionCreateInfo
        {
            Type = StructureType.SessionCreateInfo,
            SystemId = _systemId,
            Next = &glBinding
        };
        var result = Api.CreateSession(_instance, ref createInfo, ref _session);
        if (result != Result.Success)
            throw new Exception($"Failed to create session: {result}");
    }

    /// <summary>
    /// Current state of the OpenXR session.
    /// </summary>
    private SessionState _sessionState = SessionState.Unknown;

    /// <summary>
    /// Configuration information for each view (eye).
    /// </summary>
    private readonly ViewConfigurationView[] _viewConfigViews = new ViewConfigurationView[2];

    /// <summary>
    /// Swapchain handles for each view.
    /// </summary>
    private readonly Swapchain[] _swapchains = new Swapchain[2];

    /// <summary>
    /// OpenGL swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageOpenGLKHR*[] _swapchainImagesGL = new SwapchainImageOpenGLKHR*[2];

    /// <summary>
    /// OpenGL framebuffer handles for each swapchain image.
    /// </summary>
    private readonly uint[][] _swapchainFramebuffers = new uint[2][];

    /// <summary>
    /// Number of swapchain images per view.
    /// </summary>
    private readonly uint[] _swapchainImageCounts = new uint[2];

    /// <summary>
    /// Vulkan swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageVulkan2KHR*[] _swapchainImagesVK = new SwapchainImageVulkan2KHR*[2];

    /// <summary>
    /// DirectX swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageD3D12KHR*[] _swapchainImagesDX = new SwapchainImageD3D12KHR*[2];

    /// <summary>
    /// Initializes OpenGL swapchains for stereo rendering.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer to use.</param>
    /// <exception cref="Exception">Thrown when swapchain creation fails.</exception>
    private unsafe void InitializeOpenGLSwapchains(OpenGLRenderer renderer)
    {
        if (_gl is null)
            throw new Exception("OpenGL context not initialized for OpenXR");

        // Get view configuration
        var viewConfigType = ViewConfigurationType.PrimaryStereo;
        _viewCount = 0;
        Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);

        if (_viewCount != 2)
            throw new Exception($"Expected 2 views, got {_viewCount}");

        _views = new View[_viewCount];
        
        fixed (ViewConfigurationView* viewConfigViewsPtr = _viewConfigViews)
        {
            Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, _viewCount, ref _viewCount, viewConfigViewsPtr);
        }

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            var swapchainCreateInfo = new SwapchainCreateInfo
            {
                Type = StructureType.SwapchainCreateInfo,
                UsageFlags = SwapchainUsageFlags.ColorAttachmentBit,
                Format = (long)GLEnum.Rgba8,
                SampleCount = 1,
                Width = (uint)_viewConfigViews[i].RecommendedImageRectWidth,
                Height = (uint)_viewConfigViews[i].RecommendedImageRectHeight,
                FaceCount = 1,
                ArraySize = 1,
                MipCount = 1
            };

            fixed (Swapchain* swapchainPtr = &_swapchains[i])
            {
                if (Api.CreateSwapchain(_session, in swapchainCreateInfo, swapchainPtr) != Result.Success)
                    throw new Exception($"Failed to create swapchain for view {i}");
            }

            // Get swapchain images
            uint imageCount = 0;
            Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null);

            _swapchainImagesGL[i] = (SwapchainImageOpenGLKHR*)Marshal.AllocHGlobal((int)imageCount * sizeof(SwapchainImageOpenGLKHR));

            _swapchainImageCounts[i] = imageCount;
            _swapchainFramebuffers[i] = new uint[imageCount];

            for (uint j = 0; j < imageCount; j++)
                _swapchainImagesGL[i][j].Type = StructureType.SwapchainImageOpenglKhr;

            Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)_swapchainImagesGL[i]);

            for (uint j = 0; j < imageCount; j++)
            {
                uint fbo = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _swapchainImagesGL[i][j].Image, 0);
                _swapchainFramebuffers[i][j] = fbo;
            }

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({swapchainCreateInfo.Width}x{swapchainCreateInfo.Height})");
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Delegate for rendering to a framebuffer texture.
    /// </summary>
    /// <param name="textureHandle">OpenGL texture handle to render to.</param>
    /// <param name="viewIndex">Index of the view (eye) being rendered.</param>
    public delegate void DelRenderToFBO(uint textureHandle, uint viewIndex);

    private void RenderViewportsToSwapchain(uint textureHandle, uint viewIndex)
    {
        if (Window is null)
            return;

        if (Window.Renderer is OpenGLRenderer renderer)
        {
            var previous = AbstractRenderer.Current;
            try
            {
                renderer.Active = true;
                AbstractRenderer.Current = renderer;
                _gl?.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
                Window.RenderViewports();
            }
            finally
            {
                renderer.Active = false;
                AbstractRenderer.Current = previous;
            }
        }
    }

    ///// <summary>
    ///// Renders a frame for both eyes in the XR device.
    ///// </summary>
    ///// <param name="renderCallback">Callback function to render content to each eye's texture.</param>
    //public void RenderFrame(DelRenderToFBO renderCallback)
    //{
    //    PollEvents();

    //    if (_sessionState != SessionState.Focused)
    //        return;

    //    WaitFrame(out FrameState frameState);
    //    BeginFrame();

    //    var projectionViews = stackalloc CompositionLayerProjectionView[2];
    //    var layer = new CompositionLayerProjection
    //    {
    //        Type = StructureType.CompositionLayerProjection,
    //        Views = projectionViews
    //    };

    //    var layers = stackalloc CompositionLayerBaseHeader*[1];
    //    layers[0] = (CompositionLayerBaseHeader*)&layer;
    //    var frameEndInfo = new FrameEndInfo
    //    {
    //        Type = StructureType.FrameEndInfo,
    //        DisplayTime = frameState.PredictedDisplayTime,
    //        EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
    //        LayerCount = 1,
    //        Layers = layers
    //    };

    //    for (uint i = 0; i < _viewCount; i++)
    //    {
    //        // Acquire swapchain image
    //        uint imageIndex = 0;
    //        Api.AcquireSwapchainImage(_swapchains[i], null, ref imageIndex);

    //        // Wait for image ready
    //        var waitInfo = new SwapchainImageWaitInfo
    //        {
    //            Type = StructureType.SwapchainImageWaitInfo,
    //            Timeout = 1000 // 1 second
    //        };

    //        if (Api.WaitSwapchainImage(_swapchains[i], in waitInfo) != Result.Success)
    //            continue;

    //        // Render to the texture
    //        renderCallback(_swapchainImagesGL[i][imageIndex].Image, i);

    //        // Release the image
    //        var releaseInfo = new SwapchainImageReleaseInfo
    //        {
    //            Type = StructureType.SwapchainImageReleaseInfo
    //        };
    //        Api.ReleaseSwapchainImage(_swapchains[i], in releaseInfo);

    //        // Setup projection view
    //        projectionViews[i].Type = StructureType.View;
    //        projectionViews[i].Fov = new Fovf
    //        {
    //            AngleLeft = -0.5f,
    //            AngleRight = 0.5f,
    //            AngleUp = 0.5f,
    //            AngleDown = -0.5f
    //        };
    //        // Set the pose to identity for now
    //        projectionViews[i].Pose = new Posef
    //        {
    //            Orientation = new Quaternionf
    //            {
    //                X = 0,
    //                Y = 0,
    //                Z = 0,
    //                W = 1
    //            },
    //            Position = new Vector3f
    //            {
    //                X = i * 0.1f,
    //                Y = 0,
    //                Z = 0
    //            }
    //        };
    //        // Set the swapchain image
    //        projectionViews[i].SubImage.Swapchain = _swapchains[i];
    //        projectionViews[i].SubImage.ImageRect = new Rect2Di
    //        {
    //            Offset = new Offset2Di
    //            {
    //                X = 0,
    //                Y = 0
    //            },
    //            Extent = new Extent2Di
    //            {
    //                Width = (int)_viewConfigViews[i].RecommendedImageRectWidth,
    //                Height = (int)_viewConfigViews[i].RecommendedImageRectHeight
    //            }
    //        };
    //    }

    //    Api.EndFrame(_session, in frameEndInfo);
    //}

    /// <summary>
    /// Begins an OpenXR frame.
    /// </summary>
    /// <returns>True if the frame was successfully begun, false otherwise.</returns>
    private bool BeginFrame()
    {
        var frameBeginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        if (Api.BeginFrame(_session, in frameBeginInfo) != Result.Success)
        {
            Debug.LogWarning("Failed to begin OpenXR frame.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Waits for the next frame timing from the OpenXR runtime.
    /// </summary>
    /// <param name="frameState">Returns the frame state information.</param>
    /// <returns>True if successfully waited for the frame, false otherwise.</returns>
    private bool WaitFrame(out FrameState frameState)
    {
        var frameWaitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        frameState = new FrameState { Type = StructureType.FrameState };
        if (Api.WaitFrame(_session, in frameWaitInfo, ref frameState) != Result.Success)
        {
            Debug.LogWarning("Failed to wait for OpenXR frame.");
            return false;
        }
        _frameState = frameState;
        return true;
    }

    private bool LocateViews()
    {
        var viewLocateInfo = new ViewLocateInfo
        {
            Type = StructureType.ViewLocateInfo,
            DisplayTime = _frameState.PredictedDisplayTime,
            Space = _appSpace,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo
        };

        var viewState = new ViewState { Type = StructureType.ViewState };
        uint viewCountOutput = _viewCount;
        fixed (View* viewsPtr = _views)
        {
            var viewsSpan = new Span<View>(viewsPtr, (int)_viewCount);
            if (Api.LocateView(_session, &viewLocateInfo, &viewState, &viewCountOutput, viewsSpan) != Result.Success)
            {
                Debug.LogWarning("Failed to locate OpenXR views.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Polls for OpenXR events and handles them appropriately.
    /// </summary>
    private void PollEvents()
    {
        EventDataBuffer eventData = new();
        while (Api.PollEvent(_instance, ref eventData) == Result.Success)
        {
            switch (eventData.Type)
            {
                case StructureType.EventDataSessionStateChanged:
                    {
                        var stateChanged = (EventDataSessionStateChanged*)eventData.Varying;
                        _sessionState = stateChanged->State;
                        Debug.Out($"Session state changed to: {_sessionState}");
                        if (_sessionState == SessionState.Ready)
                        {
                            var beginInfo = new SessionBeginInfo
                            {
                                Type = StructureType.SessionBeginInfo,
                                PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo
                            };

                            if (Api.BeginSession(_session, in beginInfo) == Result.Success)
                            {
                                _sessionState = SessionState.Synchronized;
                                Debug.Out("Session began successfully");
                            }
                        }
                    }
                    break;
                default:
                    Debug.Out(eventData.Type.ToString());
                    break;
            }
        }
    }

    /// <summary>
    /// Cleans up OpenXR resources.
    /// </summary>
    protected void CleanUp()
    {
        if (Window is not null)
            Window.RenderViewportsCallback -= Window_RenderViewportsCallback;

        // Cleanup swapchains
        for (int i = 0; i < _viewCount; i++)
        {
            if (_swapchainFramebuffers[i] is not null && _gl is not null)
                foreach (var fbo in _swapchainFramebuffers[i])
                    _gl.DeleteFramebuffer(fbo);

            if (_swapchainImagesGL[i] != null)
                Marshal.FreeHGlobal((nint)_swapchainImagesGL[i]);
            if (_swapchains[i].Handle != 0)
                Api.DestroySwapchain(_swapchains[i]);
        }

        if (_appSpace.Handle != 0)
            Api.DestroySpace(_appSpace);

        if (_session.Handle != 0)
            Api.DestroySession(_session);

        DestroyValidationLayers();
        DestroyInstance();
    }
}
