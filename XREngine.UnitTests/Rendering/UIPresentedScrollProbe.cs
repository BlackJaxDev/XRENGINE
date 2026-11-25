using System.Numerics;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

public sealed class UIPresentedScrollProbe : UIInteractableComponent,
    IUIPointerScrollReceiver
{
    public int GeometryCalls { get; private set; }
    public int ScalarCalls { get; private set; }
    public Vector2 LastLocalPoint { get; private set; }
    public object? LastMetadata { get; private set; }
    public Func<bool>? OnGeometry { get; set; }

    public bool HandleMouseScroll(float diff)
    {
        ScalarCalls++;
        return true;
    }

    public bool HandlePointerScroll(float diff, Vector2 localPoint,
        in UIPointerGeometry geometry)
    {
        GeometryCalls++;
        LastLocalPoint = localPoint;
        LastMetadata = geometry.Metadata;
        return OnGeometry?.Invoke() ?? true;
    }
}
