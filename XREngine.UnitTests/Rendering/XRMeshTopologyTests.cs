using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class XRMeshTopologyTests
{
    [Test]
    public void CreateTriangles_ExposesVerticesInTheRemappedIndexSpace()
    {
        Vector3[] authoredPositions =
        [
            new(0.0f, 0.0f, 0.0f),
            new(1.0f, 0.0f, 0.0f),
            new(0.0f, 1.0f, 0.0f),
            new(0.0f, 1.0f, 0.0f),
            new(1.0f, 0.0f, 0.0f),
            new(1.0f, 1.0f, 0.0f),
        ];
        XRMesh mesh = XRMesh.CreateTriangles(authoredPositions);
        int[] indices = mesh.GetIndices().ShouldNotBeNull();

        mesh.Vertices.Length.ShouldBe(4);
        indices.Length.ShouldBe(authoredPositions.Length);

        Vector3[] reconstructedPositions = new Vector3[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i].ShouldBeInRange(0, mesh.Vertices.Length - 1);
            reconstructedPositions[i] = mesh.Vertices[indices[i]].Position;
        }

        reconstructedPositions.ShouldBe(authoredPositions);
    }
}
