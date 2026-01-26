using System.Diagnostics.CodeAnalysis;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.Scene
{
    public sealed partial class SceneNode
    {
        #region Static Factory Methods

        /// <summary>
        /// Creates a new scene node with one component.
        /// </summary>
        /// <typeparam name="T1">The type of component to add.</typeparam>
        /// <param name="parentNode">The optional parent node.</param>
        /// <param name="comp1">The created component.</param>
        /// <param name="name">Optional name for the node.</param>
        /// <returns>The newly created scene node.</returns>
        public static SceneNode New<T1>(SceneNode? parentNode, out T1 comp1, string? name = null) where T1 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            return node;
        }

        /// <summary>
        /// Creates a new scene node with two components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <param name="parentNode">The optional parent node.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="name">Optional name for the node.</param>
        /// <returns>The newly created scene node.</returns>
        public static SceneNode New<T1, T2>(SceneNode? parentNode, out T1 comp1, out T2 comp2, string? name = null) where T1 : XRComponent where T2 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            comp2 = node.AddComponent<T2>()!;
            return node;
        }

        /// <summary>
        /// Creates a new scene node with three components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <param name="parentNode">The optional parent node.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="name">Optional name for the node.</param>
        /// <returns>The newly created scene node.</returns>
        public static SceneNode New<T1, T2, T3>(SceneNode? parentNode, out T1 comp1, out T2 comp2, out T3 comp3, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent
        {
            name ??= string.Empty;
            var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
            comp1 = node.AddComponent<T1>()!;
            comp2 = node.AddComponent<T2>()!;
            comp3 = node.AddComponent<T3>()!;
            return node;
        }

        /// <summary>
        /// Creates a new scene node with four components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <param name="parentNode">The optional parent node.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="name">Optional name for the node.</param>
        /// <returns>The newly created scene node.</returns>
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

        /// <summary>
        /// Creates a new scene node with five components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <param name="parentNode">The optional parent node.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="comp5">The fifth created component.</param>
        /// <param name="name">Optional name for the node.</param>
        /// <returns>The newly created scene node.</returns>
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

        /// <summary>
        /// Creates a new scene node with six components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <typeparam name="T6">The type of the sixth component.</typeparam>
        /// <param name="parentNode">The optional parent node.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="comp5">The fifth created component.</param>
        /// <param name="comp6">The sixth created component.</param>
        /// <param name="name">Optional name for the node.</param>
        /// <returns>The newly created scene node.</returns>
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

        /// <summary>
        /// Helper method to set a transform on a scene node and return both.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to set.</typeparam>
        /// <param name="sceneNode">The scene node to modify.</param>
        /// <param name="tfm">The created transform.</param>
        /// <returns>The scene node with the new transform.</returns>
        private static SceneNode SetTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform>(SceneNode sceneNode, out TTransform tfm) where TTransform : TransformBase, new()
        {
            tfm = sceneNode.GetTransformAs<TTransform>(true)!;
            return sceneNode;
        }

        #endregion

        #region Instance Factory Methods

        /// <summary>
        /// Creates a new child scene node.
        /// </summary>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChild(string? name = null) => new(this) { Name = name };

        /// <summary>
        /// Creates a new child scene node with one component.
        /// </summary>
        /// <typeparam name="T1">The type of component to add.</typeparam>
        /// <param name="comp1">The created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChild<T1>(out T1 comp1, string? name = null) where T1 : XRComponent
            => New(this, out comp1, name);

        /// <summary>
        /// Creates a new child scene node with two components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChild<T1, T2>(out T1 comp1, out T2 comp2, string? name = null) where T1 : XRComponent where T2 : XRComponent
            => New(this, out comp1, out comp2, name);

        /// <summary>
        /// Creates a new child scene node with three components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChild<T1, T2, T3>(out T1 comp1, out T2 comp2, out T3 comp3, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent
            => New(this, out comp1, out comp2, out comp3, name);

        /// <summary>
        /// Creates a new child scene node with four components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChild<T1, T2, T3, T4>(out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent
            => New(this, out comp1, out comp2, out comp3, out comp4, name);

        /// <summary>
        /// Creates a new child scene node with five components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="comp5">The fifth created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChild<T1, T2, T3, T4, T5>(out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent
            => New(this, out comp1, out comp2, out comp3, out comp4, out comp5, name);

        /// <summary>
        /// Creates a new child scene node with six components.
        /// </summary>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <typeparam name="T6">The type of the sixth component.</typeparam>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="comp5">The fifth created component.</param>
        /// <param name="comp6">The sixth created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChild<T1, T2, T3, T4, T5, T6>(out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, out T6 comp6, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent
            => New(this, out comp1, out comp2, out comp3, out comp4, out comp5, out comp6, name);

        /// <summary>
        /// Creates a new child scene node with a specific transform type.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to create.</typeparam>
        /// <param name="tfm">The created transform.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform>(out TTransform tfm, string? name = null) where TTransform : TransformBase, new()
            => SetTransform(new SceneNode(this) { Name = name }, out tfm);

        /// <summary>
        /// Creates a new child scene node with a specific transform type and one component.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to create.</typeparam>
        /// <typeparam name="T1">The type of component to add.</typeparam>
        /// <param name="tfm">The created transform.</param>
        /// <param name="comp1">The created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1>(out TTransform tfm, out T1 comp1, string? name = null) where T1 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, name), out tfm);

        /// <summary>
        /// Creates a new child scene node with a specific transform type and two components.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to create.</typeparam>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <param name="tfm">The created transform.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2>(out TTransform tfm, out T1 comp1, out T2 comp2, string? name = null) where T1 : XRComponent where T2 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, name), out tfm);

        /// <summary>
        /// Creates a new child scene node with a specific transform type and three components.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to create.</typeparam>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <param name="tfm">The created transform.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, name), out tfm);

        /// <summary>
        /// Creates a new child scene node with a specific transform type and four components.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to create.</typeparam>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <param name="tfm">The created transform.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, out comp4, name), out tfm);

        /// <summary>
        /// Creates a new child scene node with a specific transform type and five components.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to create.</typeparam>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <param name="tfm">The created transform.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="comp5">The fifth created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4, T5>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, out comp4, out comp5, name), out tfm);

        /// <summary>
        /// Creates a new child scene node with a specific transform type and six components.
        /// </summary>
        /// <typeparam name="TTransform">The type of transform to create.</typeparam>
        /// <typeparam name="T1">The type of the first component.</typeparam>
        /// <typeparam name="T2">The type of the second component.</typeparam>
        /// <typeparam name="T3">The type of the third component.</typeparam>
        /// <typeparam name="T4">The type of the fourth component.</typeparam>
        /// <typeparam name="T5">The type of the fifth component.</typeparam>
        /// <typeparam name="T6">The type of the sixth component.</typeparam>
        /// <param name="tfm">The created transform.</param>
        /// <param name="comp1">The first created component.</param>
        /// <param name="comp2">The second created component.</param>
        /// <param name="comp3">The third created component.</param>
        /// <param name="comp4">The fourth created component.</param>
        /// <param name="comp5">The fifth created component.</param>
        /// <param name="comp6">The sixth created component.</param>
        /// <param name="name">Optional name for the child node.</param>
        /// <returns>The newly created child scene node.</returns>
        public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4, T5, T6>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, out T6 comp6, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent where TTransform : TransformBase, new()
            => SetTransform(New(this, out comp1, out comp2, out comp3, out comp4, out comp5, out comp6, name), out tfm);

        #endregion
    }
}
