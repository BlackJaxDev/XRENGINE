using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>One placed quad and the UTF-16 text element that produced it.</summary>
public readonly record struct UITextLayoutGlyph(Vector4 Transform, Vector4 Uvs, int ClusterStart, int ClusterLength, int Line);
