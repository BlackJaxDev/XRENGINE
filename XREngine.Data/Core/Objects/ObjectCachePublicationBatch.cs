namespace XREngine.Data.Core;

/// <summary>
/// Shared state for a synchronous, nested object-cache publication transaction.
/// </summary>
internal sealed class ObjectCachePublicationBatch
{
    private readonly HashSet<XRObjectBase> _objectSet = new(ReferenceEqualityComparer.Instance);

    public List<XRObjectBase> Objects { get; } = [];
    public bool IsAborted { get; set; }
    public bool IsPublishing { get; set; }
    public ObjectConstructionOwnership? ConstructionOwnership { get; }
    public List<ObjectCachePublishedEntry>? PublishedEntries { get; }

    public ObjectCachePublicationBatch(bool captureConstructions = false)
    {
        if (captureConstructions)
        {
            ConstructionOwnership = new();
            PublishedEntries = [];
        }
    }

    public bool Contains(XRObjectBase value) => _objectSet.Contains(value);

    public void Enlist(XRObjectBase value)
    {
        if (_objectSet.Add(value))
            Objects.Add(value);
    }
}
