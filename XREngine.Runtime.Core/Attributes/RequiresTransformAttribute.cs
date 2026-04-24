using System.Reflection;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Core.Attributes
{
    /// <summary>
    /// Requires that a specific transform is present on the scene node before adding this component.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class RequiresTransformAttribute(Type type) : XRComponentAttribute()
    {
        public Type Type { get; set; } = type;

        public override bool VerifyComponentOnAdd(SceneNode node, XRComponent comp)
        {
            if (!Type.IsAssignableTo(typeof(TransformBase)))
                return true;

            if (node.Transform.GetType().IsAssignableTo(Type))
                return true;

            foreach (var existingComponent in node.Components)
            {
                var attr = existingComponent.GetType().GetCustomAttribute<RequiresTransformAttribute>();
                if (attr is null)
                    continue;

                if (!Type.IsAssignableTo(attr.Type))
                {
                    RuntimeSceneNodeServices.Current.LogWarning(
                        $"Cannot add component {existingComponent.GetType().Name} to node {node.Name} because one or more components already on it requires a transform of type {attr.Type.Name}, but this component requires a transform of type {Type.Name}.");
                    return false;
                }
            }

            if (TransformFactoryRegistry.TryCreate(Type, out TransformBase? transform) && transform is not null)
            {
                node.SetTransform(transform);
                return true;
            }

            if (!XRRuntimeEnvironment.IsAotRuntimeBuild && Activator.CreateInstance(Type, null) is TransformBase dynamicTransform)
            {
                node.SetTransform(dynamicTransform);
                return true;
            }

            RuntimeSceneNodeServices.Current.LogWarning(
                XRRuntimeEnvironment.IsAotRuntimeBuild
                    ? $"Could not create transform of type {Type.Name} required by component {comp.GetType().Name}; register it with {nameof(TransformFactoryRegistry)} for published AOT builds."
                    : $"Could not create transform of type {Type.Name} required by component {comp.GetType().Name}; likely missing public parameterless constructor.");
            return false;
        }
    }
}
