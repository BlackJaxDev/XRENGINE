namespace XREngine.Rendering;

/// <summary>Owns secondary graphics work until its worker and native window are released.</summary>
public interface IRuntimeSecondaryGpuContext : IDisposable
{
    bool IsRunning { get; }
    bool HasRenderer { get; }
    /// <summary>False while worker shutdown or native window disposal is incomplete.</summary>
    bool IsReleased { get; }
    bool TryEnqueue(Action<AbstractRenderer> job);
}
