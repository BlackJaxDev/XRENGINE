using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene
{
    public sealed partial class SceneNode
    {
        #region Lifecycle Methods

        /// <summary>
        /// Called when the scene node is added to a world or activated.
        /// </summary>
        public void OnActivated()
        {
            using HierarchyMutationLease structural = new(this);
            ObjectDisposedException.ThrowIf(IsDestroyed, this);
            using var mutation = PhysicsChainWorld.BeginNodeActivationMutation(this);
            ActivateTransform();
            ActivateComponents();
            Activated?.Invoke(this);
        }

        /// <summary>
        /// Called when the scene is loaded / play begins. Different from activation which can toggle while data stays loaded.
        /// </summary>
        public void OnBeginPlay()
        {
            using HierarchyMutationLease structural = new(this);
            ObjectDisposedException.ThrowIf(IsDestroyed, this);
            if (_hasBegunPlay)
                return;

            BeginPlayTransform();
            BeginPlayComponents();
            _hasBegunPlay = true;
        }

        /// <summary>
        /// Called when the scene node is removed from a world or deactivated.
        /// </summary>
        public void OnDeactivated()
        {
            using HierarchyMutationLease structural = new(this);
            DeactivateCore(null);
        }

        private void DeactivateCore(OwnedHierarchyRetirement? retirement)
        {
            using var mutation = PhysicsChainWorld.BeginNodeActivationMutation(this);
            DeactivateComponents(retirement);
            DeactivateTransform(retirement);
            Deactivated?.Invoke(this);
        }

        /// <summary>
        /// Called when the scene is unloaded / play ends. Different from deactivation which can toggle while data stays loaded.
        /// </summary>
        public void OnEndPlay()
        {
            using HierarchyMutationLease structural = new(this);
            EndPlayCore(null);
        }

        private void EndPlayCore(OwnedHierarchyRetirement? retirement)
        {
            if (!_hasBegunPlay)
                return;

            EndPlayComponents(retirement);
            EndPlayTransform(retirement);
            _hasBegunPlay = false;
        }

        /// <summary>
        /// Activates all components attached to this node that are currently active.
        /// </summary>
        private void ActivateComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
                if (component.IsActive)
                    component.NotifyComponentActivated();
        }

        /// <summary>
        /// Notifies all components that play has begun.
        /// </summary>
        private void BeginPlayComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
                component.NotifyBeginPlay();
        }

        /// <summary>
        /// Deactivates all components attached to this node that are currently active.
        /// </summary>
        private void DeactivateComponents(OwnedHierarchyRetirement? retirement = null)
        {
            foreach (XRComponent component in ComponentsInternal)
                if (component.IsActive)
                {
                    if (retirement is null) component.NotifyComponentDeactivated();
                    else retirement.StopLifecycle(component, EOwnedHierarchyLifecycle.Deactivate);
                }
        }

        /// <summary>
        /// Notifies all components that play has ended.
        /// </summary>
        private void EndPlayComponents(OwnedHierarchyRetirement? retirement = null)
        {
            foreach (XRComponent component in ComponentsInternal)
            {
                if (retirement is null) component.NotifyEndPlay();
                else retirement.StopLifecycle(component, EOwnedHierarchyLifecycle.EndPlay);
            }
        }

        /// <summary>
        /// Activates the transform and propagates activation to active child nodes.
        /// </summary>
        private void ActivateTransform()
        {
            if (_transform is null)
                return;

            _transform.NotifySceneNodeActivated();
            foreach (var child in _transform.Children)
            {
                var node = child?.SceneNode;
                if (node is null)
                    continue;

                if (node.IsActiveSelf)
                    node.OnActivated();
            }
        }

        /// <summary>
        /// Notifies the transform that play has begun and propagates to children.
        /// </summary>
        private void BeginPlayTransform()
        {
            if (_transform is null)
                return;

            _transform.NotifySceneNodeBeginPlay();
            foreach (var child in _transform.Children)
                child?.SceneNode?.OnBeginPlay();
        }

        /// <summary>
        /// Deactivates the transform, clears ticks, and propagates deactivation to active children.
        /// </summary>
        private void DeactivateTransform(OwnedHierarchyRetirement? retirement = null)
        {
            if (_transform is null)
                return;

            if (retirement is null) _transform.NotifySceneNodeDeactivated();
            else retirement.StopLifecycle(_transform, EOwnedHierarchyLifecycle.Deactivate);
            _transform.ClearTicks();
            foreach (var child in _transform.Children)
            {
                var node = child?.SceneNode;
                if (node is null)
                    continue;

                if (node.IsActiveSelf)
                {
                    if (retirement is null) node.OnDeactivated();
                    else retirement.StopLifecycle(node, EOwnedHierarchyLifecycle.Deactivate);
                }
            }
        }

        /// <summary>
        /// Notifies the transform that play has ended and propagates to children.
        /// </summary>
        private void EndPlayTransform(OwnedHierarchyRetirement? retirement = null)
        {
            if (_transform is null)
                return;

            if (retirement is null) _transform.NotifySceneNodeEndPlay();
            else retirement.StopLifecycle(_transform, EOwnedHierarchyLifecycle.EndPlay);
            foreach (var child in _transform.Children)
                if (child?.SceneNode is { } node)
                {
                    if (retirement is null) node.OnEndPlay();
                    else retirement.StopLifecycle(node, EOwnedHierarchyLifecycle.EndPlay);
                }
        }

        /// <summary>
        /// Called when the scene node is being destroyed.
        /// </summary>
        /// <remarks>
        /// Performs cleanup including:
        /// <list type="bullet">
        ///   <item><description>Deactivating the node and ending play</description></item>
        ///   <item><description>Destroying all attached components</description></item>
        ///   <item><description>Clearing parent reference and destroying the transform</description></item>
        ///   <item><description>Removing the node from the world</description></item>
        /// </list>
        /// </remarks>
        protected override void OnDestroying()
        {
            if (GetOwnedRetirement() is { } retirement)
            {
                DestroyOwnedHierarchy(retirement);
                return;
            }
            OnDeactivated();
            OnEndPlay();

            DestroyCurrentChildren();

            ComponentsInternal.PostAnythingAdded -= OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved -= OnComponentRemoved;

            lock (Components)
            {
                foreach (var component in ComponentsInternal.ToArray())
                    component.Destroy();
                ComponentsInternal.Clear();
            }
            // The list itself is a registered engine object owned by this node.
            ComponentsInternal.Destroy(true);

            Parent = null;
            _transform?.Destroy();
            World = null;

            base.OnDestroying();
        }

        /// <summary>
        /// Called after this node has been deserialized from a cooked binary format.
        /// </summary>
        /// <remarks>
        /// Restores internal references and event subscriptions that are not serialized,
        /// including transform property change handlers and activation state.
        /// </remarks>
        void IPostCookedBinaryDeserialize.OnPostCookedBinaryDeserialize()
        {
            if (_transform is null)
                return;

            _transform.PropertyChanged -= TransformPropertyChangedHandler;
            _transform.PropertyChanging -= TransformPropertyChangingHandler;

            _transform.Name = Name;
            _transform.SceneNode = this;
            _transform.World = World;

            _transform.PropertyChanged += TransformPropertyChangedHandler;
            _transform.PropertyChanging += TransformPropertyChangingHandler;

            if (IsActiveInHierarchy)
                ActivateTransform();
            else
                DeactivateTransform();

            foreach (XRComponent component in ComponentsInternal)
                component.NotifyOwningSceneNodePostDeserialize();
        }

        #endregion
    }
}
