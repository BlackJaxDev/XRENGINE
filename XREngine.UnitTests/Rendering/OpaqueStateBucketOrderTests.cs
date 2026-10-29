using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpaqueStateBucketOrderTests
{
    private const int Pass = (int)EDefaultRenderPass.OpaqueDeferred;

    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousShaderServices;

    [Test]
    public void StateBuckets_InsertedAgainstIdentityOrder_DrawInInsertionOrder()
    {
        List<XRMeshRenderer> renderers = [];
        for (int i = 0; i < 8; i++)
            renderers.Add(new XRMeshRenderer(null, new XRMaterial()));

        // A sort by the identity keys draws the buckets in key order, and these keys change from process to
        // process. The first call adds the buckets in the opposite order of the keys. The second call adds the
        // buckets in the order of the keys.
        XRMeshRenderer[] descending = [.. renderers.OrderByDescending(ResolveBucket)];
        XRMeshRenderer[] ascending = [.. renderers.OrderBy(ResolveBucket)];

        PublishRenderers([.. descending.Select(renderer => (renderer, 2.0f))]).ShouldBe(descending);
        PublishRenderers([.. ascending.Select(renderer => (renderer, 2.0f))]).ShouldBe(ascending);
    }

    [Test]
    public void StateBuckets_SharedBucket_GroupsMembersNearToFar()
    {
        XRMeshRenderer shared = new(null, new XRMaterial());
        XRMeshRenderer other = new(null, new XRMaterial());

        PublishRenderers([(shared, 20.0f), (other, 2.0f), (shared, 5.0f)]).ShouldBe([shared, shared, other]);
        PublishDistances([(shared, 20.0f), (other, 2.0f), (shared, 5.0f)]).ShouldBe([25.0f, 400.0f, 4.0f]);

        PublishRenderers([(other, 2.0f), (shared, 20.0f), (shared, 5.0f)]).ShouldBe([other, shared, shared]);
        PublishDistances([(other, 2.0f), (shared, 20.0f), (shared, 5.0f)]).ShouldBe([4.0f, 25.0f, 400.0f]);
    }

    [Test]
    public void StateBuckets_SharedMaterialsInsertedInterleaved_StayTogether()
    {
        // Two materials with two mesh renderers each. The buckets of one material stay together, so the renderer does
        // not upload a material again between them. The materials, and the buckets in a material, draw in the order of
        // their first insertion.
        XRMaterial materialA = new();
        XRMaterial materialB = new();
        XRMeshRenderer a1 = new(null, materialA);
        XRMeshRenderer a2 = new(null, materialA);
        XRMeshRenderer b1 = new(null, materialB);
        XRMeshRenderer b2 = new(null, materialB);

        PublishRenderers([(a1, 2.0f), (b1, 2.0f), (a2, 2.0f), (b2, 2.0f)]).ShouldBe([a1, a2, b1, b2]);
        PublishRenderers([(b2, 2.0f), (a1, 2.0f), (b1, 2.0f), (a2, 2.0f)]).ShouldBe([b2, b1, a1, a2]);

        // The members of each bucket still draw near to far.
        PublishDistances([(a1, 20.0f), (b1, 2.0f), (a1, 5.0f), (a2, 3.0f), (b1, 1.0f)]).ShouldBe([25.0f, 400.0f, 9.0f, 1.0f, 4.0f]);
    }

    private static OpaqueStateBucketRenderCommandSorter.OpaqueStateBucketKey ResolveBucket(XRMeshRenderer renderer)
        => OpaqueStateBucketRenderCommandSorter.ResolveStateBucket(CreateCommand(renderer, 2.0f));

    private static XRMeshRenderer?[] PublishRenderers((XRMeshRenderer Renderer, float MinX)[] inserted)
        => [.. Publish(inserted).Select(command => ((IRenderCommandMesh)command).Mesh)];

    private static float[] PublishDistances((XRMeshRenderer Renderer, float MinX)[] inserted)
        => [.. Publish(inserted).Select(command => ((RenderCommand3D)command).RenderDistance)];

    /// <summary>
    /// Adds one mesh command for each item in the given order and returns the published order of the pass. The
    /// camera is at the origin, so the sort distance of a command is the square of its minimum x value.
    /// </summary>
    private static RenderCommand[] Publish((XRMeshRenderer Renderer, float MinX)[] inserted)
    {
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [Pass] = new OpaqueStateBucketRenderCommandSorter()
        });
        XRCamera camera = new(new XREngine.Scene.Transforms.Transform(Vector3.Zero));
        foreach ((XRMeshRenderer renderer, float minX) in inserted)
        {
            RenderCommandMesh3D command = CreateCommand(renderer, minX);
            command.CollectedForRender(camera);
            commands.AddCPU(command, camera);
        }

        commands.SwapBuffers();
        commands.TryGetRenderingPassCommands(Pass, out IReadOnlyCollection<RenderCommand>? published).ShouldBeTrue();
        published.ShouldNotBeNull();
        return [.. published];
    }

    private static RenderCommandMesh3D CreateCommand(XRMeshRenderer renderer, float minX)
        => new(Pass)
        {
            Mesh = renderer,
            WorldMatrix = Matrix4x4.Identity,
            WorldCullingVolumeOverride = new AABB(
                new Vector3(minX, -1.0f, -1.0f),
                new Vector3(minX + 1.0f, 1.0f, 1.0f)),
        };
}
