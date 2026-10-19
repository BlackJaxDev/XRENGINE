using System.Numerics;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Captures the source flat-light direction for one material and draw.</summary>
public static class SourceMultilayerFlatDirection
{
    /// <summary>Returns true when the material uses a source flat shadow mask.</summary>
    public static bool IsRequired(XRMaterial? material)
        => material is not null &&
            material.Parameter<ShaderInt>("_LightingMode") is { Value: 1 } &&
            material.Parameter<ShaderInt>("_SourceLightMode") is { Value: 1 } &&
            material.IsUberFeatureEnabled("advanced-stylized-lighting", false) &&
            material.Parameter<ShaderVector4>("_SourceMultilayerOptions") is
            { Value: var options } && options.Y > 0.5f;

    /// <summary>Returns true when a retained flat direction can be used.</summary>
    public static bool IsValid(in Vector4 direction)
        => direction.W > 0.5f &&
            float.IsFinite(direction.X) && float.IsFinite(direction.Y) &&
            float.IsFinite(direction.Z) &&
            MathF.Max(MathF.Abs(direction.X),
                MathF.Max(MathF.Abs(direction.Y), MathF.Abs(direction.Z))) > 0.0f;

    /// <summary>Gets a unit world direction when a source flat mask is active.</summary>
    public static bool TryCapture(XRMaterial? material, in Matrix4x4 sourceBasis,
        out Vector4 direction)
    {
        direction = Vector4.Zero;
        if (material is null || !IsRequired(material))
            return true;
        if (material.Parameter<ShaderVector4>("_SourceMultilayerFlatAxis") is not
                { Value: var axis })
            return false;
        Vector3 worldAxis = Vector3.TransformNormal(new Vector3(axis.X, axis.Y, axis.Z),
            sourceBasis);
        if (!float.IsFinite(worldAxis.X) || !float.IsFinite(worldAxis.Y) ||
            !float.IsFinite(worldAxis.Z))
            return false;
        float scale = MathF.Max(MathF.Abs(worldAxis.X),
            MathF.Max(MathF.Abs(worldAxis.Y), MathF.Abs(worldAxis.Z)));
        if (scale == 0.0f)
            return false;
        Vector3 unit = Vector3.Normalize(worldAxis / scale);
        direction = new Vector4(unit, 1.0f);
        return true;
    }

    /// <summary>Encodes the retained unit direction for a native draw row.</summary>
    public static void Pack(in Vector4 direction, out uint oct, out uint valid)
    {
        valid = direction.W > 0.5f ? 1u : 0u;
        oct = valid != 0u
            ? AdvancedPackedVertexCodec.EncodeOct(new Vector3(direction.X,
                direction.Y, direction.Z))
            : 0u;
    }
}
