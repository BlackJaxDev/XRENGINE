using System;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public void RebuildBlendshapeBuffersFromVertices()
    {
        Buffers.RemoveBuffer(ECommonBufferType.BlendshapeCount.ToString());
        Buffers.RemoveBuffer($"{ECommonBufferType.BlendshapeIndices}Buffer");
        Buffers.RemoveBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer");
        Buffers.RemoveBuffer($"{ECommonBufferType.BlendshapeSparseShapeRanges}Buffer");
        Buffers.RemoveBuffer($"{ECommonBufferType.BlendshapeSparseRecords}Buffer");
        Buffers.RemoveBuffer($"{ECommonBufferType.BlendshapeQuantizedDeltas}Buffer");
        Buffers.RemoveBuffer($"{ECommonBufferType.BlendshapeQuantizationMetadata}Buffer");

        BlendshapeCounts = null;
        BlendshapeIndices = null;
        BlendshapeDeltas = null;
        BlendshapeSparseShapeRanges = null;
        BlendshapeSparseRecords = null;
        BlendshapeQuantizedDeltas = null;
        BlendshapeQuantizationMetadata = null;
        BlendshapeAffectedVertexCount = 0;
        BlendshapeSparseRecordCount = 0;
        BlendshapeDeltaStorageMode = BlendshapeDeltaStorageMode.DensePerVertex;
        BlendshapeDeltaEncoding = BlendshapeDeltaEncoding.Float32;
        BlendshapeShaderVariant = BlendshapeShaderVariant.None;

        if (Vertices is not { Length: > 0 } || !HasBlendshapes)
            return;

        PopulateBlendshapeBuffers(Vertices);
    }

    private unsafe void PopulateBlendshapeBuffers(Vertex[] sourceList)
    {
        using var _ = RuntimeRenderingHostServices.Profiling.StartProfileScope();

        bool intVarType = RuntimeRenderingHostServices.Settings.UseIntegerUniformsInShaders;
        string[] blendshapeNames = BlendshapeNames ?? [];

        BlendshapeCounts = new XRDataBuffer(ECommonBufferType.BlendshapeCount.ToString(), EBufferTarget.ArrayBuffer, (uint)sourceList.Length,
            intVarType ? EComponentType.Int : EComponentType.Float, 2, false, intVarType);
        Buffers.Add(BlendshapeCounts.AttributeName, BlendshapeCounts);

        List<Vector3> deltas = [Vector3.Zero];
        List<IVector4> blendshapeIndices = [];

        bool remapDeltas = RuntimeRenderingHostServices.Settings.RemapBlendshapeDeltas;

        int blendshapeDeltaIndicesIndex = 0;
        int sourceCount = sourceList.Length;
        int blendshapeCount = (int)BlendshapeCount;
        List<IVector4>?[] sparseRecordsByShape = new List<IVector4>?[blendshapeCount];
        int affectedVertexCount = 0;
        int* countsInt = (int*)BlendshapeCounts.Address;
        float* countsFloat = (float*)BlendshapeCounts.Address;

        for (int i = 0; i < sourceCount; i++)
        {
            int activeBlendshapeCountForThisVertex = 0;
            var vtx = sourceList[i];

            if (vtx.Blendshapes is null || vtx.Blendshapes.Count == 0)
            {
                if (intVarType)
                {
                    *countsInt++ = 0;
                    *countsInt++ = 0;
                }
                else
                {
                    *countsFloat++ = 0;
                    *countsFloat++ = 0;
                }
                continue;
            }

            Vector3 basePos = vtx.Position;
            Vector3 baseNrm = vtx.Normal ?? Vector3.Zero;
            Vector3 baseTan = vtx.Tangent ?? Vector3.Zero;

            for (int bsInd = 0; bsInd < blendshapeCount; bsInd++)
            {
                if (!TryGetBlendshapeDataForVertex(vtx.Blendshapes, blendshapeNames, bsInd, out VertexData bsData))
                    continue;

                bool anyData = false;
                int posInd = 0, nrmInd = 0, tanInd = 0;

                Vector3 tfmPos = bsData.Position;
                Vector3 tfmNrm = bsData.Normal ?? Vector3.Zero;
                Vector3 tfmTan = bsData.Tangent ?? Vector3.Zero;

                Vector3 posDt = tfmPos - basePos;
                Vector3 nrmDt = tfmNrm - baseNrm;
                Vector3 tanDt = tfmTan - baseTan;

                if (posDt.LengthSquared() > 0)
                {
                    posInd = deltas.Count;
                    deltas.Add(posDt);
                    anyData = true;
                }
                if (nrmDt.LengthSquared() > 0)
                {
                    nrmInd = deltas.Count;
                    deltas.Add(nrmDt);
                    anyData = true;
                }
                if (tanDt.LengthSquared() > 0)
                {
                    tanInd = deltas.Count;
                    deltas.Add(tanDt);
                    anyData = true;
                }

                if (anyData)
                {
                    activeBlendshapeCountForThisVertex++;
                    IVector4 record = new(bsInd, posInd, nrmInd, tanInd);
                    blendshapeIndices.Add(record);
                    (sparseRecordsByShape[bsInd] ??= []).Add(new IVector4(i, posInd, nrmInd, tanInd));
                }
            }

            if (activeBlendshapeCountForThisVertex > 0)
                affectedVertexCount++;

            if (intVarType)
            {
                *countsInt++ = blendshapeDeltaIndicesIndex;
                *countsInt++ = activeBlendshapeCountForThisVertex;
            }
            else
            {
                *countsFloat++ = blendshapeDeltaIndicesIndex;
                *countsFloat++ = activeBlendshapeCountForThisVertex;
            }
            blendshapeDeltaIndicesIndex += activeBlendshapeCountForThisVertex;
        }

        BlendshapeIndices = new XRDataBuffer($"{ECommonBufferType.BlendshapeIndices}Buffer", EBufferTarget.ShaderStorageBuffer,
            (uint)blendshapeIndices.Count, intVarType ? EComponentType.Int : EComponentType.Float, 4, false, intVarType);
        Buffers.Add(BlendshapeIndices.AttributeName, BlendshapeIndices);

        int[]? deltaRemap = remapDeltas
            ? PopulateRemappedBlendshapeDeltas(intVarType, deltas, blendshapeIndices)
            : PopulateBlendshapeDeltas(intVarType, deltas, blendshapeIndices);

        BlendshapeAffectedVertexCount = affectedVertexCount;
        BlendshapeSparseRecordCount = blendshapeIndices.Count;
        List<IVector4>?[]? quantizedSparseRecordsByShape = PopulateQuantizedBlendshapeBuffers(deltas, sparseRecordsByShape, deltaRemap);
        PopulateSparseBlendshapeBuffers(intVarType, quantizedSparseRecordsByShape ?? sparseRecordsByShape, null);

        BlendshapeShaderVariant = BlendshapeShaderVariant.ActiveList
            | (BlendshapeSparseRecords is not null ? BlendshapeShaderVariant.SparseDeltas : BlendshapeShaderVariant.None)
            | (BlendshapeQuantizedDeltas is not null ? BlendshapeShaderVariant.QuantizedDeltas : BlendshapeShaderVariant.None);
    }

    private static bool TryGetBlendshapeDataForVertex(List<(string name, VertexData data)> blendshapes, string[] blendshapeNames, int blendshapeIndex, out VertexData blendshapeData)
    {
        if ((uint)blendshapeIndex >= (uint)blendshapeNames.Length)
        {
            blendshapeData = null!;
            return false;
        }

        string expectedBlendshapeName = blendshapeNames[blendshapeIndex];
        if (string.IsNullOrEmpty(expectedBlendshapeName))
        {
            blendshapeData = null!;
            return false;
        }

        if ((uint)blendshapeIndex < (uint)blendshapes.Count)
        {
            (string name, VertexData data) directEntry = blendshapes[blendshapeIndex];
            if (string.Equals(directEntry.name, expectedBlendshapeName, StringComparison.Ordinal) && directEntry.data is not null)
            {
                blendshapeData = directEntry.data;
                return true;
            }
        }

        for (int entryIndex = 0; entryIndex < blendshapes.Count; entryIndex++)
        {
            (string name, VertexData data) entry = blendshapes[entryIndex];
            if (entry.data is null)
                continue;
            if (!string.Equals(entry.name, expectedBlendshapeName, StringComparison.Ordinal))
                continue;

            blendshapeData = entry.data;
            return true;
        }

        blendshapeData = null!;
        return false;
    }

    private unsafe int[]? PopulateBlendshapeDeltas(bool intVarType, List<Vector3> deltas, List<IVector4> blendshapeIndices)
    {
        using var _ = RuntimeRenderingHostServices.Profiling.StartProfileScope();

        BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer,
            (uint)deltas.Count, EComponentType.Float, 4, false, false);
        Buffers.Add(BlendshapeDeltas.AttributeName, BlendshapeDeltas);

        float* deltaData = (float*)BlendshapeDeltas.Address;
        for (int i = 0; i < deltas.Count; i++)
        {
            var d = deltas[i];
            *deltaData++ = d.X;
            *deltaData++ = d.Y;
            *deltaData++ = d.Z;
            *deltaData++ = 0f;
        }

        if (BlendshapeIndices is null)
            return null;
        if (intVarType)
        {
            int* indicesData = (int*)BlendshapeIndices.Address;
            foreach (var iv in blendshapeIndices)
            {
                *indicesData++ = iv.X;
                *indicesData++ = iv.Y;
                *indicesData++ = iv.Z;
                *indicesData++ = iv.W;
            }
        }
        else
        {
            float* indicesData = (float*)BlendshapeIndices.Address;
            foreach (var iv in blendshapeIndices)
            {
                *indicesData++ = iv.X;
                *indicesData++ = iv.Y;
                *indicesData++ = iv.Z;
                *indicesData++ = iv.W;
            }
        }

        BlendshapeDeltaStorageMode = BlendshapeDeltaStorageMode.SparseAugmentsDenseFallback;
        return null;
    }

    private unsafe int[] PopulateRemappedBlendshapeDeltas(bool intVarType, List<Vector3> deltas, List<IVector4> blendshapeIndices)
    {
        using var _ = RuntimeRenderingHostServices.Profiling.StartProfileScope();

        Remapper deltaRemap = new();
        deltaRemap.Remap(deltas, null);
        BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer,
            deltaRemap.ImplementationLength, EComponentType.Float, 4, false, false);
        Buffers.Add(BlendshapeDeltas.AttributeName, BlendshapeDeltas);

        float* deltaData = (float*)BlendshapeDeltas.Address;
        for (int i = 0; i < deltaRemap.ImplementationLength; i++)
        {
            Vector3 d = deltas[deltaRemap.ImplementationTable![i]];
            *deltaData++ = d.X;
            *deltaData++ = d.Y;
            *deltaData++ = d.Z;
            *deltaData++ = 0f;
        }

        var remap = deltaRemap.RemapTable!;
        if (BlendshapeIndices is null)
            return remap;
        if (intVarType)
        {
            int* indicesData = (int*)BlendshapeIndices.Address;
            foreach (var iv in blendshapeIndices)
            {
                *indicesData++ = iv.X;
                *indicesData++ = remap[iv.Y];
                *indicesData++ = remap[iv.Z];
                *indicesData++ = remap[iv.W];
            }
        }
        else
        {
            float* indicesData = (float*)BlendshapeIndices.Address;
            foreach (var iv in blendshapeIndices)
            {
                *indicesData++ = iv.X;
                *indicesData++ = remap[iv.Y];
                *indicesData++ = remap[iv.Z];
                *indicesData++ = remap[iv.W];
            }
        }

        BlendshapeDeltaStorageMode = BlendshapeDeltaStorageMode.SparseAugmentsDenseFallback;
        return remap;
    }

    private unsafe void PopulateSparseBlendshapeBuffers(bool intVarType, List<IVector4>?[] sparseRecordsByShape, int[]? deltaRemap)
    {
        using var _ = RuntimeRenderingHostServices.Profiling.StartProfileScope();

        int shapeCount = sparseRecordsByShape.Length;
        int recordCount = 0;
        for (int i = 0; i < shapeCount; i++)
            recordCount += sparseRecordsByShape[i]?.Count ?? 0;

        if (shapeCount == 0 || recordCount == 0)
            return;

        BlendshapeSparseShapeRanges = new XRDataBuffer(
            $"{ECommonBufferType.BlendshapeSparseShapeRanges}Buffer",
            EBufferTarget.ShaderStorageBuffer,
            (uint)shapeCount,
            intVarType ? EComponentType.Int : EComponentType.Float,
            4,
            false,
            intVarType);
        Buffers.Add(BlendshapeSparseShapeRanges.AttributeName, BlendshapeSparseShapeRanges);

        BlendshapeSparseRecords = new XRDataBuffer(
            $"{ECommonBufferType.BlendshapeSparseRecords}Buffer",
            EBufferTarget.ShaderStorageBuffer,
            (uint)recordCount,
            intVarType ? EComponentType.Int : EComponentType.Float,
            4,
            false,
            intVarType);
        Buffers.Add(BlendshapeSparseRecords.AttributeName, BlendshapeSparseRecords);

        if (intVarType)
            PopulateSparseBlendshapeBuffersInt(sparseRecordsByShape, deltaRemap);
        else
            PopulateSparseBlendshapeBuffersFloat(sparseRecordsByShape, deltaRemap);
    }

    private unsafe void PopulateSparseBlendshapeBuffersInt(List<IVector4>?[] sparseRecordsByShape, int[]? deltaRemap)
    {
        int* rangeData = (int*)BlendshapeSparseShapeRanges!.Address;
        int* recordData = (int*)BlendshapeSparseRecords!.Address;
        int recordCursor = 0;

        for (int shapeIndex = 0; shapeIndex < sparseRecordsByShape.Length; shapeIndex++)
        {
            List<IVector4>? records = sparseRecordsByShape[shapeIndex];
            int count = records?.Count ?? 0;
            int flags = CalculatePresenceFlags(records);

            *rangeData++ = recordCursor;
            *rangeData++ = count;
            *rangeData++ = flags;
            *rangeData++ = 0;

            if (records is null)
                continue;

            for (int i = 0; i < records.Count; i++)
            {
                IVector4 record = RemapSparseRecord(records[i], deltaRemap);
                *recordData++ = record.X;
                *recordData++ = record.Y;
                *recordData++ = record.Z;
                *recordData++ = record.W;
            }

            recordCursor += count;
        }
    }

    private unsafe void PopulateSparseBlendshapeBuffersFloat(List<IVector4>?[] sparseRecordsByShape, int[]? deltaRemap)
    {
        float* rangeData = (float*)BlendshapeSparseShapeRanges!.Address;
        float* recordData = (float*)BlendshapeSparseRecords!.Address;
        int recordCursor = 0;

        for (int shapeIndex = 0; shapeIndex < sparseRecordsByShape.Length; shapeIndex++)
        {
            List<IVector4>? records = sparseRecordsByShape[shapeIndex];
            int count = records?.Count ?? 0;
            int flags = CalculatePresenceFlags(records);

            *rangeData++ = recordCursor;
            *rangeData++ = count;
            *rangeData++ = flags;
            *rangeData++ = 0;

            if (records is null)
                continue;

            for (int i = 0; i < records.Count; i++)
            {
                IVector4 record = RemapSparseRecord(records[i], deltaRemap);
                *recordData++ = record.X;
                *recordData++ = record.Y;
                *recordData++ = record.Z;
                *recordData++ = record.W;
            }

            recordCursor += count;
        }
    }

    private unsafe List<IVector4>?[]? PopulateQuantizedBlendshapeBuffers(List<Vector3> deltas, List<IVector4>?[] sparseRecordsByShape, int[]? deltaRemap)
    {
        if (deltas.Count == 0)
            return null;

        int shapeCount = sparseRecordsByShape.Length;
        if (shapeCount == 0)
            return null;

        List<Vector3> quantizationDeltas = deltaRemap is null
            ? deltas
            : CreateRemappedDeltaTable(deltas, deltaRemap);

        BlendshapeQuantizationMetadata = new XRDataBuffer(
            $"{ECommonBufferType.BlendshapeQuantizationMetadata}Buffer",
            EBufferTarget.ShaderStorageBuffer,
            (uint)(shapeCount * 4),
            EComponentType.Float,
            4,
            false,
            false);
        Buffers.Add(BlendshapeQuantizationMetadata.AttributeName, BlendshapeQuantizationMetadata);

        List<(uint x, uint y)> packedDeltas = [(0u, 0u)];
        List<IVector4>?[] quantizedRecordsByShape = new List<IVector4>?[shapeCount];

        for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
        {
            ComputeShapeQuantizationMetadata(quantizationDeltas, sparseRecordsByShape[shapeIndex], deltaRemap, out Vector3 min, out Vector3 max, out Vector3 scale, out Vector3 bias);
            uint baseIndex = (uint)(shapeIndex * 4);
            BlendshapeQuantizationMetadata.SetVector4(baseIndex + 0u, new Vector4(min, 0.0f));
            BlendshapeQuantizationMetadata.SetVector4(baseIndex + 1u, new Vector4(max, 0.0f));
            BlendshapeQuantizationMetadata.SetVector4(baseIndex + 2u, new Vector4(scale, 0.0f));
            BlendshapeQuantizationMetadata.SetVector4(baseIndex + 3u, new Vector4(bias, 0.0f));

            List<IVector4>? sourceRecords = sparseRecordsByShape[shapeIndex];
            if (sourceRecords is null || sourceRecords.Count == 0)
                continue;

            Dictionary<int, int> deltaIndexRemap = new(sourceRecords.Count * 3);
            List<IVector4> quantizedRecords = new(sourceRecords.Count);
            for (int recordIndex = 0; recordIndex < sourceRecords.Count; recordIndex++)
            {
                IVector4 sourceRecord = RemapSparseRecord(sourceRecords[recordIndex], deltaRemap);
                quantizedRecords.Add(new IVector4(
                    sourceRecord.X,
                    QuantizeDeltaIndex(sourceRecord.Y, quantizationDeltas, scale, bias, packedDeltas, deltaIndexRemap),
                    QuantizeDeltaIndex(sourceRecord.Z, quantizationDeltas, scale, bias, packedDeltas, deltaIndexRemap),
                    QuantizeDeltaIndex(sourceRecord.W, quantizationDeltas, scale, bias, packedDeltas, deltaIndexRemap)));
            }

            quantizedRecordsByShape[shapeIndex] = quantizedRecords;
        }

        BlendshapeQuantizedDeltas = new XRDataBuffer(
            $"{ECommonBufferType.BlendshapeQuantizedDeltas}Buffer",
            EBufferTarget.ShaderStorageBuffer,
            (uint)packedDeltas.Count,
            EComponentType.UInt,
            2,
            false,
            true);
        Buffers.Add(BlendshapeQuantizedDeltas.AttributeName, BlendshapeQuantizedDeltas);

        uint* quantized = (uint*)BlendshapeQuantizedDeltas.Address;
        for (int i = 0; i < packedDeltas.Count; i++)
        {
            (uint x, uint y) = packedDeltas[i];
            *quantized++ = x;
            *quantized++ = y;
        }

        BlendshapeDeltaEncoding = BlendshapeDeltaEncoding.Snorm16Vector3;
        return quantizedRecordsByShape;
    }

    private static List<Vector3> CreateRemappedDeltaTable(List<Vector3> deltas, int[] deltaRemap)
    {
        int remappedCount = 0;
        int count = Math.Min(deltas.Count, deltaRemap.Length);
        for (int i = 0; i < count; i++)
            remappedCount = Math.Max(remappedCount, deltaRemap[i] + 1);

        List<Vector3> remappedDeltas = new(remappedCount);
        for (int i = 0; i < remappedCount; i++)
            remappedDeltas.Add(Vector3.Zero);

        for (int i = 0; i < count; i++)
        {
            int remappedIndex = deltaRemap[i];
            if ((uint)remappedIndex < (uint)remappedDeltas.Count)
                remappedDeltas[remappedIndex] = deltas[i];
        }

        return remappedDeltas;
    }

    private static int QuantizeDeltaIndex(
        int deltaIndex,
        List<Vector3> deltas,
        Vector3 scale,
        Vector3 bias,
        List<(uint x, uint y)> packedDeltas,
        Dictionary<int, int> deltaIndexRemap)
    {
        if (deltaIndex <= 0 || deltaIndex >= deltas.Count)
            return 0;

        if (deltaIndexRemap.TryGetValue(deltaIndex, out int quantizedIndex))
            return quantizedIndex;

        (short x, short y, short z) encoded = BlendshapeDeltaQuantizer.EncodeSnorm16(deltas[deltaIndex], scale, bias);
        quantizedIndex = packedDeltas.Count;
        packedDeltas.Add((
            BlendshapeDeltaQuantizer.PackSnorm16Pair(encoded.x, encoded.y),
            BlendshapeDeltaQuantizer.PackSnorm16Pair(encoded.z, 0)));
        deltaIndexRemap.Add(deltaIndex, quantizedIndex);
        return quantizedIndex;
    }

    private static int CalculatePresenceFlags(List<IVector4>? records)
    {
        if (records is null || records.Count == 0)
            return 0;

        int flags = 0;
        for (int i = 0; i < records.Count; i++)
        {
            IVector4 record = records[i];
            if (record.Y != 0)
                flags |= 1;
            if (record.Z != 0)
                flags |= 2;
            if (record.W != 0)
                flags |= 4;
        }

        return flags;
    }

    public bool TryCalculateBlendshapeBounds(BlendshapeLodTier tier, out AABB bounds)
    {
        if (Vertices is not { Length: > 0 } vertices)
        {
            bounds = Bounds;
            return false;
        }

        bounds = new AABB(vertices[0].Position, vertices[0].Position);
        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            Vertex vertex = vertices[vertexIndex];
            bounds.ExpandToInclude(vertex.Position);

            if (tier.Evaluation == BlendshapeLodEvaluation.Disabled || vertex.Blendshapes is null)
                continue;

            for (int shapeEntryIndex = 0; shapeEntryIndex < vertex.Blendshapes.Count; shapeEntryIndex++)
            {
                (string name, VertexData data) entry = vertex.Blendshapes[shapeEntryIndex];
                if (entry.data is null)
                    continue;

                int shapeIndex = ResolveBlendshapeIndex(entry.name, shapeEntryIndex);
                if (!ShouldIncludeBlendshapeInBounds(tier, shapeIndex, entry.name))
                    continue;

                bounds.ExpandToInclude(entry.data.Position);
            }
        }

        return true;
    }

    public bool BoundsContainBlendshapeExtremes(BlendshapeLodTier tier, float tolerance = 1.0e-5f)
    {
        if (!TryCalculateBlendshapeBounds(tier, out AABB blendshapeBounds))
            return true;

        return Bounds.ContainsPoint(blendshapeBounds.Min, tolerance)
            && Bounds.ContainsPoint(blendshapeBounds.Max, tolerance);
    }

    private int ResolveBlendshapeIndex(string name, int fallbackIndex)
    {
        string[]? names = BlendshapeNames;
        if (names is null)
            return fallbackIndex;

        for (int i = 0; i < names.Length; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return i;

        return fallbackIndex;
    }

    private static bool ShouldIncludeBlendshapeInBounds(BlendshapeLodTier tier, int shapeIndex, string shapeName)
    {
        if (tier.Evaluation == BlendshapeLodEvaluation.Full)
            return true;

        if (ContainsShapeIndex(tier.ShapeIndices, shapeIndex))
            return true;

        return ContainsShapeName(tier.ProtectedShapeNames, shapeName);
    }

    private static bool ContainsShapeIndex(IReadOnlyList<int>? shapeIndices, int shapeIndex)
    {
        if (shapeIndices is null)
            return false;

        for (int i = 0; i < shapeIndices.Count; i++)
            if (shapeIndices[i] == shapeIndex)
                return true;

        return false;
    }

    private static bool ContainsShapeName(IReadOnlyList<string>? shapeNames, string shapeName)
    {
        if (shapeNames is null)
            return false;

        for (int i = 0; i < shapeNames.Count; i++)
            if (string.Equals(shapeNames[i], shapeName, StringComparison.Ordinal))
                return true;

        return false;
    }

    private static IVector4 RemapSparseRecord(IVector4 record, int[]? deltaRemap)
    {
        if (deltaRemap is null)
            return record;

        int RemapIndex(int index)
            => (uint)index < (uint)deltaRemap.Length ? deltaRemap[index] : index;

        return new IVector4(record.X, RemapIndex(record.Y), RemapIndex(record.Z), RemapIndex(record.W));
    }

    private static void ComputeShapeQuantizationMetadata(
        List<Vector3> deltas,
        List<IVector4>? records,
        int[]? deltaRemap,
        out Vector3 min,
        out Vector3 max,
        out Vector3 scale,
        out Vector3 bias)
    {
        if (records is null || records.Count == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            scale = Vector3.One;
            bias = Vector3.Zero;
            return;
        }

        bool any = false;
        min = new Vector3(float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity);

        for (int i = 0; i < records.Count; i++)
        {
            IVector4 record = RemapSparseRecord(records[i], deltaRemap);
            AccumulateDeltaBounds(deltas, record.Y, ref min, ref max, ref any);
            AccumulateDeltaBounds(deltas, record.Z, ref min, ref max, ref any);
            AccumulateDeltaBounds(deltas, record.W, ref min, ref max, ref any);
        }

        if (!any)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        BlendshapeDeltaQuantizer.ComputeScaleBias(min, max, out scale, out bias);
    }

    private static void AccumulateDeltaBounds(List<Vector3> deltas, int index, ref Vector3 min, ref Vector3 max, ref bool any)
    {
        if (index <= 0 || index >= deltas.Count)
            return;

        Vector3 value = deltas[index];
        min = Vector3.Min(min, value);
        max = Vector3.Max(max, value);
        any = true;
    }
}
