using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Components.Capture.Lights.Types;

/// <summary>Value-owned cube-face projections retained without heap arrays or hash-based equality.</summary>
[InlineArray(PointLightComponent.ShadowFaceCount)]
internal struct BrowserShadowProjectionSnapshot
{
    private Matrix4x4 _element0;
}
