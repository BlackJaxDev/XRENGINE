using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Editor-owned composition for the hidden scene associated with one live
/// <see cref="RuntimeWorld"/>.  The service deliberately owns only editor
/// policy; Core retains world identity, root lifetime, and play lifecycle.
/// </summary>
public sealed class EditorWorldIntegration : IRuntimeWorldScenePolicy, IRuntimeEditorSceneQuery, IDisposable
{
    private readonly RuntimeWorld _world;
    private readonly IRuntimeWorldScenePolicy? _previousScenePolicy;
    private IDisposable? _capabilityLease;
    private readonly Dictionary<XRScene, HashSet<SceneNode>> _editorOnlyNodesByScene =
        new(ReferenceEqualityComparer.Instance);
    private XRScene? _editorScene;
    private RuntimeWorldRenderer? _renderer;
    private bool _disposed;

    internal EditorWorldIntegration(RuntimeWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _previousScenePolicy = world.ScenePolicy;
        if (_previousScenePolicy is not null)
            throw new InvalidOperationException("The runtime world already has a scene policy.");

        try
        {
            _world.ScenePolicy = this;
            _capabilityLease = _world.RegisterCapability(this);
            _world.Disposing += OnWorldDisposing;
            TryBindRenderer();
        }
        catch
        {
            _world.Disposing -= OnWorldDisposing;
            _capabilityLease?.Dispose();
            _capabilityLease = null;
            if (ReferenceEquals(_world.ScenePolicy, this))
                _world.ScenePolicy = _previousScenePolicy;
            throw;
        }
    }

    /// <summary>The Core world composed with this editor policy.</summary>
    public RuntimeWorld World => _world;

    /// <summary>
    /// Binds the renderer's editor-scene query to this policy. The binding is
    /// reference-count safe: disposal clears only the query installed here.
    /// </summary>
    public void BindRenderer(RuntimeWorldRenderer renderer)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(renderer);
        if (!ReferenceEquals(renderer.WorldContext, _world))
            throw new ArgumentException("The renderer must belong to this runtime world.", nameof(renderer));

        if (_renderer is not null && !ReferenceEquals(_renderer, renderer)
            && ReferenceEquals(_renderer.EditorSceneQuery, this))
        {
            _renderer.EditorSceneQuery = null;
        }

        _renderer = renderer;
        _renderer.EditorSceneQuery = this;
    }

    /// <summary>Attempts to bind an already attached renderer capability.</summary>
    public bool TryBindRenderer()
    {
        if (_world.TryGetCapability<IRuntimeRenderWorld>(out IRuntimeRenderWorld? renderWorld)
            && renderWorld is RuntimeWorldRenderer renderer)
        {
            BindRenderer(renderer);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lazily-created scene for gizmos, editor cameras, and other content that
    /// must be present in edit mode without participating in gameplay.
    /// </summary>
    public XRScene EditorScene
    {
        get
        {
            ThrowIfDisposed();
            return _editorScene ??= new XRScene("__EditorScene__")
            {
                IsEditorOnly = true,
                IsVisible = true
            };
        }
    }

    /// <summary>Adds a root to the hidden editor scene.</summary>
    public void AddToEditorScene(SceneNode node)
    {
        ThrowIfDisposed();
        if (node is null)
            return;

        void AddAsEditorRoot()
        {
            XRScene editorScene = EditorScene;
            if (!editorScene.RootNodes.Contains(node))
                editorScene.RootNodes.Add(node);
            if (!_world.RootNodes.Any(existing => ReferenceEquals(existing, node)))
                _world.RootNodes.Add(node);

            RuntimeWorldInputIntegration.RefreshControlledPawns(node);
        }

        // Parent reassignment is deferred because this path is frequently
        // reached from component callbacks while child collections are read.
        if (node.Transform?.Parent is not null)
        {
            node.Transform.SetParent(
                null,
                preserveWorldTransform: true,
                EParentAssignmentMode.Deferred,
                onApplied: (_, _) => AddAsEditorRoot());
            return;
        }

        AddAsEditorRoot();
    }

    /// <summary>Removes a root from the hidden editor scene.</summary>
    public void RemoveFromEditorScene(SceneNode node)
    {
        if (_disposed || node is null || _editorScene is null)
            return;

        _editorScene.RootNodes.Remove(node);
        _world.RootNodes.Remove(node);
        RemoveFromSourceSceneMembership(node);
    }

    /// <summary>Returns whether a node belongs to the hidden editor scene.</summary>
    public bool IsInEditorScene(SceneNode? node)
    {
        if (node is null || _editorScene is null)
            return false;

        SceneNode? root = node;
        while (root?.Transform?.Parent?.SceneNode is SceneNode parent)
            root = parent;

        return root is not null && _editorScene.RootNodes.Contains(root);
    }

    /// <summary>
    /// Moves editor-only roots from a visible source scene to the hidden scene
    /// while retaining enough source membership to restore them on unload.
    /// </summary>
    public void AttachScene(XRScene scene)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        if (!scene.IsVisible)
            return;

        foreach (SceneNode node in scene.RootNodes)
            TryAttachSceneRoot(_world, scene, node);
    }

    /// <summary>Removes editor-only roots that were attached for a source scene.</summary>
    public void DetachScene(XRScene scene)
    {
        if (_disposed || scene is null || !_editorOnlyNodesByScene.TryGetValue(scene, out HashSet<SceneNode>? roots))
            return;

        foreach (SceneNode root in roots.ToArray())
            TryDetachSceneRoot(_world, scene, root);
    }

    /// <inheritdoc />
    public bool TryAttachSceneRoot(RuntimeWorld world, XRScene scene, SceneNode root)
    {
        if (_disposed || !ReferenceEquals(world, _world) || root is null || !root.IsEditorOnly)
            return false;

        HashSet<SceneNode> roots = GetOrCreateSourceRoots(scene);
        roots.Add(root);
        AddToEditorScene(root);
        return true;
    }

    /// <inheritdoc />
    public bool TryDetachSceneRoot(RuntimeWorld world, XRScene scene, SceneNode root)
    {
        if (_disposed || !ReferenceEquals(world, _world) || root is null
            || !_editorOnlyNodesByScene.TryGetValue(scene, out HashSet<SceneNode>? roots)
            || !roots.Remove(root))
        {
            return false;
        }

        if (roots.Count == 0)
            _editorOnlyNodesByScene.Remove(scene);

        if (!IsTrackedByAnySourceScene(root))
            RemoveFromEditorScene(root);

        return true;
    }

    /// <inheritdoc />
    public bool ShouldParticipateInPlay(RuntimeWorld world, SceneNode root)
        => ReferenceEquals(world, _world) && !IsInEditorScene(root);

    /// <inheritdoc />
    public void OnRootNodeDestroying(RuntimeWorld world, SceneNode root)
    {
        if (!ReferenceEquals(world, _world) || root is null)
            return;

        _editorScene?.RootNodes.Remove(root);
        RemoveFromSourceSceneMembership(root);
    }

    /// <inheritdoc />
    public void Dispose()
        => Dispose(restoreScenePolicy: true);

    private void Dispose(bool restoreScenePolicy)
    {
        if (_disposed)
            return;
        if (restoreScenePolicy && _world.PlayState != RuntimeWorldPlayState.Stopped)
        {
            throw new InvalidOperationException(
                "Editor world integration cannot be detached during an active play or edit session.");
        }

        _disposed = true;
        foreach (SceneNode root in _editorScene?.RootNodes.ToArray() ?? [])
            _world.RootNodes.Remove(root);

        _editorScene?.RootNodes.Clear();
        _editorOnlyNodesByScene.Clear();
        _world.Disposing -= OnWorldDisposing;
        if (_renderer is not null && ReferenceEquals(_renderer.EditorSceneQuery, this))
            _renderer.EditorSceneQuery = null;
        _renderer = null;
        _capabilityLease?.Dispose();
        _capabilityLease = null;
        if (restoreScenePolicy && ReferenceEquals(_world.ScenePolicy, this))
            _world.ScenePolicy = _previousScenePolicy;

        EditorWorldIntegrationRegistry.Remove(_world, this);
    }

    private void OnWorldDisposing(RuntimeWorld world)
        // Core clears its policy immediately after this event. Restoring it here
        // would reconcile every loaded scene during shutdown, bouncing component
        // activation and render registration for roots about to be unloaded.
        => Dispose(restoreScenePolicy: false);

    private void RemoveFromSourceSceneMembership(SceneNode node)
    {
        List<XRScene>? emptyScenes = null;
        foreach ((XRScene scene, HashSet<SceneNode> roots) in _editorOnlyNodesByScene)
        {
            if (!roots.Remove(node) || roots.Count > 0)
                continue;

            emptyScenes ??= [];
            emptyScenes.Add(scene);
        }

        foreach (XRScene scene in emptyScenes ?? [])
            _editorOnlyNodesByScene.Remove(scene);
    }

    private HashSet<SceneNode> GetOrCreateSourceRoots(XRScene scene)
    {
        if (_editorOnlyNodesByScene.TryGetValue(scene, out HashSet<SceneNode>? roots))
            return roots;

        roots = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);
        _editorOnlyNodesByScene.Add(scene, roots);
        return roots;
    }

    private bool IsTrackedByAnySourceScene(SceneNode node)
        => _editorOnlyNodesByScene.Values.Any(roots => roots.Contains(node));

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
