using System.Diagnostics.CodeAnalysis;
using XREngine.Components;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;

namespace XREngine.Scene
{
    public sealed partial class SceneNode
    {
        #region Transform Management

        /// <summary>
        /// Disconnects the current transform from this scene node.
        /// </summary>
        /// <remarks>
        /// Unsubscribes from property change events and clears the transform's references.
        /// </remarks>
        private void UnlinkTransform()
        {
            if (_transform is null)
                return;

            if (IsActiveInHierarchy)
                DeactivateTransform();
            _transform.PropertyChanged -= TransformPropertyChanged;
            _transform.PropertyChanging -= TransformPropertyChanging;
            _transform.SceneNode = null;
            _transform.World = null;
            _transform.Parent = null;
        }

        /// <summary>
        /// Connects the current transform to this scene node.
        /// </summary>
        /// <remarks>
        /// Subscribes to property change events and sets up the transform's references.
        /// </remarks>
        private void LinkTransform()
        {
            if (_transform is null)
                return;

            _transform.SceneNode = this;
            _transform.World = World;
            _transform.PropertyChanged += TransformPropertyChanged;
            _transform.PropertyChanging += TransformPropertyChanging;
            if (IsActiveInHierarchy)
                ActivateTransform();
        }

        /// <summary>
        /// Handles transform property changes that are about to occur.
        /// </summary>
        /// <param name="sender">The transform raising the event.</param>
        /// <param name="e">The property changing event arguments.</param>
        private void TransformPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(TransformBase.Parent):
                    OnParentChanging();
                    break;
            }
        }

        /// <summary>
        /// Handles transform property changes that have occurred.
        /// </summary>
        /// <param name="sender">The transform raising the event.</param>
        /// <param name="e">The property changed event arguments.</param>
        private void TransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(TransformBase.Parent):
                    OnParentChanged();
                    break;
                case nameof(TransformBase.World):
                    World = Transform.World;
                    break;
            }
        }

        /// <summary>
        /// Called before the parent of this node changes.
        /// </summary>
        /// <remarks>
        /// Override point for subclasses to react to parent changes before they occur.
        /// </remarks>
        private void OnParentChanging() { }

        /// <summary>
        /// Called after the parent of this node has changed.
        /// </summary>
        /// <remarks>
        /// Updates the world reference and activation state based on the new hierarchy.
        /// </remarks>
        private void OnParentChanged()
        {
            World = Parent?.World;
            if (IsActiveInHierarchy)
                ActivateTransform();
            else
                DeactivateTransform();
        }

        /// <summary>
        /// Flags that control how a transform is set on a scene node.
        /// </summary>
        /// <remarks>
        /// These flags can be combined to achieve different behaviors when changing a node's transform.
        /// </remarks>
        public enum ETransformSetFlags
        {
            /// <summary>
            /// Transform is set as-is.
            /// </summary>
            None = 0,
            /// <summary>
            /// The parent of the new transform will be set to the parent of the current transform.
            /// </summary>
            RetainCurrentParent = 1,
            /// <summary>
            /// The world transform of the new transform will be set to the world transform of the current transform, if possible.
            /// </summary>
            RetainWorldTransform = 2,
            /// <summary>
            /// The children of the new transform will be cleared before it is set.
            /// </summary>
            ClearNewChildren = 4,
            /// <summary>
            /// The children of the current transform will be retained when setting the new transform.
            /// </summary>
            RetainCurrentChildren = 8,
            /// <summary>
            /// The children of the current transform will be retained and their world transforms will be maintained.
            /// </summary>
            RetainedChildrenMaintainWorldTransform = 16,
            /// <summary>
            /// Retain the current parent, clear the new children, and retain the current children.
            /// World transform will not be retained.
            /// </summary>
            Default = RetainCurrentParent | ClearNewChildren | RetainCurrentChildren
        }

        /// <summary>
        /// Sets the transform of this scene node.
        /// </summary>
        public void SetTransform(TransformBase transform, ETransformSetFlags flags = ETransformSetFlags.Default)
        {
            if (transform is UICanvasTransform && !TryGetComponent<UICanvasComponent>(out _))
            {
                if (TryGetComponent<PawnComponent>(out _) || TryGetComponent<CameraComponent>(out _))
                {
                    Debug.LogWarning($"Ignoring attempt to assign UICanvasTransform to node '{Name}' because it has no UICanvasComponent.");
                    return;
                }
            }

            if (flags.HasFlag(ETransformSetFlags.ClearNewChildren))
                transform.Clear();

            if (flags.HasFlag(ETransformSetFlags.RetainCurrentParent))
                transform.SetParent(_transform?.Parent, flags.HasFlag(ETransformSetFlags.RetainWorldTransform), EParentAssignmentMode.Immediate);

            if (flags.HasFlag(ETransformSetFlags.RetainCurrentChildren) && _transform is not null)
            {
                bool maintainWorldTransform = flags.HasFlag(ETransformSetFlags.RetainedChildrenMaintainWorldTransform);
                var copy = _transform.Children.ToArray();
                foreach (var child in copy)
                    transform.AddChild(child, maintainWorldTransform, EParentAssignmentMode.Immediate);
            }

            Transform = transform;
        }

        /// <summary>
        /// Sets the transform of this scene node to a new instance of type T.
        /// </summary>
        public T SetTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(ETransformSetFlags flags = ETransformSetFlags.Default) where T : TransformBase, new()
        {
            T value = new();
            SetTransform(value, flags);
            return value;
        }

        /// <summary>
        /// Retrieves the transform of this scene node as type T.
        /// If forceConvert is true, the transform will be converted to type T if it is not already.
        /// </summary>
        public T? GetTransformAs<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(bool forceConvert = false) where T : TransformBase, new()
            => !forceConvert
                ? Transform as T
                : Transform is T value
                    ? value
                    : SetTransform<T>();

        /// <summary>
        /// Attempts to retrieve the transform of this scene node as type T.
        /// </summary>
        public bool TryGetTransformAs<T>([MaybeNullWhen(false)] out T? transform) where T : TransformBase
        {
            transform = Transform as T;
            return transform != null;
        }

        #endregion
    }
}
