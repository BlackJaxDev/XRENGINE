using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Copies a supported static desktop mesh into an independent browser payload.</summary>
public static class BrowserAssetAdapter
{
    public static BrowserMeshData FromXRMesh(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.HasSkinning || mesh.HasBlendshapes)
            throw new NotSupportedException("Browser mesh export does not support skinning or blendshapes.");
        foreach (Vertex vertex in mesh.Vertices)
            if (vertex.Weights is { Count: > 0 } || vertex.Blendshapes is { Count: > 0 })
                throw new NotSupportedException("Browser mesh export does not support per-vertex skinning or blendshapes.");
        return CopyGeometry(mesh);
    }

    /// <summary>Copies resident geometry only; callers exporting deformation must separately preserve its canonical animation data.</summary>
    public static BrowserMeshData CopyGeometry(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Type != EPrimitiveType.Triangles || mesh.Triangles is not { Count: > 0 } triangles)
            throw new NotSupportedException("Browser mesh export requires indexed triangles.");
        if (mesh.VertexCount < 3 || mesh.VertexCount > 16 * 1024 * 1024 / 5)
            throw new NotSupportedException("Browser mesh vertex count is outside the supported payload range.");
        if (mesh.ColorCount != 0 || mesh.TexCoordCount != 1)
            throw new NotSupportedException("Browser mesh export requires exactly UV set zero and no vertex colors.");
        if (mesh.Interleaved)
        {
            if (mesh.InterleavedVertexBuffer is null || mesh.TexCoordOffset is null)
                throw new NotSupportedException("Browser mesh export requires resident interleaved position and UV data.");
        }
        else if (mesh.PositionsBuffer is null || mesh.TexCoordBuffers is not { Length: > 0 } uvBuffers || uvBuffers[0] is null)
            throw new NotSupportedException("Browser mesh export requires resident position and UV buffers.");

        long geometryRevision = mesh.GeometryRevision;
        float[] vertices = new float[checked(mesh.VertexCount * 5)];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            Vector3 position = mesh.GetPosition((uint)i);
            Vector2 uv = mesh.GetTexCoord((uint)i, 0);
            int offset = i * 5;
            vertices[offset] = position.X;
            vertices[offset + 1] = position.Y;
            vertices[offset + 2] = position.Z;
            vertices[offset + 3] = uv.X;
            vertices[offset + 4] = uv.Y;
        }

        if (triangles.Count > 16 * 1024 * 1024 / 3)
            throw new NotSupportedException("Browser mesh index count exceeds the supported payload range.");
        uint[] indices = new uint[checked(triangles.Count * 3)];
        for (int i = 0; i < triangles.Count; i++)
        {
            int a = triangles[i].Point0, b = triangles[i].Point1, c = triangles[i].Point2;
            if (a < 0 || b < 0 || c < 0 || a >= mesh.VertexCount || b >= mesh.VertexCount || c >= mesh.VertexCount)
                throw new InvalidOperationException("Mesh triangle index is outside the vertex range.");
            indices[i * 3] = (uint)a;
            indices[i * 3 + 1] = (uint)b;
            indices[i * 3 + 2] = (uint)c;
        }
        if (mesh.GeometryRevision != geometryRevision)
            throw new InvalidOperationException("Mesh geometry changed during browser snapshot export.");
        return new BrowserMeshData(vertices, indices);
    }

    /// <summary>Requires an authored recipe for the exact source material; no shader is translated implicitly.</summary>
    public static BrowserMaterialData FromXRMaterial(XRMaterial material, BrowserMaterialRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(recipe);
        if (!ReferenceEquals(material, recipe.Source))
            throw new ArgumentException("The browser recipe belongs to a different XRMaterial.", nameof(recipe));
        return recipe.Data;
    }
}
