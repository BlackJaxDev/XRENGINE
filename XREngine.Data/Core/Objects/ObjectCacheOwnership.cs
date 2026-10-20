namespace XREngine.Data.Core;

/// <summary>
/// Owns exactly the engine objects allocated by one completed publication transaction.
/// The owner must stop all users before disposal; referenced pre-existing objects are borrowed.
/// </summary>
public sealed class ObjectCacheOwnership : IDisposable
{
    private readonly List<XRObjectBase> _objects;
    private readonly object _disposalGate = new();
    private bool _disposing;

    internal ObjectCacheOwnership(List<XRObjectBase> objects) => _objects = objects;

    /// <summary>The exact allocations owned by this lifetime; do not mutate or retain the backing collection.</summary>
    public IReadOnlyList<XRObjectBase> Objects => _objects;

    /// <summary>Destroys owned objects immediately, retaining failed members for a cleanup retry.</summary>
    public void Dispose()
    {
        lock (_disposalGate)
        {
            if (_disposing)
                return;
            _disposing = true;
            try
            {
                List<Exception>? failures = null;
                for (int index = _objects.Count - 1; index >= 0; index--)
                {
                    XRObjectBase value = _objects[index];
                    try
                    {
                        value.Destroy(now: true);
                        if (!value.IsDestroyed)
                            throw new InvalidOperationException($"Object ownership release was vetoed for '{value.GetType().FullName}'.");
                    }
                    catch (Exception error)
                    {
                        (failures ??= []).Add(error);
                    }
                    if (value.IsDestroyed)
                        _objects.RemoveAt(index);
                }
                if (failures is not null)
                    throw new AggregateException("One or more owned engine objects could not be released.", failures);
            }
            finally
            {
                _disposing = false;
            }
        }
    }
}
