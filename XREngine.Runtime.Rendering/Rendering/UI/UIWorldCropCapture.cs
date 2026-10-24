using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI;

/// <summary>Immutable canvas-local clip values for one owned world draw.</summary>
internal readonly record struct UIWorldCropCapture(
    BoundingRectangle Region, Vector4 MinMax, Matrix4x4 ModelToCanvas);
