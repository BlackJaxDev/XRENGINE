using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace XREngine;

/// <summary>
/// Makes a serializer round trip on the current thread share selected nested assets with the
/// source instead of copying them. A clone serializes a hierarchy to YAML and then reads it
/// back. Inside this scope, a type converter that supports sharing writes a nested asset as a
/// reference that holds only its ID, and records the asset instance. When the same thread reads
/// that reference back in the same scope, the converter gets the recorded instance, so the
/// clone and the source use one asset object.
/// </summary>
/// <remarks>
/// Use one scope around both the serialize and the deserialize steps of one clone. Scopes nest:
/// disposing a scope restores the scope that was current when it began. The scope is
/// thread-static, so a clone on a worker thread does not affect serialization on other threads.
/// </remarks>
public sealed class SharedAssetCloneScope : IDisposable
{
    [ThreadStatic]
    private static SharedAssetCloneScope? s_current;

    private readonly Dictionary<Guid, XRAsset> _assets = [];
    private readonly SharedAssetCloneScope? _previous;
    private bool _disposed;

    private SharedAssetCloneScope()
    {
        _previous = s_current;
        s_current = this;
    }

    /// <summary>Starts a scope on the current thread. Dispose it after the clone is read back.</summary>
    public static SharedAssetCloneScope Begin()
        => new();

    /// <summary>
    /// Writes <paramref name="asset"/> as an ID-only reference and records it, when a scope is
    /// current on this thread and the asset is nested in the serialized object. Returns false
    /// (and writes nothing) otherwise, so the caller serializes the asset normally.
    /// </summary>
    public static bool TryEmitSharedReference(IEmitter emitter, XRAsset asset)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(asset);

        SharedAssetCloneScope? scope = s_current;
        // Depth 0 is the root object of the round trip, which the clone must copy.
        if (scope is null || asset.ID == Guid.Empty || DepthTrackingEventEmitter.CurrentDepth < 1)
            return false;

        scope._assets[asset.ID] = asset;
        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        emitter.Emit(new Scalar("ID"));
        emitter.Emit(new Scalar(asset.ID.ToString()));
        emitter.Emit(new MappingEnd());
        return true;
    }

    /// <summary>
    /// Gets the asset that <see cref="TryEmitSharedReference"/> recorded for <paramref name="id"/>
    /// in the current scope of this thread. Returns false when no scope is current, or when the
    /// scope recorded no asset of type <typeparamref name="T"/> with that ID.
    /// </summary>
    public static bool TryResolve<T>(Guid id, out T? asset)
        where T : XRAsset
    {
        if (s_current is { } scope && scope._assets.TryGetValue(id, out XRAsset? recorded) && recorded is T typed)
        {
            asset = typed;
            return true;
        }

        asset = null;
        return false;
    }

    /// <summary>Ends the scope and restores the scope that was current when it began.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (ReferenceEquals(s_current, this))
            s_current = _previous;
        _assets.Clear();
    }
}
