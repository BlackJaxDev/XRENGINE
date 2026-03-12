using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.Scene
{
    public sealed partial class SceneNode
    {
        #region Hierarchy Iteration

        /// <summary>
        /// Iterates through all components attached to the scene node and calls the componentAction on each.
        /// If iterateChildHierarchy is true, the method will also iterate through all child nodes and their components, recursively.
        /// </summary>
        public void IterateComponents(Action<XRComponent> componentAction, bool iterateChildHierarchy)
        {
            foreach (var component in ComponentsInternal)
                componentAction(component);

            if (!iterateChildHierarchy)
                return;

            foreach (var child in Transform.Children)
                child?.SceneNode?.IterateComponents(componentAction, true);
        }

        /// <summary>
        /// Iterates through all components *only of type T* that are attached to the scene node and calls the componentAction on each.
        /// If iterateChildHierarchy is true, the method will also iterate through all child nodes and their components, recursively.
        /// </summary>
        public void IterateComponents<T>(Action<T> componentAction, bool iterateChildHierarchy) where T : XRComponent
            => IterateComponents(c =>
            {
                if (c is T t)
                    componentAction(t);
            }, iterateChildHierarchy);

        /// <summary>
        /// Iterates through all nodes in the hierarchy starting from this node.
        /// </summary>
        public void IterateHierarchy(Action<SceneNode> nodeAction)
        {
            nodeAction(this);

            foreach (var child in Transform.Children)
                child?.SceneNode?.IterateHierarchy(nodeAction);
        }

        /// <summary>
        /// Returns a string representation of the scene node and its children.
        /// </summary>
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

        #endregion

        #region Hierarchy Search

        /// <summary>
        /// Delegate for finding descendants in the scene hierarchy.
        /// </summary>
        /// <param name="fullPath">The full path from the search root to the current node.</param>
        /// <param name="nodeName">The name of the current node being tested.</param>
        /// <returns><c>true</c> if the node matches the search criteria; otherwise, <c>false</c>.</returns>
        public delegate bool DelFindDescendant(string fullPath, string nodeName);

        /// <summary>
        /// Returns the full path of the scene node in the scene hierarchy.
        /// </summary>
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
        /// Finds the first descendant of the scene node that has a name that matches the given name.
        /// </summary>
        public SceneNode? FindDescendantByName(string name, StringComparison comp = StringComparison.Ordinal)
            => FindDescendant((fullPath, nodeName) => string.Equals(name, nodeName, comp));

        /// <summary>
        /// Finds the first descendant of the scene node that has a path that matches the given path.
        /// </summary>
        public SceneNode? FindDescendant(string path, string pathSplitter = "/")
            => FindDescendantInternal(path, (fullPath, nodeName) => fullPath == path, pathSplitter);

        /// <summary>
        /// Finds the first descendant of the scene node that matches the given comparer.
        /// </summary>
        public SceneNode? FindDescendant(DelFindDescendant comparer, string pathSplitter = "/")
            => FindDescendantInternal(Name ?? string.Empty, comparer, pathSplitter);

        /// <summary>
        /// Finds the first descendant whose transform matches the given predicate.
        /// </summary>
        /// <param name="predicate">A function to test each transform.</param>
        /// <returns>The first matching scene node, or <c>null</c> if not found.</returns>
        public SceneNode? FindDescendant(Func<TransformBase, bool> predicate)
        {
            if (predicate(Transform))
                return this;

            foreach (var child in Transform.Children)
                if (child?.SceneNode is SceneNode node)
                    if (node.FindDescendant(predicate) is SceneNode found)
                        return found;

            return null;
        }

        /// <summary>
        /// Finds descendants matching each of the provided predicates.
        /// </summary>
        /// <param name="predicates">Functions to test transforms against.</param>
        /// <returns>An array of scene nodes, one for each predicate (may contain nulls for unmatched predicates).</returns>
        public SceneNode?[] FindDescendants(params Func<TransformBase, bool>[] predicates)
        {
            SceneNode?[] nodes = new SceneNode?[predicates.Length];
            foreach (var child in Transform.Children)
            {
                if (child?.SceneNode is not SceneNode node)
                    continue;

                for (int i = 0; i < predicates.Length; i++)
                {
                    if (nodes[i] is null && predicates[i](child))
                    {
                        nodes[i] = node;
                        break;
                    }
                }

                if (nodes.Any(x => x is null))
                    node.FindDescendants(predicates);
                else
                    break;
            }
            return nodes;
        }

        /// <summary>
        /// Internal recursive implementation for finding descendants.
        /// </summary>
        /// <param name="fullPath">The accumulated path from the search root.</param>
        /// <param name="comparer">The comparison delegate to use.</param>
        /// <param name="pathSplitter">The path separator string.</param>
        /// <returns>The first matching scene node, or <c>null</c> if not found.</returns>
        private SceneNode? FindDescendantInternal(string fullPath, DelFindDescendant comparer, string pathSplitter)
        {
            string name = Name ?? string.Empty;
            if (comparer(fullPath, name))
                return this;

            fullPath += $"{pathSplitter}{name}";
            foreach (var child in Transform.Children)
                if (child?.SceneNode is SceneNode node)
                    if (node.FindDescendantInternal(fullPath, comparer, pathSplitter) is SceneNode found)
                        return found;

            return null;
        }

        /// <summary>
        /// Finds the first component of type T in this node or any descendant.
        /// </summary>
        /// <typeparam name="T">The type of component to find.</typeparam>
        /// <returns>The first matching component, or <c>null</c> if not found.</returns>
        public T? FindFirstDescendantComponent<T>() where T : XRComponent
        {
            foreach (var component in ComponentsInternal)
                if (component is T t)
                    return t;

            foreach (var child in Transform.Children)
                if (child?.SceneNode is SceneNode node)
                    if (node.FindFirstDescendantComponent<T>() is T found)
                        return found;

            return null;
        }

        /// <summary>
        /// Finds all components of type T in this node and all descendants.
        /// </summary>
        /// <typeparam name="T">The type of component to find.</typeparam>
        /// <returns>An array of all matching components.</returns>
        public T[] FindAllDescendantComponents<T>() where T : XRComponent
        {
            List<T> components = [];
            foreach (var component in ComponentsInternal)
                if (component is T t)
                    components.Add(t);

            foreach (var child in Transform.Children)
                if (child?.SceneNode is SceneNode node)
                    components.AddRange(node.FindAllDescendantComponents<T>());

            return [.. components];
        }

        /// <summary>
        /// Gets the child scene node at the specified index.
        /// </summary>
        /// <param name="index">The zero-based index of the child.</param>
        /// <returns>The child scene node, or <c>null</c> if the index is out of range.</returns>
        public SceneNode? GetChild(int index) => Transform.GetChild(index)?.SceneNode;

        #endregion

        #region Child Management

        /// <summary>
        /// Adds a scene node as a child of this node.
        /// </summary>
        /// <param name="node">The node to add as a child.</param>
        public void AddChild(SceneNode node) => Transform.Add(node.Transform);

        /// <summary>
        /// Inserts a scene node as a child at the specified index.
        /// </summary>
        /// <param name="node">The node to insert.</param>
        /// <param name="index">The zero-based index at which to insert the child.</param>
        public void InsertChild(SceneNode node, int index) => Transform.Insert(index, node.Transform);

        /// <summary>
        /// Removes a child scene node from this node.
        /// </summary>
        /// <param name="node">The child node to remove.</param>
        public void RemoveChild(SceneNode node) => Transform.Remove(node.Transform);

        /// <summary>
        /// Removes the child at the specified index.
        /// </summary>
        /// <param name="index">The zero-based index of the child to remove.</param>
        public void RemoveChildAt(int index) => Transform.RemoveAt(index);

        #endregion
    }
}
