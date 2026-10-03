using XREngine.Extensions;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
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
    private OpenGLImGuiController? _imguiController;
    private OpenGLImGuiBackend? _imguiBackend;
    private OpenGLImGuiMultiViewportController? _imguiMultiViewportController;
    private int _imguiFontValidationCountdown;
    private int _imguiFontAtlasRebuildRequested;

    private const int ImGuiFontValidationIntervalFrames = 120;

    protected override bool SupportsImGui => true;

    private sealed class OpenGLImGuiBackend(OpenGLRenderer renderer, OpenGLImGuiController controller) : IImGuiRendererBackend
    {
        private readonly OpenGLRenderer _renderer = renderer;
        private readonly OpenGLImGuiController _controller = controller;
        private readonly Action _queueMultiViewportInput = () => renderer._imguiMultiViewportController?.QueueMainViewportInput();

        public void MakeCurrent()
            => _controller.MakeCurrent();

        public void Update(float deltaSeconds)
        {
            _controller.Update(deltaSeconds, _queueMultiViewportInput);
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

    private OpenGLImGuiController? GetImGuiController()
    {
        var controller = _imguiController;
        if (controller is not null)
            return controller;

        if (XRWindow.DesktopGlContext is null)
            return null;

        OpenGLImGuiMultiViewportController? multiViewport = null;
        controller = new OpenGLImGuiController(Api, XRWindow, () =>
        {
            // Configure the context before device textures and the first frame.
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            ImGuiControllerUtilities.TryUseDefaultEditorFont(io, 18.0f);

            multiViewport = OpenGLImGuiMultiViewportController.TryCreate(this);
            multiViewport?.Install();
        }, () => multiViewport?.Dispose());

        ImGuiContextTracker.Register(controller.Context);
        multiViewport?.AttachController(controller);

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

    private unsafe void EnsureImGuiFontAtlasValid(OpenGLImGuiController controller)
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
                if (ImGuiFontAtlasUtilities.TryUseDefaultEditorFont((nint)ImGui.GetIO().NativePtr, 18.0f, forceReload: true))
                    controller.RebuildFontTexture();
                return;
            }

            var io = ImGui.GetIO();
            nint texIdPtr = io.Fonts.TexID;
            uint texId = (uint)(nuint)texIdPtr;

            if (texId != 0 && Api.IsTexture(texId))
                return;

            Debug.TexturesWarning($"ImGui font atlas texture became invalid (texId={texId}); rebuilding font device texture.");
            if (ImGuiFontAtlasUtilities.TryUseDefaultEditorFont((nint)ImGui.GetIO().NativePtr, 18.0f, forceReload: true))
                controller.RebuildFontTexture();
        }
        catch (Exception ex)
        {
            Debug.TexturesWarning($"Failed validating/rebuilding ImGui font atlas texture: {ex.Message}");
        }
    }

    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
        => GetOrCreateImGuiBackend();
}
