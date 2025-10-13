using ImGuiNET;
using System;
using XREngine.Core.Attributes;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;

namespace XREngine.Components;

/// <summary>
/// Provides a Dear ImGui overlay that renders within a UI canvas.
/// Subscribe to <see cref="Draw"/> to submit ImGui widgets every frame.
/// </summary>
[RequiresTransform(typeof(UITransform))]
public class DearImGuiComponent : UIComponent, IRenderable
{
    private readonly RenderInfo2D _renderInfo;
    private readonly RenderCommandMethod2D _renderCommand;

    private bool _showDemoWindow = true;

    public DearImGuiComponent()
    {
        RenderedObjects =
        [
            _renderInfo = RenderInfo2D.New(
                this,
                _renderCommand = new RenderCommandMethod2D((int)EDefaultRenderPass.OnTopForward, RenderImGui))
        ];
        _renderInfo.CullingVolume = null;
    }

    /// <summary>
    /// Displays the Dear ImGui demo window when true. The value can also be toggled from inside the demo window itself.
    /// </summary>
    public bool ShowDemoWindow
    {
        get => _showDemoWindow;
        set => SetField(ref _showDemoWindow, value);
    }

    /// <summary>
    /// Raised each frame while the ImGui context is current. Use this to issue Dear ImGui commands.
    /// </summary>
    public event Action? Draw;

    public RenderInfo[] RenderedObjects { get; }

    private void RenderImGui()
    {
        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
            return;

        var viewport = Engine.Rendering.State.RenderingViewport;
        renderer.TryRenderImGui(viewport, UserInterfaceCanvas, Engine.Rendering.State.RenderingCamera, OnDraw);
    }

    protected virtual void OnDraw()
    {
        if (ShowDemoWindow)
        {
            bool show = ShowDemoWindow;
            ImGui.ShowDemoWindow(ref show);
            ShowDemoWindow = show;
        }

        Draw?.Invoke();
    }
}
