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

        /// <summary>
        /// Stable root membership for concurrent traversal. A new array is published only
        /// when roots are added or removed; readers do not lock or allocate.
        /// </summary>
        public ReadOnlySpan<SceneNode> Snapshot => Volatile.Read(ref _rootSnapshot);

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

            node.Destroying -= RootNodeDestroying;
            node.Destroying += RootNodeDestroying;
            node.SetWorldContext(_world);

            lock (_rootNodesLock)
            {
                SceneNode[] next = new SceneNode[_rootNodes.Count + 1];
                _rootNodes.CopyTo(next);
                next[^1] = node;
                _rootNodes.Add(node);
                Volatile.Write(ref _rootSnapshot, next);
            }
            CacheComponents(node);

            if (_world.IsPlaySessionActive)
            {
                if ((_participatesInPlay?.Invoke(node) ?? true) && !node.HasBegunPlay)
                    node.OnBeginPlay();
                if (node.IsActiveSelf)
                    node.OnActivated();
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

            lock (_rootNodesLock)
            {
                int index = _rootNodes.IndexOf(node);
                if (index < 0)
                    return false;

                SceneNode[] current = _rootSnapshot;
                SceneNode[] next = new SceneNode[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, next.Length - index);
                _rootNodes.RemoveAt(index);
                Volatile.Write(ref _rootSnapshot, next);
            }

            node.Destroying -= RootNodeDestroying;

            if (notifyLifecycle && _world.IsPlaySessionActive)
            {
                if (node.IsActiveSelf)
                    node.OnDeactivated();
                if ((_participatesInPlay?.Invoke(node) ?? true) && node.HasBegunPlay)
                    node.OnEndPlay();
            }

            UncacheComponents(node);

            if (clearWorld && node.Transform?.Parent is null && ReferenceEquals(node.World, _world))
                node.SetWorldContext(null);

            return true;
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
