namespace XREngine.Rendering;

/// <summary>Explicit asynchronous diagnostics/capture readback; never a synchronous render-loop wait.</summary>
public interface IBrowserReadbackCapability
{
    Task<byte[]> ReadBufferAsync(BrowserBufferReadbackDescription description, CancellationToken cancellationToken = default);
    Task<byte[]> ReadTextureAsync(BrowserTextureReadbackDescription description, CancellationToken cancellationToken = default);
}
