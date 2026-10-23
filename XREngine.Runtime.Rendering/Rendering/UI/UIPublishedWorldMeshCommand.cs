using XREngine.Components;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.UI;

/// <summary>An owned world draw from one immutable canvas publication.</summary>
internal sealed class UIPublishedWorldMeshCommand : RenderCommandMesh3D, IUiPublicationRenderCommand
{
    private readonly WeakReference<UICanvasComponent> _canvas;
    private readonly Action _renderFrozen;

    internal UIPublishedWorldMeshCommand(UICanvasComponent canvas, UIPublication publication,
        RenderCommandMesh3D frozen, bool castsShadows)
        : base(frozen.RenderPass)
    {
        _canvas = new WeakReference<UICanvasComponent>(canvas);
        _renderFrozen = RenderFrozen;
        Publication = publication;
        CastsShadows = castsShadows;
        Mesh = frozen.Mesh;
        WorldMatrix = frozen.WorldMatrix;
        MaterialOverride = frozen.MaterialOverride;
        RenderOptionsOverride = frozen.RenderOptionsOverride;
        Instances = frozen.Instances;
        WorldMatrixIsModelMatrix = frozen.WorldMatrixIsModelMatrix;
        ForceCpuRendering = frozen.ForceCpuRendering;
        EditorHighlightBits = frozen.EditorHighlightBits;
        WorldCullingVolumeOverride = frozen.WorldCullingVolumeOverride;
        GpuProfilingLabel = frozen.GpuProfilingLabel;
        Enabled = frozen.Enabled;
    }

    public UIPublication Publication { get; }
    internal bool CastsShadows { get; }

    public override void Render()
    {
        if (_canvas.TryGetTarget(out UICanvasComponent? canvas))
            canvas.RenderPublishedWorldCommand(this, _renderFrozen);
    }

    private void RenderFrozen() => base.Render();
}
