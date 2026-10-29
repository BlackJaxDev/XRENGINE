namespace XREngine.Rendering;

/// <summary>Publishes texture cache payloads using independent staged files.</summary>
public interface ITextureStreamingCacheReplaceBackend : ITextureStreamingCacheFileBackend
{
    /// <summary>Writes and publishes the payload. Consumes the span before returning.</summary>
    /// <remarks>Call <see cref="ITextureStreamingCacheFileBackend.PrepareDirectory"/> first. Concurrent calls may target one path. Preserve the destination on failures before publication and clean up the operation's temporary file when possible. Apply the source timestamp on a best-effort basis.</remarks>
    void WriteAndReplace(string cachePath, ReadOnlySpan<byte> payload, DateTime sourceLastWriteTimeUtc);
}
