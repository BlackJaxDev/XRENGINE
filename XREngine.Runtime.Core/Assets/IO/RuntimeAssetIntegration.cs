namespace XREngine.Core.Files;

/// <summary>Verified staging bytes and an optional platform integration admission, owned until synchronous hydration finishes.</summary>
public sealed class RuntimeAssetIntegration(byte[] payload, IDisposable? admission = null) : IDisposable
{
    private IDisposable? _admission = admission;
    public byte[] Payload { get; } = payload;
    public void Dispose() => Interlocked.Exchange(ref _admission, null)?.Dispose();
}
