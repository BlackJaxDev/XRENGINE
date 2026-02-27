using XREngine.Components;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Rendering.Info;

namespace XREngine.Scene
{
    public sealed partial class SceneNode
    {
        #region Component Management

        /// <summary>
        /// Creates and adds a component of type T to the scene node.
        /// </summary>
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

        /// <summary>
        /// Creates and adds a component of type to the scene node.
        /// </summary>
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

            ComponentsInternal.Add(comp);

            comp.Destroying += ComponentDestroying;
            comp.Destroyed += ComponentDestroyed;

            if (IsActiveInHierarchy && World is not null)
                comp.OnComponentActivated();
        }

        /// <summary>
        /// Attempts to add a component of type T to this scene node.
        /// </summary>
        /// <typeparam name="T">The type of component to add.</typeparam>
        /// <param name="comp">The created component, or <c>null</c> if creation failed.</param>
        /// <param name="name">Optional name for the component.</param>
        /// <returns><c>true</c> if the component was successfully added; otherwise, <c>false</c>.</returns>
        public bool TryAddComponent<T>(out T? comp, string? name = null) where T : XRComponent
        {
            comp = AddComponent<T>(name);
            return comp != null;
        }

        /// <summary>
        /// Attempts to add a component of the specified type to this scene node.
        /// </summary>
        /// <param name="type">The type of component to add.</param>
        /// <param name="comp">The created component, or <c>null</c> if creation failed.</param>
        /// <param name="name">Optional name for the component.</param>
        /// <returns><c>true</c> if the component was successfully added; otherwise, <c>false</c>.</returns>
        public bool TryAddComponent(Type type, out XRComponent? comp, string? name = null)
        {
            comp = AddComponent(type, name);
            return comp != null;
        }

        /// <summary>
        /// Reads the attributes of the component and runs the logic for them.
        /// Returns true if the component should be added, false if it should not.
        /// </summary>
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
        public void RemoveComponent<T>() where T : XRComponent
        {
            var comp = GetComponent<T>();
            if (comp is null)
                return;

            ComponentsInternal.Remove(comp);
            comp.Destroying -= ComponentDestroying;
            comp.Destroyed -= ComponentDestroyed;
            comp.Destroy();
        }

        /// <summary>
        /// Removes the first component of type from the scene node and destroys it.
        /// </summary>
        public void RemoveComponent(Type type)
        {
            var comp = GetComponent(type);
            if (comp is null)
                return;

            ComponentsInternal.Remove(comp);
            comp.Destroying -= ComponentDestroying;
            comp.Destroyed -= ComponentDestroyed;
            comp.Destroy();
        }

        /// <summary>
        /// Detaches a component from this scene node without destroying it.
        /// The component is removed from the internal list, its events are unhooked,
        /// and it is deactivated, but it remains alive for potential reattachment.
        /// </summary>
        /// <param name="component">The component to detach.</param>
        /// <returns><c>true</c> if the component was successfully detached; otherwise, <c>false</c>.</returns>
        public bool DetachComponent(XRComponent component)
        {
            if (!ComponentsInternal.Contains(component))
                return false;

            ComponentsInternal.Remove(component);
            component.Destroying -= ComponentDestroying;
            component.Destroyed -= ComponentDestroyed;

            if (component.IsActive)
                component.OnComponentDeactivated();

            return true;
        }

        /// <summary>
        /// Reattaches a previously detached component to this scene node.
        /// The component is added back to the internal list, its events are hooked,
        /// and it is reactivated if the node is active.
        /// </summary>
        /// <param name="component">The component to reattach.</param>
        public void ReattachComponent(XRComponent component)
        {
            ComponentsInternal.Add(component);
            component.Destroying += ComponentDestroying;
            component.Destroyed += ComponentDestroyed;

            if (IsActiveInHierarchy && World is not null)
                component.OnComponentActivated();
        }

        /// <summary>
        /// Returns the first component of type T attached to the scene node.
        /// </summary>
        public T1? GetComponent<T1>() where T1 : XRComponent
            => ComponentsInternal.FirstOrDefault(x => x is T1) as T1;

        /// <summary>
        /// Gets or adds a component of type T to the scene node.
        /// </summary>
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
        public T1? GetLastComponent<T1>() where T1 : XRComponent
            => ComponentsInternal.LastOrDefault(x => x is T1) as T1;

        /// <summary>
        /// Returns all components of type T attached to the scene node.
        /// </summary>
        public IEnumerable<T1> GetComponents<T1>() where T1 : XRComponent
            => ComponentsInternal.OfType<T1>();

        /// <summary>
        /// Returns the first component of type attached to the scene node.
        /// </summary>
        public XRComponent? GetComponent(Type type)
            => ComponentsInternal.FirstOrDefault(type.IsInstanceOfType);

        /// <summary>
        /// Returns the first component with the specified type name.
        /// </summary>
        /// <param name="typeName">The simple name of the component type to find.</param>
        /// <returns>The first matching component, or <c>null</c> if not found.</returns>
        public XRComponent? GetComponent(string typeName)
            => ComponentsInternal.FirstOrDefault(x => string.Equals(x.GetType().Name, typeName));

        /// <summary>
        /// Returns the first component with the specified type name on this node or any descendant.
        /// </summary>
        /// <param name="typeName">The simple name of the component type to find.</param>
        /// <returns>The first matching component, or <c>null</c> if not found.</returns>
        public XRComponent? GetComponentInHierarchy(string typeName)
        {
            XRComponent? found = null;
            IterateComponents(component =>
            {
                if (found is not null)
                    return;
                if (string.Equals(component.GetType().Name, typeName))
                    found = component;
            }, iterateChildHierarchy: true);
            return found;
        }

        /// <summary>
        /// Returns the component at the given index.
        /// </summary>
        public XRComponent? GetComponentAtIndex(int index)
            => ComponentsInternal.ElementAtOrDefault(index);

        /// <summary>
        /// Returns the last component of type attached to the scene node.
        /// </summary>
        public XRComponent? GetLastComponent(Type type)
            => ComponentsInternal.LastOrDefault(type.IsInstanceOfType);

        /// <summary>
        /// Returns all components of type attached to the scene node.
        /// </summary>
        public IEnumerable<XRComponent> GetComponents(Type type)
            => ComponentsInternal.Where(type.IsInstanceOfType);

        /// <summary>
        /// Returns true if the scene node has a component of the given type attached to it.
        /// </summary>
        public bool HasComponent(Type requiredType)
            => ComponentsInternal.Any(requiredType.IsInstanceOfType);

        /// <summary>
        /// Returns true if the scene node has a component of type T attached to it.
        /// </summary>
        public bool HasComponent<T>() where T : XRComponent
            => ComponentsInternal.Any(x => x is T);

        /// <summary>
        /// Attempts to retrieve the first component of the given type attached to the scene node.
        /// </summary>
        public bool TryGetComponent(Type type, out XRComponent? comp)
        {
            comp = GetComponent(type);
            return comp != null;
        }

        /// <summary>
        /// Attempts to retrieve the first component of type T attached to the scene node.
        /// </summary>
        public bool TryGetComponent<T>(out T? comp) where T : XRComponent
        {
            comp = GetComponent<T>();
            return comp != null;
        }

        /// <summary>
        /// Attempts to retrieve all components of the given type attached to the scene node.
        /// </summary>
        public bool TryGetComponents(Type type, out IEnumerable<XRComponent> comps)
        {
            comps = GetComponents(type);
            return comps.Any();
        }

        /// <summary>
        /// Attempts to retrieve all components of type T attached to the scene node.
        /// </summary>
        public bool TryGetComponents<T>(out IEnumerable<T> comps) where T : XRComponent
        {
            comps = GetComponents<T>();
            return comps.Any();
        }

        #endregion

        #region AddComponents Overloads

        /// <summary>
        /// Adds two components of the specified types to this scene node.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <param name="names">Optional names for the components (in order).</param>
        /// <returns>A tuple containing the created components.</returns>
        public (T1? comp1, T2? comp2) AddComponents<T1, T2>(params string?[] names) where T1 : XRComponent where T2 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            for (int i = 0; i < names.Length; i++)
            {
                XRComponent? comp = i switch
                {
                    0 => comp1,
                    1 => comp2,
                    _ => null,
                };
                if (comp != null)
                    comp.Name = names[i];
            }
            return (comp1, comp2);
        }

        /// <summary>
        /// Adds three components of the specified types to this scene node.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <param name="names">Optional names for the components (in order).</param>
        /// <returns>A tuple containing the created components.</returns>
        public (T1? comp1, T2? comp2, T3? comp3) AddComponents<T1, T2, T3>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            for (int i = 0; i < names.Length; i++)
            {
                XRComponent? comp = i switch
                {
                    0 => comp1,
                    1 => comp2,
                    2 => comp3,
                    _ => null,
                };
                if (comp != null)
                    comp.Name = names[i];
            }
            return (comp1, comp2, comp3);
        }

        /// <summary>
        /// Adds four components of the specified types to this scene node.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <param name="names">Optional names for the components (in order).</param>
        /// <returns>A tuple containing the created components.</returns>
        public (T1? comp1, T2? comp2, T3? comp3, T4? comp4) AddComponents<T1, T2, T3, T4>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            var comp4 = AddComponent<T4>();
            for (int i = 0; i < names.Length; i++)
            {
                XRComponent? comp = i switch
                {
                    0 => comp1,
                    1 => comp2,
                    2 => comp3,
                    3 => comp4,
                    _ => null,
                };
                if (comp != null)
                    comp.Name = names[i];
            }
            return (comp1, comp2, comp3, comp4);
        }

        /// <summary>
        /// Adds five components of the specified types to this scene node.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <param name="names">Optional names for the components (in order).</param>
        /// <returns>A tuple containing the created components.</returns>
        public (T1? comp1, T2? comp2, T3? comp3, T4? comp4, T5? comp5) AddComponents<T1, T2, T3, T4, T5>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            var comp4 = AddComponent<T4>();
            var comp5 = AddComponent<T5>();
            for (int i = 0; i < names.Length; i++)
            {
                XRComponent? comp = i switch
                {
                    0 => comp1,
                    1 => comp2,
                    2 => comp3,
                    3 => comp4,
                    4 => comp5,
                    _ => null,
                };
                if (comp != null)
                    comp.Name = names[i];
            }
            return (comp1, comp2, comp3, comp4, comp5);
        }

        /// <summary>
        /// Adds six components of the specified types to this scene node.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <typeparam name="T6">The type of the sixth component.</typeparam>
        /// <param name="names">Optional names for the components (in order).</param>
        /// <returns>A tuple containing the created components.</returns>
        public (T1? comp1, T2? comp2, T3? comp3, T4? comp4, T5? comp5, T6? comp6) AddComponents<T1, T2, T3, T4, T5, T6>(params string?[] names) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent
        {
            var comp1 = AddComponent<T1>();
            var comp2 = AddComponent<T2>();
            var comp3 = AddComponent<T3>();
            var comp4 = AddComponent<T4>();
            var comp5 = AddComponent<T5>();
            var comp6 = AddComponent<T6>();
            for (int i = 0; i < names.Length; i++)
            {
                XRComponent? comp = i switch
                {
                    0 => comp1,
                    1 => comp2,
                    2 => comp3,
                    3 => comp4,
                    4 => comp5,
                    5 => comp6,
                    _ => null,
                };
                if (comp != null)
                    comp.Name = names[i];
            }
            return (comp1, comp2, comp3, comp4, comp5, comp6);
        }

        #endregion

        #region Component Event Handlers

        /// <summary>
        /// Handles cleanup when a component is removed from the node.
        /// </summary>
        /// <param name="item">The component that was removed.</param>
        private void OnComponentRemoved(XRComponent item)
        {
            item.RemovedFromSceneNode(this);
            ComponentRemoved?.Invoke((this, item));
        }

        /// <summary>
        /// Handles initialization when a component is added to the node.
        /// </summary>
        /// <param name="item">The component that was added.</param>
        private void OnComponentAdded(XRComponent item)
        {
            item.AddedToSceneNode(this);
            ApplyLayerToComponent(item);
            ComponentAdded?.Invoke((this, item));
        }

        /// <summary>
        /// Callback invoked before a component is destroyed.
        /// </summary>
        /// <param name="comp">The component being destroyed.</param>
        /// <returns>Always returns <c>true</c> to allow destruction.</returns>
        private bool ComponentDestroying(XRObjectBase comp) => true;

        /// <summary>
        /// Callback invoked after a component is destroyed, removing it from this node.
        /// </summary>
        /// <param name="comp">The component that was destroyed.</param>
        private void ComponentDestroyed(XRObjectBase comp)
        {
            if (comp is not XRComponent xrComp)
                return;

            ComponentsInternal.Remove(xrComp);
            xrComp.Destroying -= ComponentDestroying;
            xrComp.Destroyed -= ComponentDestroyed;
        }

        #endregion
        
        /// <summary>
        /// Applies the current layer setting to all renderable components on this node.
        /// </summary>
        private void ApplyLayerToAllComponents()
        {
            foreach (var component in ComponentsInternal)
                ApplyLayerToComponent(component);
        }

        /// <summary>
        /// Applies the current layer setting to a specific renderable component.
        /// </summary>
        /// <param name="component">The component to apply the layer to.</param>
        /// <remarks>
        /// Gizmo layer objects are not affected by this method.
        /// </remarks>
        private void ApplyLayerToComponent(XRComponent component)
        {
            if (component is not IRenderable renderable)
                return;

            var targetLayer = Layer;
            foreach (var ri in renderable.RenderedObjects)
            {
                if (ri is not RenderInfo3D ri3d)
                    continue;

                if (ri3d.Layer == DefaultLayers.GizmosIndex)
                    continue;

                ri3d.Layer = targetLayer;
            }
        }
    }
}
