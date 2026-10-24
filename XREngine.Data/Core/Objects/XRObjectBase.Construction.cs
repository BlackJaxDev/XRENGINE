namespace XREngine.Data.Core;

public abstract partial class XRObjectBase
{
    /// <summary>
    /// Runs a synchronous factory with independent constructor-origin ownership and deferred publication.
    /// Failure retains every allocation without calling destruction callbacks. The factory must close
    /// nested scopes before it returns and must not cross an asynchronous boundary. Only that
    /// thread can change an owned identity or request publication until the result is settled.
    /// Publication callbacks cannot construct more objects in this batch or change its identities.
    /// </summary>
    public static ObjectConstructionResult<T> ConstructOwned<T>(Func<ObjectConstructionContext, T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ObjectCachePublicationScope scope = new(CurrentObjectCachePublicationScope,
            independentBatch: true, captureConstructions: true);
        ObjectConstructionOwnership ownership = scope.ConstructionOwnership!;
        T? value = null;
        Exception? failure = null;
        try
        {
            value = factory(new ObjectConstructionContext(ownership)) ??
                throw new InvalidOperationException("An owned construction returned no result.");
            scope.Complete();
        }
        catch (Exception error)
        {
            failure = error;
            try { scope.RetainFailedConstruction(); }
            catch (Exception closeError)
            {
                failure = new AggregateException("Owned construction failed and could not close normally.", error, closeError);
            }
        }
        finally { ownership.ReleaseIdentityGuards(); }
        return new(value, failure, ownership);
    }

    internal static void PublishOwnedObjectCacheBatch(IReadOnlyList<XRObjectBase> objects,
        List<ObjectCachePublishedEntry> published)
    {
        lock (ObjectCacheMutationLock)
        {
            for (int index = 0; index < objects.Count; index++)
            {
                XRObjectBase value = objects[index];
                if (value.IsDestroyed)
                    continue;
                bool alreadyPublished = ObjectsCacheInternal.TryGetValue(value.ID, out XRObjectBase? previous) &&
                    ReferenceEquals(previous, value);
                try { value.PublishObjectCacheRegistrationUnderLock(); }
                finally
                {
                    // A publication override can insert its entry and then throw.
                    if (!alreadyPublished && ObjectsCacheInternal.TryGetValue(value.ID, out XRObjectBase? current) &&
                        ReferenceEquals(current, value))
                        published.Add(new(value.ID, value));
                }
            }
        }
    }

    internal static void RollBackOwnedObjectCacheBatch(IReadOnlyList<ObjectCachePublishedEntry> published)
    {
        lock (ObjectCacheMutationLock)
        {
            for (int index = published.Count - 1; index >= 0; index--)
            {
                ObjectCachePublishedEntry entry = published[index];
                bool removed = ObjectsCacheInternal.TryGetValue(entry.Id, out XRObjectBase? current) &&
                    ReferenceEquals(current, entry.Value) && ObjectsCacheInternal.TryRemove(entry.Id, out _);
                if (removed && entry.Value.ID == entry.Id)
                    entry.Value._isRegisteredInObjectCache = false;
            }
        }
    }
}
