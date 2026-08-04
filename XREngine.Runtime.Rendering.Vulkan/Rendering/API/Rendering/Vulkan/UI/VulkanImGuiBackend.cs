using ImGuiNET;
using System;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

internal unsafe sealed class VulkanImGuiBackend : IImGuiRendererBackend, IDisposable
{
    private readonly VulkanRenderer _renderer;
    private readonly VulkanImGuiInputRouter _input;
    private readonly IntPtr _context;
    private bool _disposed;


    public IntPtr ContextHandle => _context;

    public VulkanImGuiBackend(VulkanRenderer renderer)
    {
        _renderer = renderer;
        _input = new VulkanImGuiInputRouter(renderer);
        _context = ImGui.CreateContext();
        ImGuiContextTracker.Register(_context);
        MakeCurrent();

        // ImGui.NewFrame() asserts that the font atlas has been built.
        // The GPU texture upload happens later in EnsureImGuiFontResources(),
        // but the CPU-side atlas must be built now so NewFrame() doesn't AV.
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        if (!ImGuiFontAtlasUtilities.TryUseDefaultEditorFont(io, 18.0f))
        {
            if (io.Fonts.Fonts.Size == 0)
                io.Fonts.AddFontDefault();
            io.Fonts.Build();
        }

        // The draw-list command header snapshots the atlas texture ID at frame
        // start. Assign the backend's reserved font ID before the first frame so
        // the render-resource upload cannot change it while AddText is running.
        io.Fonts.SetTexID((IntPtr)1);

        // Enable docking early so DockContextInitialize runs on the first
        // NewFrame().  Without this, the INI's [Docking][Data] section would be
        // silently ignored because no docking handler is registered.
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        VulkanImGuiClipboard.InstallCallbacks();
        _input.TryAttachInputHandlers();
    }

    public void MakeCurrent()
    {
        if (_disposed)
            return;

        ImGui.SetCurrentContext(_context);
    }

    public void Update(float deltaSeconds)
    {
        if (_disposed || !ImGuiContextTracker.IsAlive(_context))
            return;

        MakeCurrent();
        if (ImGui.GetCurrentContext() != _context)
            return;

        var io = ImGui.GetIO();
        io.DeltaTime = deltaSeconds > 0f ? deltaSeconds : 1f / 60f;

        _input.TryAttachInputHandlers();
        _input.PushModifierKeyState(io);
        _input.FlushPendingInputEvents(io);

        ImGui.NewFrame();
    }

    public void Render()
    {
        if (_disposed || !ImGuiContextTracker.IsAlive(_context))
            return;

        MakeCurrent();
        if (ImGui.GetCurrentContext() != _context)
            return;

        ImGui.Render();
        var drawData = ImGui.GetDrawData();
        if (drawData.NativePtr == null)
            return;

        _renderer.StoreImGuiDrawData(drawData);
    }

    public void RenderPlatformWindows()
    {
        // Vulkan multi-viewports require a swapchain/render-pass path per
        // platform window. The OpenGL backend owns the current implementation.
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _input.Dispose();
        if (ImGuiContextTracker.IsAlive(_context))
        {
            ImGui.SetCurrentContext(_context);
            ImGuiFontAtlasUtilities.MarkContextDestroyed(_context);
            ImGuiContextTracker.Unregister(_context);
            ImGui.DestroyContext(_context);
        }
    }
}
