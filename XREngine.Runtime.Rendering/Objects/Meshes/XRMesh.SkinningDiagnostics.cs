using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Reconstructs the bind-pose palette and weighted vertex positions on the CPU.
    /// The audit is deliberately allocation-tolerant because it is only called by
    /// editor diagnostics and tests.
    /// </summary>
    internal SkinningBindPoseAuditResult CalculateBindPoseAudit()
    {
        var paletteInverseBinds = new Dictionary<TransformBase, Matrix4x4>(
            UtilizedBones.Length,
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var paletteIndices = new Dictionary<TransformBase, int>(
            UtilizedBones.Length,
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        Matrix4x4 rootBindMatrix = BindRootMatrix ?? Matrix4x4.Identity;
        int nonFiniteMatrixCount = IsFinite(rootBindMatrix) ? 0 : 1;
        float maximumBoneIdentityError = 0.0f;
        string? maximumBoneIdentityErrorBoneName = null;

        for (int boneIndex = 0; boneIndex < UtilizedBones.Length; ++boneIndex)
        {
            (TransformBase bone, Matrix4x4 inverseBind) = UtilizedBones[boneIndex];
            if (!paletteInverseBinds.TryAdd(bone, inverseBind))
                continue;
            paletteIndices.Add(bone, boneIndex);

            Matrix4x4 bindPaletteMatrix = rootBindMatrix * inverseBind * bone.BindMatrix;
            if (!IsFinite(inverseBind) || !IsFinite(bone.BindMatrix) || !IsFinite(bindPaletteMatrix))
            {
                ++nonFiniteMatrixCount;
                continue;
            }

            float identityError = MaximumElementDifference(bindPaletteMatrix, Matrix4x4.Identity);
            if (identityError <= maximumBoneIdentityError)
                continue;

            maximumBoneIdentityError = identityError;
            maximumBoneIdentityErrorBoneName = bone.SceneNode?.Name ?? bone.Name;
        }

        int weightedVertexCount = 0;
        int unweightedVertexCount = 0;
        int influenceCount = 0;
        int missingPaletteBoneCount = 0;
        int invalidInfluenceCount = 0;
        int nonFiniteVertexCount = 0;
        int maximumInfluenceCount = 0;
        float minimumWeightSum = float.PositiveInfinity;
        float maximumWeightSum = float.NegativeInfinity;
        float maximumWeightSumError = 0.0f;
        int maximumWeightSumErrorVertexIndex = -1;
        float maximumVertexBindDisplacement = 0.0f;
        int maximumVertexBindDisplacementIndex = -1;
        float maximumInfluenceInverseBindDifference = 0.0f;
        int maximumInfluenceInverseBindDifferenceVertexIndex = -1;
        Vector3 sourceBoundsMinimum = new(float.PositiveInfinity);
        Vector3 sourceBoundsMaximum = new(float.NegativeInfinity);
        Vector3 bindBoundsMinimum = new(float.PositiveInfinity);
        Vector3 bindBoundsMaximum = new(float.NegativeInfinity);
        bool usedPackedInfluenceBuffers = TryReadPackedSkinningData(
            out byte[] packedCoreIndices,
            out byte[] packedCoreWeights,
            out byte[] packedSpillHeaders,
            out byte[] packedSpillEntries);
        int influenceCapacity = Math.Max(
            UtilizedBones.Length,
            4 + MaxSpillInfluenceCount);
        int[] decodedBoneIndices = new int[Math.Max(1, influenceCapacity)];
        float[] decodedWeights = new float[decodedBoneIndices.Length];

        for (int vertexIndex = 0; vertexIndex < Vertices.Length; ++vertexIndex)
        {
            Vertex vertex = Vertices[vertexIndex];
            Vector3 sourcePosition = vertex.Position;
            if (!IsFinite(sourcePosition))
            {
                ++nonFiniteVertexCount;
                continue;
            }

            sourceBoundsMinimum = Vector3.Min(sourceBoundsMinimum, sourcePosition);
            sourceBoundsMaximum = Vector3.Max(sourceBoundsMaximum, sourcePosition);

            int decodedInfluenceCount = 0;
            bool bindPositionIsValid = true;
            if (usedPackedInfluenceBuffers)
            {
                int coreBase = vertexIndex * 4;
                for (int coreIndex = 0; coreIndex < 4; ++coreIndex)
                {
                    byte packedWeight = packedCoreWeights[coreBase + coreIndex];
                    uint packedIndex = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x8
                        ? packedCoreIndices[coreBase + coreIndex]
                        : BitConverter.ToUInt16(
                            packedCoreIndices,
                            (coreBase + coreIndex) * sizeof(ushort));
                    if (packedIndex != 0u || packedWeight != 0)
                        ++influenceCount;

                    if (TryDecodePackedInfluence(
                        packedIndex,
                        packedWeight,
                        UtilizedBones.Length,
                        out int boneIndex,
                        out float weight,
                        out bool invalid))
                    {
                        decodedBoneIndices[decodedInfluenceCount] = boneIndex;
                        decodedWeights[decodedInfluenceCount] = weight;
                        ++decodedInfluenceCount;
                    }
                    else if (invalid)
                    {
                        ++invalidInfluenceCount;
                        bindPositionIsValid = false;
                    }
                }

                if (HasSpillInfluences)
                {
                    uint header = BitConverter.ToUInt32(
                        packedSpillHeaders,
                        vertexIndex * sizeof(uint));
                    int spillOffset = checked((int)(header & 0x00FF_FFFFu));
                    int spillCount = checked((int)(header >> 24));
                    if (spillCount > MaxSpillInfluenceCount ||
                        spillOffset + spillCount > packedSpillEntries.Length / sizeof(uint))
                    {
                        ++invalidInfluenceCount;
                        bindPositionIsValid = false;
                    }
                    else
                    {
                        for (int spillIndex = 0; spillIndex < spillCount; ++spillIndex)
                        {
                            uint packedEntry = BitConverter.ToUInt32(
                                packedSpillEntries,
                                (spillOffset + spillIndex) * sizeof(uint));
                            uint packedIndex = packedEntry & 0xFFFFu;
                            byte packedWeight = (byte)((packedEntry >> 16) & 0xFFu);
                            if (packedIndex != 0u || packedWeight != 0)
                                ++influenceCount;

                            if (TryDecodePackedInfluence(
                                packedIndex,
                                packedWeight,
                                UtilizedBones.Length,
                                out int boneIndex,
                                out float weight,
                                out bool invalid))
                            {
                                decodedBoneIndices[decodedInfluenceCount] = boneIndex;
                                decodedWeights[decodedInfluenceCount] = weight;
                                ++decodedInfluenceCount;
                            }
                            else if (invalid)
                            {
                                ++invalidInfluenceCount;
                                bindPositionIsValid = false;
                            }
                        }
                    }
                }
            }
            else
            {
                Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights = vertex.Weights;
                if (weights is not null)
                {
                    foreach ((TransformBase bone, (float weight, Matrix4x4 inverseBind) influence) in weights)
                    {
                        ++influenceCount;
                        float weight = influence.weight;
                        if (!float.IsFinite(weight) || weight < 0.0f)
                        {
                            ++invalidInfluenceCount;
                            bindPositionIsValid = false;
                            continue;
                        }

                        TransformBase runtimeBone = RuntimeBoneReferenceRemap is not null &&
                            RuntimeBoneReferenceRemap.TryGetValue(bone, out TransformBase? remappedBone)
                                ? remappedBone
                                : bone;
                        if (!paletteIndices.TryGetValue(runtimeBone, out int boneIndex))
                        {
                            ++missingPaletteBoneCount;
                            bindPositionIsValid = false;
                            continue;
                        }

                        float inverseBindDifference = MaximumElementDifference(
                            influence.inverseBind,
                            paletteInverseBinds[runtimeBone]);
                        if (inverseBindDifference > maximumInfluenceInverseBindDifference)
                        {
                            maximumInfluenceInverseBindDifference = inverseBindDifference;
                            maximumInfluenceInverseBindDifferenceVertexIndex = vertexIndex;
                        }

                        decodedBoneIndices[decodedInfluenceCount] = boneIndex;
                        decodedWeights[decodedInfluenceCount] = weight;
                        ++decodedInfluenceCount;
                    }
                }
            }

            if (decodedInfluenceCount == 0)
            {
                ++unweightedVertexCount;
                bindBoundsMinimum = Vector3.Min(bindBoundsMinimum, sourcePosition);
                bindBoundsMaximum = Vector3.Max(bindBoundsMaximum, sourcePosition);
                continue;
            }

            ++weightedVertexCount;
            maximumInfluenceCount = Math.Max(maximumInfluenceCount, decodedInfluenceCount);
            Vector3 bindPosition = Vector3.Zero;
            float weightSum = 0.0f;
            for (int influenceIndex = 0; influenceIndex < decodedInfluenceCount; ++influenceIndex)
            {
                int boneIndex = decodedBoneIndices[influenceIndex];
                float weight = decodedWeights[influenceIndex];
                weightSum += weight;
                (TransformBase bone, Matrix4x4 inverseBind) = UtilizedBones[boneIndex];
                Matrix4x4 bindSkinMatrix = rootBindMatrix * inverseBind * bone.BindMatrix;
                if (!IsFinite(inverseBind) || !IsFinite(bindSkinMatrix))
                {
                    ++nonFiniteMatrixCount;
                    bindPositionIsValid = false;
                    continue;
                }

                Vector3 influencedPosition = Vector3.Transform(sourcePosition, bindSkinMatrix);
                if (!IsFinite(influencedPosition))
                {
                    ++nonFiniteVertexCount;
                    bindPositionIsValid = false;
                    continue;
                }
                bindPosition += influencedPosition * weight;
            }

            minimumWeightSum = MathF.Min(minimumWeightSum, weightSum);
            maximumWeightSum = MathF.Max(maximumWeightSum, weightSum);
            float weightSumError = MathF.Abs(1.0f - weightSum);
            if (weightSumError > maximumWeightSumError)
            {
                maximumWeightSumError = weightSumError;
                maximumWeightSumErrorVertexIndex = vertexIndex;
            }

            if (!bindPositionIsValid || !IsFinite(bindPosition))
                continue;

            bindBoundsMinimum = Vector3.Min(bindBoundsMinimum, bindPosition);
            bindBoundsMaximum = Vector3.Max(bindBoundsMaximum, bindPosition);
            float bindDisplacement = Vector3.Distance(sourcePosition, bindPosition);
            if (bindDisplacement <= maximumVertexBindDisplacement)
                continue;

            maximumVertexBindDisplacement = bindDisplacement;
            maximumVertexBindDisplacementIndex = vertexIndex;
        }

        if (weightedVertexCount == 0)
        {
            minimumWeightSum = 0.0f;
            maximumWeightSum = 0.0f;
        }

        if (Vertices.Length == 0)
        {
            sourceBoundsMinimum = Vector3.Zero;
            sourceBoundsMaximum = Vector3.Zero;
            bindBoundsMinimum = Vector3.Zero;
            bindBoundsMaximum = Vector3.Zero;
        }

        return new SkinningBindPoseAuditResult
        {
            VertexCount = Vertices.Length,
            UsedPackedInfluenceBuffers = usedPackedInfluenceBuffers,
            WeightedVertexCount = weightedVertexCount,
            UnweightedVertexCount = unweightedVertexCount,
            InfluenceCount = influenceCount,
            MissingPaletteBoneCount = missingPaletteBoneCount,
            InvalidInfluenceCount = invalidInfluenceCount,
            NonFiniteVertexCount = nonFiniteVertexCount,
            NonFiniteMatrixCount = nonFiniteMatrixCount,
            MaximumInfluenceCount = maximumInfluenceCount,
            MinimumWeightSum = minimumWeightSum,
            MaximumWeightSum = maximumWeightSum,
            MaximumWeightSumError = maximumWeightSumError,
            MaximumWeightSumErrorVertexIndex = maximumWeightSumErrorVertexIndex,
            MaximumBoneIdentityError = maximumBoneIdentityError,
            MaximumBoneIdentityErrorBoneName = maximumBoneIdentityErrorBoneName,
            MaximumVertexBindDisplacement = maximumVertexBindDisplacement,
            MaximumVertexBindDisplacementIndex = maximumVertexBindDisplacementIndex,
            MaximumInfluenceInverseBindDifference = maximumInfluenceInverseBindDifference,
            MaximumInfluenceInverseBindDifferenceVertexIndex = maximumInfluenceInverseBindDifferenceVertexIndex,
            SourceBoundsMinimum = sourceBoundsMinimum,
            SourceBoundsMaximum = sourceBoundsMaximum,
            BindBoundsMinimum = bindBoundsMinimum,
            BindBoundsMaximum = bindBoundsMaximum,
        };
    }

    internal bool TryReadPackedSkinningData(
        out byte[] coreIndices,
        out byte[] coreWeights,
        out byte[] spillHeaders,
        out byte[] spillEntries)
    {
        coreIndices = [];
        coreWeights = [];
        spillHeaders = [];
        spillEntries = [];
        if (!HasCanonicalComputeSkinningBuffers() ||
            BoneInfluenceCoreIndices?.ClientSideSource is null ||
            BoneInfluenceCoreWeights?.ClientSideSource is null)
        {
            return false;
        }

        uint vertexCount = checked((uint)Vertices.Length);
        uint coreIndexBytesPerVertex = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x8
            ? 4u
            : 4u * sizeof(ushort);
        coreIndices = BoneInfluenceCoreIndices.GetRawBytes(
            checked(vertexCount * coreIndexBytesPerVertex));
        coreWeights = BoneInfluenceCoreWeights.GetRawBytes(checked(vertexCount * 4u));
        if (!HasSpillInfluences)
            return true;

        if (BoneInfluenceSpillHeaders?.ClientSideSource is null ||
            BoneInfluenceSpillEntries?.ClientSideSource is null)
        {
            return false;
        }

        spillHeaders = BoneInfluenceSpillHeaders.GetRawBytes(
            checked(vertexCount * sizeof(uint)));
        spillEntries = BoneInfluenceSpillEntries.GetRawBytes(BoneInfluenceSpillEntries.Length);
        return true;
    }

    internal static bool TryDecodePackedInfluence(
        uint boneIndexPlusOne,
        byte quantizedWeight,
        int paletteBoneCount,
        out int boneIndex,
        out float weight,
        out bool invalid)
    {
        boneIndex = -1;
        weight = 0.0f;
        invalid = false;
        if (boneIndexPlusOne == 0u && quantizedWeight == 0)
            return false;

        if (boneIndexPlusOne == 0u || quantizedWeight == 0)
        {
            invalid = true;
            return false;
        }

        uint decodedBoneIndex = boneIndexPlusOne - 1u;
        if (decodedBoneIndex >= paletteBoneCount)
        {
            invalid = true;
            return false;
        }

        boneIndex = checked((int)decodedBoneIndex);
        weight = quantizedWeight / (float)byte.MaxValue;
        return true;
    }

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z);

    private static bool IsFinite(in Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
           float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
           float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
           float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
           float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static float MaximumElementDifference(in Matrix4x4 left, in Matrix4x4 right)
    {
        float maximum = 0.0f;
        maximum = MathF.Max(maximum, MathF.Abs(left.M11 - right.M11));
        maximum = MathF.Max(maximum, MathF.Abs(left.M12 - right.M12));
        maximum = MathF.Max(maximum, MathF.Abs(left.M13 - right.M13));
        maximum = MathF.Max(maximum, MathF.Abs(left.M14 - right.M14));
        maximum = MathF.Max(maximum, MathF.Abs(left.M21 - right.M21));
        maximum = MathF.Max(maximum, MathF.Abs(left.M22 - right.M22));
        maximum = MathF.Max(maximum, MathF.Abs(left.M23 - right.M23));
        maximum = MathF.Max(maximum, MathF.Abs(left.M24 - right.M24));
        maximum = MathF.Max(maximum, MathF.Abs(left.M31 - right.M31));
        maximum = MathF.Max(maximum, MathF.Abs(left.M32 - right.M32));
        maximum = MathF.Max(maximum, MathF.Abs(left.M33 - right.M33));
        maximum = MathF.Max(maximum, MathF.Abs(left.M34 - right.M34));
        maximum = MathF.Max(maximum, MathF.Abs(left.M41 - right.M41));
        maximum = MathF.Max(maximum, MathF.Abs(left.M42 - right.M42));
        maximum = MathF.Max(maximum, MathF.Abs(left.M43 - right.M43));
        maximum = MathF.Max(maximum, MathF.Abs(left.M44 - right.M44));
        return maximum;
    }
}
