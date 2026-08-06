namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Declares why operations in a logical pass must remain outside reusable
/// secondary-command ranges. This semantic policy is independent of pass names.
/// </summary>
[Flags]
public enum ERenderPassSecondaryCachePolicy
{
    Stable = 0,
    DynamicUi = 1 << 0,
    DynamicText = 1 << 1,
    DynamicDebug = 1 << 2,
    DynamicResource = 1 << 3,
    OutputSensitive = 1 << 4,
}
