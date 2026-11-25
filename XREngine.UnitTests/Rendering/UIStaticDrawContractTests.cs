using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIStaticDrawContractTests
{
    [Test]
    public void RetainedContractRejectsChangedGeometryAndReplacementMesh()
    {
        XRMesh mesh = XRMesh.Create(VertexQuad.PosZ(1, true, 0, false));
        XRMesh replacement = XRMesh.Create(VertexQuad.PosZ(1, true, 0, false));
        XRMeshRenderer renderer = new(mesh, null);
        try
        {
            UIStaticDrawContract contract = new(1,
                new(new(10, 1), EUICompositionLayerKind.StaticChrome), 2, 3, 4, 1,
                UIIndexedDrawIntent.Capture(renderer, EPrimitiveType.Triangles, 2))
                { CapturedMesh = mesh };
            contract.MatchesIntent(renderer, 2).ShouldBeTrue();
            contract.MatchesIntent(renderer, 1).ShouldBeFalse();
            mesh.SetPosition(0, new Vector3(2));
            contract.MatchesIntent(renderer, 2).ShouldBeFalse();
            renderer.Mesh = replacement;
            // Equal revision values cannot make a different mesh the retained owner.
            contract = contract with { Intent = contract.Intent with { GeometryRevision = replacement.GeometryRevision } };
            contract.MatchesIntent(renderer, 2).ShouldBeFalse();
        }
        finally
        {
            renderer.Destroy(true);
            mesh.Destroy(true);
            replacement.Destroy(true);
        }
    }
}
