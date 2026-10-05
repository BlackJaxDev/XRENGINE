using System.Numerics;

namespace XREngine.Scene.Transforms;

/// <summary>Matrix storage used only while a transform is detached from a runtime world.</summary>
internal sealed class DetachedTransformMatrices
{
    internal Matrix4x4 Local = Matrix4x4.Identity;
    internal Matrix4x4 World = Matrix4x4.Identity;
    internal Matrix4x4 Render = Matrix4x4.Identity;
}
