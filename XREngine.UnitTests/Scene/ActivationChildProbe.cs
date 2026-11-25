using NUnit.Framework;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

/// <summary>Creates a world-bound child during the owner's activation callback.</summary>
internal sealed class ActivationChildProbe(bool requireTransform) : XRComponent
{
    internal SceneNode? Child { get; private set; }
    internal TransformBase? OriginalTransform { get; private set; }
    internal int ActivationCount { get; private set; }

    protected override void OnComponentActivated()
    {
        ActivationCount++;
        Child = SceneNode.NewChild("Child");
        OriginalTransform = Child.Transform;
        Assert.That(OriginalTransform.World, Is.Not.Null);
        if (requireTransform)
            Child.AddComponent<RequiredRectTransformProbe>();
        else
            Child.SetTransform<RectTransform>();
        base.OnComponentActivated();
    }
}
