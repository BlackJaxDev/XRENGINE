using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Fbx;

namespace XREngine;

/// <summary>
/// Resolves the serialized FBX mesh bind for Assimp skin vertices.
/// Assimp node world transforms can include parent offsets that the FBX skin bind excludes.
/// </summary>
internal sealed class AssimpFbxSkinBindPoseResolver
{
    private readonly Dictionary<(string geometry, string owner), Entry> _entries = new();

    private readonly record struct Entry(Matrix4x4 SourceBind, string? Error);

    public AssimpFbxSkinBindPoseResolver(
        string sourceFilePath,
        FbxPivotImportPolicy pivotPolicy,
        bool makeLeftHanded)
    {
        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(sourceFilePath);
        FbxSceneSemanticsPolicy policy = new(pivotPolicy, FbxModelHierarchyPolicy.PreserveAuthoredNodes);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural, policy);
        FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
        IReadOnlyList<FbxIntermediateNode> nodes = semantic.IntermediateScene.Nodes;

        foreach (FbxIntermediateMesh mesh in semantic.IntermediateScene.Meshes)
        {
            if (!deformers.TryGetSkinBinding(mesh.ObjectId, out FbxSkinBinding skin))
                continue;

            string? error = TryGetConsistentBind(skin, out Matrix4x4 sourceBind)
                ? null
                : "the weighted FBX clusters have missing or inconsistent bind matrices";
            foreach (long ownerId in mesh.ModelObjectIds)
            {
                FbxIntermediateNode? owner = null;
                for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                {
                    if (nodes[nodeIndex].ObjectId != ownerId)
                        continue;
                    owner = nodes[nodeIndex];
                    break;
                }
                if (owner is null)
                    continue;

                (string geometry, string owner) key = (mesh.Name, owner.Name);
                string? ownerError = IsNear(owner.GeometryTransform, Matrix4x4.Identity)
                    ? error
                    : "the FBX skin uses a geometric transform that needs a separate vertex conversion";
                Matrix4x4 importedSourceBind = sourceBind;
                if (makeLeftHanded)
                {
                    Matrix4x4 reflection = Matrix4x4.CreateScale(1.0f, 1.0f, -1.0f);
                    importedSourceBind = reflection * sourceBind * reflection;
                }
                Entry entry = new(importedSourceBind, ownerError);
                if (_entries.ContainsKey(key))
                    _entries[key] = new Entry(default, "the geometry and owner names are ambiguous");
                else
                    _entries.Add(key, entry);
            }
        }
    }

    public bool TryResolve(
        string geometryName,
        string ownerName,
        Matrix4x4 originalImportedRootWorld,
        Matrix4x4 inverseRuntimeRootWorld,
        out Matrix4x4 importedBind,
        out string reason)
    {
        importedBind = default;
        if (!_entries.TryGetValue((geometryName, ownerName), out Entry entry))
        {
            reason = "no matching serialized FBX skin bind was found";
            return false;
        }
        if (entry.Error is not null)
        {
            reason = entry.Error;
            return false;
        }
        importedBind = entry.SourceBind * originalImportedRootWorld * inverseRuntimeRootWorld;
        if (!IsFiniteAffine(importedBind))
        {
            reason = "the converted FBX skin bind is not a finite affine matrix";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetConsistentBind(FbxSkinBinding skin, out Matrix4x4 bind)
    {
        bind = default;
        bool found = false;
        for (int clusterIndex = 0; clusterIndex < skin.Clusters.Count; clusterIndex++)
        {
            FbxClusterBinding cluster = skin.Clusters[clusterIndex];
            if (cluster.ControlPointWeights.Count == 0)
                continue;
            if (!cluster.HasTransformMatrix || !cluster.HasTransformLinkMatrix)
                return false;

            Matrix4x4 candidate = cluster.TransformMatrix * cluster.TransformLinkMatrix;
            if (!IsFiniteAffine(candidate))
                return false;
            if (found && !IsNear(bind, candidate))
                return false;
            if (found)
                continue;
            bind = candidate;
            found = true;
        }
        return found;
    }

    private static bool IsNear(Matrix4x4 left, Matrix4x4 right)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                float leftValue = left[row, column];
                float rightValue = right[row, column];
                if (!float.IsFinite(leftValue) || !float.IsFinite(rightValue))
                    return false;
                float tolerance = 0.000001f + 0.00001f * MathF.Max(MathF.Abs(leftValue), MathF.Abs(rightValue));
                if (MathF.Abs(leftValue - rightValue) > tolerance)
                    return false;
            }
        return true;
    }

    private static bool IsFiniteAffine(Matrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (!float.IsFinite(matrix[row, column]))
                    return false;
        return MathF.Abs(matrix.M14) < 0.001f
            && MathF.Abs(matrix.M24) < 0.001f
            && MathF.Abs(matrix.M34) < 0.001f
            && MathF.Abs(matrix.M44 - 1.0f) < 0.001f;
    }
}
