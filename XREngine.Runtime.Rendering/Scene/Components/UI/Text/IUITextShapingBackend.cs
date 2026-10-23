namespace XREngine.Rendering.UI;

/// <summary>Shape one analyzed text run from owned font data.</summary>
public interface IUITextShapingBackend
{
    UITextShapingResult Shape(UITextShapingRequest request);
}
