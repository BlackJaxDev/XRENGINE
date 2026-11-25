using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Frozen operator values supplied by a custom layout transform.</summary>
public readonly record struct UILayoutValuePayload(Vector4 Values, float Extra, int Flags);
