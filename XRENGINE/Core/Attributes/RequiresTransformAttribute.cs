using System.Reflection;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Core.Attributes
{
    /// <summary>
    /// Requires that a specific transform is present on the scene node before adding this component.
    /// </summary>
    /// <param name="type"></param>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class RequiresTransformAttribute(Type type) : XRComponentAttribute()
    {
        public Type Type { get; set; } = type;

        public override bool VerifyComponentOnAdd(SceneNode node, XRComponent comp)
        {
            if (!Type.IsAssignableTo(typeof(TransformBase)))
                return true; //This attribute was used incorrectly, ignore it

            if (node.Transform.GetType().IsAssignableTo(Type))
                return true; //No problem, same type or derived type

            //Verify if any other already existing components also require a specific transform.
            //If so, we can't add this component and should throw an error.
            foreach (var c in node.Components)
            {
                var compType = c.GetType();
                var attr = compType.GetCustomAttribute<RequiresTransformAttribute>();
                if (attr is null)
                    continue;

                //This other component also requires a transform. Make sure it's the same type requested by this component, or a derived type.
                if (!Type.IsAssignableTo(attr.Type))
                {
                    Debug.LogWarning($"Cannot add component {compType.Name} to node {node.Name} because one or more components already on it requires a transform of type {attr.Type.Name}, but this component requires a transform of type {Type.Name}.");
                    return false;
                }
            }

            if (Activator.CreateInstance(Type, null) is TransformBase transform)
            {
                node.SetTransform(transform);
                return true;
            }

            Debug.LogWarning($"Could not create transform of type {Type.Name} required by component {comp.GetType().Name}; likely missing public parameterless constructor.");
            return false;
        }
    }
}