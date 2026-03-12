using System.Collections;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Core.Attributes
{
    /// <summary>
    /// Requires that a scene node has certain components for this component to operate correctly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class RequireComponentsAttribute(params Type[] types) : XRComponentAttribute, IEnumerable<Type>
    {
        public Type[] RequiredComponents { get; } = types;

        public override bool VerifyComponentOnAdd(SceneNode node, XRComponent comp)
        {
            foreach (var requiredType in RequiredComponents)
            {
                if (!node.HasComponent(requiredType))
                    node.AddComponent(requiredType);
            }

            return true;
        }

        public IEnumerator<Type> GetEnumerator()
            => ((IEnumerable<Type>)RequiredComponents).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => RequiredComponents.GetEnumerator();
    }
}
