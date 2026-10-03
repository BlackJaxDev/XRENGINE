using System.Numerics;

namespace XREngine.Rendering;

/// <summary>One static draw referencing resources in a browser scene snapshot.</summary>
public readonly record struct BrowserSceneInstance(int MeshIndex, int MaterialIndex, Matrix4x4 ModelMatrix);
