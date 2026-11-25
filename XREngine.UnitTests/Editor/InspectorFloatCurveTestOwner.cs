using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine.UnitTests.Editor;

public sealed class InspectorFloatCurveTestOwner : XRAsset
{
    public PropAnimFloat? Curve { get; set; }
}
