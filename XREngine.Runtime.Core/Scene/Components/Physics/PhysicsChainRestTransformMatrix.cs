using System.Numerics;
using XREngine.Animation;
using XREngine.Data.Transforms;

namespace XREngine.Components;

/// <summary>Builds the built-in transform's local matrix without changing its pose.</summary>
internal static class PhysicsChainRestTransformMatrix
{
    internal static AffineMatrix4x3 Compose(
        Vector3 translation,
        Quaternion rotation,
        Vector3 scale,
        ETransformOrder order)
    {
        if (order == ETransformOrder.TRS)
            return AffineMatrix4x3.CreateTRS(scale, rotation, translation);

        AffineMatrix4x3 t = AffineMatrix4x3.CreateTranslation(translation);
        AffineMatrix4x3 r = AffineMatrix4x3.CreateFromQuaternion(rotation);
        AffineMatrix4x3 s = AffineMatrix4x3.CreateScale(scale);
        return order switch
        {
            ETransformOrder.RST => t * s * r,
            ETransformOrder.STR => r * t * s,
            ETransformOrder.TSR => r * s * t,
            ETransformOrder.SRT => t * r * s,
            ETransformOrder.RTS => s * t * r,
            _ => AffineMatrix4x3.CreateTRS(scale, rotation, translation),
        };
    }
}
