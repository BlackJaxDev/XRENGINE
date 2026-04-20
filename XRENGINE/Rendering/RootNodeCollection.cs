using System;
using System.Collections;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Rendering
{
    public partial class XRWorldInstance
    {
        public class RootNodeCollection : IReadOnlyList<SceneNode>
        {
            private readonly XRWorldInstance _world;

            public RootNodeCollection(XRWorldInstance world)
            {
                _world = world ?? throw new ArgumentNullException(nameof(world));
            }

            public Action<XRComponent>? ComponentCacheAction { get; set; }
            public Action<XRComponent>? ComponentUncacheAction { get; set; }
            public Action<SceneNode>? NodeCacheAction { get; set; }
            public Action<SceneNode>? NodeUncacheAction { get; set; }

            private readonly List<SceneNode> _rootNodes = [];

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
                node.World = _world;

                _rootNodes.Add(node);
                CacheComponents(node);

                if (_world.IsPlaySessionActive)
                {
                    if (!node.HasBegunPlay)
                        node.OnBeginPlay();
                    if (node.IsActiveSelf)
                        node.OnActivated();
                }
            }

            private bool RootNodeDestroying(XRObjectBase obj)
            {
                if (obj is SceneNode node)
                    _world.OnRootNodeDestroying(node);

                return true;
            }

            private bool RemoveInternal(SceneNode node, bool notifyLifecycle, bool clearWorld)
            {
                if (node is null)
                    return false;

                if (!_rootNodes.Remove(node))
                    return false;

                node.Destroying -= RootNodeDestroying;

                if (notifyLifecycle && _world.IsPlaySessionActive)
                {
                    if (node.IsActiveSelf)
                        node.OnDeactivated();
                    if (node.HasBegunPlay)
                        node.OnEndPlay();
                }

                UncacheComponents(node);

                if (clearWorld && node.Transform?.Parent is null && ReferenceEquals(node.World, _world))
                    node.World = null;

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
}
