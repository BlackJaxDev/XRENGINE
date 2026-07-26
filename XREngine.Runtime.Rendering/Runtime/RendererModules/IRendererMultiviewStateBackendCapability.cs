namespace XREngine.Rendering;

/// <summary>
/// Exposes backend multiview target state used by stable submission policy.
/// </summary>
public interface IRendererMultiviewStateBackendCapability
{
    bool HasActiveMultiviewDrawTarget { get; }
    uint CurrentDrawViewMask { get; }
}
