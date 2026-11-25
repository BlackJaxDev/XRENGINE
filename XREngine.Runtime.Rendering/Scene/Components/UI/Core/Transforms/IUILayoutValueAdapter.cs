namespace XREngine.Rendering.UI;

/// <summary>Lets a custom transform declare an existing pure layout operation.</summary>
public interface IUILayoutValueAdapter
{
    UILayoutOperator LayoutOperator { get; }
    UILayoutValuePayload CaptureLayoutPayload();
}
