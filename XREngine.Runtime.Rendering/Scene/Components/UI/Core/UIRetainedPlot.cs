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
    public SceneNode Root => _root;

    public void Begin() => _used = 0;

    public void AddLine(Vector2 from, Vector2 to, Vector4 color,
        float thickness = 2.0f, Func<bool>? validate = null)
    {
        if (validate?.Invoke() == false || _used >= MaximumSegments ||
            !float.IsFinite(from.X) || !float.IsFinite(from.Y) ||
            !float.IsFinite(to.X) || !float.IsFinite(to.Y))
            return;

        float width = Width;
        if (validate?.Invoke() == false)
            return;
        float height = Height;
        if (validate?.Invoke() == false)
            return;
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
            var rootTransform = _root.Transform;
            SceneNode node = _root.NewChildWithTransform(out UIBoundableTransform transform, "PlotSegment");
            bool committed = false;
            try
            {
                if (validate?.Invoke() == false)
                    return;
                transform.MinAnchor = Vector2.Zero;
                if (validate?.Invoke() == false)
                    return;
                transform.MaxAnchor = Vector2.Zero;
                if (validate?.Invoke() == false)
                    return;
                transform.NormalizedPivot = new Vector2(0.0f, 0.5f);
                if (validate?.Invoke() == false)
                    return;
                UIMaterialComponent material = node.AddComponent<UIMaterialComponent>()!;
                if (validate?.Invoke() == false)
                    return;
                _strokes.Add((transform, material));
                committed = true;
            }
            finally
            {
                if (!committed && !node.IsDestroyed &&
                    ReferenceEquals(_root.Transform, rootTransform) &&
                    ReferenceEquals(node.Parent, _root) &&
                    ReferenceEquals(node.Transform, transform) &&
                    ReferenceEquals(transform.Parent, rootTransform))
                    node.Destroy(true);
            }
        }
        var stroke = _strokes[_used++];
        stroke.Transform.Width = length;
        if (validate?.Invoke() == false)
            return;
        stroke.Transform.Height = thickness;
        if (validate?.Invoke() == false)
            return;
        stroke.Transform.Margins = new Vector4(from.X, from.Y, 0, 0);
        if (validate?.Invoke() == false)
            return;
        stroke.Transform.RotationRadians = MathF.Atan2(difference.Y, difference.X);
        if (validate?.Invoke() == false)
            return;
        stroke.Material.SetBatchSolidColor(color);
        _ = validate?.Invoke();
    }

    public void End(Func<bool>? validate = null)
    {
        while (_used < _strokes.Count)
        {
            if (validate?.Invoke() == false)
                return;
            _strokes[_used++].Material.SetBatchSolidColor(Vector4.Zero);
            if (validate?.Invoke() == false)
                return;
        }
    }
}
