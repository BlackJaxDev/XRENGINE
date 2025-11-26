using ImGuiNET;
using Silk.NET.Vulkan;
using System;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private VulkanImGuiBackend? _imguiBackend;
    private readonly ImGuiDrawDataCache _imguiDrawData = new();

    protected override bool SupportsImGui => true;

    private sealed class VulkanImGuiBackend : IImGuiRendererBackend, IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly IntPtr _context;

        public VulkanImGuiBackend(VulkanRenderer renderer)
        {
            _renderer = renderer;
            _context = ImGui.CreateContext();
            ImGuiContextTracker.Register(_context);
            MakeCurrent();
        }

        public void MakeCurrent()
            => ImGui.SetCurrentContext(_context);

        public void Update(float deltaSeconds)
        {
            var io = ImGui.GetIO();
            io.DeltaTime = deltaSeconds > 0f ? deltaSeconds : 1f / 60f;

            uint width = Math.Max(_renderer.swapChainExtent.Width, 1u);
            uint height = Math.Max(_renderer.swapChainExtent.Height, 1u);
            io.DisplaySize = new Vector2(width, height);

            ImGui.NewFrame();
        }

        public void Render()
        {
            ImGui.Render();
            var drawData = ImGui.GetDrawData();
            if (drawData.NativePtr == null)
                return;

            _renderer._imguiDrawData.Store(drawData);
        }

        public void Dispose()
        {
            ImGui.SetCurrentContext(_context);
            ImGuiContextTracker.Unregister(_context);
            ImGui.DestroyContext(_context);
        }
    }

    private VulkanImGuiBackend GetOrCreateImGuiBackend()
        => _imguiBackend ??= new VulkanImGuiBackend(this);

    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
        => GetOrCreateImGuiBackend();

    private void DisposeImGuiResources()
    {
        _imguiBackend?.Dispose();
        _imguiBackend = null;
        _imguiDrawData.Clear();
        ResetImGuiFrameMarker();
    }

    private void RenderImGui(CommandBuffer commandBuffer, uint imageIndex)
    {
        if (!_imguiDrawData.HasPendingData)
            return;

        // TODO: Implement Vulkan ImGui rendering. For now, clear cached data to avoid replays.
        _imguiDrawData.Clear();
    }

    private sealed class ImGuiDrawDataCache
    {
        private ImDrawDataPtr _drawData;

        public bool HasPendingData => _drawData.NativePtr != null;

        public void Store(ImDrawDataPtr drawData)
            => _drawData = drawData;

        public void Clear()
            => _drawData = new ImDrawDataPtr(null);
    }
}
