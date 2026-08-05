using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Scene.Mesh;

public partial class RenderableMesh
{
    /// <summary>
    /// Re-publishes the authored mesh bounds with conservative vertex-effect
    /// displacement padding. Material-animation bindings call this immediately
    /// after changing a displacement parameter so an off-screen mesh cannot
    /// retain a stale, smaller broad-phase volume.
    /// </summary>
    internal void RefreshVertexEffectCullingBounds()
    {
        XRMaterial? material = CurrentLODRenderer?.Material;
        if (IsSkinned)
        {
            RefreshSkinnedCullingBoundsForSceneCulling();
            return;
        }

        RenderInfo.LocalCullingVolume = ExpandVertexEffectLocalBounds(_bindPoseBounds, material);
        PublishRenderCommandCullingVolume();
    }

    private AABB ExpandVertexEffectLocalBounds(in AABB bounds, XRMaterial? material)
    {
        if (material?.Parameter<ShaderFloat>("_VertexEffectsEnabled")?.Value is not > 0.0001f)
            return ExpandOutlineBounds(bounds, material);

        Vector3 halfExtents = bounds.HalfExtents;
        Vector3 padding = GetAbsolute(material, "_VertexConservativeBounds");
        float uniformPadding = GetAbsoluteW(material, "_VertexConservativeBounds");
        padding += new Vector3(uniformPadding);

        padding += GetAbsolute(material, "_VertexManipulationLocalTranslation");
        padding += new Vector3(GetAbsoluteFloat(material, "_VertexManipulationHeight"));
        padding.X += GetAbsoluteX(material, "_VertexGlitch");
        padding += new Vector3(GetAbsoluteW(material, "_VertexWave"));
        padding += new Vector3(GetAbsoluteZ(material, "_VertexEquation"));
        padding += new Vector3(GetAbsoluteW(material, "_VertexDepthBulge"));

        Vector4 vertexColorPosition = GetVector4(material, "_VertexColorPositionOffset");
        padding += Vector3.Abs(new Vector3(vertexColorPosition.X, vertexColorPosition.Y, vertexColorPosition.Z)) *
            MathF.Abs(vertexColorPosition.W);

        Vector3 scale = Vector3.Abs(GetVector3(material, "_VertexManipulationLocalScale", Vector3.One));
        padding += halfExtents * Vector3.Max(scale - Vector3.One, Vector3.Zero);

        if (GetAbsoluteFloat(material, "_VertexRoundingEnabled") > 0.5f)
            padding += new Vector3(GetAbsoluteFloat(material, "_VertexRoundingDivision") * 0.5f);

        if (GetAbsoluteFloat(material, "_VertexBarrelMode") > 0.5f)
        {
            float barrelScale = GetAbsoluteFloat(material, "_VertexBarrelWidth") *
                MathF.Max(1.0f, GetAbsoluteFloat(material, "_VertexBarrelAlpha"));
            padding.X += halfExtents.X * barrelScale;
            padding.Z += halfExtents.Z * barrelScale;
        }

        bool rotates =
            GetAbsolute(material, "_VertexManipulationLocalRotation") != Vector3.Zero ||
            GetAbsolute(material, "_VertexManipulationLocalRotationSpeed") != Vector3.Zero ||
            GetAbsoluteFloat(material, "_VertexLookAtWeight") > 0.0001f;
        if (rotates)
        {
            float radius = (halfExtents + padding).Length();
            padding = Vector3.Max(padding, new Vector3(radius) - halfExtents);
        }

        AABB expanded = new(bounds.Min - padding, bounds.Max + padding);
        Vector3 worldTranslation = GetAbsolute(material, "_VertexManipulationWorldTranslation");
        if (worldTranslation != Vector3.Zero)
        {
            Matrix4x4 basis = RenderInfo.CullingOffsetMatrix;
            if (Matrix4x4.Invert(basis, out Matrix4x4 inverse))
            {
                Vector3 localWorldTranslation = Vector3.TransformNormal(worldTranslation, inverse);
                Vector3 directionalPadding = Vector3.Abs(localWorldTranslation);
                expanded = new AABB(expanded.Min - directionalPadding, expanded.Max + directionalPadding);
            }
            else
                expanded = new AABB(expanded.Min - worldTranslation, expanded.Max + worldTranslation);
        }

        return ExpandOutlineBounds(expanded, material);
    }

    private AABB ExpandVertexEffectWorldBounds(in AABB bounds, in Matrix4x4 localToWorld)
    {
        AABB expandedLocalBounds = ExpandVertexEffectLocalBounds(_bindPoseBounds, CurrentLODRenderer?.Material);
        Vector3 localPadding = Vector3.Max(
            Vector3.Max(_bindPoseBounds.Min - expandedLocalBounds.Min, Vector3.Zero),
            Vector3.Max(expandedLocalBounds.Max - _bindPoseBounds.Max, Vector3.Zero));
        Vector3 worldPadding = TransformExtents(localPadding, localToWorld);
        return new AABB(bounds.Min - worldPadding, bounds.Max + worldPadding);
    }

    private static AABB ExpandOutlineBounds(in AABB bounds, XRMaterial? material)
    {
        if (material is null || !material.IsUberFeatureEnabled("outline", defaultEnabled: false))
            return bounds;

        float outline = GetAbsoluteFloat(material, "_OutlineWidth") * 0.01f;
        if (outline <= 0.0f)
            return bounds;
        Vector3 padding = new(outline);
        return new AABB(bounds.Min - padding, bounds.Max + padding);
    }

    private static Vector3 TransformExtents(in Vector3 extents, in Matrix4x4 matrix)
        => new(
            MathF.Abs(matrix.M11) * extents.X + MathF.Abs(matrix.M21) * extents.Y + MathF.Abs(matrix.M31) * extents.Z,
            MathF.Abs(matrix.M12) * extents.X + MathF.Abs(matrix.M22) * extents.Y + MathF.Abs(matrix.M32) * extents.Z,
            MathF.Abs(matrix.M13) * extents.X + MathF.Abs(matrix.M23) * extents.Y + MathF.Abs(matrix.M33) * extents.Z);

    private static Vector3 GetAbsolute(XRMaterial? material, string name)
        => Vector3.Abs(GetVector3(material, name, Vector3.Zero));

    private static float GetAbsoluteFloat(XRMaterial? material, string name)
        => MathF.Abs(material?.Parameter<ShaderFloat>(name)?.Value ?? 0.0f);

    private static float GetAbsoluteX(XRMaterial? material, string name)
        => MathF.Abs(GetVector4(material, name).X);

    private static float GetAbsoluteZ(XRMaterial? material, string name)
        => MathF.Abs(GetVector4(material, name).Z);

    private static float GetAbsoluteW(XRMaterial? material, string name)
        => MathF.Abs(GetVector4(material, name).W);

    private static Vector3 GetVector3(XRMaterial? material, string name, in Vector3 fallback)
        => material?.Parameter<ShaderVector3>(name)?.Value ?? fallback;

    private static Vector4 GetVector4(XRMaterial? material, string name)
        => material?.Parameter<ShaderVector4>(name)?.Value ?? Vector4.Zero;
}
