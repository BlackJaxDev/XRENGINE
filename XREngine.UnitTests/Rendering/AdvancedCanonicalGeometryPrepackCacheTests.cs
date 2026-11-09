using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// A worker-packed canonical geometry entry must hold the same bytes that the publication packs
/// on the swap thread, so copying it into the arenas gives the same rendering.
/// </summary>
[TestFixture]
public sealed class AdvancedCanonicalGeometryPrepackCacheTests
{
    [Test]
    public void PrepackedEntry_MatchesInlinePack()
    {
        XRMesh mesh = CreateMesh();
        AdvancedCanonicalGeometryPrepackCache cache = new();

        cache.TryPackImmediately(mesh, out AdvancedCanonicalGeometryPrepackCache.Entry entry).ShouldBeTrue();

        entry.VertexCount.ShouldBe(mesh.VertexCount);
        for (uint vertex = 0; vertex < (uint)mesh.VertexCount; ++vertex)
            entry.Vertices[vertex].ShouldBe(AdvancedPackedVertexCodec.Pack(mesh, vertex, vertex));

        List<IndexTriangle> triangles = mesh.Triangles!;
        entry.IndexCount.ShouldBe(triangles.Count * 3);
        for (int triangle = 0; triangle < triangles.Count; ++triangle)
        {
            entry.Indices[triangle * 3].ShouldBe((uint)triangles[triangle].Point0);
            entry.Indices[triangle * 3 + 1].ShouldBe((uint)triangles[triangle].Point1);
            entry.Indices[triangle * 3 + 2].ShouldBe((uint)triangles[triangle].Point2);
        }
    }

    [Test]
    public void ReadyEntry_IsBoundToItsGeometryRevision()
    {
        XRMesh mesh = CreateMesh();
        AdvancedCanonicalGeometryPrepackCache cache = new();
        cache.TryPackImmediately(mesh, out _).ShouldBeTrue();
        long revision = mesh.GeometryRevision;

        cache.TryGetReady(mesh, revision, out _).ShouldBeTrue();
        cache.TryGetReady(mesh, revision + 1, out _).ShouldBeFalse();

        mesh.SetPosition(0u, new Vector3(5.0f, 6.0f, 7.0f));
        mesh.GeometryRevision.ShouldNotBe(revision);
        cache.TryGetReady(mesh, mesh.GeometryRevision, out _).ShouldBeFalse();
    }

    [Test]
    public void ReleasedEntry_IsNoLongerReady()
    {
        XRMesh mesh = CreateMesh();
        AdvancedCanonicalGeometryPrepackCache cache = new();
        cache.TryPackImmediately(mesh, out _).ShouldBeTrue();

        cache.Release(mesh);

        cache.TryGetReady(mesh, mesh.GeometryRevision, out _).ShouldBeFalse();
    }

    private static XRMesh CreateMesh()
    {
        List<Vertex> vertices = [];
        for (int index = 0; index < 6; ++index)
        {
            vertices.Add(new Vertex(new Vector3(index, index * 0.5f, -index))
            {
                Normal = Vector3.Normalize(new Vector3(0.1f * index, 1.0f, 0.2f)),
                Tangent = Vector3.UnitX,
                BitangentSign = index % 2 == 0 ? 1.0f : -1.0f,
                TextureCoordinateSets = [new Vector2(index * 0.25f, 1.0f - index * 0.125f)],
            });
        }
        return new XRMesh(vertices, [0, 1, 2, 3, 4, 5, 0, 2, 4]);
    }
}
