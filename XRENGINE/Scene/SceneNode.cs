using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using XREngine.Components;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Attributes;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Scene
{
    /// <summary>
    /// Represents a node in the scene hierarchy. SceneNodes are the fundamental building blocks of the scene graph,
    /// containing transforms for positioning and components for behavior/rendering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SceneNode follows a component-based architecture where functionality is added through <see cref="XRComponent"/> instances.
    /// Each node has a <see cref="TransformBase"/> that defines its position, rotation, and scale in 3D space.
    /// </para>
    /// <para>
    /// The node lifecycle includes:
    /// <list type="bullet">
    ///   <item><description><see cref="OnActivated"/> - Called when the node becomes active in the hierarchy</description></item>
    ///   <item><description><see cref="OnBeginPlay"/> - Called when play mode starts</description></item>
    ///   <item><description><see cref="OnDeactivated"/> - Called when the node becomes inactive</description></item>
    ///   <item><description><see cref="OnEndPlay"/> - Called when play mode ends</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Nodes support serialization via MemoryPack for binary serialization and YAML for human-readable formats.
    /// They can be linked to prefabs via <see cref="SceneNodePrefabLink"/> for asset reuse.
    /// </para>
    /// </remarks>
    /// <example>
    /// Creating a scene node with components:
    /// <code>
    /// var node = new SceneNode("Player");
    /// var meshRenderer = node.AddComponent&lt;MeshRendererComponent&gt;();
    /// var controller = node.AddComponent&lt;PlayerController&gt;();
    /// parentNode.AddChild(node);
    /// </code>
    /// </example>
    [Serializable]
    [MemoryPackable]
    public sealed partial class SceneNode : XRWorldObjectBase, IPostCookedBinaryDeserialize
    {
        #region Constants and Fields

        /// <summary>
        /// The default name assigned to newly created scene nodes.
        /// </summary>
        public const string DefaultName = "New Scene Node";

        /// <summary>
        /// Thread-safe collection of components attached to this node.
        /// </summary>
        private readonly EventList<XRComponent> _components = new() { ThreadSafe = true };

        /// <summary>
        /// The transform that defines this node's position, rotation, and scale in the scene.
        /// </summary>
        private TransformBase _transform;

        /// <summary>
        /// Tracks whether <see cref="OnBeginPlay"/> has been called for lifecycle management.
        /// </summary>
        private bool _hasBegunPlay = false;

        /// <summary>
        /// Whether this node is locally active (not considering parent hierarchy).
        /// </summary>
        private bool _isActiveSelf = true;

        /// <summary>
        /// Whether this node is only visible/active in the editor, not at runtime.
        /// </summary>
        private bool _isEditorOnly;

        /// <summary>
        /// The rendering layer index (0-31) for visibility culling and rendering order.
        /// </summary>
        private int _layer = DefaultLayers.DynamicIndex;

        /// <summary>
        /// Optional link to a prefab asset for instancing and overrides.
        /// </summary>
        private SceneNodePrefabLink? _prefab;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNode"/> class with the default name.
        /// Used by MemoryPack for deserialization.
        /// </summary>
        [MemoryPackConstructor]
        public SceneNode()
            : this(DefaultName) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNode"/> class with a specific transform.
        /// </summary>
        /// <param name="transform">The transform to use for this node.</param>
        public SceneNode(TransformBase transform)
            : this(DefaultName, transform) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNode"/> class as a child of the specified parent.
        /// </summary>
        /// <param name="parent">The parent node in the scene hierarchy.</param>
        public SceneNode(SceneNode parent)
            : this(parent, DefaultName) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNode"/> class as a child with an optional transform.
        /// </summary>
        /// <param name="parent">The parent node in the scene hierarchy.</param>
        /// <param name="transform">The transform to use, or <c>null</c> to create a default <see cref="Transform"/>.</param>
        public SceneNode(SceneNode parent, TransformBase? transform = null)
            : this(parent, DefaultName, transform) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNode"/> class with a parent, name, and optional transform.
        /// </summary>
        /// <param name="parent">The parent node in the scene hierarchy.</param>
        /// <param name="name">The display name for this node.</param>
        /// <param name="transform">The transform to use, or <c>null</c> to create a default <see cref="Transform"/>.</param>
#pragma warning disable CS8618
        public SceneNode(SceneNode parent, string name, TransformBase? transform = null)
#pragma warning restore CS8618
        {
            Transform = transform ?? new Transform();
            Transform.Parent = parent?.Transform;

            Name = name;
            ComponentsInternal.PostAnythingAdded += OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved += OnComponentRemoved;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNode"/> class with a name and optional transform.
        /// </summary>
        /// <param name="name">The display name for this node.</param>
        /// <param name="transform">The transform to use, or <c>null</c> to create a default <see cref="Transform"/>.</param>
#pragma warning disable CS8618
        public SceneNode(string name, TransformBase? transform = null)
#pragma warning restore CS8618
        {
            Transform = transform ?? new Transform();

            Name = name;
            ComponentsInternal.PostAnythingAdded += OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved += OnComponentRemoved;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNode"/> class within a specific world instance.
        /// </summary>
        /// <param name="world">The world instance this node belongs to, or <c>null</c> for unattached nodes.</param>
        /// <param name="name">The display name for this node, or <c>null</c> to use <see cref="DefaultName"/>.</param>
        /// <param name="transform">The transform to use, or <c>null</c> to create a default <see cref="Transform"/>.</param>
#pragma warning disable CS8618
        public SceneNode(XRWorldInstance? world, string? name = null, TransformBase? transform = null)
#pragma warning restore CS8618
        {
            Transform = transform ?? new Transform();

            World = world;
            Name = name ?? DefaultName;
            ComponentsInternal.PostAnythingAdded += OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved += OnComponentRemoved;
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised when a component is added to this scene node.
        /// </summary>
        /// <remarks>
        /// The tuple contains the node and the component that was added.
        /// This event is raised after the component has been fully initialized and added to the internal collection.
        /// </remarks>
        [YamlIgnore]
        public XREvent<(SceneNode node, XRComponent comp)>? ComponentAdded;

        /// <summary>
        /// Raised when a component is removed from this scene node.
        /// </summary>
        /// <remarks>
        /// The tuple contains the node and the component that was removed.
        /// This event is raised after the component has been removed from the internal collection.
        /// </remarks>
        [YamlIgnore]
        public XREvent<(SceneNode node, XRComponent comp)>? ComponentRemoved;

        /// <summary>
        /// Raised when this scene node is activated in the hierarchy.
        /// </summary>
        /// <seealso cref="OnActivated"/>
        /// <seealso cref="IsActiveInHierarchy"/>
        public event Action<SceneNode>? Activated;

        /// <summary>
        /// Raised when this scene node is deactivated in the hierarchy.
        /// </summary>
        /// <seealso cref="OnDeactivated"/>
        /// <seealso cref="IsActiveInHierarchy"/>
        public event Action<SceneNode>? Deactivated;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the internal mutable collection of components attached to this node.
        /// </summary>
        /// <remarks>
        /// This is the backing store for components. Use <see cref="Components"/> for read-only access.
        /// </remarks>
        private EventList<XRComponent> ComponentsInternal => _components;

        /// <summary>
        /// True after the node has received a begin play call and until end play is invoked.
        /// </summary>
        [YamlIgnore]
        public bool HasBegunPlay => _hasBegunPlay;

        /// <summary>
        /// Gets or sets the components collection for YAML serialization.
        /// </summary>
        /// <remarks>
        /// This property is used by the YAML serializer for persistence. Do not use directly.
        /// </remarks>
        [YamlMember(Order = 0)]
        public EventList<XRComponent> ComponentsSerialized
        {
            get => _components;
            set
            {
                _components.Clear();
                if (value is null)
                    return;

                foreach (var component in value)
                {
                    if (component is null)
                        continue;

                    _components.Add(component);
                }
            }
        }

        /// <summary>
        /// Determines if the scene node is active in the scene hierarchy.
        /// When set to false, Stop() will be called and all child nodes and components will be deactivated.
        /// When set to true, Start() will be called and all child nodes and components will be activated.
        /// </summary>
        public bool IsActiveSelf
        {
            get => _isActiveSelf;
            set => SetField(ref _isActiveSelf, value);
        }

        /// <summary>
        /// If true, this node is editor-only (gizmos, tools, editor UI) and should not be loaded into
        /// the normal visible scene graph at runtime. The world instance may automatically attach it
        /// to its hidden editor scene instead.
        /// </summary>
        public bool IsEditorOnly
        {
            get => _isEditorOnly;
            set => SetField(ref _isEditorOnly, value);
        }

        /// <summary>
        /// If the scene node is active in the scene hierarchy. Dependent on the IsActiveSelf property of this scene node and all of its ancestors. 
        /// If any ancestor is inactive, this will return false. 
        /// When setting to true, if the scene node has a parent, it will set the parent's IsActiveInHierarchy property to true, recursively. 
        /// When setting to false, it will set the IsActiveSelf property to false.
        /// </summary>
        [YamlIgnore]
        public bool IsActiveInHierarchy
        {
            get
            {
                if (!IsActiveSelf || World is null)
                    return false;

                var parent = Parent;
                return parent is null || parent.IsActiveInHierarchy;
            }
            set
            {
                if (!value)
                    IsActiveSelf = false;
                else
                {
                    IsActiveSelf = true;
                    Parent?.IsActiveInHierarchy = true;
                }
            }
        }

        /// <summary>
        /// The components attached to this scene node.
        /// Use AddComponent&lt;T&gt;() and RemoveComponent&lt;T&gt;() or XRComponent.Destroy() to add and remove components.
        /// </summary>
        public IEventListReadOnly<XRComponent> Components => ComponentsInternal;

        /// <summary>
        /// The transform of this scene node.
        /// Will never be null, because scene nodes all have transformations in the scene.
        /// </summary>
        public TransformBase Transform
        {
            get => _transform ?? SetTransform<Transform>();
            private set => SetField(ref _transform, value);
        }

        /// <summary>
        /// Retrieves the transform of this scene node as type T.
        /// If forceConvert is true, the transform will be converted to type T if it is not already.
        /// If the transform is a derived type of T, it will be returned as type T but will not be converted.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="forceConvert"></param>
        /// <returns></returns>
        public T? GetTransformAs<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(bool forceConvert = false) where T : TransformBase, new()
            => !forceConvert
                ? Transform as T :
                Transform is T value
                    ? value
                    : SetTransform<T>();

        /// <summary>
        /// Attempts to retrieve the transform of this scene node as type T.
        /// If the transform is not of type T, transform will be null and the method will return false.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="transform"></param>
        /// <returns></returns>
        public bool TryGetTransformAs<T>([MaybeNullWhen(false)] out T? transform) where T : TransformBase
        {
            transform = Transform as T;
            return transform != null;
        }

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
            /// For a transform's world matrix to be preserved, 
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
        /// If retainParent is true, the parent of the new transform will be set to the parent of the current transform.
        /// </summary>
        /// <param name="transform"></param>
        /// <param name="retainParent"></param>
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
        /// If retainParent is true, the parent of the new transform will be set to the parent of the current transform.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="retainParent"></param>
        public T SetTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(ETransformSetFlags flags = ETransformSetFlags.Default) where T : TransformBase, new()
        {
            T value = new();
            SetTransform(value, flags);
            return value;
        }

        /// <summary>
        /// The immediate ancestor of this scene node, or null if this scene node is the root of the scene.
        /// </summary>
        [YamlIgnore]
        public SceneNode? Parent
        {
            get => _transform?.Parent?.SceneNode;
            set
            {
                if (_transform is null)
                    return;

                var oldParent = _transform.Parent;
                var newParent = value?.Transform;
                if (ReferenceEquals(oldParent, newParent))
                    return;

                OnParentChanging();

                _transform.Parent = newParent;

                if (oldParent is not null)
                {
                    lock (oldParent.Children)
                        oldParent.Children.Remove(_transform);
                }

                if (newParent is not null)
                {
                    lock (newParent.Children)
                    {
                        if (!newParent.Children.Contains(_transform))
                            newParent.Children.Add(_transform);
                    }
                }

                OnParentChanged();
            }
        }

        /// <summary>
        /// Gets or sets the prefab link for this node, enabling connection to a prefab asset.
        /// </summary>
        /// <value>
        /// A <see cref="SceneNodePrefabLink"/> that defines the connection to a prefab, or <c>null</c> if this node is not linked to a prefab.
        /// </value>
        /// <remarks>
        /// When linked to a prefab, this node receives updates from the prefab asset and tracks property overrides.
        /// </remarks>
        public SceneNodePrefabLink? Prefab
        {
            get => _prefab;
            set => SetField(ref _prefab, value);
        }

        /// <summary>
        /// Gets or sets the child nodes for YAML serialization.
        /// </summary>
        /// <remarks>
        /// This property is used by the YAML serializer for persistence.
        /// When setting, existing children are cleared and new children are re-parented to this node.
        /// </remarks>
        [YamlMember(Alias = "ChildNodes", Order = 1)]
        public SceneNode[] ChildNodesSerialized
        {
            get
            {
                var nodes = new List<SceneNode>();
                foreach (var child in Transform.Children)
                    if (child?.SceneNode is SceneNode node)
                        nodes.Add(node);
                return [.. nodes];
            }
            set
            {
                Transform.Clear();
                if (value is null)
                    return;

                foreach (var child in value)
                    child?.Parent = this;
            }
        }

        /// <summary>
        /// True when the node is part of a prefab instance.
        /// </summary>
        [YamlIgnore]
        public bool IsPrefabInstance => Prefab?.HasValidPrefab ?? false;

        /// <summary>
        /// Gets or sets the rendering layer index for this node.
        /// </summary>
        /// <value>An integer from 0 to 31 representing the layer.</value>
        /// <remarks>
        /// Layers are used for camera culling masks, physics collision filtering, and raycasting.
        /// The value is automatically clamped to the valid range [0, 31].
        /// </remarks>
        public int Layer
        {
            get => _layer;
            set => SetField(ref _layer, Math.Clamp(value, 0, 31));
        }

        /// <summary>
        /// Returns the first child of this scene node, if any.
        /// </summary>
        public SceneNode? FirstChild => Transform.FirstChild()?.SceneNode;

        /// <summary>
        /// Returns the last child of this scene node, if any.
        /// </summary>
        public SceneNode? LastChild => Transform.LastChild()?.SceneNode;

        /// <summary>
        /// Gets a value indicating whether this node has no transform assigned.
        /// </summary>
        /// <remarks>
        /// In normal circumstances, every scene node should have a transform. This is useful for validation.
        /// </remarks>
        public bool IsTransformNull => _transform is null;

        /// <summary>
        /// Gets the first component of the specified type.
        /// </summary>
        /// <param name="type">The type of component to retrieve.</param>
        /// <returns>The first matching component, or <c>null</c> if not found.</returns>
        public XRComponent? this[Type type] => GetComponent(type);

        /// <summary>
        /// Gets the component at the specified index.
        /// </summary>
        /// <param name="index">The zero-based index of the component.</param>
        /// <returns>The component at the index, or <c>null</c> if the index is out of range.</returns>
        public XRComponent? this[int index] => GetComponentAtIndex(index);

        #endregion

        #region Property Change Handlers

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Transform):
                        if (_transform != null)
                            UnlinkTransform();
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(IsActiveSelf):
                    if (IsActiveSelf)
                        OnActivated();
                    else
                        OnDeactivated();
                    break;
                case nameof(World):
                    Transform.World = World;
                    foreach (var component in Components)
                        component.World = World;
                    break;
                case nameof(Transform):
                    if (_transform != null)
                    {
                        _transform.Name = Name;
                        LinkTransform();
                    }
                    break;
                case nameof(Name):
                    _transform?.Name = Name;
                    break;
                case nameof(Layer):
                    ApplyLayerToAllComponents();
                    break;
            }
        }

        #endregion
    }
}
