using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.UI;

/// <summary>An owned world draw from one immutable canvas publication.</summary>
internal sealed class UIPublishedWorldMeshCommand : RenderCommandMesh3D, IUiPublicationRenderCommand
{
    private readonly WeakReference<UICanvasComponent> _canvas;
    private readonly Action _renderFrozen;
    private readonly UIIndexedDrawIntent[] _indexedDrawIntents = new UIIndexedDrawIntent[3];
    private readonly int _indexedDrawIntentCount;

    internal UIPublishedWorldMeshCommand(UICanvasComponent canvas, UIPublication publication,
        RenderCommandMesh3D frozen, bool castsShadows, int layer, Box? cullingVolume)
        : base(frozen.RenderPass)
    {
        _canvas = new WeakReference<UICanvasComponent>(canvas);
        _renderFrozen = RenderFrozen;
        Publication = publication;
        CastsShadows = castsShadows;
        Layer = layer;
        FrozenCullingVolume = cullingVolume;
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

        XRMeshRenderer renderer = Mesh ??
            throw new InvalidOperationException("A frozen world command has no mesh renderer.");
        XRMesh mesh = renderer.Mesh ??
            throw new InvalidOperationException("A frozen world command has no indexed mesh.");
        int count = 0;
        if (mesh.Triangles is { Count: > 0 })
            _indexedDrawIntents[count++] = UIIndexedDrawIntent.Capture(renderer,
                EPrimitiveType.Triangles, Instances);
        if (mesh.Lines is { Count: > 0 })
            _indexedDrawIntents[count++] = UIIndexedDrawIntent.Capture(renderer,
                EPrimitiveType.Lines, Instances);
        if (mesh.Points is { Count: > 0 })
            _indexedDrawIntents[count++] = UIIndexedDrawIntent.Capture(renderer,
                EPrimitiveType.Points, Instances);
        _indexedDrawIntentCount = count;
    }

    public UIPublication Publication { get; }
    internal bool CastsShadows { get; }
    internal int Layer { get; }
    internal Box? FrozenCullingVolume { get; }
    internal ReadOnlySpan<UIIndexedDrawIntent> IndexedDrawIntents
        => _indexedDrawIntents.AsSpan(0, _indexedDrawIntentCount);

    public override void Render()
    {
        if (_canvas.TryGetTarget(out UICanvasComponent? canvas))
            canvas.RenderPublishedWorldCommand(this, _renderFrozen);
    }

    private void RenderFrozen() => base.Render();
}
