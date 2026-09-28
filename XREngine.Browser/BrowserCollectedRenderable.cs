using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>One scene component's published state, captured before either viewport is traversed.</summary>
internal readonly record struct BrowserCollectedRenderable(
    BrowserMeshData Mesh,
    BrowserResourceHandle MeshHandle,
    BrowserResourceHandle MaterialHandle,
    Matrix4x4 ModelMatrix);
