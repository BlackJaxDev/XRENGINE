using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

/// <summary>Requests transform replacement through the normal component attribute path.</summary>
[RequiresTransform(typeof(RectTransform))]
public sealed class RequiredRectTransformProbe : XRComponent
{
    public RequiredRectTransformProbe() { }
}
