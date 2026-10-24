namespace XREngine.Scene;

public partial class XRScene
{
    private bool _sceneRootLifetimeEnded;
    private bool _sceneRootDestroying;
    private bool _sceneRootGenerating;

    public override void Destroy(bool now = false)
    {
        if (!now)
        {
            lock (SceneRootCollection.MutationGate)
            {
                if (_sceneRootDestroying || _sceneRootGenerating)
                    throw new InvalidOperationException("The scene root owner is already changing its lifetime.");
                base.Destroy();
            }
            return;
        }

        SceneRootCollection? roots;
        lock (SceneRootCollection.MutationGate)
        {
            if (_sceneRootDestroying || _sceneRootGenerating)
                throw new InvalidOperationException("The scene root owner is already changing its lifetime.");
            roots = _sceneRoots;
            SetField(ref _sceneRootDestroying, true, publishNotifications: false);
        }
        try
        {
            base.Destroy(now: true);
            lock (SceneRootCollection.MutationGate)
            {
                // A veto or failed teardown keeps the original owner links for recovery.
                if (!IsDestroyed || !ReferenceEquals(_sceneRoots, roots))
                    return;
                roots?.Retire();
                SetField(ref _sceneRootLifetimeEnded, true, publishNotifications: false);
            }
        }
        finally
        {
            lock (SceneRootCollection.MutationGate)
                SetField(ref _sceneRootDestroying, false, publishNotifications: false);
        }
    }

    /// <summary>Restores root ownership through a new collection incarnation when an asset is revived.</summary>
    public override void Generate()
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_sceneRootDestroying || _sceneRootGenerating || IsDestroyQueued)
                throw new InvalidOperationException("The scene cannot be generated while its lifetime is changing.");
            SetField(ref _sceneRootGenerating, true, publishNotifications: false);
        }
        try
        {
            base.Generate();
            lock (SceneRootCollection.MutationGate)
            {
                if (!_sceneRootLifetimeEnded)
                    return;
                SceneRootCollection replacement = new(this, _sceneRoots);
                replacement.Attach();
                SetField(ref _sceneRoots, replacement, publishNotifications: false, nameof(SceneRoots));
                SetField(ref _sceneRootLifetimeEnded, false, publishNotifications: false);
            }
        }
        finally
        {
            lock (SceneRootCollection.MutationGate)
                SetField(ref _sceneRootGenerating, false, publishNotifications: false);
        }
    }

    internal bool CanChangeSceneRoots
        => !_sceneRootDestroying && !_sceneRootGenerating && !_sceneRootLifetimeEnded && !IsDestroyed && !IsDestroyQueued;
}
