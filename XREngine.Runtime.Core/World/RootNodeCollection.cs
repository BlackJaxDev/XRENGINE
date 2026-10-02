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
            if (node is null || _rootNodes.Contains(node))
                return;

            IRuntimeWorldContext? previousWorld = node.World;
            bool cacheStarted = false;
            node.Destroying -= RootNodeDestroying;
            node.Destroying += RootNodeDestroying;
            // Track the root before world binding can activate a component and throw.
            // A failed rollback must remain reachable by the world's teardown owner.
            _rootNodes.Add(node);
            try
            {
                node.SetWorldContext(_world);
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
                _rootNodes.Remove(node);
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

            if (!_rootNodes.Contains(node) || !_removing.Add(node))
                return false;
            try
            {
                if (!_removalProgress.TryGetValue(node, out RemovalProgress? progress))
                    _removalProgress[node] = progress = new RemovalProgress();
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
                _rootNodes.Remove(node);
                _removalProgress.Remove(node);
                return true;
            }
            finally
            {
                _removing.Remove(node);
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
