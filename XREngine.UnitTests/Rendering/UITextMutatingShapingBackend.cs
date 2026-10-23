using System.Numerics;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UITextMutatingShapingBackend(Action<UITextShapingRequest> onShape) : IUITextShapingBackend
{
    public UITextShapingResult Shape(UITextShapingRequest request)
    {
        onShape(request);
        UIRuneMetrics metric = request.Face.Glyphs[51];
        return new UITextShapingResult(request,
            [new UITextShapedGlyph(51, metric, new Vector2(9, 0), Vector2.Zero, 0, request.Text.Length)]);
    }
}
