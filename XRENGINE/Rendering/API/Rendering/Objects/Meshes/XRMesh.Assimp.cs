using Assimp;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Scene;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public unsafe XRMesh(
        Mesh mesh,
        AssimpContext assimp,
        Dictionary<string, List<SceneNode>> nodeCache,
        Matrix4x4 dataTransform) : this()
    {
        using var _ = Engine.Profiler.Start("Assimp XRMesh Constructor");

        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(assimp);
        ArgumentNullException.ThrowIfNull(nodeCache);

        Vertex[] points;
        Vertex[] lines;
        Vertex[] triangles;
        ConcurrentDictionary<int, DelVertexAction> vertexActions = [];

        int maxColorCount = 0, maxTexCoordCount = 0;

        _bounds = new(Vector3.Zero, Vector3.Zero);
        var vertexCache = new ConcurrentDictionary<int, Vertex>();

        PopulateVerticesAssimpParallelPrecomputed(
            mesh,
            vertexActions,
            ref maxColorCount,
            ref maxTexCoordCount,
            vertexCache,
            out points,
            out lines,
            out triangles,
            out var faceRemap,
            dataTransform);

        SetTriangleIndices(triangles, false);
        SetLineIndices(lines, false);
        SetPointIndices(points, false);

        int count;
        Vertex[] sourceList;
        if (triangles.Length > lines.Length && triangles.Length > points.Length)
        {
            _type = EPrimitiveType.Triangles;
            count = triangles.Length;
            sourceList = triangles;
        }
        else if (lines.Length > triangles.Length && lines.Length > points.Length)
        {
            _type = EPrimitiveType.Lines;
            count = lines.Length;
            sourceList = lines;
        }
        else
        {
            _type = EPrimitiveType.Points;
            count = points.Length;
            sourceList = points;
        }

        VertexCount = count;

        InitializeSkinning(mesh, nodeCache, faceRemap, sourceList);

        InitMeshBuffers(vertexActions.ContainsKey(1), vertexActions.ContainsKey(2), maxColorCount, maxTexCoordCount);
        AddPositionsAction(vertexActions);

        PopulateVertexData(
            vertexActions.Values,
            sourceList,
            count,
            Matrix4x4.Identity,
            Engine.Rendering.Settings.PopulateVertexDataInParallel);

        PopulateAssimpBlendshapeData(mesh, sourceList);

        Vertices = sourceList;
    }

    private unsafe void PopulateAssimpBlendshapeData(Mesh mesh, Vertex[] sourceList)
    {
        if (!Engine.Rendering.Settings.AllowBlendshapes || !mesh.HasMeshAnimationAttachments)
            return;

        string[] names = new string[mesh.MeshAnimationAttachmentCount];
        for (int i = 0; i < mesh.MeshAnimationAttachmentCount; i++)
            names[i] = mesh.MeshAnimationAttachments[i].Name;
        BlendshapeNames = names;

        PopulateBlendshapeBuffers(sourceList);
    }

    private static void PopulateVerticesAssimpParallelPrecomputed(
        Mesh mesh,
        ConcurrentDictionary<int, DelVertexAction> vertexActions,
        ref int mcc,
        ref int mtc,
        ConcurrentDictionary<int, Vertex> vertexCache,
        out Vertex[] points,
        out Vertex[] lines,
        out Vertex[] triangles,
        out Dictionary<int, List<int>> faceRemap,
        Matrix4x4 dataTransform)
    {
        int faceCount = mesh.FaceCount;
        using var _ = Engine.Profiler.Start($"PopulateVerticesAssimpParallelPrecomputed with {faceCount} faces");

        int maxColorCount = 0;
        int maxTexCoordCount = 0;

        int[] offsetPoints = new int[faceCount];
        int[] offsetLines = new int[faceCount];
        int[] offsetTriangles = new int[faceCount];
        int totalPoints = 0, totalLines = 0, totalTriangles = 0;

        for (int i = 0; i < faceCount; i++)
        {
            Face face = mesh.Faces[i];
            if (face.IndexCount == 1)
            {
                offsetPoints[i] = totalPoints;
                totalPoints += 1;
            }
            else if (face.IndexCount == 2)
            {
                offsetLines[i] = totalLines;
                totalLines += 2;
            }
            else
            {
                offsetTriangles[i] = totalTriangles;
                int numTriangles = face.IndexCount - 2;
                totalTriangles += numTriangles * 3;
            }
        }

        Vertex[] pointsArray = new Vertex[totalPoints];
        Vertex[] linesArray = new Vertex[totalLines];
        Vertex[] trianglesArray = new Vertex[totalTriangles];
        var concurrentFaceRemap = new ConcurrentDictionary<int, List<int>>();

        bool hasNormalAction = vertexActions.ContainsKey(1);
        bool hasTangentAction = vertexActions.ContainsKey(2);
        bool hasTexCoordAction = vertexActions.ContainsKey(3);
        bool hasColorAction = vertexActions.ContainsKey(4);

        Parallel.For(0, faceCount, i =>
        {
            Face face = mesh.Faces[i];
            int numInd = face.IndexCount;

            if (numInd == 1)
            {
                int baseOffset = offsetPoints[i];
                ProcessAssimpVertexPrecomputed(face.Indices[0], pointsArray, baseOffset, mesh, vertexCache, vertexActions,
                    ref maxTexCoordCount, ref maxColorCount, concurrentFaceRemap,
                    ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction,
                    dataTransform);
            }
            else if (numInd == 2)
            {
                int baseOffset = offsetLines[i];
                ProcessAssimpVertexPrecomputed(face.Indices[0], linesArray, baseOffset, mesh, vertexCache, vertexActions,
                    ref maxTexCoordCount, ref maxColorCount, concurrentFaceRemap,
                    ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction,
                    dataTransform);
                ProcessAssimpVertexPrecomputed(face.Indices[1], linesArray, baseOffset + 1, mesh, vertexCache, vertexActions,
                    ref maxTexCoordCount, ref maxColorCount, concurrentFaceRemap,
                    ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction,
                    dataTransform);
            }
            else
            {
                int baseOffset = offsetTriangles[i];
                int localOffset = 0;
                int index0 = face.Indices[0];
                for (int j = 0; j < numInd - 2; j++)
                {
                    int index1 = face.Indices[j + 1];
                    int index2 = face.Indices[j + 2];
                    ProcessAssimpVertexPrecomputed(index0, trianglesArray, baseOffset + localOffset, mesh, vertexCache, vertexActions,
                        ref maxTexCoordCount, ref maxColorCount, concurrentFaceRemap,
                        ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction, dataTransform);
                    localOffset++;
                    ProcessAssimpVertexPrecomputed(index1, trianglesArray, baseOffset + localOffset, mesh, vertexCache, vertexActions,
                        ref maxTexCoordCount, ref maxColorCount, concurrentFaceRemap,
                        ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction, dataTransform);
                    localOffset++;
                    ProcessAssimpVertexPrecomputed(index2, trianglesArray, baseOffset + localOffset, mesh, vertexCache, vertexActions,
                        ref maxTexCoordCount, ref maxColorCount, concurrentFaceRemap,
                        ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction, dataTransform);
                    localOffset++;
                }
            }
        });

        points = pointsArray;
        lines = linesArray;
        triangles = trianglesArray;
        faceRemap = new Dictionary<int, List<int>>(concurrentFaceRemap);
        mtc = maxTexCoordCount;
        mcc = maxColorCount;
    }

    private static void ProcessAssimpVertexPrecomputed(
        int originalIndex,
        Vertex[] targetArray,
        int targetIndex,
        Mesh mesh,
        ConcurrentDictionary<int, Vertex> vertexCache,
        ConcurrentDictionary<int, DelVertexAction> vertexActions,
        ref int maxTexCoordCount,
        ref int maxColorCount,
        ConcurrentDictionary<int, List<int>> faceRemap,
        ref bool hasNormalAction,
        ref bool hasTangentAction,
        ref bool hasTexCoordAction,
        ref bool hasColorAction,
        Matrix4x4 dataTransform)
    {
        Vertex v = vertexCache.GetOrAdd(originalIndex, x => Vertex.FromAssimp(mesh, x, dataTransform));
        if (v == null) return;
        targetArray[targetIndex] = v;

        if (v.Normal != null && !hasNormalAction)
            hasNormalAction |= AddNormalAction(vertexActions);
        if (v.Tangent != null && !hasTangentAction)
            hasTangentAction |= AddTangentAction(vertexActions);
        if (v.TextureCoordinateSets != null && v.TextureCoordinateSets.Count > 0 && !hasTexCoordAction)
        {
            Interlocked.Exchange(ref maxTexCoordCount, Math.Max(maxTexCoordCount, v.TextureCoordinateSets.Count));
            hasTexCoordAction |= AddTexCoordAction(vertexActions);
        }
        if (v.ColorSets != null && v.ColorSets.Count > 0 && !hasColorAction)
        {
            Interlocked.Exchange(ref maxColorCount, Math.Max(maxColorCount, v.ColorSets.Count));
            hasColorAction |= AddColorAction(vertexActions);
        }

        faceRemap.AddOrUpdate(originalIndex,
            _ => [targetIndex],
            (_, list) =>
            {
                lock (list) list.Add(targetIndex);
                return list;
            });
    }
}