namespace XREngine;

/// <summary>
/// Marks serialization on the current thread as a transient in-process round trip: the
/// serialized output is read back in the same process and then discarded, for example when
/// a hierarchy is cloned through the serializers. Inside this scope, writers that support it
/// store data raw instead of compressed or quantized. Compression only makes stored files
/// smaller, so in a round trip it costs time and gives no benefit. A lossy encoding would
/// also make the clone differ from its source.
/// </summary>
/// <remarks>
/// Use one scope around both the serialize and the deserialize steps. Scopes nest by count.
/// The scope is thread-static, so it does not change serialization on other threads, such as
/// asset saves that run at the same time.
/// </remarks>
public readonly struct TransientSerializationScope : IDisposable
{
    [ThreadStatic]
    private static int s_depth;

    private readonly bool _active;

    private TransientSerializationScope(bool active)
        => _active = active;

    /// <summary>True when a scope is open on the current thread.</summary>
    public static bool IsActive
        => s_depth > 0;

    /// <summary>Opens a scope on the current thread. Dispose it after the output is read back.</summary>
    public static TransientSerializationScope Begin()
    {
        s_depth++;
        return new TransientSerializationScope(true);
    }

    /// <summary>Closes the scope that <see cref="Begin"/> opened.</summary>
    public void Dispose()
    {
        if (_active && s_depth > 0)
            s_depth--;
    }
}
