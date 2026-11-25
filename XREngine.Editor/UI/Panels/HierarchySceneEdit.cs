using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Retains exact scene order, loaded state, and callback-local write admission.</summary>
internal sealed class HierarchySceneEdit
{
    private readonly XRScene _scene;
    private readonly RuntimeWorld _world;
    private readonly XRWorld? _asset;
    private readonly ObjectConstructionIdentity _sceneIdentity;
    private readonly ObjectConstructionIdentity? _assetIdentity;
    private readonly XRAsset _sceneDocument;
    private readonly ObjectConstructionIdentity _sceneDocumentIdentity;
    private readonly XRAsset? _worldDocument;
    private readonly ObjectConstructionIdentity? _worldDocumentIdentity;
    private readonly IRuntimeWorldScenePolicy? _policy;
    private readonly List<XRScene>? _scenes;
    private readonly XRScene[] _beforeScenes;
    private readonly XRScene[] _afterScenes;
    private readonly SceneRootCollection _roots;
    private readonly ObjectConstructionIdentity[] _rootIdentities;
    private readonly int _index;
    private readonly bool _unload;
    private readonly bool _beforeVisible;
    private readonly bool _afterVisible;
    private readonly bool _beforeLoaded;
    private bool _recoveryBlocked;
    private bool _expectedVisible;
    private bool _expectedLoaded;
    private XRScene[] _expectedScenes;
    private ulong _visibilityIntent;
    private ulong _rootsRevision;
    private HierarchySceneActionSelection? _selection;

    internal HierarchySceneEdit(XRScene scene, RuntimeWorld world, bool unload, bool visible)
    {
        _scene = scene;
        _world = world;
        _asset = world.TargetWorld;
        _assetIdentity = _asset is null ? null : ObjectConstructionIdentity.Capture(_asset);
        _sceneIdentity = ObjectConstructionIdentity.Capture(scene);
        _sceneDocument = scene.SourceAsset;
        _sceneDocumentIdentity = ObjectConstructionIdentity.Capture(_sceneDocument);
        _worldDocument = unload ? _asset?.SourceAsset : null;
        _worldDocumentIdentity = _worldDocument is null ? null : ObjectConstructionIdentity.Capture(_worldDocument);
        _policy = world.ScenePolicy;
        _scenes = _asset?.Scenes;
        _beforeScenes = _scenes is null ? [] : [.. _scenes];
        _index = Array.FindIndex(_beforeScenes, item => ReferenceEquals(item, scene));
        int occurrences = _beforeScenes.Count(item => ReferenceEquals(item, scene));
        _unload = unload;
        _beforeVisible = scene.IsVisible;
        _afterVisible = unload ? false : visible;
        // Loaded state is independent of visibility and authored membership.
        _beforeLoaded = world.IsSceneLoaded(scene);
        if (occurrences > 1 || unload && (_asset is null || occurrences != 1) ||
            occurrences == 0 && !_beforeLoaded || _beforeLoaded && !world.IsSceneReady(scene))
            throw new InvalidOperationException("The scene has no complete exact authoring membership.");
        _afterScenes = unload ? _beforeScenes.Where((_, index) => index != _index).ToArray() : _beforeScenes;
        _roots = scene.SceneRoots;
        _rootIdentities = _roots.Select(ObjectConstructionIdentity.Capture).ToArray();
        _expectedScenes = _beforeScenes;
        RequireOwner();
    }

    internal XRScene Scene => _scene;
    internal bool AttemptStarted { get; private set; }
    internal bool IsApplied() => Matches(true);
    internal bool IsRestored() => Matches(false);

    internal void ReconcileOwnedProperties(StructuralChangeHandle handle)
    {
        handle.RemoveOwnedProperty(_scene, nameof(XRScene.IsVisible));
        handle.RemoveOwnedProperty(_sceneDocument, nameof(XRAsset.IsDirty));
        if (_worldDocument is not null && !ReferenceEquals(_worldDocument, _sceneDocument))
            handle.RemoveOwnedProperty(_worldDocument, nameof(XRAsset.IsDirty));
    }

    private bool Matches(bool applied)
    {
        if (_recoveryBlocked || !OwnerCurrent()) return false;
        bool loaded = !applied || !_unload ? _beforeLoaded : false;
        return _scene.IsVisible == (applied ? _afterVisible : _beforeVisible) &&
            _world.IsSceneLoaded(_scene) == loaded && (!loaded || _world.IsSceneReady(_scene)) &&
            SceneOrderMatches(applied ? _afterScenes : _beforeScenes);
    }

    internal void Run(bool forward)
    {
        AttemptStarted = false;
        if (!Matches(!forward))
            throw new InvalidOperationException("The scene action no longer owns its expected source state.");
        _expectedVisible = _scene.IsVisible;
        _expectedLoaded = _world.IsSceneLoaded(_scene);
        _expectedScenes = forward ? _beforeScenes : _afterScenes;
        _visibilityIntent = _scene.VisibilityWriteIntentRevision;
        _rootsRevision = _roots.Revision;
        if (_unload && forward)
            _selection = new(_scene);
        RequireProgress();
        AttemptStarted = true;
        try
        {
            SetDestination(forward);
            RequireProgress();
            if (!Matches(forward))
                throw new InvalidOperationException("The scene action did not reach its exact destination.");
        }
        catch
        {
            // Compensate only the progress still owned by this attempt. A newer
            // callback intent or a foreign source leaves a guarded recovery step.
            try
            {
                RequireProgress();
                SetDestination(!forward);
                RequireProgress();
                if (!Matches(!forward)) _recoveryBlocked = true;
            }
            catch { _recoveryBlocked = true; }
            throw;
        }
    }

    private void SetDestination(bool applied)
    {
        if (!_unload)
        {
            SetVisible(applied ? _afterVisible : _beforeVisible);
            return;
        }
        if (applied)
        {
            SetVisible(false);
            SetLoaded(false);
            SetMembership(false);
            _selection?.Apply();
        }
        else
        {
            SetMembership(true);
            SetLoaded(_beforeLoaded);
            SetVisible(_beforeVisible);
            _selection?.Restore();
        }
        RequireProgress();
    }

    private void SetVisible(bool visible)
    {
        RequireProgress();
        if (_expectedVisible == visible) return;
        _visibilityIntent = checked(_visibilityIntent + 1);
        try
        {
            _scene.SetVisibilityGuarded(visible, this,
                static edit => edit.RequireProgress(ignoreVisibility: true));
        }
        finally
        {
            // PropertyChanged can fail after the field write. Account only for
            // this exact admitted intent so compensation can settle that write.
            if (_scene.VisibilityWriteIntentRevision == _visibilityIntent && _scene.IsVisible == visible)
                _expectedVisible = visible;
        }
        RequireProgress();
    }

    private void SetLoaded(bool loaded)
    {
        RequireProgress();
        if (_expectedLoaded == loaded) return;
        try
        {
            if (loaded) _world.LoadScene(_scene);
            else _world.UnloadScene(_scene);
        }
        finally
        {
            if (_world.IsSceneLoaded(_scene) == loaded && (!loaded || _world.IsSceneReady(_scene)))
                _expectedLoaded = loaded;
        }
        RequireProgress();
    }

    private void SetMembership(bool present)
    {
        RequireProgress();
        XRScene[] expected = present ? _beforeScenes : _afterScenes;
        if (ReferenceEquals(_expectedScenes, expected)) return;
        // List<T> storage changes call no application code. Exact ordered snapshots
        // protect both this index and every peer from a stale continuation.
        if (present) _scenes!.Insert(_index, _scene);
        else _scenes!.RemoveAt(_index);
        _expectedScenes = expected;
        RequireProgress();
    }

    private bool OwnerCurrent()
    {
        if (_world.IsDisposing || !_sceneIdentity.IsCurrent || _scene.IsDestroyed || _scene.IsDestroyQueued ||
            _assetIdentity is { } identity && (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued) ||
            !SourceDocumentsCurrent() ||
            !ReferenceEquals(_world.TargetWorld, _asset) || !ReferenceEquals(_asset?.Scenes, _scenes) ||
            !ReferenceEquals(_world.ScenePolicy, _policy) || !ReferenceEquals(_scene.SceneRoots, _roots) ||
            !_roots.IsCurrent || _roots.Count != _rootIdentities.Length)
            return false;
        for (int index = 0; index < _rootIdentities.Length; index++)
            if (!_rootIdentities[index].IsCurrent || _rootIdentities[index].Target.IsDestroyed ||
                _rootIdentities[index].Target.IsDestroyQueued || !ReferenceEquals(_roots[index], _rootIdentities[index].Target))
                return false;
        return true;
    }

    private bool SourceDocumentsCurrent()
        => SceneDocumentCurrent() && WorldDocumentCurrent();

    private bool SceneDocumentCurrent()
        => _sceneDocumentIdentity.IsCurrent && !_sceneDocument.IsDestroyed && !_sceneDocument.IsDestroyQueued &&
            ReferenceEquals(_scene.SourceAsset, _sceneDocument);

    private bool WorldDocumentCurrent()
        => _worldDocument is null || _worldDocumentIdentity is { IsCurrent: true } &&
                !_worldDocument.IsDestroyed && !_worldDocument.IsDestroyQueued &&
                ReferenceEquals(_asset?.SourceAsset, _worldDocument);

    private void RequireOwner()
    {
        if (!OwnerCurrent())
            throw new InvalidOperationException("The scene action changed its exact lifetime or authoring owner.");
    }

    private bool SceneOrderMatches(XRScene[] expected)
    {
        if (_scenes is null) return expected.Length == 0;
        if (_scenes.Count != expected.Length) return false;
        for (int index = 0; index < expected.Length; index++)
            if (!ReferenceEquals(_scenes[index], expected[index])) return false;
        return true;
    }

    private void RequireProgress(bool ignoreVisibility = false)
    {
        RequireOwner();
        if (_recoveryBlocked || _scene.VisibilityWriteIntentRevision != _visibilityIntent ||
            _roots.Revision != _rootsRevision || !SceneOrderMatches(_expectedScenes) ||
            _world.IsSceneLoaded(_scene) != _expectedLoaded || _expectedLoaded && !_world.IsSceneReady(_scene) ||
            !ignoreVisibility && _scene.IsVisible != _expectedVisible)
            throw new InvalidOperationException("A callback changed scene action progress or write intent.");
    }

    internal void RequireCompleted(bool applied)
    {
        try
        {
            RequireProgress();
            if (!Matches(applied))
                throw new InvalidOperationException("The scene action no longer owns its completed state.");
        }
        catch { _recoveryBlocked = true; throw; }
    }

    internal void MarkDirty()
    {
        if (!AttemptStarted) return;
        bool markedSceneDocument = _sceneIdentity.IsCurrent && SceneDocumentCurrent();
        if (markedSceneDocument)
            Mark(_sceneDocument, _sceneDocumentIdentity);
        if (_worldDocument is not null && (!markedSceneDocument || !ReferenceEquals(_worldDocument, _sceneDocument)) &&
            _assetIdentity is { IsCurrent: true } && WorldDocumentCurrent())
            Mark(_worldDocument, _worldDocumentIdentity!.Value);
        try { RequireProgress(); }
        catch { _recoveryBlocked = true; }
    }

    private static void Mark(XRAsset asset, ObjectConstructionIdentity identity)
    {
        if (!identity.IsCurrent || asset.IsDestroyed || asset.IsDestroyQueued) return;
        try { asset.MarkDirty(); }
        catch (Exception exception) { Debug.LogException(exception, "Could not mark a scene action document dirty."); }
    }
}
