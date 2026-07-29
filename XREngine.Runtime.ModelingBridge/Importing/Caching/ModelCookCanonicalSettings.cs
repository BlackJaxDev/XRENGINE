using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Canonical projection of model-level and per-submesh geometry cook settings.
/// </summary>
public static class ModelCookCanonicalSettings
{
    public static byte[] Serialize(ModelCookSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using ModelCacheCanonicalWriter writer = new();
        writer.WriteString(1, "xrengine.model-cook-settings");
        writer.WriteUInt32(2, settings.PolicyVersion);
        writer.WriteUInt32(3, ModelBinaryCacheVersions.DeterministicOrdering);
        writer.WriteString(4, MeshOptimizerIntegration.MeshOptimizerVersionKey);
        writer.WriteInt32(5, (int)settings.RepairPolicy);
        writer.WriteBytes(10, SerializeMeshlets(MeshletGenerationSettingsSnapshot.From(settings.Meshlets)));
        writer.WriteBytes(11, SerializeLods(MeshLodGenerationSettingsSnapshot.From(settings.Lods)));
        return writer.ToArray();
    }

    public static byte[] Serialize(MeshOptimizerSubMeshSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return SerializeSubMeshPolicy(settings.Meshlets, settings.Lods);
    }

    public static byte[] SerializeSubMeshPolicy(
        MeshletGenerationSettings meshlets,
        MeshLodGenerationSettings lods)
    {
        ArgumentNullException.ThrowIfNull(meshlets);
        ArgumentNullException.ThrowIfNull(lods);

        using ModelCacheCanonicalWriter writer = new();
        writer.WriteString(1, "xrengine.submesh-cook-settings");
        writer.WriteUInt32(2, ModelBinaryCacheVersions.CookPolicy);
        writer.WriteBytes(10, SerializeMeshlets(MeshletGenerationSettingsSnapshot.From(meshlets)));
        writer.WriteBytes(11, SerializeLods(MeshLodGenerationSettingsSnapshot.From(lods)));
        return writer.ToArray();
    }

    internal static byte[] SerializeMeshlets(MeshletGenerationSettingsSnapshot settings)
    {
        using ModelCacheCanonicalWriter writer = new();
        writer.WriteBoolean(1, settings.Enabled);
        writer.WriteInt32(2, (int)settings.BuildMode);
        writer.WriteUInt32(3, settings.MaxVertices);
        writer.WriteUInt32(4, settings.MinTriangles);
        writer.WriteUInt32(5, settings.MaxTriangles);
        writer.WriteSingle(6, settings.ConeWeight);
        writer.WriteSingle(7, settings.SplitFactor);
        writer.WriteSingle(8, settings.FillWeight);
        writer.WriteBoolean(9, settings.OptimizeMeshlets);
        writer.WriteInt32(10, settings.OptimizeLevel);
        writer.WriteBoolean(11, settings.ComputeBounds);
        writer.WriteBoolean(12, settings.EncodeMeshlets);
        writer.WriteBoolean(13, settings.EncodeVertexReferences);
        return writer.ToArray();
    }

    internal static byte[] SerializeLods(MeshLodGenerationSettingsSnapshot settings)
    {
        using ModelCacheCanonicalWriter writer = new();
        writer.WriteBoolean(1, settings.Enabled);
        writer.WriteInt32(2, (int)settings.Mode);
        writer.WriteInt32(3, settings.AdditionalLodCount);
        writer.WriteSingle(4, settings.FirstLodIndexRatio);
        writer.WriteSingle(5, settings.LodRatioScale);
        writer.WriteSingle(6, settings.TargetError);
        writer.WriteSingle(7, settings.FirstLodDistance);
        writer.WriteSingle(8, settings.LodDistanceScale);
        writer.WriteBoolean(9, settings.ReusePreviousLodAsSource);
        writer.WriteUInt32(10, (uint)settings.Options);
        writer.WriteBoolean(11, settings.UseNormals);
        writer.WriteSingle(12, settings.NormalWeight);
        writer.WriteBoolean(13, settings.UseTangents);
        writer.WriteSingle(14, settings.TangentWeight);
        writer.WriteBoolean(15, settings.UseTexCoords);
        writer.WriteSingle(16, settings.TexCoordWeight);
        writer.WriteBoolean(17, settings.UseColors);
        writer.WriteSingle(18, settings.ColorWeight);
        writer.WriteBoolean(19, settings.ProtectAttributeSeams);
        writer.WriteBoolean(20, settings.PrioritizeBorderVertices);
        writer.WriteBoolean(21, settings.LockWeightedVertices);
        return writer.ToArray();
    }
}
