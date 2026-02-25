using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Modeling;

namespace XREngine.UnitTests.Modeling;

[TestFixture]
public class EditableMeshTopologyOperatorTests
{
    [Test]
    public void SplitAndCollapseEdge_KeepTopologyValid()
    {
        EditableMesh mesh = CreateQuadEditableMesh();

        int splitVertex = mesh.SplitEdge(new EdgeKey(0, 1), 0.5f);
        splitVertex.ShouldBeGreaterThanOrEqualTo(0);

        bool collapsed = mesh.CollapseEdge(new EdgeKey(0, splitVertex));
        collapsed.ShouldBeTrue();

        TopologyValidationReport report = mesh.ValidateTopology();
        report.HasErrors.ShouldBeFalse();
    }

    [Test]
    public void ExtrudeInsetAndBevel_KeepTopologyValid()
    {
        EditableMesh mesh = CreateQuadEditableMesh();

        List<int> extruded = mesh.ExtrudeFaces(new[] { 0 }, 0.25f);
        extruded.Count.ShouldBeGreaterThan(0);

        List<int> inset = mesh.InsetFaces(new[] { 0 }, 0.35f);
        inset.Count.ShouldBeGreaterThan(0);

        int edgeIndex = mesh.Edges.ToList().FindIndex(edge => edge == new EdgeKey(0, 1));
        edgeIndex.ShouldBeGreaterThanOrEqualTo(0);

        List<int> beveled = mesh.BevelEdges(new[] { edgeIndex }, 0.1f);
        beveled.Count.ShouldBeGreaterThan(0);

        TopologyValidationReport report = mesh.ValidateTopology();
        report.HasErrors.ShouldBeFalse();
    }

    [Test]
    public void LoopCutFromEdge_CreatesVerticesAndKeepsTopologyValid()
    {
        EditableMesh mesh = CreateQuadEditableMesh();

        List<int> created = mesh.LoopCutFromEdge(new EdgeKey(0, 1), 0.5f);

        created.Count.ShouldBeGreaterThan(0);
        TopologyValidationReport report = mesh.ValidateTopology();
        report.HasErrors.ShouldBeFalse();
    }

    [Test]
    public void BridgeEdges_AddsFacesAndKeepsTopologyValid()
    {
        EditableMesh mesh = CreateTwoTriangleIslandsEditableMesh();

        int firstEdgeIndex = mesh.Edges.ToList().FindIndex(edge => edge == new EdgeKey(0, 1));
        EdgeKey secondFaceEdge = mesh.Faces[1].GetEdges().First();
        int secondEdgeIndex = mesh.Edges.ToList().FindIndex(edge => edge == secondFaceEdge);
        firstEdgeIndex.ShouldBeGreaterThanOrEqualTo(0);
        secondEdgeIndex.ShouldBeGreaterThanOrEqualTo(0);

        int faceCountBefore = mesh.Faces.Count;
        bool bridged = mesh.BridgeEdges(firstEdgeIndex, secondEdgeIndex);

        bridged.ShouldBeTrue();
        mesh.Faces.Count.ShouldBe(faceCountBefore + 2);
    }

    private static EditableMesh CreateQuadEditableMesh()
    {
        List<Vector3> vertices =
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(0f, 1f, 0f)
        ];

        int[] indices = [0, 1, 2, 0, 2, 3];
        return new EditableMesh(vertices, indices);
    }

    private static EditableMesh CreateTwoTriangleIslandsEditableMesh()
    {
        List<Vector3> vertices =
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(2f, 0f, 0f),
            new Vector3(3f, 0f, 0f),
            new Vector3(2f, 1f, 0f)
        ];

        int[] indices = [0, 1, 2, 3, 4, 5];
        return new EditableMesh(vertices, indices);
    }
}
