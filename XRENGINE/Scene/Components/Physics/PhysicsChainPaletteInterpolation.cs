using System.Numerics;

namespace XREngine.Components;

/// <summary>Allocation-free affine palette interpolation for CPU consumers.</summary>
public static class PhysicsChainPaletteInterpolation
{
    public static Matrix4x4 Interpolate(in Matrix4x4 previous, in Matrix4x4 current, float alpha)
    {
        float normalizedAlpha = Math.Clamp(alpha, 0.0f, 1.0f);
        if (!Matrix4x4.Decompose(previous, out Vector3 previousScale, out Quaternion previousRotation, out Vector3 previousTranslation)
            || !Matrix4x4.Decompose(current, out Vector3 currentScale, out Quaternion currentRotation, out Vector3 currentTranslation))
            return Matrix4x4.Lerp(previous, current, normalizedAlpha);

        Vector3 scale = Vector3.Lerp(previousScale, currentScale, normalizedAlpha);
        Quaternion rotation = Quaternion.Normalize(Quaternion.Slerp(previousRotation, currentRotation, normalizedAlpha));
        Vector3 translation = Vector3.Lerp(previousTranslation, currentTranslation, normalizedAlpha);
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
    }
}
