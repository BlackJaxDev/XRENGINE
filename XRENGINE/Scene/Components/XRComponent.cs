using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components
{
    [Serializable]
    public abstract class XRComponent : XRWorldObjectBase
    {
        /// <summary>
        /// Global event for when a component is created.
        /// </summary>
        public static event Action<XRComponent>? ComponentCreated;

        /// <summary>
        /// Global event for when a component is destroyed.
        /// </summary>
        public static event Action<XRComponent>? ComponentDestroyed;

        private bool _isActive = true;
        [Browsable(false)]
        public bool IsActive
        {
            get => _isActive;
            set => SetField(ref _isActive, value);
        }

        [Browsable(false)]
        public new string? Name
        {
            get => base.Name;
            set => base.Name = value;
        }

        [Browsable(false)]
        public bool IsActiveInHierarchy
        {
            get
            {
                // During cooked-binary deserialization, components can exist before being
                // attached to a SceneNode. Treat them as inactive-in-hierarchy until wired.
                var node = _sceneNode;
                return node is not null && IsActive && node.IsActiveInHierarchy;
            }
        }

        //TODO: figure out how to disallow users from constructing xrcomponents
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        internal protected XRComponent() { }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

        internal static T New<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(SceneNode node) where T : XRComponent 
            => (T)New(node, typeof(T))!;

        internal static XRComponent? New(SceneNode node, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type t)
        {
            if (t is null || !t.IsSubclassOf(typeof(XRComponent)))
                return null;

            //Specific order of operations to ensure the component is properly constructed:
            //1. Get uninitialized object
            //2. Call the private method to set the scene node
            //3. Call the constructor with no parameters if it exists
            //4. Call OnTransformChanged to allow the component to set up any events or transforms AFTER the constructor is called
            //5. Set the world to the scene node's world to fully initialize the component in the scene
            //6. Invoke the ComponentCreated event

#pragma warning disable SYSLIB0050 // Type or member is obsolete
            object obj = FormatterServices.GetUninitializedObject(t);
#pragma warning restore SYSLIB0050 // Type or member is obsolete
            Type t2 = obj!.GetType();
            var method = typeof(XRComponent).GetMethod(nameof(ConstructionSetSceneNode), BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            method!.Invoke(obj, [node]);
#pragma warning disable IL2075 // 'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.
            t2.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy, Type.EmptyTypes)?.Invoke(obj, null);
#pragma warning restore IL2075 // 'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.

            var component = (XRComponent)obj;
            component.OnTransformChanged();
            component.World = component.SceneNode.World;

            ComponentCreated?.Invoke(component);

            return component;
        }

        /// <summary>
        /// Retrieves a component also located on the same parent scene node.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="component"></param>
        /// <returns></returns>
        public bool TryGetSiblingComponent<T>(out T? component) where T : XRComponent
            => SceneNode.TryGetComponent<T>(out component) && component != this;
        /// <summary>
        /// Retrieves a component also located on the same parent scene node.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T? GetSiblingComponent<T>(bool createIfNotExist = false) where T : XRComponent
        {
            if (TryGetSiblingComponent(out T? comp))
                return comp;
            if (createIfNotExist)
                return SceneNode.AddComponent<T>();
            return null;
        }
        /// <summary>
        /// Retrieves all components also located on the same parent scene node.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public IEnumerable<T> GetSiblingComponents<T>() where T : XRComponent
            => SceneNode.GetComponents<T>().Where(x => x != this);

#pragma warning disable IDE0051 // Remove unused private members
        private void ConstructionSetSceneNode(SceneNode node)
#pragma warning restore IDE0051 // Remove unused private members
        {
            _sceneNode = node;
            _sceneNode.PropertyChanging += SceneNodePropertyChanging;
            _sceneNode.PropertyChanged += SceneNodePropertyChanged;
        }

        private SceneNode _sceneNode;
        private bool _clearTicksOnStop = true;

        /// <summary>
        /// Scene node refers to the node that this component is attached to.
        /// It will be set automatically when the component is added to a scene node, and never change.
        /// If you set any events on the scene node from a component, make sure to unregister them by overriding OnDestroying().
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        public SceneNode SceneNode
        {
            get => _sceneNode;
            private set => SetField(ref _sceneNode, value);
        }

        /// <summary>
        /// The transform of the scene node this component is attached to.
        /// Will never be null, because components always have to exist attached to a scene node.
        /// </summary>
        [Browsable(false)]
        public TransformBase Transform => SceneNode.Transform;

        public T? TransformAs<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(bool forceConvert = false) where T : TransformBase, new()
            => _sceneNode?.GetTransformAs<T>(forceConvert);

        /// <summary>
        /// Returns the transform of the scene node this component is attached to, or null if the scene node doesn't have a default transform.
        /// </summary>
        [Browsable(false)]
        public Transform? DefaultTransform => _sceneNode?.GetTransformAs<Transform>(false);

        /// <summary>
        /// Returns the transform of the scene node this component is attached to as a default transform.
        /// </summary>
        public Transform GetForcedDefaultTransform()
            => SceneNode.GetTransformAs<Transform>(true)!;

        public bool TransformIs<T>(out T? transform) where T : TransformBase
        {
            if (Transform is T t)
            {
                transform = t;
                return true;
            }
            transform = null;
            return false;
        }

        private void SceneNodePropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XREngine.Scene.SceneNode.Transform):
                    if (!SceneNode.IsTransformNull)
                        OnTransformChanging();
                    break;
            }
        }

        private void SceneNodePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XREngine.Scene.SceneNode.Transform):
                    if (!SceneNode.IsTransformNull)
                        OnTransformChanged();
                    break;
            }
        }

        /// <summary>
        /// Called right before the transform object is changed.
        /// </summary>
        protected virtual void OnTransformChanging()
        {
            //If the transform is null, we don't need to do anything. This check avoids a potential stack overflow.
            if (SceneNode.IsTransformNull)
                return;

            Transform.RenderMatrixChanged -= OnTransformRenderWorldMatrixChanged;
        }

        /// <summary>
        /// Called right after the transform object is set.
        /// </summary>
        protected virtual void OnTransformChanged()
        {
            //If the transform is null, we don't need to do anything. This check avoids a potential stack overflow.
            if (SceneNode.IsTransformNull)
                return;

            Transform.RenderMatrixChanged += OnTransformRenderWorldMatrixChanged;
            OnTransformRenderWorldMatrixChanged(Transform, Transform.RenderMatrix);
        }

        protected virtual void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {

        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(SceneNode):
                        var oldNode = _sceneNode;
                        if (oldNode is not null)
                        {
                            oldNode.PropertyChanging -= SceneNodePropertyChanging;
                            oldNode.PropertyChanged -= SceneNodePropertyChanged;
                            if (!oldNode.IsTransformNull)
                                OnTransformChanging();
                        }
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
                case nameof(World):
                    // During snapshot restore/load, components can already be active-in-hierarchy
                    // by the time their owning SceneNode assigns World. Re-bind render infos so
                    // renderables are registered into the VisualScene.
                    if (World is null)
                        VerifyInterfacesOnStop();
                    else if (IsActiveInHierarchy)
                        VerifyInterfacesOnStart();
                    break;
                case nameof(IsActive):
                    if (IsActiveInHierarchy)
                        OnComponentActivated();
                    else
                        OnComponentDeactivated();
                    break;
                case nameof(SceneNode):
                    World = _sceneNode.World;
                    _sceneNode.PropertyChanging += SceneNodePropertyChanging;
                    _sceneNode.PropertyChanged += SceneNodePropertyChanged;
                    if (!_sceneNode.IsTransformNull)
                        OnTransformChanged();

                    // If IsActive was restored before SceneNode wiring, we may have skipped activation.
                    if (IsActiveInHierarchy)
                        OnComponentActivated();
                    break;
            }
        }

        /// <summary>
        /// Called when the component is made active.
        /// This is where ticks should register and connections to the world should be established.
        /// </summary>
        protected internal virtual void OnComponentActivated()
        {
            VerifyInterfacesOnStart();
        }

        /// <summary>
        /// Called once when the owning scene begins play (scene loaded).
        /// </summary>
        protected internal virtual void OnBeginPlay()
        {
        }

        /// <summary>
        /// If true, all registered ticks will be unregistered when the component is set to inactive.
        /// If false, ticks will remain registered when the component is stopped and must be manually unregistered.
        /// True by default.
        /// </summary>
        [Browsable(false)]
        public bool UnregisterTicksOnStop
        {
            get => _clearTicksOnStop;
            set => SetField(ref _clearTicksOnStop, value);
        }

        /// <summary>
        /// Called when the component is made inactive.
        /// </summary>
        protected internal virtual void OnComponentDeactivated()
        {
            VerifyInterfacesOnStop();
            if (UnregisterTicksOnStop)
                ClearTicks();
        }

        /// <summary>
        /// Called once when the owning scene ends play (scene unloaded).
        /// </summary>
        protected internal virtual void OnEndPlay()
        {
        }

        /// <summary>
        /// This method is called when the component is set to active in the world.
        /// It will check for known engine interfaces set by the user and apply engine data to them.
        /// </summary>
        internal virtual void VerifyInterfacesOnStart()
        {
            if (this is IRenderable rend)
                foreach (var obj in rend.RenderedObjects)
                    obj.WorldInstance = World;
        }

        /// <summary>
        /// This method is called when the component is set to inactive in the world.
        /// It will check for known engine interfaces set by the user and clear engine data from them.
        /// </summary>
        internal virtual void VerifyInterfacesOnStop()
        {
            if (this is IRenderable rend)
                foreach (var obj in rend.RenderedObjects)
                    obj.WorldInstance = null;
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();

            //Unsubscribe from scene node events
            if (_sceneNode is not null)
            {
                _sceneNode.PropertyChanging -= SceneNodePropertyChanging;
                _sceneNode.PropertyChanged -= SceneNodePropertyChanged;
            }

            //Unsubscribe from transform events
            if (_sceneNode is not null && !_sceneNode.IsTransformNull)
                OnTransformChanging();

            ComponentDestroyed?.Invoke(this);
        }

        internal protected virtual void RemovedFromSceneNode(SceneNode sceneNode)
        {

        }

        internal protected virtual void AddedToSceneNode(SceneNode sceneNode)
        {

        }
    }
    public enum ETickGroup
    {
        /// <summary>
        /// Variable update tick, occurs every frame.
        /// </summary>
        Normal,
        /// <summary>
        /// Variable update tick, occurs after the normal tick.
        /// </summary>
        Late,
        /// <summary>
        /// Fixed update tick, occurs before physics calculations.
        /// </summary>
        PrePhysics,
        /// <summary>
        /// Fixed update tick, occurs during physics calculations.
        /// </summary>
        DuringPhysics,
        /// <summary>
        /// Fixed update tick, occurs after physics calculations.
        /// </summary>
        PostPhysics,
    }
    /// <summary>
    /// Cast to an int and add any value to change the order of ticks within a group.
    /// These are default ticking groups for the default render pipeline, but you may use any values you wish that correspond to the render pipeline.
    /// </summary>
    public enum ETickOrder
    {
        /// <summary>
        /// Timing events
        /// </summary>
        Timers = 0,
        /// <summary>
        /// Input consumption events
        /// </summary>
        Input = 200000,
        /// <summary>
        /// Animation evaluation events
        /// </summary>
        Animation = 400000,
        /// <summary>
        /// Gameplay logic events
        /// </summary>
        Logic = 600000,
        /// <summary>
        /// Scene hierarchy events
        /// </summary>
        Scene = 800000,
    }
}
