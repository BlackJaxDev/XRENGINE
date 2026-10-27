using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Models.Gaussian.Baking.Surface;

/// <summary>
/// Calculates on the CPU the world-space triangle surface of content at its current pose, with blendshapes and
/// skinning applied.
/// </summary>
public static partial class DeformedSurfaceEvaluator
{
    /// <summary>
    /// The GPU skinning pass keeps a vertex unskinned when its total bone weight is not more than this value.
    /// Such a vertex uses the component transform here, as the CPU skinned bounds do for a vertex with no influences.
    /// </summary>
    private const float MinimumTotalWeight = 1e-4f;

    /// <summary>
    /// Calculates the surface of every active <see cref="RenderableComponent"/> under <paramref name="root"/>,
    /// in depth-first order. The calculation reads the current transform matrices, so update the hierarchy first
    /// (<see cref="Motion.BakeFrameSampler"/> does this).
    /// </summary>
    /// <remarks>
    /// <para>The calculation uses the mesh of the first level of detail of each renderable mesh. Each vertex starts at
    /// its rest position plus the sum of weight × delta of its blendshapes. The weights come from
    /// <see cref="XRMeshRenderer.GetBlendshapeWeightNormalized(uint)"/>.</para>
    /// <para>A skinned vertex then goes to world space through the sum of influence × (BindRootMatrix × inverse bind
    /// matrix × bone render matrix). This is the palette of the CPU skinned bounds in <see cref="RenderableMesh"/>.
    /// The influence weights of one vertex are normalized to a sum of 1, as the GPU skinning pass does. A vertex
    /// with no influences, and every vertex of a mesh that is not skinned, uses the world matrix of the component
    /// transform.</para>
    /// <para>Components and nodes that are not active are left out, because they do not render. Gaussian splat
    /// components are left out, because their meshes are not surfaces. <see cref="FindSkipped(SceneNode)"/> reports them.</para>
    /// </remarks>
    /// <param name="root">The root node of the content.</param>
    /// <param name="reuse">
    /// A surface from an earlier evaluation of the same root. The evaluation writes the new positions into it and
    /// does not allocate. Use it only while the content keeps the same components and meshes.
    /// </param>
    /// <returns><paramref name="reuse"/> when it is given; otherwise a new surface.</returns>
    public static DeformedSurface Evaluate(SceneNode root, DeformedSurface? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        DeformedSurface surface = reuse ?? new DeformedSurface();
        if (!ReferenceEquals(surface.Root, root) || !AreSourcesCurrent(surface.Sources))
            BuildLayout(root, surface);

        DeformedMeshSource[] sources = surface.Sources;
        for (int i = 0; i < sources.Length; i++)
            Deform(sources[i], surface.Positions);

        return surface;
    }

    private static bool AreSourcesCurrent(DeformedMeshSource[] sources)
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (!sources[i].IsCurrent())
                return false;
        }

        return true;
    }

    /// <summary>
    /// Finds the meshes under the root and reads the data that does not change between frames:
    /// rest positions, influences, blendshape deltas, and triangles.
    /// </summary>
    private static void BuildLayout(SceneNode root, DeformedSurface surface)
    {
        List<DeformedMeshSource> sources = [];
        List<uint> indices = [];
        List<int> triangleMesh = [];
        int vertexCount = 0;

        root.IterateComponents<RenderableComponent>(component =>
        {
            // Gaussian splat components are not renderable mesh components, so the walk does not see them.
            if (!IsRendered(component, root))
                return;

            foreach (RenderableMesh renderableMesh in component.Meshes)
            {
                XRMeshRenderer? renderer = renderableMesh.LODs.First?.Value.Renderer;
                XRMesh? mesh = renderer?.Mesh;
                if (renderer is null || mesh is null || !IsTriangleSurface(mesh))
                    continue;

                int meshIndex = sources.Count;
                sources.Add(CreateSource(component, renderer, mesh, vertexCount));
                AppendTriangles(mesh, vertexCount, meshIndex, indices, triangleMesh);
                vertexCount += mesh.VertexCount;
            }
        }, true);

        int[] meshVertexStart = new int[sources.Count + 1];
        for (int i = 0; i < sources.Count; i++)
            meshVertexStart[i] = sources[i].VertexStart;
        meshVertexStart[sources.Count] = vertexCount;

        surface.Root = root;
        surface.Sources = [.. sources];
        surface.Positions = new float[vertexCount * 3];
        surface.Indices = [.. indices];
        surface.TriangleMesh = [.. triangleMesh];
        surface.MeshVertexStart = meshVertexStart;
        surface.MeshCount = sources.Count;
        surface.VertexCount = vertexCount;
        surface.TriangleCount = triangleMesh.Count;
    }

    private static bool IsTriangleSurface(XRMesh mesh)
        => mesh.VertexCount > 0
        && mesh.Type == EPrimitiveType.Triangles
        && mesh.Triangles is { Count: > 0 };

    /// <summary>
    /// Returns true when the component and every node from its node up to <paramref name="root"/> are active.
    /// </summary>
    private static bool IsRendered(XRComponent component, SceneNode root)
    {
        if (!component.IsActive)
            return false;

        for (SceneNode? node = component.SceneNode; node is not null; node = node.Parent)
        {
            if (!node.IsActiveSelf)
                return false;
            if (ReferenceEquals(node, root))
                break;
        }

        return true;
    }

    /// <summary>Adds the triangles of the mesh. A triangle with an index outside the mesh is left out.</summary>
    private static void AppendTriangles(XRMesh mesh, int vertexStart, int meshIndex, List<uint> indices, List<int> triangleMesh)
    {
        int vertexCount = mesh.VertexCount;
        foreach (IndexTriangle triangle in mesh.Triangles!)
        {
            int a = triangle.Point0;
            int b = triangle.Point1;
            int c = triangle.Point2;
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                continue;

            indices.Add((uint)(vertexStart + a));
            indices.Add((uint)(vertexStart + b));
            indices.Add((uint)(vertexStart + c));
            triangleMesh.Add(meshIndex);
        }
    }

    private static DeformedMeshSource CreateSource(RenderableComponent component, XRMeshRenderer renderer, XRMesh mesh, int vertexStart)
    {
        int vertexCount = mesh.VertexCount;
        Vector3[] basePositions = new Vector3[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
            basePositions[vertex] = mesh.GetPosition((uint)vertex);

        ReadInfluences(mesh, out int[] influenceOffsets, out int[] influenceBones, out float[] influenceWeights);
        ReadBlendshapes(mesh, out int[] blendshapeOffsets, out int[] blendshapeShapes, out Vector3[] blendshapeDeltas);

        return new DeformedMeshSource
        {
            Component = component,
            Renderer = renderer,
            Mesh = mesh,
            GeometryRevision = mesh.GeometryRevision,
            VertexStart = vertexStart,
            BasePositions = basePositions,
            InfluenceOffsets = influenceOffsets,
            InfluenceBones = influenceBones,
            InfluenceWeights = influenceWeights,
            Palette = influenceOffsets.Length != 0 ? new Matrix4x4[mesh.UtilizedBones.Length] : [],
            BlendshapeOffsets = blendshapeOffsets,
            BlendshapeShapes = blendshapeShapes,
            BlendshapeDeltas = blendshapeDeltas,
            BlendshapeWeights = blendshapeOffsets.Length != 0 ? new float[mesh.BlendshapeCount] : [],
        };
    }

    /// <summary>
    /// Reads the bone influences of each vertex and normalizes the weights of each vertex to a sum of 1.
    /// The arrays are empty when the mesh is not skinned or has no readable influence data.
    /// </summary>
    private static void ReadInfluences(XRMesh mesh, out int[] offsets, out int[] bones, out float[] weights)
    {
        offsets = [];
        bones = [];
        weights = [];
        if (!mesh.HasSkinning || !XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader reader))
            return;

        int vertexCount = mesh.VertexCount;
        int[] vertexBones = new int[reader.MaxInfluenceCount];
        float[] vertexWeights = new float[reader.MaxInfluenceCount];
        List<int> boneList = new(vertexCount);
        List<float> weightList = new(vertexCount);
        offsets = new int[vertexCount + 1];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            offsets[vertex] = boneList.Count;
            int count = reader.ReadInfluences(vertex, vertexBones, vertexWeights);
            float total = 0.0f;
            for (int influence = 0; influence < count; influence++)
                total += vertexWeights[influence];
            if (total <= MinimumTotalWeight)
                continue;

            for (int influence = 0; influence < count; influence++)
            {
                boneList.Add(vertexBones[influence]);
                weightList.Add(vertexWeights[influence] / total);
            }
        }

        offsets[vertexCount] = boneList.Count;
        bones = [.. boneList];
        weights = [.. weightList];
    }

    /// <summary>
    /// Reads the position delta of each blendshape entry. Entries with no position delta are left out.
    /// The arrays are empty when the mesh has no blendshapes or has no readable blendshape data.
    /// </summary>
    private static void ReadBlendshapes(XRMesh mesh, out int[] offsets, out int[] shapes, out Vector3[] deltas)
    {
        offsets = [];
        shapes = [];
        deltas = [];
        if (!mesh.HasBlendshapes || !XRMeshBlendshapeActiveListReader.TryCreate(mesh, out XRMeshBlendshapeActiveListReader reader))
            return;

        int vertexCount = mesh.VertexCount;
        int shapeCount = (int)mesh.BlendshapeCount;
        List<int> shapeList = [];
        List<Vector3> deltaList = [];
        offsets = new int[vertexCount + 1];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            offsets[vertex] = shapeList.Count;
            reader.GetVertexEntries(vertex, out int first, out int count);
            for (int entry = first; entry < first + count; entry++)
            {
                reader.ReadEntry(entry, out int shape, out Vector3 position, out _, out _);
                if ((uint)shape >= (uint)shapeCount || position == Vector3.Zero)
                    continue;

                shapeList.Add(shape);
                deltaList.Add(position);
            }
        }

        offsets[vertexCount] = shapeList.Count;
        shapes = [.. shapeList];
        deltas = [.. deltaList];
    }

    /// <summary>Writes the world-space positions of one mesh at the current pose. It does not allocate.</summary>
    private static void Deform(DeformedMeshSource source, float[] positions)
    {
        bool morphed = ReadBlendshapeWeights(source);
        if (source.IsSkinned)
            UpdatePalette(source);

        Matrix4x4 world = source.Component.Transform.WorldMatrix;
        Vector3[] basePositions = source.BasePositions;
        int[] blendshapeOffsets = source.BlendshapeOffsets;
        int[] blendshapeShapes = source.BlendshapeShapes;
        Vector3[] blendshapeDeltas = source.BlendshapeDeltas;
        float[] blendshapeWeights = source.BlendshapeWeights;
        int[] influenceOffsets = source.InfluenceOffsets;
        int[] influenceBones = source.InfluenceBones;
        float[] influenceWeights = source.InfluenceWeights;
        Matrix4x4[] palette = source.Palette;
        bool skinned = source.IsSkinned;

        int output = source.VertexStart * 3;
        for (int vertex = 0; vertex < basePositions.Length; vertex++)
        {
            Vector3 position = basePositions[vertex];
            if (morphed)
            {
                for (int entry = blendshapeOffsets[vertex]; entry < blendshapeOffsets[vertex + 1]; entry++)
                {
                    float weight = blendshapeWeights[blendshapeShapes[entry]];
                    if (weight != 0.0f)
                        position += blendshapeDeltas[entry] * weight;
                }
            }

            Vector3 worldPosition;
            if (skinned && influenceOffsets[vertex] != influenceOffsets[vertex + 1])
            {
                worldPosition = Vector3.Zero;
                for (int influence = influenceOffsets[vertex]; influence < influenceOffsets[vertex + 1]; influence++)
                    worldPosition += Vector3.Transform(position, palette[influenceBones[influence]]) * influenceWeights[influence];
            }
            else
            {
                worldPosition = Vector3.Transform(position, world);
            }

            positions[output++] = worldPosition.X;
            positions[output++] = worldPosition.Y;
            positions[output++] = worldPosition.Z;
        }
    }

    /// <summary>Reads the current blendshape weights from the renderer. Returns true when any weight is not zero.</summary>
    private static bool ReadBlendshapeWeights(DeformedMeshSource source)
    {
        if (!source.HasBlendshapes)
            return false;

        float[] weights = source.BlendshapeWeights;
        bool any = false;
        for (int shape = 0; shape < weights.Length; shape++)
        {
            float weight = source.Renderer.GetBlendshapeWeightNormalized((uint)shape);
            weights[shape] = weight;
            any |= weight != 0.0f;
        }

        return any;
    }

    /// <summary>
    /// Writes one palette matrix for each utilized bone: BindRootMatrix × inverse bind matrix × bone render matrix.
    /// This is the same order as the CPU skinned bounds of <see cref="RenderableMesh"/>. The bone table of a runtime
    /// mesh already holds the bones of this content instance, so no extra bone remap is necessary.
    /// </summary>
    private static void UpdatePalette(DeformedMeshSource source)
    {
        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] bones = source.Mesh.UtilizedBones;
        Matrix4x4 bindRootMatrix = source.Mesh.BindRootMatrix ?? Matrix4x4.Identity;
        Matrix4x4[] palette = source.Palette;
        for (int bone = 0; bone < palette.Length; bone++)
        {
            (TransformBase transform, Matrix4x4 inverseBind) = bones[bone];
            palette[bone] = transform is null
                ? Matrix4x4.Identity
                : bindRootMatrix * inverseBind * transform.RenderMatrix;
        }
    }
}
