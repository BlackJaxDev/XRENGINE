using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;
using static Meshoptimizer.Meshopt;
using MeshoptMeshlet = Meshoptimizer.Meshopt.Meshlet;

namespace XREngine.Rendering.Meshlets
{
    /// <summary>
    /// Manages meshlet generation and rendering
    /// </summary>
    public static class MeshletGenerator
    {
        public static unsafe Meshlet[] Build(
            out uint[] meshletVertexIndices,
            out byte[] meshletTriangleIndices,
            uint maxVerticesPerMeshlet,
            uint maxTrianglesPerMeshlet,
            float coneWeight,
            params XRMesh[] meshes)
            => Build(
                meshes,
                out meshletVertexIndices,
                out meshletTriangleIndices,
                maxVerticesPerMeshlet,
                maxTrianglesPerMeshlet,
                coneWeight);

        /// <summary>
        /// Generates a list of meshlets from the provided meshes.
        /// </summary>
        /// <param name="meshes">The source meshes to generate meshlets from.</param>
        /// <param name="meshletVertexIndices">The output array of vertex indices for all meshlets.</param>
        /// <param name="meshletTriangleIndices">The output array of triangle indices for all meshlets.</param>
        /// <param name="maxVerticesPerMeshlet">The maximum number of vertices allowed per meshlet.</param>
        /// <param name="maxTrianglesPerMeshlet">The maximum number of triangles allowed per meshlet.</param>
        /// <param name="coneWeight">The cone weight for meshlet generation, affecting the clustering of triangles.</param>
        /// <returns></returns>
        public static unsafe Meshlet[] Build(
            IEnumerable<XRMesh> meshes,
            out uint[] meshletVertexIndices,
            out byte[] meshletTriangleIndices,
            uint maxVerticesPerMeshlet = 64u,
            uint maxTrianglesPerMeshlet = 124u,
            float coneWeight = 0.0f)
        {
            meshletVertexIndices = [];
            meshletTriangleIndices = [];

            if (meshes is null || !meshes.Any())
                return [];

            List<Meshlet> allMeshlets = [];
            List<uint> allMeshletVerts = [];
            List<byte> allMeshletTris = [];
            foreach (XRMesh mesh in meshes)
            {
                Meshlet[] meshlets = Build(
                    mesh,
                    out uint[] meshletVerts,
                    out byte[] meshletTris,
                    maxVerticesPerMeshlet,
                    maxTrianglesPerMeshlet,
                    coneWeight);
                if (meshlets.Length == 0 || meshletVerts.Length == 0 || meshletTris.Length == 0)
                    continue;
                uint vertOffset = (uint)allMeshletVerts.Count;
                byte triOffset = (byte)(allMeshletTris.Count / 3); //3 indices per triangle
                //Adjust offsets
                for (int i = 0; i < meshlets.Length; i++)
                {
                    Meshlet m = meshlets[i];
                    m.VertexOffset += vertOffset;
                    m.TriangleOffset += triOffset;
                    allMeshlets.Add(m);
                }
                allMeshletVerts.AddRange(meshletVerts);
                allMeshletTris.AddRange(meshletTris);
            }
            meshletVertexIndices = [.. allMeshletVerts];
            meshletTriangleIndices = [.. allMeshletTris];
            return [.. allMeshlets];
        }

        /// <summary>
        /// Generates a list of meshlets from the provided mesh.
        /// </summary>
        /// <param name="mesh">The source mesh to generate meshlets from.</param>
        /// <param name="meshletVertexIndices">The output array of vertex indices for all meshlets.</param>
        /// <param name="meshletTriangleIndices">The output array of triangle indices for all meshlets.</param>
        /// <param name="maxVerticesPerMeshlet">The maximum number of vertices allowed per meshlet.</param>
        /// <param name="maxTrianglesPerMeshlet">The maximum number of triangles allowed per meshlet.</param>
        /// <param name="coneWeight">The cone weight for meshlet generation, affecting the clustering of triangles.</param>
        /// <returns></returns>
        public static unsafe Meshlet[] Build(
            XRMesh mesh,
            out uint[] meshletVertexIndices,
            out byte[] meshletTriangleIndices,
            uint maxVerticesPerMeshlet = 64u,
            uint maxTrianglesPerMeshlet = 124u,
            float coneWeight = 0.0f)
        {
            meshletVertexIndices = [];
            meshletTriangleIndices = [];
            if (mesh is null || mesh.VertexCount == 0)
                return [];

            int[]? indices = mesh.GetIndices(EPrimitiveType.Triangles);
            if (indices is null || indices.Length == 0)
                return [];

            uint[] uInd = [.. indices.Select(x => (uint)x)];

            nuint maxMeshlets = BuildMeshletsBound((uint)indices.Length, maxVerticesPerMeshlet, maxTrianglesPerMeshlet);
            if (maxMeshlets == 0)
                return [];

            MeshoptMeshlet[] meshlets = new MeshoptMeshlet[maxMeshlets];
            uint* meshletVertices = stackalloc uint[(int)(maxMeshlets * maxVerticesPerMeshlet)];
            byte* meshletTriangles = stackalloc byte[(int)(maxMeshlets * maxTrianglesPerMeshlet * 3)]; //3 indices per triangle

            VoidPtr posAddr = mesh.Interleaved 
                ? mesh.InterleavedVertexBuffer!.Address
                : mesh.PositionsBuffer!.Address;

            uint stride = mesh.Interleaved 
                ? mesh.InterleavedStride
                : 12;

            uint vertexCount = (uint)mesh.VertexCount;

            nuint count = BuildMeshlets(
                ref meshlets[0],
                meshletVertices[0],
                meshletTriangles[0],
                uInd[0],
                (uint)indices.Length,
                *(float*)posAddr,
                vertexCount,
                stride,
                maxVerticesPerMeshlet,
                maxTrianglesPerMeshlet,
                coneWeight);

            if (count == 0)
                return [];

            int meshletCount = (int)count;
            meshletVertexIndices = new uint[meshletCount * maxVerticesPerMeshlet];
            meshletTriangleIndices = new byte[meshletCount * maxTrianglesPerMeshlet * 3];

            fixed (uint* pMeshletVert = &meshletVertexIndices[0])
                Buffer.MemoryCopy(meshletVertices, pMeshletVert, meshletVertexIndices.Length * sizeof(uint), meshletVertexIndices.Length * sizeof(uint));
            fixed (byte* pMeshletTri = &meshletTriangleIndices[0])
                Buffer.MemoryCopy(meshletTriangles, pMeshletTri, meshletTriangleIndices.Length * sizeof(byte), meshletTriangleIndices.Length * sizeof(byte));

            Meshlet[] results = new Meshlet[meshletCount];
            for (int i = 0; i < meshletCount; i++)
            {
                MeshoptMeshlet m = meshlets[i];

                Vector4 bounds = ComputeBounds(mesh, meshletVertexIndices, m);

                results[i] = new Meshlet
                {
                    BoundingSphere = bounds,
                    VertexOffset = m.VertexOffset,
                    TriangleOffset = m.TriangleOffset,
                    VertexCount = m.VertexCount,
                    TriangleCount = m.TriangleCount,
                    MeshID = 0,
                    MaterialID = 0,
                };
            }

            return results;
        }

        private static Vector4 ComputeBounds(XRMesh mesh, IReadOnlyList<uint> vertexIndices, MeshoptMeshlet meshlet)
        {
            if (meshlet.VertexCount == 0)
                return Vector4.Zero;

            uint offset = meshlet.VertexOffset;
            Vector3 center = Vector3.Zero;
            for (int i = 0; i < meshlet.VertexCount; i++)
            {
                Vector3 pos = mesh.GetPosition(vertexIndices[(int)(offset + i)]);
                center += pos;
            }

            center /= meshlet.VertexCount;

            float radius = 0f;
            for (int i = 0; i < meshlet.VertexCount; i++)
            {
                Vector3 pos = mesh.GetPosition(vertexIndices[(int)(offset + i)]);
                radius = Math.Max(radius, Vector3.Distance(center, pos));
            }

            return new Vector4(center, radius);
        }
    }
}
