namespace XREngine.Scene.Physics;

/// <summary>Decoded intensity samples used when authoring a native height field.</summary>
public readonly record struct PhysicsHeightFieldImage(uint Width, uint Height, ushort[] Samples);
