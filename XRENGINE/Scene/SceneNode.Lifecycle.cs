using XREngine.Components;
using XREngine.Core.Files;

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
            ActivateTransform();
            ActivateComponents();
            Activated?.Invoke(this);
        }

        /// <summary>
        /// Called when the scene is loaded / play begins. Different from activation which can toggle while data stays loaded.
        /// </summary>
        public void OnBeginPlay()
        {
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
            DeactivateComponents();
            DeactivateTransform();
            Deactivated?.Invoke(this);
        }

        /// <summary>
        /// Called when the scene is unloaded / play ends. Different from deactivation which can toggle while data stays loaded.
        /// </summary>
        public void OnEndPlay()
        {
            if (!_hasBegunPlay)
                return;

            EndPlayComponents();
            EndPlayTransform();
            _hasBegunPlay = false;
        }

        /// <summary>
        /// Activates all components attached to this node that are currently active.
        /// </summary>
        private void ActivateComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
                if (component.IsActive)
                    component.OnComponentActivated();
        }

        /// <summary>
        /// Notifies all components that play has begun.
        /// </summary>
        private void BeginPlayComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
                component.OnBeginPlay();
        }

        /// <summary>
        /// Deactivates all components attached to this node that are currently active.
        /// </summary>
        private void DeactivateComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
                if (component.IsActive)
                    component.OnComponentDeactivated();
        }

        /// <summary>
        /// Notifies all components that play has ended.
        /// </summary>
        private void EndPlayComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
                component.OnEndPlay();
        }

        /// <summary>
        /// Activates the transform and propagates activation to active child nodes.
        /// </summary>
        private void ActivateTransform()
        {
            if (_transform is null)
                return;

            _transform.OnSceneNodeActivated();
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

            _transform.OnSceneNodeBeginPlay();
            foreach (var child in _transform.Children)
                child?.SceneNode?.OnBeginPlay();
        }

        /// <summary>
        /// Deactivates the transform, clears ticks, and propagates deactivation to active children.
        /// </summary>
        private void DeactivateTransform()
        {
            if (_transform is null)
                return;

            _transform.OnSceneNodeDeactivated();
            _transform.ClearTicks();
            foreach (var child in _transform.Children)
            {
                var node = child?.SceneNode;
                if (node is null)
                    continue;

                if (node.IsActiveSelf)
                    node.OnDeactivated();
            }
        }

        /// <summary>
        /// Notifies the transform that play has ended and propagates to children.
        /// </summary>
        private void EndPlayTransform()
        {
            if (_transform is null)
                return;

            _transform.OnSceneNodeEndPlay();
            foreach (var child in _transform.Children)
                child?.SceneNode?.OnEndPlay();
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
            OnDeactivated();
            OnEndPlay();

            ComponentsInternal.PostAnythingAdded -= OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved -= OnComponentRemoved;

            lock (Components)
            {
                foreach (var component in ComponentsInternal.ToArray())
                    component.Destroy();
                ComponentsInternal.Clear();
            }

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

            _transform.PropertyChanged -= TransformPropertyChanged;
            _transform.PropertyChanging -= TransformPropertyChanging;

            _transform.Name = Name;
            _transform.SceneNode = this;
            _transform.World = World;

            _transform.PropertyChanged += TransformPropertyChanged;
            _transform.PropertyChanging += TransformPropertyChanging;

            if (IsActiveInHierarchy)
                ActivateTransform();
            else
                DeactivateTransform();
        }

        #endregion
    }
}
