using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XREngine.Components.Physics;

/// <summary>
/// Builds sanitized, backend-neutral triangle input for convex decomposition.
/// Model and render-mesh extraction belongs to a higher-level adapter.
/// </summary>
public static class ConvexHullUtility
{
    public static bool TryCreateInput(
        ReadOnlySpan<Vector3> sourcePositions,
        ReadOnlySpan<int> indices,
        Matrix4x4? transform,
        out ConvexHullInput input)
    {
        input = default;
        if (sourcePositions.Length == 0 || indices.Length < 3)
            return false;

        Vector3[] transformedPositions = new Vector3[sourcePositions.Length];
        if (transform is Matrix4x4 localToTarget)
        {
            for (int i = 0; i < sourcePositions.Length; i++)
                transformedPositions[i] = Vector3.Transform(sourcePositions[i], localToTarget);
        }
        else
            sourcePositions.CopyTo(transformedPositions);

        int[] remap = new int[transformedPositions.Length];
        Array.Fill(remap, -1);

        List<Vector3> positions = new(sourcePositions.Length);
        List<int> sanitizedIndices = new(indices.Length);

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int index0 = indices[i];
            int index1 = indices[i + 1];
            int index2 = indices[i + 2];

            if ((uint)index0 >= (uint)transformedPositions.Length
                || (uint)index1 >= (uint)transformedPositions.Length
                || (uint)index2 >= (uint)transformedPositions.Length)
                continue;

            if (index0 == index1 || index1 == index2 || index0 == index2)
                continue;

            Vector3 p0 = transformedPositions[index0];
            Vector3 p1 = transformedPositions[index1];
            Vector3 p2 = transformedPositions[index2];
            if (!IsFinite(p0) || !IsFinite(p1) || !IsFinite(p2))
                continue;

            Vector3 edge01 = p1 - p0;
            Vector3 edge02 = p2 - p0;
            if (Vector3.Cross(edge01, edge02).LengthSquared() <= 1e-12f)
                continue;

            sanitizedIndices.Add(GetOrAddRemappedIndex(index0, transformedPositions, remap, positions));
            sanitizedIndices.Add(GetOrAddRemappedIndex(index1, transformedPositions, remap, positions));
            sanitizedIndices.Add(GetOrAddRemappedIndex(index2, transformedPositions, remap, positions));
        }

        if (positions.Count < 3 || sanitizedIndices.Count < 3)
            return false;

        input = new ConvexHullInput([.. positions], [.. sanitizedIndices]);
        return true;
    }

    private static int GetOrAddRemappedIndex(int sourceIndex, IReadOnlyList<Vector3> sourcePositions, int[] remap, List<Vector3> remappedPositions)
    {
        int existing = remap[sourceIndex];
        if (existing >= 0)
            return existing;

        int remappedIndex = remappedPositions.Count;
        remappedPositions.Add(sourcePositions[sourceIndex]);
        remap[sourceIndex] = remappedIndex;
        return remappedIndex;
    }

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
