using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

/// <summary>Owns one image atlas for descendant controls and releases it at teardown.</summary>
public sealed class UIImageAtlasComponent : XRComponent
{
    private UIImageAtlas? _atlas;
    private XRTexture2D? _texture;
    private ObjectCacheOwnership? _ownership;
    private readonly List<WeakReference<UIMaterialComponent>> _bindings = [];
    private int _activeChanges;
    private bool _stopping;
    private bool _releasing;
    private bool _deferredDestroy;

    public void Initialize(UIImageAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        VerifyOwnerAccess();
        ThrowIfStopping();
        if (_atlas is not null || _activeChanges != 0)
            throw new InvalidOperationException("UI.AtlasOwnerState: initialize a live owner once.");
        _activeChanges++;
        try
        {
            using ObjectCachePublicationScope scope = XRObjectBase.BeginIndependentObjectCachePublication();
            XRTexture2D texture = atlas.CreateTexture();
            _ownership = scope.CompleteWithOwnership();
            SetField(ref _atlas, atlas);
            ThrowIfStopping();
            SetField(ref _texture, texture);
            ThrowIfStopping();
            if (!ReferenceEquals(_atlas, atlas) || !ReferenceEquals(_texture, texture))
                throw new InvalidOperationException("UI.AtlasOwnerState: initialization was vetoed.");
        }
        catch
        {
            _stopping = true;
            throw;
        }
        finally
        {
            _activeChanges--;
            CompleteChange();
        }
    }

    /// <summary>Bind a descendant image on the owner thread without changing atlas pixels.</summary>
    public void Bind(UIMaterialComponent target, string key, Vector4 tint)
    {
        ArgumentNullException.ThrowIfNull(target);
        VerifyOwnerAccess();
        ThrowIfStopping();
        if (_atlas is null || _texture is null)
            throw new InvalidOperationException("UI.AtlasOwnerState: the atlas owner is unavailable.");
        bool descendant = false;
        for (var node = target.SceneNode; node is not null; node = node.Parent)
            if (ReferenceEquals(node, SceneNode)) { descendant = true; break; }
        if (!descendant)
            throw new ArgumentException("UI.AtlasOwnerScope: bind only controls under the atlas owner.", nameof(target));
        Vector4 region = _atlas.GetRegion(key);
        TrackBinding(target);
        // SetBatchImage invokes authored callbacks. Keep the source alive until
        // the complete setter unwinds, even if a callback destroys this owner.
        _activeChanges++;
        try
        {
            target.SetBatchImage(_texture, tint, region);
            ThrowIfStopping();
        }
        finally
        {
            _activeChanges--;
            CompleteChange();
        }
    }

    private void TrackBinding(UIMaterialComponent target)
    {
        for (int index = _bindings.Count - 1; index >= 0; index--)
        {
            if (!_bindings[index].TryGetTarget(out UIMaterialComponent? existing) || existing.IsDestroyed)
                _bindings.RemoveAt(index);
            else if (ReferenceEquals(existing, target))
                return;
        }
        _bindings.Add(new(target));
    }

    private void VerifyOwnerAccess()
    {
        if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
            transform.GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
    }

    private void ThrowIfStopping() => ObjectDisposedException.ThrowIf(_stopping || IsDestroyed, this);

    public override void Destroy(bool now = false)
    {
        if (now && (_activeChanges != 0 || _releasing))
        {
            // Keep the owner in the object cache if deferred unbinding fails.
            // The caller can remove the veto and retry destruction safely.
            _stopping = true;
            _deferredDestroy = true;
            return;
        }
        base.Destroy(now);
    }

    private void CompleteChange()
    {
        if (_activeChanges == 0 && _deferredDestroy && !_releasing)
        {
            _deferredDestroy = false;
            Destroy(true);
        }
        else
            ReleaseStoppedStorage();
    }

    protected override void OnDestroying()
    {
        // This internal gate must precede every observable teardown callback.
        _stopping = true;
        try { base.OnDestroying(); }
        finally { ReleaseStoppedStorage(); }
    }

    private void ReleaseStoppedStorage()
    {
        if (!_stopping || _activeChanges != 0 || _releasing)
            return;
        _releasing = true;
        try
        {
            List<Exception>? failures = null;
            if (_texture is { } texture)
                foreach (WeakReference<UIMaterialComponent> binding in _bindings)
                    if (binding.TryGetTarget(out UIMaterialComponent? target) && !target.IsDestroyed)
                    {
                        try { target.ClearBatchImage(texture); }
                        catch (Exception error) { (failures ??= []).Add(error); }
                    }
            if (failures is not null)
                throw new AggregateException("UI.AtlasUnbindFailed: source storage remains owned until all bindings stop.", failures);
            _bindings.Clear();
            _ownership?.Dispose();
            _ownership = null;
            SetField(ref _texture, null);
            SetField(ref _atlas, null);
        }
        finally { _releasing = false; }
    }
}
