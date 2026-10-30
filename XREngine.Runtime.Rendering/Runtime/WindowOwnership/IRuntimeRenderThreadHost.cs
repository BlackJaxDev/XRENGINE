namespace XREngine.Rendering;

/// <summary>Owns one native render loop, including its event pump and thread lifetime.</summary>
public interface IRuntimeRenderThreadHost
{
    int CurrentRenderThreadId { get; }
    void BlockForRendering(Func<bool> runUntilPredicate, RuntimeRenderThreadHostMode mode);
}
