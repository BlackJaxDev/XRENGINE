using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private unsafe void PopulateBlendshapeBuffers(Vertex[] sourceList)
    {
        using var _ = Engine.Profiler.Start();

        bool intVarType = Engine.Rendering.Settings.UseIntegerUniformsInShaders;

        BlendshapeCounts = new XRDataBuffer(ECommonBufferType.BlendshapeCount.ToString(), EBufferTarget.ArrayBuffer, (uint)sourceList.Length,
            intVarType ? EComponentType.Int : EComponentType.Float, 2, false, intVarType);
        Buffers.Add(BlendshapeCounts.AttributeName, BlendshapeCounts);

        List<Vector3> deltas = [Vector3.Zero];
        List<IVector4> blendshapeIndices = [];

        bool remapDeltas = Engine.Rendering.Settings.RemapBlendshapeDeltas;

        int blendshapeDeltaIndicesIndex = 0;
        int sourceCount = sourceList.Length;
        int blendshapeCount = (int)BlendshapeCount;
        int* countsInt = (int*)BlendshapeCounts.Address;
        float* countsFloat = (float*)BlendshapeCounts.Address;

        for (int i = 0; i < sourceCount; i++)
        {
            int activeBlendshapeCountForThisVertex = 0;
            var vtx = sourceList[i];

            if (vtx.Blendshapes is null)
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
                var (_, bsData) = vtx.Blendshapes[bsInd];
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
                    blendshapeIndices.Add(new IVector4(bsInd, posInd, nrmInd, tanInd));
                }
            }

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

        if (remapDeltas)
            PopulateRemappedBlendshapeDeltas(intVarType, deltas, blendshapeIndices);
        else
            PopulateBlendshapeDeltas(intVarType, deltas, blendshapeIndices);
    }

    private unsafe void PopulateBlendshapeDeltas(bool intVarType, List<Vector3> deltas, List<IVector4> blendshapeIndices)
    {
        using var _ = Engine.Profiler.Start();

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

        if (BlendshapeIndices is null) return;
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
    }

    private unsafe void PopulateRemappedBlendshapeDeltas(bool intVarType, List<Vector3> deltas, List<IVector4> blendshapeIndices)
    {
        using var _ = Engine.Profiler.Start();

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

        if (BlendshapeIndices is null) return;
        var remap = deltaRemap.RemapTable!;
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
    }
}