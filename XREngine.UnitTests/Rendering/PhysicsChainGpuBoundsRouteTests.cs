using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PhysicsChainGpuBoundsRouteTests
{
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
    public void RendererRoutes_StayFrozenUntilCommandSwapAfterCompactionAndSlotReuse()
    {
        GPUScene scene = new();
        XRMesh mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        XRMaterial material = new();
        XRMeshRenderer firstRenderer = new(mesh, material);
        XRMeshRenderer secondRenderer = new(mesh, material);
        XRMeshRenderer thirdRenderer = new(mesh, material);
        RenderInfo3D first = CreateRenderInfo(firstRenderer);
        RenderInfo3D second = CreateRenderInfo(secondRenderer);
        RenderInfo3D third = CreateRenderInfo(thirdRenderer);
        GpuSceneRendererCommandIndexSnapshot routes = new();
        List<uint> indices = [];

        scene.Add(first);
        scene.Add(second);
        scene.SwapCommandBuffers();
        scene.CaptureRendererCommandIndices(routes);
        routes.TryGetCommandIndices(firstRenderer, indices).ShouldBeTrue();
        indices.ShouldBe([0u]);
        routes.TryGetCommandIndices(secondRenderer, indices).ShouldBeTrue();
        indices.ShouldBe([1u]);
        long publishedGeneration = routes.PublicationGeneration;

        scene.Remove(first);
        scene.TryGetCommandIndicesForRenderer(secondRenderer, indices).ShouldBeTrue();
        indices.ShouldBe([0u]);
        scene.CaptureRendererCommandIndices(routes);
        routes.PublicationGeneration.ShouldBe(publishedGeneration);
        routes.TryGetCommandIndices(secondRenderer, indices).ShouldBeTrue();
        indices.ShouldBe([1u]);

        scene.SwapCommandBuffers();
        scene.CaptureRendererCommandIndices(routes);
        routes.PublicationGeneration.ShouldBeGreaterThan(publishedGeneration);
        routes.TryGetCommandIndices(firstRenderer, indices).ShouldBeFalse();
        routes.TryGetCommandIndices(secondRenderer, indices).ShouldBeTrue();
        indices.ShouldBe([0u]);

        scene.Add(third);
        scene.CaptureRendererCommandIndices(routes);
        routes.TryGetCommandIndices(thirdRenderer, indices).ShouldBeFalse();
        scene.SwapCommandBuffers();
        scene.CaptureRendererCommandIndices(routes);
        routes.TryGetCommandIndices(thirdRenderer, indices).ShouldBeTrue();
        indices.ShouldBe([1u]);
    }

    [Test]
    public void OutputPageTokens_RejectInvalidAndStaleIdentitiesWithoutGpuWork()
    {
        GPUPhysicsChainDispatcher dispatcher = new();
        PhysicsChainGpuOutputPageToken invalid = new(0u, 1u, 1u);
        PhysicsChainGpuOutputPageToken stale = new(1u, 1u, 1u);
        invalid.IsValid.ShouldBeFalse();
        stale.IsValid.ShouldBeTrue();
        dispatcher.TryRetainOutputPage(invalid, out _).ShouldBeFalse();
        dispatcher.TryValidateOutputPage(stale, out _).ShouldBeFalse();
        dispatcher.ReleaseOutputPage(stale);
        dispatcher.TryAcquirePublishedOutputPage(out _).ShouldBeFalse();
        dispatcher.GetOutputPageStatesSnapshot().ShouldAllBe(page => page.RetainCount == 0);
    }

    private static RenderInfo3D CreateRenderInfo(XRMeshRenderer renderer)
    {
        RenderCommandMesh3D command = new(0) { Mesh = renderer };
        TestRenderable owner = new();
        RenderInfo3D info = RenderInfo3D.New(owner, command);
        owner.RenderedObjects = [info];
        return info;
    }

    private sealed class TestRenderable : IRenderable
    {
        public RenderInfo[] RenderedObjects { get; set; } = [];
    }
}
