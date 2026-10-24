using System;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIFrozenWorldCollectionTests
{
    [Test]
    public void IndexedIntentCapturesTheOwnedMeshStreamAndRejectsMissingTopology()
    {
        XRMesh mesh = XRMesh.Create(VertexQuad.PosZ(1, true, 0, false));
        XRMeshRenderer renderer = new(mesh, null);
        try
        {
            UIIndexedDrawIntent intent = UIIndexedDrawIntent.Capture(
                renderer, EPrimitiveType.Triangles, instances: 2);

            intent.Renderer.ShouldBeSameAs(renderer);
            intent.GeometryRevision.ShouldBe(mesh.GeometryRevision);
            intent.IndexCount.ShouldBe(checked((uint)(mesh.Triangles!.Count * 3)));
            intent.ElementType.ShouldBe(IndexSize.TwoBytes);
            intent.Instances.ShouldBe(2u);
            intent.IndexOffset.ShouldBe((nint)0);
            Should.Throw<InvalidOperationException>(() => UIIndexedDrawIntent.Capture(
                renderer, EPrimitiveType.Lines, instances: 2));
        }
        finally
        {
            renderer.Destroy(true);
            mesh.Destroy(true);
        }
    }

    [Test]
    public void FrozenChildCullingUsesTheActualCollectionVolume()
    {
        AABB view = new(new Vector3(-1), new Vector3(1));
        Box inside = new AABB(new Vector3(-0.5f), new Vector3(0.5f))
            .ToBox(Matrix4x4.Identity);
        Box outside = new AABB(new Vector3(2), new Vector3(3))
            .ToBox(Matrix4x4.Identity);

        RenderInfo3D.FrozenWorldVolumeIntersects(view, inside, containsOnly: false)
            .ShouldBeTrue();
        RenderInfo3D.FrozenWorldVolumeIntersects(view, inside, containsOnly: true)
            .ShouldBeTrue();
        RenderInfo3D.FrozenWorldVolumeIntersects(view, outside, containsOnly: false)
            .ShouldBeFalse();
    }
}
