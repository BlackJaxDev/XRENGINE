using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private ImGuiController? _imguiController;
    private OpenGLImGuiBackend? _imguiBackend;
    private OpenGLImGuiMultiViewportController? _imguiMultiViewportController;
    private int _imguiFontValidationCountdown;
    private int _imguiFontAtlasRebuildRequested;

    private const int ImGuiFontValidationIntervalFrames = 120;

    protected override bool SupportsImGui => true;

    private sealed class OpenGLImGuiBackend(OpenGLRenderer renderer, ImGuiController controller) : IImGuiRendererBackend
    {
        private readonly OpenGLRenderer _renderer = renderer;
        private readonly ImGuiController _controller = controller;
        private readonly Action _queueMultiViewportInput = () => renderer._imguiMultiViewportController?.QueueMainViewportInput();

        public void MakeCurrent()
            => _controller.MakeCurrent();

        public void Update(float deltaSeconds)
        {
            if (_renderer._imguiMultiViewportController is not null
                && ImGuiControllerUtilities.TryUpdateWithoutPollingInput(_controller, deltaSeconds, _queueMultiViewportInput))
            {
                return;
            }

            _renderer._imguiMultiViewportController?.ClearQueuedMainMouseWheelEvents();
            _controller.Update(deltaSeconds);
        }

        public void Render()
        {
            using var clipScope = _renderer.PushUiClipSpacePolicy();
            // ImGui's vertex colors and font atlas are authored in sRGB and
            // its draw shader writes those bytes directly. With
            // GL_FRAMEBUFFER_SRGB enabled (so scene rendering can rely on
            // hardware linear->sRGB encoding), the default framebuffer is
            // typically sRGB-capable and would re-encode ImGui's already-
            // sRGB output, washing it out. Disable framebuffer-sRGB while
            // ImGui draws so its bytes pass through unchanged.
            using var _ = FramebufferSrgbScope.Disable(_renderer.Api);
            _controller.Render();
        }

        public void UpdatePlatformWindows(bool deferGpuLifecycle)
        {
            using var clipScope = _renderer.PushUiClipSpacePolicy();
            using var _ = FramebufferSrgbScope.Disable(_renderer.Api);
            _renderer._imguiMultiViewportController?.UpdatePlatformWindows(deferGpuLifecycle);
        }

        public void RenderPlatformWindows()
        {
            using var clipScope = _renderer.PushUiClipSpacePolicy();
            using var _ = FramebufferSrgbScope.Disable(_renderer.Api);
            _renderer._imguiMultiViewportController?.RenderPlatformWindows();
        }
    }

    private readonly ref struct FramebufferSrgbScope
    {
        private readonly Silk.NET.OpenGL.GL _api;
        private readonly bool _wasEnabled;

        private FramebufferSrgbScope(Silk.NET.OpenGL.GL api, bool wasEnabled)
        {
            _api = api;
            _wasEnabled = wasEnabled;
        }

        public static FramebufferSrgbScope Disable(Silk.NET.OpenGL.GL api)
        {
            bool wasEnabled = api.IsEnabled(EnableCap.FramebufferSrgb);
            if (wasEnabled)
                api.Disable(EnableCap.FramebufferSrgb);
            return new FramebufferSrgbScope(api, wasEnabled);
        }

        public void Dispose()
        {
            if (_wasEnabled)
                _api.Enable(EnableCap.FramebufferSrgb);
        }
    }

    private ImGuiController? GetImGuiController()
    {
        var controller = _imguiController;
        if (controller is not null)
            return controller;

        var input = XRWindow.Input;
        if (input is null)
            return null;

        OpenGLImGuiMultiViewportController? multiViewport = null;
        try
        {
            controller = new ImGuiController(Api, XRWindow.Window, input, () =>
            {
                // Silk invokes this callback before it creates the font device
                // texture and before its constructor's first NewFrame(). Dear
                // ImGui requires docking, viewports, platform callbacks, DPI,
                // and font-atlas configuration to be complete by that boundary.
                var io = ImGui.GetIO();
                io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
                ImGuiControllerUtilities.TryUseDefaultEditorFont(io, 18.0f);

                multiViewport = OpenGLImGuiMultiViewportController.TryCreate(this);
                multiViewport?.Install();
            });
        }
        catch
        {
            multiViewport?.Dispose();
            throw;
        }

        ImGuiContextTracker.Register(controller.Context);
        multiViewport?.AttachController(controller);

        // Silk starts an implicit ImGui frame in its constructor. Complete
        // that frame and its platform-window phase before the renderer starts
        // frame 2; otherwise Dear ImGui correctly reports that the required
        // UpdatePlatformWindows call was skipped between frames.
        controller.Render();
        multiViewport?.UpdatePlatformWindows(deferGpuLifecycle: false);
        multiViewport?.RenderPlatformWindows();

        _imguiMultiViewportController = multiViewport;

        _imguiController = controller;
        _imguiBackend = null;
        return controller;
    }

    private OpenGLImGuiBackend? GetOrCreateImGuiBackend()
    {
        var controller = GetImGuiController();
        if (controller is null)
            return null;

        EnsureImGuiFontAtlasValid(controller);

        return _imguiBackend ??= new OpenGLImGuiBackend(this, controller);
    }

    public void ForceRebuildImGuiFontAtlas()
        => Interlocked.Exchange(ref _imguiFontAtlasRebuildRequested, 1);

    private void EnsureImGuiFontAtlasValid(ImGuiController controller)
    {
        bool rebuildRequested = Interlocked.Exchange(ref _imguiFontAtlasRebuildRequested, 0) != 0;
        if (!rebuildRequested && _imguiFontValidationCountdown > 0)
        {
            _imguiFontValidationCountdown--;
            return;
        }

        _imguiFontValidationCountdown = ImGuiFontValidationIntervalFrames;

        try
        {
            controller.MakeCurrent();

            if (rebuildRequested)
            {
                Debug.Textures("Rebuilding queued ImGui font atlas at a frame boundary.");
                ImGuiControllerUtilities.TryUseDefaultEditorFont(controller, 18.0f, forceReload: true);
                return;
            }

            var io = ImGui.GetIO();
            nint texIdPtr = io.Fonts.TexID;
            uint texId = (uint)(nuint)texIdPtr;

            if (texId != 0 && Api.IsTexture(texId))
                return;

            Debug.TexturesWarning($"ImGui font atlas texture became invalid (texId={texId}); rebuilding font device texture.");
            ImGuiControllerUtilities.TryUseDefaultEditorFont(controller, 18.0f, forceReload: true);
        }
        catch (Exception ex)
        {
            Debug.TexturesWarning($"Failed validating/rebuilding ImGui font atlas texture: {ex.Message}");
        }
    }

    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
        => GetOrCreateImGuiBackend();
}
