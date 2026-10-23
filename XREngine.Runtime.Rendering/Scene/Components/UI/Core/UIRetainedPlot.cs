using System.Numerics;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Reuses bounded solid UI records to draw sampled plot lines.</summary>
public sealed class UIRetainedPlot
{
    public const int MaximumSegments = 128;
    private readonly SceneNode _root;
    private readonly List<(UIBoundableTransform Transform, UIMaterialComponent Material)> _strokes = [];
    private int _used;

    public UIRetainedPlot(SceneNode parent, string name)
    {
        _root = parent.NewChildWithTransform<UIBoundableTransform>(out _, name);
        UIMaterialComponent background = _root.AddComponent<UIMaterialComponent>()!;
        background.SetBatchSolidColor(new Vector4(0.08f, 0.09f, 0.11f, 1.0f));
        background.ClipToBounds = true;
    }

    public float Width => _root.GetTransformAs<UIBoundableTransform>(true)?.ActualWidth ?? 0;
    public float Height => _root.GetTransformAs<UIBoundableTransform>(true)?.ActualHeight ?? 0;

    public void Begin() => _used = 0;

    public void AddLine(Vector2 from, Vector2 to, Vector4 color, float thickness = 2.0f)
    {
        if (_used >= MaximumSegments || !float.IsFinite(from.X) || !float.IsFinite(from.Y) ||
            !float.IsFinite(to.X) || !float.IsFinite(to.Y))
            return;

        float width = Width;
        float height = Height;
        if (width <= 0 || height <= 0)
            return;
        from = Vector2.Clamp(from, Vector2.Zero, new Vector2(width, height));
        to = Vector2.Clamp(to, Vector2.Zero, new Vector2(width, height));
        Vector2 difference = to - from;
        float length = difference.Length();
        if (length < 0.001f)
            return;

        if (_used == _strokes.Count)
        {
            SceneNode node = _root.NewChildWithTransform(out UIBoundableTransform transform, "PlotSegment");
            transform.MinAnchor = Vector2.Zero;
            transform.MaxAnchor = Vector2.Zero;
            transform.NormalizedPivot = new Vector2(0.0f, 0.5f);
            UIMaterialComponent material = node.AddComponent<UIMaterialComponent>()!;
            _strokes.Add((transform, material));
        }
        var stroke = _strokes[_used++];
        stroke.Transform.Width = length;
        stroke.Transform.Height = thickness;
        stroke.Transform.Margins = new Vector4(from.X, from.Y, 0, 0);
        stroke.Transform.RotationRadians = MathF.Atan2(difference.Y, difference.X);
        stroke.Material.SetBatchSolidColor(color);
    }

    public void End()
    {
        while (_used < _strokes.Count)
            _strokes[_used++].Material.SetBatchSolidColor(Vector4.Zero);
    }
}
