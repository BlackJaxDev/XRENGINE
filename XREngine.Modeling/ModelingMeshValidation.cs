using System.Numerics;

namespace XREngine.Modeling;

public enum ModelingValidationSeverity
{
    Error = 0,
    Warning
}

public sealed record ModelingMeshValidationIssue(
    ModelingValidationSeverity Severity,
    string Code,
    string Message,
    int? ElementIndex = null);

public sealed class ModelingMeshValidationReport
{
    private readonly List<ModelingMeshValidationIssue> _issues = [];

    public IReadOnlyList<ModelingMeshValidationIssue> Issues => _issues;
    public bool HasErrors => _issues.Any(x => x.Severity == ModelingValidationSeverity.Error);
    public bool IsValid => !HasErrors;

    public void Add(ModelingMeshValidationIssue issue)
        => _issues.Add(issue);
}

public static class ModelingMeshValidation
{
    public static ModelingMeshValidationReport Validate(ModelingMeshDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ModelingMeshValidationReport report = new();

        int vertexCount = document.Positions.Count;
        int indexCount = document.TriangleIndices.Count;

        if (vertexCount == 0)
        {
            report.Add(new ModelingMeshValidationIssue(
                ModelingValidationSeverity.Error,
                "empty_positions",
                "The mesh document has no positions."));
        }

        if (indexCount == 0)
        {
            report.Add(new ModelingMeshValidationIssue(
                ModelingValidationSeverity.Error,
                "empty_triangles",
                "The mesh document has no triangle indices."));
        }

        if ((indexCount % 3) != 0)
        {
            report.Add(new ModelingMeshValidationIssue(
                ModelingValidationSeverity.Error,
                "triangle_index_count_not_multiple_of_three",
                "Triangle index count must be divisible by 3."));
        }

        for (int i = 0; i < indexCount; i++)
        {
            int index = document.TriangleIndices[i];
            if (index < 0 || index >= vertexCount)
            {
                report.Add(new ModelingMeshValidationIssue(
                    ModelingValidationSeverity.Error,
                    "triangle_index_out_of_range",
                    $"Triangle index {index} is out of range for vertex count {vertexCount}.",
                    i));
            }
        }

        for (int i = 0; i + 2 < indexCount; i += 3)
        {
            int a = document.TriangleIndices[i];
            int b = document.TriangleIndices[i + 1];
            int c = document.TriangleIndices[i + 2];

            if (a == b || b == c || c == a)
            {
                report.Add(new ModelingMeshValidationIssue(
                    ModelingValidationSeverity.Error,
                    "degenerate_triangle_duplicate_indices",
                    $"Triangle {i / 3} is degenerate because it reuses at least one vertex index.",
                    i / 3));
                continue;
            }

            if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount)
                continue;

            Vector3 pa = document.Positions[a];
            Vector3 pb = document.Positions[b];
            Vector3 pc = document.Positions[c];
            Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
            if (cross.LengthSquared() <= float.Epsilon)
            {
                report.Add(new ModelingMeshValidationIssue(
                    ModelingValidationSeverity.Error,
                    "degenerate_triangle_zero_area",
                    $"Triangle {i / 3} is degenerate because its area is zero.",
                    i / 3));
            }
        }

        ValidateChannelCardinality(report, "normals", document.Normals?.Count, vertexCount);
        ValidateChannelCardinality(report, "tangents", document.Tangents?.Count, vertexCount);

        if (document.TexCoordChannels is not null)
        {
            for (int i = 0; i < document.TexCoordChannels.Count; i++)
            {
                List<Vector2>? channel = document.TexCoordChannels[i];
                if (channel is null)
                {
                    report.Add(new ModelingMeshValidationIssue(
                        ModelingValidationSeverity.Error,
                        "texcoord_channel_null",
                        $"Texcoord channel {i} is null.",
                        i));
                    continue;
                }

                ValidateChannelCardinality(report, $"texcoord_channel_{i}", channel.Count, vertexCount, i);
            }
        }

        if (document.ColorChannels is not null)
        {
            for (int i = 0; i < document.ColorChannels.Count; i++)
            {
                List<Vector4>? channel = document.ColorChannels[i];
                if (channel is null)
                {
                    report.Add(new ModelingMeshValidationIssue(
                        ModelingValidationSeverity.Error,
                        "color_channel_null",
                        $"Color channel {i} is null.",
                        i));
                    continue;
                }

                ValidateChannelCardinality(report, $"color_channel_{i}", channel.Count, vertexCount, i);
            }
        }

        if (document.SkinBones is not null)
        {
            for (int i = 0; i < document.SkinBones.Count; i++)
            {
                ModelingSkinBone? bone = document.SkinBones[i];
                if (bone is null)
                {
                    report.Add(new ModelingMeshValidationIssue(
                        ModelingValidationSeverity.Error,
                        "skin_bone_null",
                        $"Skin bone {i} is null.",
                        i));
                }
            }
        }

        if (document.SkinWeights is not null)
        {
            ValidateChannelCardinality(report, "skin_weights", document.SkinWeights.Count, vertexCount);

            int skinBoneCount = document.SkinBones?.Count ?? 0;
            bool hasAnyWeights = false;

            for (int vertexIndex = 0; vertexIndex < document.SkinWeights.Count; vertexIndex++)
            {
                List<ModelingSkinWeight>? weights = document.SkinWeights[vertexIndex];
                if (weights is null)
                {
                    report.Add(new ModelingMeshValidationIssue(
                        ModelingValidationSeverity.Error,
                        "skin_weight_set_null",
                        $"Skin weight set for vertex {vertexIndex} is null.",
                        vertexIndex));
                    continue;
                }

                if (weights.Count > 0)
                    hasAnyWeights = true;

                for (int weightIndex = 0; weightIndex < weights.Count; weightIndex++)
                {
                    ModelingSkinWeight weight = weights[weightIndex];
                    if (weight.BoneIndex < 0 || weight.BoneIndex >= skinBoneCount)
                    {
                        report.Add(new ModelingMeshValidationIssue(
                            ModelingValidationSeverity.Error,
                            "skin_weight_bone_index_out_of_range",
                            $"Skin weight bone index {weight.BoneIndex} is out of range for skin bone count {skinBoneCount}.",
                            vertexIndex));
                    }

                    if (!float.IsFinite(weight.Weight) || weight.Weight < 0f)
                    {
                        report.Add(new ModelingMeshValidationIssue(
                            ModelingValidationSeverity.Error,
                            "skin_weight_invalid",
                            $"Skin weight value {weight.Weight} for vertex {vertexIndex} is invalid.",
                            vertexIndex));
                    }
                }
            }

            if (hasAnyWeights && skinBoneCount == 0)
            {
                report.Add(new ModelingMeshValidationIssue(
                    ModelingValidationSeverity.Error,
                    "skin_bones_missing",
                    "Skin weights are present but no skin bones were provided."));
            }
        }

        if (document.BlendshapeChannels is not null)
        {
            HashSet<string> blendshapeNames = new(StringComparer.Ordinal);
            for (int i = 0; i < document.BlendshapeChannels.Count; i++)
            {
                ModelingBlendshapeChannel? channel = document.BlendshapeChannels[i];
                if (channel is null)
                {
                    report.Add(new ModelingMeshValidationIssue(
                        ModelingValidationSeverity.Error,
                        "blendshape_channel_null",
                        $"Blendshape channel {i} is null.",
                        i));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(channel.Name))
                {
                    report.Add(new ModelingMeshValidationIssue(
                        ModelingValidationSeverity.Error,
                        "blendshape_name_empty",
                        $"Blendshape channel {i} has an empty name.",
                        i));
                }
                else if (!blendshapeNames.Add(channel.Name))
                {
                    report.Add(new ModelingMeshValidationIssue(
                        ModelingValidationSeverity.Error,
                        "blendshape_name_duplicate",
                        $"Blendshape channel name '{channel.Name}' is duplicated.",
                        i));
                }

                ValidateChannelCardinality(report, $"blendshape_position_deltas_{i}", channel.PositionDeltas?.Count, vertexCount, i);
                ValidateChannelCardinality(report, $"blendshape_normal_deltas_{i}", channel.NormalDeltas?.Count, vertexCount, i);
                ValidateChannelCardinality(report, $"blendshape_tangent_deltas_{i}", channel.TangentDeltas?.Count, vertexCount, i);
            }
        }

        return report;
    }

    private static void ValidateChannelCardinality(
        ModelingMeshValidationReport report,
        string channelName,
        int? channelCount,
        int vertexCount,
        int? elementIndex = null)
    {
        if (!channelCount.HasValue)
            return;

        if (channelCount.Value != vertexCount)
        {
            report.Add(new ModelingMeshValidationIssue(
                ModelingValidationSeverity.Error,
                "channel_cardinality_mismatch",
                $"Channel '{channelName}' has {channelCount.Value} entries but expected {vertexCount}.",
                elementIndex));
        }
    }
}
