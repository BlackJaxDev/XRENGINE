using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Scene
{
    [Serializable]
    public sealed class SceneNode : XRWorldObjectBase
    {
        //private static SceneNode? _dummy;
        //internal static SceneNode Dummy => _dummy ??= new SceneNode() { IsDummy = true };
        //internal bool IsDummy { get; private set; } = false;

        public const string DefaultName = "New Scene Node";

        public SceneNode()
            : this(DefaultName) { }
        public SceneNode(TransformBase transform)
            : this(DefaultName, transform) { }
        //public SceneNode(XRScene scene)
        //    : this(scene, DefaultName) { }
        public SceneNode(SceneNode parent)
            : this(parent, DefaultName) { }
        public SceneNode(SceneNode parent, TransformBase? transform = null)
            : this(parent, DefaultName, transform) { }
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public SceneNode(SceneNode parent, string name, TransformBase? transform = null)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Transform = transform ?? new Transform();
            Transform.Parent = parent?.Transform;

            Name = name;
            ComponentsInternal.PostAnythingAdded += OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved += OnComponentRemoved;
        }
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public SceneNode(string name, TransformBase? transform = null)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Transform = transform ?? new Transform();

            Name = name;
            ComponentsInternal.PostAnythingAdded += OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved += OnComponentRemoved;
        }
//#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
//        public SceneNode(XRScene scene, string name, TransformBase? transform = null)
//#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
//        {
//            Transform = transform ?? new Transform();

//            scene.RootNodes.Add(this);

//            Name = name;
//            ComponentsInternal.PostAnythingAdded += ComponentAdded;
//            ComponentsInternal.PostAnythingRemoved += ComponentRemoved;
//        }
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public SceneNode(XRWorldInstance? world, string? name = null, TransformBase? transform = null)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Transform = transform ?? new Transform();

            World = world;
            Name = name ?? DefaultName;
            ComponentsInternal.PostAnythingAdded += OnComponentAdded;
            ComponentsInternal.PostAnythingRemoved += OnComponentRemoved;
        }

        public XREvent<(SceneNode node, XRComponent comp)>? ComponentAdded;
        public XREvent<(SceneNode node, XRComponent comp)>? ComponentRemoved;

        private void OnComponentRemoved(XRComponent item)
        {
            item.RemovedFromSceneNode(this);
            ComponentRemoved?.Invoke((this, item));
        }
        private void OnComponentAdded(XRComponent item)
        {
            item.AddedToSceneNode(this);
            ComponentAdded?.Invoke((this, item));
        }

        private readonly EventList<XRComponent> _components = new() { ThreadSafe = true };
        private EventList<XRComponent> ComponentsInternal => _components;

        [YamlMember(Order = 0)]
        public EventList<XRComponent> ComponentsSerialized
        {
            get => _components;
            set
            {
                _components.Clear();
                _components.AddRange(value);
            }
        }

        private bool _isActiveSelf = true;
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
                    var parent = Parent;
                    if (parent != null)
                        parent.IsActiveInHierarchy = true;
                }
            }
        }

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
                    //lock (Components)
                    //{
                        foreach (var component in Components)
                            component.World = World;
                    //}
                    break;
                case nameof(Transform):
                    if (_transform != null)
                    {
                        _transform.Name = Name;
                        LinkTransform();
                    }
                    break;
                case nameof(Name):
                    if (_transform != null)
                        _transform.Name = Name;
                    break;
            }
        }

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

        private void TransformPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(TransformBase.Parent):
                    OnParentChanging();
                    break;
            }
        }

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

        private void OnParentChanging()
        {

        }

        private void OnParentChanged()
        {
            World = Parent?.World;
            if (IsActiveInHierarchy)
                ActivateTransform();
            else
                DeactivateTransform();
        }

        /// <summary>
        /// The components attached to this scene node.
        /// Use AddComponent<T>() and RemoveComponent<T>() or XRComponent.Destroy() to add and remove components.
        /// </summary>
        public IEventListReadOnly<XRComponent> Components => ComponentsInternal;

        private TransformBase _transform;
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
            if (flags.HasFlag(ETransformSetFlags.ClearNewChildren))
                transform.Clear();

            if (flags.HasFlag(ETransformSetFlags.RetainCurrentParent))
                transform.SetParent(_transform?.Parent, flags.HasFlag(ETransformSetFlags.RetainWorldTransform), true);

            if (flags.HasFlag(ETransformSetFlags.RetainCurrentChildren) && _transform is not null)
            {
                bool maintainWorldTransform = flags.HasFlag(ETransformSetFlags.RetainedChildrenMaintainWorldTransform);
                foreach (var child in _transform.Children)
                    transform.AddChild(child, maintainWorldTransform, true);
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
                if (_transform is not null)
                    _transform.Parent = value?.Transform;
            }
        }

        /// <summary>
        /// Returns the full path of the scene node in the scene hierarchy.
        /// </summary>
        /// <param name="splitter"></param>
        /// <returns></returns>
        public string GetPath(string splitter = "/")
        {
            var path = Name ?? string.Empty;
            var parent = Parent;
            while (parent != null)
            {
                path = $"{parent.Name}{splitter}{path}";
                parent = parent.Parent;
            }
            return path;
        }

        /// <summary>
        /// Creates and adds a component of type T to the scene node.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public T? AddComponent<T>(string? name = null) where T : XRComponent
        {
            using var t = Engine.Profiler.Start();

            var comp = XRComponent.New<T>(this);
            comp.World = World;
            comp.Name = name;

            if (!VerifyComponentAttributesOnAdd(comp, out XRComponent? existingComponent))
            {
                comp.World = null;
                comp.Destroy();
                return existingComponent as T;
            }

            AddComponent(comp);
            return comp;
        }

        public (T1? comp1, T2? comp2) AddComponents<T1, T2>(params string?[] names) where T1 : XRComponent where T2 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            if (names.Length > 0)
            {
                if (comp1 is not null)
                    comp1.Name = names[0];
                if (names.Length > 1 && comp2 is not null)
                    comp2.Name = names[1];
            }
            return (comp1, comp2);
        }

        public (T1? comp1, T2? comp2, T3? comp3) AddComponents<T1, T2, T3>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            if (names.Length > 0)
            {
                if (comp1 is not null)
                    comp1.Name = names[0];
                if (names.Length > 1 && comp2 is not null)
                    comp2.Name = names[1];
                if (names.Length > 2 && comp3 is not null)
                    comp3.Name = names[2];
            }
            return (comp1, comp2, comp3);
        }

        public (T1? comp1, T2? comp2, T3? comp3, T4? comp4) AddComponents<T1, T2, T3, T4>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            var comp4 = AddComponent<T4>();
            if (names.Length > 0)
            {
                if (comp1 is not null)
                    comp1.Name = names[0];
                if (names.Length > 1 && comp2 is not null)
                    comp2.Name = names[1];
                if (names.Length > 2 && comp3 is not null)
                    comp3.Name = names[2];
                if (names.Length > 3 && comp4 is not null)
                    comp4.Name = names[3];
            }
            return (comp1, comp2, comp3, comp4);
        }

        public (T1? comp1, T2? comp2, T3? comp3, T4? comp4, T5? comp5) AddComponents<T1, T2, T3, T4, T5>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            var comp4 = AddComponent<T4>();
            var comp5 = AddComponent<T5>();
            if (names.Length > 0)
            {
                if (comp1 is not null)
                    comp1.Name = names[0];
                if (names.Length > 1 && comp2 is not null)
                    comp2.Name = names[1];
                if (names.Length > 2 && comp3 is not null)
                    comp3.Name = names[2];
                if (names.Length > 3 && comp4 is not null)
                    comp4.Name = names[3];
                if (names.Length > 4 && comp5 is not null)
                    comp5.Name = names[4];
            }
            return (comp1, comp2, comp3, comp4, comp5);
        }

        public (T1? comp1, T2? comp2, T3? comp3, T4? comp4, T5? comp5, T6? comp6) AddComponents<T1, T2, T3, T4, T5, T6>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            var comp4 = AddComponent<T4>();
            var comp5 = AddComponent<T5>();
            var comp6 = AddComponent<T6>();
            if (names.Length > 0)
            {
                if (comp1 is not null)
                    comp1.Name = names[0];
                if (names.Length > 1 && comp2 is not null)
                    comp2.Name = names[1];
                if (names.Length > 2 && comp3 is not null)
                    comp3.Name = names[2];
                if (names.Length > 3 && comp4 is not null)
                    comp4.Name = names[3];
                if (names.Length > 4 && comp5 is not null)
                    comp5.Name = names[4];
                if (names.Length > 5 && comp6 is not null)
                    comp6.Name = names[5];
            }
            return (comp1, comp2, comp3, comp4, comp5, comp6);
        }

        /// <summary>
        /// Creates and adds a component of type to the scene node.
        /// </summary>
        /// <param name="type"></param>
        public XRComponent? AddComponent(Type type, string? name = null)
        {
            XRComponent? existingComponent = null;

            if (XRComponent.New(this, type) is not XRComponent comp || !VerifyComponentAttributesOnAdd(comp, out existingComponent))
                return existingComponent;

            AddComponent(comp);
            comp.Name = name;
            return comp;
        }

        private void AddComponent(XRComponent comp)
        {
            using var t = Engine.Profiler.Start();

            //lock (Components)
            //{
                ComponentsInternal.Add(comp);
            //}

            comp.Destroying += ComponentDestroying;
            comp.Destroyed += ComponentDestroyed;

            if (IsActiveInHierarchy && World is not null)
            {
                comp.VerifyInterfacesOnStart();
                comp.OnComponentActivated();
            }
        }

        public bool TryAddComponent<T>(out T? comp, string? name = null) where T : XRComponent
        {
            comp = AddComponent<T>(name);
            return comp != null;
        }

        public bool TryAddComponent(Type type, out XRComponent? comp, string? name = null)
        {
            comp = AddComponent(type, name);
            return comp != null;
        }

        /// <summary>
        /// Reads the attributes of the component and runs the logic for them.
        /// Returns true if the component should be added, false if it should not.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="comp"></param>
        /// <returns></returns>
        private bool VerifyComponentAttributesOnAdd<T>(T comp, out XRComponent? existingComponent) where T : XRComponent
        {
            existingComponent = GetComponent<T>();

            var attribs = comp.GetType().GetCustomAttributes(true);
            if (attribs.Length == 0)
                return true;

            foreach (var attrib in attribs)
                if (attrib is XRComponentAttribute xrAttrib && !xrAttrib.VerifyComponentOnAdd(this, comp))
                    return false;

            return true;
        }

        /// <summary>
        /// Removes the first component of type T from the scene node and destroys it.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void RemoveComponent<T>() where T : XRComponent
        {
            var comp = GetComponent<T>();
            if (comp is null)
                return;

            //lock (Components)
            //{
                ComponentsInternal.Remove(comp);
            //}
            comp.Destroying -= ComponentDestroying;
            comp.Destroyed -= ComponentDestroyed;
            comp.Destroy();
        }

        /// <summary>
        /// Removes the first component of type from the scene node and destroys it.
        /// </summary>
        /// <param name="type"></param>
        public void RemoveComponent(Type type)
        {
            var comp = GetComponent(type);
            if (comp is null)
                return;

            //lock (Components)
            //{
                ComponentsInternal.Remove(comp);
            //}
            comp.Destroying -= ComponentDestroying;
            comp.Destroyed -= ComponentDestroyed;
            comp.Destroy();
        }

        private bool ComponentDestroying(XRObjectBase comp)
        {
            return true;
        }
        private void ComponentDestroyed(XRObjectBase comp)
        {
            if (comp is not XRComponent xrComp)
                return;

            //lock (Components)
            //{
                ComponentsInternal.Remove(xrComp);
            //}
            xrComp.Destroying -= ComponentDestroying;
            xrComp.Destroyed -= ComponentDestroyed;
        }

        /// <summary>
        /// Returns the first component of type T attached to the scene node.
        /// </summary>
        /// <typeparam name="T1"></typeparam>
        /// <returns></returns>
        public T1? GetComponent<T1>() where T1 : XRComponent
        {
            //lock (Components)
            //{
                return ComponentsInternal.FirstOrDefault(x => x is T1) as T1;
            //}
        }

        /// <summary>
        /// Gets or adds a component of type T to the scene node.
        /// </summary>
        /// <typeparam name="T1"></typeparam>
        /// <param name="wasAdded"></param>
        /// <returns></returns>
        public T1? GetOrAddComponent<T1>(out bool wasAdded) where T1 : XRComponent
        {
            var comp = GetComponent<T1>();
            if (comp is null)
            {
                comp = AddComponent<T1>();
                wasAdded = true;
            }
            else
                wasAdded = false;

            return comp;
        }

        /// <summary>
        /// Returns the last component of type T attached to the scene node.
        /// </summary>
        /// <typeparam name="T1"></typeparam>
        /// <returns></returns>
        public T1? GetLastComponent<T1>() where T1 : XRComponent
        {
            //lock (Components)
            //{
                return ComponentsInternal.LastOrDefault(x => x is T1) as T1;
            //}
        }

        /// <summary>
        /// Returns all components of type T attached to the scene node.
        /// </summary>
        /// <typeparam name="T1"></typeparam>
        /// <returns></returns>
        public IEnumerable<T1> GetComponents<T1>() where T1 : XRComponent
        {
            //lock (Components)
            //{
                return ComponentsInternal.OfType<T1>();
            //}
        }

        /// <summary>
        /// Returns the first component of type attached to the scene node.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public XRComponent? GetComponent(Type type)
        {
            //lock (Components)
            //{
                return ComponentsInternal.FirstOrDefault(type.IsInstanceOfType);
            //}
        }

        public XRComponent? GetComponent(string typeName)
        {
            //lock (Components)
            //{
                return ComponentsInternal.FirstOrDefault(x => string.Equals(x.GetType().Name, typeName));
            //}
        }

        /// <summary>
        /// Returns the component at the given index.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public XRComponent? GetComponentAtIndex(int index)
        {
            //lock (Components)
            //{
                return ComponentsInternal.ElementAtOrDefault(index);
            //}
        }

        /// <summary>
        /// Returns the last component of type attached to the scene node.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public XRComponent? GetLastComponent(Type type)
        {
            //lock (Components)
            //{
                return ComponentsInternal.LastOrDefault(type.IsInstanceOfType);
            //}
        }

        /// <summary>
        /// Returns all components of type attached to the scene node.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public IEnumerable<XRComponent> GetComponents(Type type)
        {
            //lock (Components)
            //{
                return ComponentsInternal.Where(type.IsInstanceOfType);
            //}
        }

        public XRComponent? this[Type type]
            => GetComponent(type);
        public XRComponent? this[int index]
            => GetComponentAtIndex(index);

        public event Action<SceneNode>? Activated;
        public event Action<SceneNode>? Deactivated;

        /// <summary>
        /// Called when the scene node is added to a world or activated.
        /// </summary>
        public void OnActivated()
        {
            ActivateTransform();
            ActivateComponents();
            Activated?.Invoke(this);
        }

        private void ActivateComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
            {
                if (!component.IsActive)
                    continue;
                
                component.VerifyInterfacesOnStart();
                component.OnComponentActivated();
            }
        }

        private void ActivateTransform()
        {
            if (_transform is null)
                return;

            _transform.OnSceneNodeActivated();
            //lock (_transform.Children)
            //{
                foreach (var child in _transform.Children)
                {
                    var node = child?.SceneNode;
                    if (node is null)
                        continue;

                    if (node.IsActiveSelf)
                        node.OnActivated();
                }
            //}
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

        private void DeactivateComponents()
        {
            foreach (XRComponent component in ComponentsInternal)
            {
                if (!component.IsActive)
                    continue;
                
                component.OnComponentDeactivated();
                component.VerifyInterfacesOnStop();

                if (component.UnregisterTicksOnStop)
                    ClearTicks();
            }
        }

        private void DeactivateTransform()
        {
            if (_transform is null)
                return;

            _transform.OnSceneNodeDeactivated();
            _transform.ClearTicks();
            //lock (_transform.Children)
            //{
                foreach (var child in _transform.Children)
                {
                    var node = child?.SceneNode;
                    if (node is null)
                        continue;

                    if (node.IsActiveSelf)
                        node.OnDeactivated();
                }
            //}
        }

        /// <summary>
        /// Iterates through all components attached to the scene node and calls the componentAction on each.
        /// If iterateChildHierarchy is true, the method will also iterate through all child nodes and their components, recursively.
        /// </summary>
        /// <param name="componentAction"></param>
        /// <param name="iterateChildHierarchy"></param>
        public void IterateComponents(Action<XRComponent> componentAction, bool iterateChildHierarchy)
        {
            //lock (Components)
            //{
                foreach (var component in ComponentsInternal)
                    componentAction(component);
            //}

            if (!iterateChildHierarchy)
                return;

            //lock (Transform.Children)
            //{
                foreach (var child in Transform.Children)
                    child?.SceneNode?.IterateComponents(componentAction, true);
            //}
        }

        /// <summary>
        /// Iterates through all components *only of type T* that are attached to the scene node and calls the componentAction on each.
        /// If iterateChildHierarchy is true, the method will also iterate through all child nodes and their components, recursively.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="componentAction"></param>
        /// <param name="iterateChildHierarchy"></param>
        public void IterateComponents<T>(Action<T> componentAction, bool iterateChildHierarchy) where T : XRComponent
            => IterateComponents(c =>
            {
                if (c is T t)
                    componentAction(t);
            }, iterateChildHierarchy);

        /// <summary>
        /// Iterates through all components of type T attached to the scene node and calls the componentAction on each.
        /// </summary>
        /// <param name="nodeAction"></param>
        public void IterateHierarchy(Action<SceneNode> nodeAction)
        {
            nodeAction(this);

            //lock (Transform.Children)
            //{
                foreach (var child in Transform.Children)
                    child?.SceneNode?.IterateHierarchy(nodeAction);
            //}
        }

        /// <summary>
        /// Returns true if the scene node has a component of the given type attached to it.
        /// </summary>
        /// <param name="requiredType"></param>
        /// <returns></returns>
        public bool HasComponent(Type requiredType)
        {
            //lock (Components)
            //{
                return ComponentsInternal.Any(requiredType.IsInstanceOfType);
            //}
        }

        /// <summary>
        /// Returns true if the scene node has a component of type T attached to it.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public bool HasComponent<T>() where T : XRComponent
        {
            //lock (Components)
            //{
                return ComponentsInternal.Any(x => x is T);
            //}
        }

        /// <summary>
        /// Attempts to retrieve the first component of the given type attached to the scene node.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="comp"></param>
        /// <returns></returns>
        public bool TryGetComponent(Type type, out XRComponent? comp)
        {
            comp = GetComponent(type);
            return comp != null;
        }

        /// <summary>
        /// Attempts to retrieve the first component of type T attached to the scene node.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="comp"></param>
        /// <returns></returns>
        public bool TryGetComponent<T>(out T? comp) where T : XRComponent
        {
            comp = GetComponent<T>();
            return comp != null;
        }

        /// <summary>
        /// Attempts to retrieve all components of the given type attached to the scene node.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="comps"></param>
        /// <returns></returns>
        public bool TryGetComponents(Type type, out IEnumerable<XRComponent> comps)
        {
            comps = GetComponents(type);
            return comps.Any();
        }

        /// <summary>
        /// Attempts to retrieve all components of type T attached to the scene node.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="comps"></param>
        /// <returns></returns>
        public bool TryGetComponents<T>(out IEnumerable<T> comps) where T : XRComponent
        {
            comps = GetComponents<T>();
            return comps.Any();
        }

        /// <summary>
        /// Returns a string representation of the scene node and its children.
        /// </summary>
        /// <param name="depth"></param>
        /// <returns></returns>
        public string PrintTree(int depth = 0)
        {
            string d = new(' ', depth++);
            string output = $"{d}{Transform}{Environment.NewLine}";
            lock (Transform.Children)
            {
                foreach (var child in Transform.Children)
                    if (child?.SceneNode is SceneNode node)
                        output += node.PrintTree(depth);
            }
            return output;
        }

        public delegate bool DelFindDescendant(string fullPath, string nodeName);

        /// <summary>
        /// Finds the first descendant of the scene node that has a name that matches the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="comp"></param>
        /// <returns></returns>
        public SceneNode? FindDescendantByName(string name, StringComparison comp = StringComparison.Ordinal)
            => FindDescendant((fullPath, nodeName) => string.Equals(name, nodeName, comp));

        /// <summary>
        /// Finds the first descendant of the scene node that has a path that matches the given path.
        /// pathSplitter is the character that separates the names in the path, and should be the same as the character used for splitting names in the path parameter.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="pathSplitter"></param>
        /// <returns></returns>
        public SceneNode? FindDescendant(string path, string pathSplitter = "/")
            => FindDescendantInternal(path, (fullPath, nodeName) => fullPath == path, pathSplitter);

        /// <summary>
        /// Finds the first descendant of the scene node that matches the given comparer.
        /// pathSplitter is the character that will be used to separate names in the path.
        /// </summary>
        /// <param name="comparer"></param>
        /// <param name="pathSplitter"></param>
        /// <returns></returns>
        public SceneNode? FindDescendant(DelFindDescendant comparer, string pathSplitter = "/")
            => FindDescendantInternal(Name ?? string.Empty, comparer, pathSplitter);

        /// <summary>
        /// Finds the first descendant of the scene node that matches the given comparer.
        /// fullPath is the path of the current node in the hierarchy recursion.
        /// pathSplitter is the character that will be used to separate names in the path.
        /// </summary>
        /// <param name="fullPath"></param>
        /// <param name="comparer"></param>
        /// <param name="pathSplitter"></param>
        /// <returns></returns>
        private SceneNode? FindDescendantInternal(string fullPath, DelFindDescendant comparer, string pathSplitter)
        {
            string name = Name ?? string.Empty;
            if (comparer(fullPath, name))
                return this;
            fullPath += $"{pathSplitter}{name}";
            //lock (Transform.Children)
            //{
                foreach (var child in Transform.Children)
                    if (child?.SceneNode is SceneNode node)
                        if (node.FindDescendantInternal(fullPath, comparer, pathSplitter) is SceneNode found)
                            return found;
            //}
            return null;
        }
        public SceneNode? FindDescendant(Func<TransformBase, bool> predicate)
        {
            if (predicate(Transform))
                return this;
            //lock (Transform.Children)
            //{
                foreach (var child in Transform.Children)
                    if (child?.SceneNode is SceneNode node)
                        if (node.FindDescendant(predicate) is SceneNode found)
                            return found;
            //}
            return null;
        }

        public SceneNode?[] FindDescendants(params Func<TransformBase, bool>[] predicates)
        {
            SceneNode?[] nodes = new SceneNode?[predicates.Length];
            //lock (Transform.Children)
            //{
                foreach (var child in Transform.Children)
                    if (child?.SceneNode is SceneNode node)
                    {
                        for (int i = 0; i < predicates.Length; i++)
                            if (nodes[i] is null && predicates[i](child))
                            {
                                nodes[i] = node;
                                break;
                            }
                        if (nodes.Any(x => x is null))
                            node.FindDescendants(predicates);
                        else
                            break;
                    }
            //}
            return nodes;
        }

        public void AddChild(SceneNode node)
        {
            Transform.Add(node.Transform);
        }
        public void InsertChild(SceneNode node, int index)
        {
            Transform.Insert(index, node.Transform);
        }
        public void RemoveChild(SceneNode node)
        {
            Transform.Remove(node.Transform);
        }
        public void RemoveChildAt(int index)
        {
            Transform.RemoveAt(index);
        }

        public static SceneNode New<T1>(SceneNode? parentNode, out T1 comp1, string? name = null) where T1 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            return node;
        }
        public static SceneNode New<T1, T2>(SceneNode? parentNode, out T1 comp1, out T2 comp2, string? name = null) where T1 : XRComponent where T2 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            comp2 = node.AddComponent<T2>()!;
            return node;
        }
        public static SceneNode New<T1, T2, T3>(SceneNode? parentNode, out T1 comp1, out T2 comp2, out T3 comp3, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            comp2 = node.AddComponent<T2>()!;
            comp3 = node.AddComponent<T3>()!;
            return node;
        }
        public static SceneNode New<T1, T2, T3, T4>(SceneNode? parentNode, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            comp2 = node.AddComponent<T2>()!;
            comp3 = node.AddComponent<T3>()!;
            comp4 = node.AddComponent<T4>()!;
            return node;
        }
        public static SceneNode New<T1, T2, T3, T4, T5>(SceneNode? parentNode, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            comp2 = node.AddComponent<T2>()!;
            comp3 = node.AddComponent<T3>()!;
            comp4 = node.AddComponent<T4>()!;
            comp5 = node.AddComponent<T5>()!;
            return node;
        }
        public static SceneNode New<T1, T2, T3, T4, T5, T6>(SceneNode? parentNode, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, out T6 comp6, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            comp2 = node.AddComponent<T2>()!;
            comp3 = node.AddComponent<T3>()!;
            comp4 = node.AddComponent<T4>()!;
            comp5 = node.AddComponent<T5>()!;
            comp6 = node.AddComponent<T6>()!;
            return node;
        }

        public SceneNode NewChild(string? name = null)
            => new(this) { Name = name };
        public SceneNode NewChild<T1>(out T1 comp1, string? name = null) where T1 : XRComponent
            => New(this, out comp1, name);
        public SceneNode NewChild<T1, T2>(out T1 comp1, out T2 comp2, string? name = null) where T1 : XRComponent where T2 : XRComponent
            => New(this, out comp1, out comp2, name);
        public SceneNode NewChild<T1, T2, T3>(out T1 comp1, out T2 comp2, out T3 comp3, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent
            => New(this, out comp1, out comp2, out comp3, name);
        public SceneNode NewChild<T1, T2, T3, T4>(out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent
            => New(this, out comp1, out comp2, out comp3, out comp4, name);
        public SceneNode NewChild<T1, T2, T3, T4, T5>(out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent
            => New(this, out comp1, out comp2, out comp3, out comp4, out comp5, name);
        public SceneNode NewChild<T1, T2, T3, T4, T5, T6>(out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, out T6 comp6, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent
            => New(this, out comp1, out comp2, out comp3, out comp4, out comp5, out comp6, name);
        
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform>(out TTransform tfm, string? name = null) where TTransform : TransformBase, new()
            => SetTransform(new SceneNode(this) { Name = name }, out tfm);
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1>(out TTransform tfm, out T1 comp1, string? name = null) where T1 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, name), out tfm);
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2>(out TTransform tfm, out T1 comp1, out T2 comp2, string? name = null) where T1 : XRComponent where T2 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, name), out tfm);
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, name), out tfm);
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, out comp4, name), out tfm);
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4, T5>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, out comp4, out comp5, name), out tfm);
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4, T5, T6>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, out T6 comp6, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, out comp4, out comp5, out comp6, name), out tfm);

        private static SceneNode SetTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform>(SceneNode sceneNode, out TTransform tfm) where TTransform : TransformBase, new()
        {
            tfm = sceneNode.GetTransformAs<TTransform>(true)!;
            return sceneNode;
        }

        /// <summary>
        /// Returns the first child of this scene node, if any.
        /// </summary>
        /// <returns></returns>
        public SceneNode? FirstChild
            => Transform.FirstChild()?.SceneNode;

        /// <summary>
        /// Returns the last child of this scene node, if any.
        /// </summary>
        /// <returns></returns>
        public SceneNode? LastChild
            => Transform.LastChild()?.SceneNode;

        public SceneNode? GetChild(int index)
            => Transform.GetChild(index)?.SceneNode;

        public bool IsTransformNull => _transform is null;
    }
}
