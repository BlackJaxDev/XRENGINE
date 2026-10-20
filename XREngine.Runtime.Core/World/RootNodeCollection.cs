using System;
using System.Collections;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine
{
    public class RootNodeCollection(
        IRuntimeWorldContext world,
        Action<SceneNode>? onRootNodeDestroying = null,
        Func<SceneNode, bool>? participatesInPlay = null) : IReadOnlyList<SceneNode>
    {
        private readonly IRuntimeWorldContext _world = world ?? throw new ArgumentNullException(nameof(world));
        private readonly Action<SceneNode>? _onRootNodeDestroying = onRootNodeDestroying;
        private readonly Func<SceneNode, bool>? _participatesInPlay = participatesInPlay;

        public Action<XRComponent>? ComponentCacheAction { get; set; }
        public Action<XRComponent>? ComponentUncacheAction { get; set; }
        public Action<SceneNode>? NodeCacheAction { get; set; }
        public Action<SceneNode>? NodeUncacheAction { get; set; }

        private readonly List<SceneNode> _rootNodes = [];
        private readonly object _rootNodesLock = new();
        private SceneNode[] _rootSnapshot = [];

        /// <summary>Stable root membership, replaced only when a root is added or removed.</summary>
        public ReadOnlySpan<SceneNode> Snapshot => Volatile.Read(ref _rootSnapshot);
        private readonly HashSet<SceneNode> _removing = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SceneNode, RemovalProgress> _removalProgress = new(ReferenceEqualityComparer.Instance);

        public SceneNode this[int index] => _rootNodes[index];

        public int Count => _rootNodes.Count;

        public SceneNode NewRootNode(string name = "RootNode")
        {
            var node = new SceneNode(name);
            Add(node);
            return node;
        }

        public void Remove(SceneNode node)
        {
            RemoveInternal(node, notifyLifecycle: true, clearWorld: true);
        }

        public void Add(SceneNode node)
        {
            if (node is null)
                return;

            IRuntimeWorldContext? previousWorld = node.World;
            bool cacheStarted = false;
            lock (_rootNodesLock)
            {
                if (_rootNodes.Contains(node))
                    return;
                node.Destroying -= RootNodeDestroying;
                node.Destroying += RootNodeDestroying;
                // Keep the root reachable if world binding and its rollback both fail.
                _rootNodes.Add(node);
            }
            try
            {
                node.SetWorldContext(_world);
                PublishRoot(node);
                cacheStarted = true;
                CacheComponents(node);

                if (_world.IsPlaySessionActive)
                {
                    if ((_participatesInPlay?.Invoke(node) ?? true) && !node.HasBegunPlay)
                        node.OnBeginPlay();
                    if (node.IsActiveSelf)
                        node.OnActivated();
                }
            }
            catch (Exception attachmentError)
            {
                lock (_rootNodesLock)
                    RemovePublishedRoot(node);
                List<Exception> failures = [attachmentError];
                void Rollback(Action action)
                {
                    try { action(); }
                    catch (Exception cleanupError) { failures.Add(cleanupError); }
                }

                if (_world.IsPlaySessionActive)
                {
                    if (node.IsActiveSelf)
                        Rollback(node.OnDeactivated);
                    if (node.HasBegunPlay)
                        Rollback(node.OnEndPlay);
                }
                if (cacheStarted)
                    Rollback(() => UncacheComponents(node));
                Rollback(() => node.SetWorldContext(previousWorld));
                if (failures.Count > 1)
                    throw new AggregateException("Root attachment and rollback both failed.", failures);

                node.Destroying -= RootNodeDestroying;
                RemoveTrackedRoot(node);
                throw;
            }
        }

        private bool RootNodeDestroying(XRObjectBase obj)
        {
            if (obj is SceneNode node)
                _onRootNodeDestroying?.Invoke(node);

            return true;
        }

        private bool RemoveInternal(SceneNode node, bool notifyLifecycle, bool clearWorld)
        {
            if (node is null)
                return false;

            RemovalProgress progress;
            lock (_rootNodesLock)
            {
                if (!_rootNodes.Contains(node) || !_removing.Add(node))
                    return false;
                if (!_removalProgress.TryGetValue(node, out progress!))
                    _removalProgress[node] = progress = new RemovalProgress();
                RemovePublishedRoot(node);
            }
            try
            {
                if (!progress.Deactivated)
                {
                    if (notifyLifecycle && _world.IsPlaySessionActive && node.IsActiveSelf)
                        node.OnDeactivated();
                    progress.Deactivated = true;
                }
                if (!progress.EndedPlay)
                {
                    if (notifyLifecycle && _world.IsPlaySessionActive
                        && (_participatesInPlay?.Invoke(node) ?? true) && node.HasBegunPlay)
                        node.OnEndPlay();
                    progress.EndedPlay = true;
                }

                if (!progress.Uncached)
                {
                    UncacheComponents(node);
                    progress.Uncached = true;
                }

                if (!progress.WorldCleared)
                {
                    if (clearWorld && node.Transform?.Parent is null && ReferenceEquals(node.World, _world))
                        node.SetWorldContext(null);
                    progress.WorldCleared = true;
                }

                node.Destroying -= RootNodeDestroying;
                RemoveTrackedRoot(node);
                lock (_rootNodesLock)
                    _removalProgress.Remove(node);
                return true;
            }
            finally
            {
                lock (_rootNodesLock)
                    _removing.Remove(node);
            }
        }

        private void PublishRoot(SceneNode node)
        {
            lock (_rootNodesLock)
            {
                if (!_rootNodes.Contains(node) || Array.IndexOf(_rootSnapshot, node) >= 0)
                    return;
                SceneNode[] next = new SceneNode[_rootSnapshot.Length + 1];
                _rootSnapshot.CopyTo(next, 0);
                next[^1] = node;
                Volatile.Write(ref _rootSnapshot, next);
            }
        }

        // The mutable collection retains cleanup ownership after a callback fails.
        // The caller holds _rootNodesLock. Render traversal excludes roots during cleanup.
        private void RemovePublishedRoot(SceneNode node)
        {
            int index = Array.IndexOf(_rootSnapshot, node);
            if (index < 0)
                return;
            SceneNode[] next = new SceneNode[_rootSnapshot.Length - 1];
            Array.Copy(_rootSnapshot, 0, next, 0, index);
            Array.Copy(_rootSnapshot, index + 1, next, index, next.Length - index);
            Volatile.Write(ref _rootSnapshot, next);
        }

        private void RemoveTrackedRoot(SceneNode node)
        {
            lock (_rootNodesLock)
            {
                RemovePublishedRoot(node);
                _rootNodes.Remove(node);
            }
        }

        private sealed class RemovalProgress
        {
            public bool Deactivated { get; set; }
            public bool EndedPlay { get; set; }
            public bool Uncached { get; set; }
            public bool WorldCleared { get; set; }
        }

        internal bool RemoveDuringNodeDestroy(SceneNode node)
            => RemoveInternal(node, notifyLifecycle: false, clearWorld: false);

        private void CacheComponents(SceneNode node)
            => node.IterateHierarchy(c =>
            {
                NodeCacheAction?.Invoke(c);

                lock (c.Components)
                {
                    foreach (var comp in c.Components)
                        ComponentCacheAction?.Invoke(comp);
                }
            });

        private void UncacheComponents(SceneNode node)
            => node.IterateHierarchy(c =>
            {
                NodeUncacheAction?.Invoke(c);

                lock (c.Components)
                {
                    foreach (var comp in c.Components)
                        ComponentUncacheAction?.Invoke(comp);
                }
            });

        public IEnumerator<SceneNode> GetEnumerator()
            => _rootNodes.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable)_rootNodes).GetEnumerator();
    }
}
