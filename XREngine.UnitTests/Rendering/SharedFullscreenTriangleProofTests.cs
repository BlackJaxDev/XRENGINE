using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SharedFullscreenTriangleProofTests
{
    [Test]
    public void CanonicalCoverageRequiresTheLiveCacheOwner()
    {
        using SharedRenderHelperGeometry.Lease lease =
            SharedRenderHelperGeometry.AcquireFullscreen(useTriangle: true);
        SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(lease.Mesh).ShouldBeTrue();

        XRMesh copy = XRMesh.CreateTriangles(
            new Vector3(-1, -1, 0),
            new Vector3(3, -1, 0),
            new Vector3(-1, 3, 0));
        try
        {
            SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(copy).ShouldBeFalse();
        }
        finally
        {
            copy.Destroy(now: true);
        }
    }

    [Test]
    public void GeometryMutationIsRejectedBeforeAnAttemptCapturesTheMesh()
    {
        XRMesh triangle = XRMesh.CreateTriangles(
            new Vector3(-1, -1, 0),
            new Vector3(3, -1, 0),
            new Vector3(-1, 3, 0));
        try
        {
            long canonicalRevision = triangle.GeometryRevision;
            SharedRenderHelperGeometry.MatchesFullscreenTriangleContent(
                triangle, canonicalRevision).ShouldBeTrue();

            triangle.Triangles![0].ReadFromString("0 2 1");
            SharedRenderHelperGeometry.MatchesFullscreenTriangleContent(
                triangle, canonicalRevision).ShouldBeFalse();
            triangle.Triangles[0].ReadFromString("0 1 2");

            triangle.SetPosition(0, new Vector3(-0.5f, -1, 0));
            SharedRenderHelperGeometry.MatchesFullscreenTriangleContent(
                triangle, canonicalRevision).ShouldBeFalse();
        }
        finally
        {
            triangle.Destroy(now: true);
        }
    }
}
