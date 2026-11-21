using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Turns the inputs of a binding publisher into a non-zero monotonic generation.
/// The generation advances only when the key changes, so a backend can keep a
/// captured binding snapshot while the published values stay the same.
/// </summary>
/// <remarks>
/// The key must hold every input that changes a published value. Record structs
/// fit well: their equality compares each field, and class fields compare by
/// reference. The tracker is thread safe and does not allocate.
/// </remarks>
public sealed class RenderBindingGenerationTracker<TKey>
    where TKey : struct, IEquatable<TKey>
{
    private readonly Lock _sync = new();
    private TKey _lastKey;
    private bool _hasKey;
    private ulong _generation = 1;

    /// <summary>Returns the generation for <paramref name="key"/>, and advances it when the key changed.</summary>
    public ulong Advance(in TKey key)
    {
        lock (_sync)
        {
            if (_hasKey && _lastKey.Equals(key))
                return _generation;

            _lastKey = key;
            _hasKey = true;
            _generation = _generation == ulong.MaxValue ? 1 : _generation + 1;
            return _generation;
        }
    }
}
