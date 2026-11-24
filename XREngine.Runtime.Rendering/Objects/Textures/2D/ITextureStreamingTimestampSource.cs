namespace XREngine.Rendering;

/// <summary>
/// A fixed set of GPU timestamp queries that <see cref="TextureStreamingGpuTimer"/> addresses by index. The renderer
/// implementation uses backend timestamp queries; tests use fake results.
/// </summary>
internal interface ITextureStreamingTimestampSource
{
    /// <summary>The number of queries. Index 0 to <c>QueryCount - 1</c> are valid.</summary>
    int QueryCount { get; }

    /// <summary>Writes a GPU timestamp into a query at the current point of the command stream.</summary>
    /// <returns>True when the timestamp was written.</returns>
    bool TryWriteTimestamp(int query);

    /// <summary>Reads a query without a wait.</summary>
    /// <param name="query">The query index.</param>
    /// <param name="nanoseconds">The timestamp in nanoseconds when the result is ready.</param>
    /// <returns><see cref="ERenderQueryReadStatus.Ready"/>, <see cref="ERenderQueryReadStatus.NotReady"/>, or a failure.</returns>
    ERenderQueryReadStatus TryReadTimestamp(int query, out ulong nanoseconds);
}
