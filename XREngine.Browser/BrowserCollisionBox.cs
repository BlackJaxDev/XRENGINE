using System.Numerics;

namespace XREngine.Browser;

/// <summary>One immutable static axis-aligned collider in the selected sample physics profile.</summary>
public readonly record struct BrowserCollisionBox(Vector3 Minimum, Vector3 Maximum);
