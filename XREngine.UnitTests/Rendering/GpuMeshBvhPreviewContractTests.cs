using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using SimpleScene.Util.ssBVH;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Editor;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuMeshBvhPreviewContractTests
{
    [Test]
    public void MathBvhGpuTests_ReuseCpuInputsAndDispatchGpuQueries()
    {
        string sharedSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.cs");
        string queryVolumeSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhQueryVolume.cs");
        string cpuSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.Cpu.cs");
        string gpuSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.Gpu.cs");
        string debugSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.Debug.cs");
        string sceneQueryShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/math_bvh_scene_query.comp");
        string meshQueryShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/math_bvh_mesh_query.comp");

        sharedSource.ShouldContain("private const int SceneMovesPerUpdate = 3;");
        sharedSource.ShouldContain("private static AABB AnimateSceneBounds(");
        sharedSource.ShouldContain("private static void CalculateMeshBruteForce(");
        queryVolumeSource.ShouldContain("public void Update(MathBvhQueryShape shape, float time)");
        queryVolumeSource.ShouldContain("MathBvhQueryShape.Sphere => Sphere.ContainsAABB(box, tolerance)");
        queryVolumeSource.ShouldContain("MathBvhQueryShape.Frustum => ClassifyFrustumAabb(_frustum, box, tolerance)");
        queryVolumeSource.ShouldContain("Mirrors the GPU frustum/AABB SAT classifier operation-for-operation");
        queryVolumeSource.ShouldContain("MathBvhQueryShape.Raycast => SegmentIntersectsAabb(");

        cpuSource.ShouldContain("for (int move = 0; move < SceneMovesPerUpdate; move++)");
        cpuSource.ShouldContain("AnimateSceneBounds(item.BaseBounds, time, index)");
        cpuSource.ShouldContain("_queryVolume.Update(QueryShape, time);");
        cpuSource.ShouldContain("_meshQuerySegment = _queryVolume.Raycast;");
        cpuSource.ShouldContain("CalculateMeshBruteForce(triangles, _meshQuerySegment");

        gpuSource.ShouldContain("for (int move = 0; move < SceneMovesPerUpdate; move++)");
        gpuSource.ShouldContain("AnimateSceneBounds(_gpuSceneBaseBounds[movedIndex], time, movedIndex)");
        gpuSource.ShouldContain("_queryVolume.Update(QueryShape, time);");
        gpuSource.ShouldContain("_meshQuerySegment = _queryVolume.Raycast;");
        gpuSource.ShouldContain("DispatchGpuSceneQuery(queryId)");
        gpuSource.ShouldContain("DispatchGpuMeshQuery(bvh, queryId)");
        gpuSource.ShouldContain("Interlocked.Increment(ref _queryOperationCount);");
        gpuSource.ShouldContain("SetValidationState(ready: true, passed, hitCount);");

        sceneQueryShader.ShouldContain("SceneAabb primitive = Aabbs[objectId];");
        sceneQueryShader.ShouldContain("uint nodeClass = ClassifyAabb(");
        sceneQueryShader.ShouldContain("NodeClasses[nodeIndex] = nodeClass;");
        sceneQueryShader.ShouldContain("uint ClassifySphereAabb(");
        sceneQueryShader.ShouldContain("uint ClassifyFrustumAabb(");
        sceneQueryShader.ShouldContain("PrimitiveClasses[objectId] = primitiveClass;");
        meshQueryShader.ShouldContain("uint nodeClass = ClassifyQueryAabb(");
        meshQueryShader.ShouldContain("ClassifyQueryTriangle(");
        meshQueryShader.ShouldContain("PrimitiveClasses[sourceFace] = packedClasses;");

        debugSource.ShouldContain("GpuBvhDebugNodeClassMode.ClassifiedOnly");
        debugSource.ShouldContain("BvhDebugOverlayLayer.Highlight");
        debugSource.ShouldContain("_cpuSceneTree.DebugRenderNodes(null, _sceneTreeBaseDebugRenderer);");
        debugSource.ShouldContain("_cpuSceneTree.DebugRenderNodes(_queryVolume, _sceneTreeQueryDebugRenderer);");
        debugSource.ShouldContain("RenderQueryVolume(");
        debugSource.ShouldContain("RenderLocalLine(_meshQuerySegment.Start, _meshQuerySegment.End");
    }

    [Test]
    public void MathBvhDebugControls_ExposeSharedVisibilityPaletteAndGpuLimits()
    {
        string settingsSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.DebugSettings.cs");
        string debugSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.Debug.cs");
        string rigSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/UnitTestingWorld.Math.Bvh.cs");
        string customUiSource = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Debug/CustomUIComponent.cs");
        string customUiEditorSource = ReadWorkspaceFile("XREngine.Editor/ComponentEditors/CustomUIComponentEditor.cs");

        settingsSource.ShouldContain("public MathBvhDebugNodeVisibility NodeVisibility");
        settingsSource.ShouldContain("public MathBvhQueryShape QueryShape");
        settingsSource.ShouldContain("public bool QueryPoints");
        settingsSource.ShouldContain("public bool QueryLines");
        settingsSource.ShouldContain("public bool QueryTriangles");
        settingsSource.ShouldContain("MathBvhDebugNodeVisibility.LeavesOnly => isLeaf");
        settingsSource.ShouldContain("MathBvhDebugNodeVisibility.InternalOnly => !isLeaf");
        settingsSource.ShouldContain("public bool RenderBaseNodes");
        settingsSource.ShouldContain("public bool RenderVisitedNodes");
        settingsSource.ShouldContain("public bool RenderIntersectedGeometry");
        settingsSource.ShouldContain("public bool RenderSourceGeometry");
        settingsSource.ShouldContain("public ColorF4 LeafNodeColor");
        settingsSource.ShouldContain("public ColorF4 InternalNodeColor");
        settingsSource.ShouldContain("public ColorF4 VisitedNodeColor");
        settingsSource.ShouldContain("public ColorF4 DisjointGeometryColor");
        settingsSource.ShouldContain("public ColorF4 IntersectedGeometryColor");
        settingsSource.ShouldContain("public ColorF4 ContainedGeometryColor");
        settingsSource.ShouldContain("public int MaxDebugNodes");
        settingsSource.ShouldContain("World-space width of CPU and GPU base-node lines.");
        settingsSource.ShouldContain("World-space width of CPU and GPU visited-node highlight lines.");
        settingsSource.ShouldContain("customUi.AddEnumField(");

        debugSource.ShouldContain("uint showFilter = GetGpuNodeShowFilter();");
        debugSource.ShouldContain("if (RenderBaseNodes)");
        debugSource.ShouldContain("if (RenderVisitedNodes");
        debugSource.ShouldContain("ToVector4(LeafNodeColor)");
        debugSource.ShouldContain("ToVector4(InternalNodeColor)");
        debugSource.ShouldContain("ToVector4(VisitedNodeColor),\n                    0b11u)");
        debugSource.ShouldContain("TryGetGeometryColor(");
        debugSource.ShouldContain("RenderLocalOverlayBox(");
        debugSource.ShouldContain("BvhDebugOverlayLayer.Base");
        debugSource.ShouldContain("BvhDebugOverlayLayer.Highlight");
        rigSource.ShouldContain("XRMaterial.CreateLitColorMaterial(");
        rigSource.ShouldNotContain("XRMaterial.CreateUnlitColorMaterialForward(");
        rigSource.ShouldContain("new Vertex(a, GridNormal(a.X, a.Z))");
        rigSource.ShouldContain("AddMathBvhMeshLight(rigNode);");
        rigSource.ShouldContain("light.CastsShadows = false;");
        rigSource.ShouldContain("if (controller?.IsSpawningBenchmarkInstances != true)");
        rigSource.ShouldContain("test.RegisterDebugControls(debugControls);");
        customUiSource.ShouldContain("public CustomUIEnumField AddEnumField<TEnum>(");
        customUiEditorSource.ShouldContain("case CustomUIEnumField enumField:");
    }

    [Test]
    public void CustomUiEnumField_RoundTripsSelectedValue()
    {
        CustomUiEnumTestValue value = CustomUiEnumTestValue.All;
        var customUi = new CustomUIComponent();
        CustomUIEnumField field = customUi.AddEnumField(
            "Node Visibility",
            () => value,
            selected => value = selected);

        field.Options.ShouldBe(["All", "LeavesOnly", "InternalOnly"]);
        field.GetSelectedIndex().ShouldBe(0);

        field.SetSelectedIndex(2);

        value.ShouldBe(CustomUiEnumTestValue.InternalOnly);
        field.GetSelectedIndex().ShouldBe(2);
    }

    [TestCase(MathBvhQueryShape.Box)]
    [TestCase(MathBvhQueryShape.Sphere)]
    [TestCase(MathBvhQueryShape.Frustum)]
    public void MathBvhQueryVolume_ClassifiesContainedPartialAndDisjointBounds(
        MathBvhQueryShape shape)
    {
        var query = new MathBvhQueryVolume();
        query.Update(shape, 0.0f);

        Vector3 insideCenter = shape switch
        {
            MathBvhQueryShape.Sphere => query.Sphere.Center,
            MathBvhQueryShape.Frustum =>
                (query.Frustum.LeftBottomNear + query.Frustum.RightTopNear +
                 query.Frustum.LeftBottomFar + query.Frustum.RightTopFar) * 0.25f,
            _ => query.Box.Center,
        };
        Vector3 partialCenter = shape switch
        {
            MathBvhQueryShape.Sphere => query.Sphere.Center + Vector3.UnitX * query.Sphere.Radius,
            MathBvhQueryShape.Frustum => query.Frustum.LeftBottomNear,
            _ => query.Box.Max,
        };
        AABB queryBounds = query.GetAABB(transformed: false);
        AABB contained = AABB.FromCenterSize(insideCenter, new Vector3(0.05f));
        AABB partial = AABB.FromCenterSize(partialCenter, new Vector3(0.2f));
        AABB disjoint = AABB.FromCenterSize(queryBounds.Max + new Vector3(10.0f), Vector3.One);

        query.ContainsAABB(contained).ShouldBe(EContainment.Contains);
        query.ContainsAABB(partial).ShouldBe(EContainment.Intersects);
        query.ContainsAABB(disjoint).ShouldBe(EContainment.Disjoint);
    }

    [Test]
    public void MathBvhQueryVolume_ClassifiesSourceGeometryByContainment()
    {
        var query = new MathBvhQueryVolume();
        query.Update(MathBvhQueryShape.Box, 0.0f);
        Vector3 center = query.Box.Center;
        Vector3 outside = query.Box.Max + Vector3.One;

        query.ClassifyPoint(center).ShouldBe(EContainment.Contains);
        query.ClassifyPoint(outside).ShouldBe(EContainment.Disjoint);
        query.ClassifySegment(new Segment(center - Vector3.UnitX, center + Vector3.UnitX))
            .ShouldBe(EContainment.Contains);
        query.ClassifySegment(new Segment(center, outside))
            .ShouldBe(EContainment.Intersects);

        var containedTriangle = new Triangle(
            center + Vector3.UnitX * 0.1f,
            center + Vector3.UnitY * 0.1f,
            center + Vector3.UnitZ * 0.1f);
        var partialTriangle = new Triangle(
            center,
            outside,
            center + Vector3.UnitZ * 0.1f);
        var disjointTriangle = new Triangle(
            outside,
            outside + Vector3.UnitX,
            outside + Vector3.UnitY);

        query.ClassifyTriangle(containedTriangle).ShouldBe(EContainment.Contains);
        query.ClassifyTriangle(partialTriangle).ShouldBe(EContainment.Intersects);
        query.ClassifyTriangle(disjointTriangle).ShouldBe(EContainment.Disjoint);
    }

    [Test]
    public void MathBvhRayQuery_ClassifiesHitsAsIntersectionsNotContainment()
    {
        var query = new MathBvhQueryVolume();
        query.Update(MathBvhQueryShape.Raycast, 0.0f);
        Triangle hit = CreateIntersectingTriangle(query, MathBvhQueryShape.Raycast);
        AABB hitBounds = AABB.FromCenterSize(
            (query.Raycast.Start + query.Raycast.End) * 0.5f,
            Vector3.One);

        query.ClassifyTriangle(hit).ShouldBe(EContainment.Intersects);
        query.ContainsAABB(hitBounds).ShouldBe(EContainment.Intersects);
    }

    [TestCase(MathBvhQueryShape.Box)]
    [TestCase(MathBvhQueryShape.Sphere)]
    [TestCase(MathBvhQueryShape.Frustum)]
    [TestCase(MathBvhQueryShape.Raycast)]
    public void MathBvhMeshQuery_BvhCandidatesMatchBruteForceTriangleResults(
        MathBvhQueryShape shape)
    {
        var query = new MathBvhQueryVolume();
        query.Update(shape, 0.0f);

        Triangle intersecting = CreateIntersectingTriangle(query, shape);
        AABB queryBounds = query.GetAABB(transformed: true);
        Vector3 distantCenter = queryBounds.Max + new Vector3(10.0f);
        var distant = new Triangle(
            distantCenter + Vector3.UnitX,
            distantCenter + Vector3.UnitY,
            distantCenter + Vector3.UnitZ);
        var triangles = new List<Triangle> { intersecting, distant };
        var tree = new BVH<Triangle>(new XREngine.Rendering.TriangleAdapter(), triangles);

        List<BVHNode<Triangle>> candidates = tree.Traverse(
            bounds => query.ContainsAABB(bounds) != EContainment.Disjoint);
        int acceleratedHits = 0;
        for (int nodeIndex = 0; nodeIndex < candidates.Count; nodeIndex++)
        {
            if (candidates[nodeIndex].gobjects is not { } nodeTriangles)
                continue;

            for (int triangleIndex = 0; triangleIndex < nodeTriangles.Count; triangleIndex++)
                if (query.IntersectsTriangle(nodeTriangles[triangleIndex]))
                    acceleratedHits++;
        }

        int bruteForceHits = 0;
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            if (query.IntersectsTriangle(triangles[triangleIndex]))
                bruteForceHits++;

        bruteForceHits.ShouldBe(1);
        acceleratedHits.ShouldBe(bruteForceHits);
    }

    [Test]
    public void MathBvhSphereQuery_RejectsAnInfiniteLineHitOutsideTheFiniteSegment()
    {
        var query = new MathBvhQueryVolume();
        query.Update(MathBvhQueryShape.Sphere, 0.0f);
        Vector3 direction = Vector3.UnitX;
        var outsideSegment = new Segment(
            query.Sphere.Center + direction * (query.Sphere.Radius + 2.0f),
            query.Sphere.Center + direction * (query.Sphere.Radius + 4.0f));

        query.IntersectsSegment(outsideSegment).ShouldBeFalse();
    }

    [Test]
    public void MathBvhGpuPreview_QueuesWorkloadAndDrawsInLateDebugOverlay()
    {
        string componentSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.cs");
        string debugSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.Debug.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhDebugLineRenderer.cs");
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhDebugOverlayQueue.cs");
        string overlaySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugShapes.cs");
        string engineDebugSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Debug.cs");
        string modelPreviewSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Debug/Visualize/ModelBvhPreviewComponent.cs");

        componentSource.ShouldContain("=> Interlocked.Exchange(ref _gpuWorkQueued, 1);");
        componentSource.ShouldNotContain("Engine.EnqueueRenderThreadTask(");
        componentSource.ShouldNotContain("RenderedObjects[0].IsVisible = value;");

        int passScope = debugSource.IndexOf("PushRenderGraphPassIndex(", StringComparison.Ordinal);
        int workload = debugSource.IndexOf("ExecuteQueuedGpuWorkload();", StringComparison.Ordinal);
        int debugGate = debugSource.IndexOf("if (!DebugRenderEnabled)", StringComparison.Ordinal);
        passScope.ShouldBeGreaterThanOrEqualTo(0);
        workload.ShouldBeGreaterThan(passScope);
        debugGate.ShouldBeGreaterThan(workload);
        debugSource.ShouldContain("_gpuDebugRenderer.Queue(");
        debugSource.ShouldNotContain("_gpuDebugRenderer.Render(");
        modelPreviewSource.ShouldContain("_gpuRenderer.Queue(");
        modelPreviewSource.ShouldNotContain("_gpuRenderer.Render(");

        rendererSource.ShouldContain("ConditionalWeakTable<XRRenderPipelineInstance.RenderingState, GpuBvhDebugOverlayQueue>");
        rendererSource.ShouldContain("BaseQueues = new();");
        rendererSource.ShouldContain("HighlightQueues = new();");
        rendererSource.ShouldContain("internal static void RenderQueued(");
        rendererSource.ShouldNotContain("PushRenderGraphPassIndex(");
        int baseOverlay = overlaySource.IndexOf("BvhDebugOverlayLayer.Base", StringComparison.Ordinal);
        int debugShapes = overlaySource.IndexOf("RuntimeEngine.Rendering.Debug.RenderShapes();", StringComparison.Ordinal);
        int highlightOverlay = overlaySource.IndexOf("BvhDebugOverlayLayer.Highlight", StringComparison.Ordinal);
        baseOverlay.ShouldBeGreaterThanOrEqualTo(0);
        debugShapes.ShouldBeGreaterThan(baseOverlay);
        highlightOverlay.ShouldBeGreaterThan(debugShapes);
        queueSource.ShouldContain("(_pending, _rendering) = (_rendering, _pending);");
        queueSource.ShouldContain("ReferenceEquals(_pending[i].Renderer, request.Renderer)");

        engineDebugSource.ShouldContain("public static void RenderOverlayBox(");
        string cpuOverlayDraw = Slice(
            engineDebugSource,
            "public static void RenderShapes()",
            "private static DebugPrimitiveSceneState ResolveDebugPrimitiveSceneState",
            StringComparison.Ordinal);
        int cpuBaseOverlay = cpuOverlayDraw.IndexOf("scene.BaseOverlayLines.Render();", StringComparison.Ordinal);
        int ordinaryDebugShapes = cpuOverlayDraw.IndexOf("scene.Visualizer.Render();", StringComparison.Ordinal);
        int cpuHighlightOverlay = cpuOverlayDraw.IndexOf("scene.HighlightOverlayLines.Render();", StringComparison.Ordinal);
        cpuBaseOverlay.ShouldBeGreaterThanOrEqualTo(0);
        ordinaryDebugShapes.ShouldBeGreaterThan(cpuBaseOverlay);
        cpuHighlightOverlay.ShouldBeGreaterThan(ordinaryDebugShapes);

        string immediate = Slice(
            rendererSource,
            "private bool RenderImmediate(",
            "private static void ResetStencilState",
            StringComparison.Ordinal);
        int ensureResources = immediate.IndexOf("EnsureResources(visualizedLines)", StringComparison.Ordinal);
        int prepareRenderer = immediate.IndexOf("_linesRenderer!.TryPrepareForRendering(forceNoStereo: true)", StringComparison.Ordinal);
        int dispatch = immediate.IndexOf("_computeProgram.DispatchCompute(", StringComparison.Ordinal);
        int draw = immediate.IndexOf("_linesRenderer.Render(", StringComparison.Ordinal);
        ensureResources.ShouldBeGreaterThanOrEqualTo(0);
        prepareRenderer.ShouldBeGreaterThan(ensureResources);
        dispatch.ShouldBeGreaterThan(prepareRenderer);
        draw.ShouldBeGreaterThan(dispatch);
        rendererSource.ShouldContain("Name = \"GpuBvhDebugLines\"");
    }

    [Test]
    public void MeshBvhPreview_AccuracyInputControlsMaxLeafPrimitives()
    {
        string editorSource = ReadWorkspaceFile("XREngine.Editor/ComponentEditors/ModelComponentEditor.cs");
        string gpuMeshBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string renderableGpuBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.GpuBvh.cs");

        editorSource.ShouldContain("public float MeshAccuracy = BvhPreviewMaxMeshAccuracy;");
        editorSource.ShouldContain("ImGui.InputFloat(\"Mesh Accuracy\", ref meshAccuracy, 0.01f, 0.1f, \"%.3f\")");
        editorSource.ShouldContain("uint maxLeafPrimitives = GetBvhMaxLeafPrimitives(state.MeshAccuracy);");
        editorSource.ShouldContain("mesh.HasCurrentGpuMeshBvhForMaxLeafPrimitives(maxLeafPrimitives)");
        editorSource.ShouldContain("mesh.PrepareGpuMeshBvh(realtimeSkinned, maxLeafPrimitives: maxLeafPrimitives)");
        editorSource.ShouldContain("return state.GpuRenderer.Queue(");
        editorSource.ShouldNotContain("return state.GpuRenderer.Render(");

        gpuMeshBvhSource.ShouldContain("public const uint DefaultMaxLeafPrimitives = 1u;");
        gpuMeshBvhSource.ShouldContain("public uint MaxLeafPrimitives => _tree.MaxLeafPrimitives;");
        gpuMeshBvhSource.ShouldContain("ConfigureTree(maxLeafPrimitives);");
        gpuMeshBvhSource.ShouldContain("_tree.MaxLeafPrimitives = Math.Max(1u, maxLeafPrimitives);");

        renderableGpuBvhSource.ShouldContain("HasCurrentGpuMeshBvhForMaxLeafPrimitives(uint maxLeafPrimitives)");
        renderableGpuBvhSource.ShouldContain("_gpuMeshBvh.MaxLeafPrimitives == clampedMaxLeafPrimitives");
    }

    [Test]
    public void GpuMeshBvhHoverBounds_UsesLastKnownGpuBoundsWithoutForcingPrepass()
    {
        string renderableGpuBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.GpuBvh.cs");
        string renderableDebugSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Debug.cs");
        string boundsCalculatorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBoundsCalculator.cs");

        string requestBounds = Slice(
            renderableGpuBvhSource,
            "public bool TryGetGpuMeshBvhRequestWorldBounds(out AABB worldBounds)",
            "internal void ProcessPendingGpuMeshBvhRefresh()",
            StringComparison.Ordinal);

        requestBounds.ShouldContain("TryGetLastKnownGpuSkinnedWorldBounds(out worldBounds)");
        requestBounds.ShouldContain("return TryGetWorldBounds(out worldBounds);");
        requestBounds.ShouldNotContain("TryGetLiveGpuSkinnedWorldBounds");

        renderableDebugSource.ShouldContain("public bool TryGetLastKnownGpuSkinnedWorldBounds(out AABB bounds)");
        renderableDebugSource.ShouldContain("TryReadLastKnownGpuDebugBounds(this, out bounds)");

        string lastKnownRead = Slice(
            boundsCalculatorSource,
            "internal bool TryReadLastKnownGpuDebugBounds(RenderableMesh mesh, out AABB worldBounds)",
            "private bool TryPrepareStandaloneGpuDebugBounds",
            StringComparison.Ordinal);

        lastKnownRead.ShouldContain("SkinningPrepassDispatcher.Instance.TryReadSkinnedWorldBounds(renderer, out worldBounds)");
        lastKnownRead.ShouldNotContain("RunForGpuMeshBvh");
        lastKnownRead.ShouldNotContain("Run(renderer");
    }

    [Test]
    public void GpuMeshBvhLargeMortonSort_UsesStableRadixPipeline()
    {
        string dispatchSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs");
        string programsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Programs.cs");
        string histogramShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/radix_morton_histogram.comp");
        string prefixShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/radix_morton_prefix.comp");
        string scatterShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/radix_morton_scatter.comp");

        dispatchSource.ShouldContain("for (uint shift = 0u; shift < 32u; shift += 8u)");
        dispatchSource.ShouldContain("histogramProgram.DispatchCompute");
        dispatchSource.ShouldContain("prefixProgram.DispatchCompute");
        dispatchSource.ShouldContain("scatterProgram.DispatchCompute");
        programsSource.ShouldContain("radix_morton_histogram.comp");
        programsSource.ShouldContain("radix_morton_prefix.comp");
        programsSource.ShouldContain("radix_morton_scatter.comp");
        histogramShader.ShouldContain("atomicAdd(histogram[digit], 1u)");
        prefixShader.ShouldContain("offsets[index] = running");
        scatterShader.ShouldContain("localRank += uint(digits[prior] == digit)");
        scatterShader.ShouldContain("outputObjects[outputIndex] = inputObjects[index]");
    }

    [Test]
    public void GpuMeshBvhStaticTriangles_ArePackedOnlyWhenInvalidated()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string markDirty = Slice(
            source,
            "public void MarkDirty()",
            "public bool Prepare(",
            StringComparison.Ordinal);
        string staticPack = Slice(
            source,
            "private bool PackStaticTriangles(XRMesh mesh, uint triangleCount)",
            "private static XRDataBuffer? GetOrCreateStorageView",
            StringComparison.Ordinal);

        staticPack.ShouldContain("if (_packedTrianglesUploaded)");
        staticPack.ShouldContain("return true;");
        source.ShouldContain("InvalidateStaticGeometryIfChanged(mesh);");
        source.ShouldContain("_staticGeometryRevision == revision");
        source.ShouldContain("ulong revision = source?.Revision ?? 0u;");
        source.ShouldContain("_packedTrianglesUploaded = false;");
        source.ShouldContain("_packedTrianglesUploaded = true;");
        markDirty.ShouldNotContain("ReleaseStaticStorageViews();");
    }

    [Test]
    public void GpuMeshBvhPositionInputs_UseTheirActualScalarStride()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string aabbShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/mesh_triangle_aabbs.comp");
        string packShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/mesh_bvh_pack_triangles.comp");

        source.ShouldContain("program.Uniform(\"PositionStrideScalars\", positions?.ComponentCount ?? 0u);");
        foreach (string shader in new[] { aabbShader, packShader })
        {
            shader.ShouldContain("uint PositionScalars[];");
            shader.ShouldContain("uniform uint PositionStrideScalars;");
            shader.ShouldContain("uint word = vertexIndex * max(PositionStrideScalars, 3u);");
            shader.ShouldNotContain("vec4 PositionsIn[];");
        }
    }

    [Test]
    public void GpuMeshBvhClickPick_UsesMortonSortedPackedTriangles()
    {
        string gpuMeshBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string pickSource = ReadWorkspaceFile("XRENGINE/Rendering/XRWorldInstance.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EditorFlyingCameraPawnComponent.cs");
        string glDataBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Buffers/GLDataBuffer.cs");
        string visualSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs");
        string raycastDispatcherSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");
        string packShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/mesh_bvh_pack_triangles.comp");

        gpuMeshBvhSource.ShouldContain("PackedTriangleShaderPath");
        gpuMeshBvhSource.ShouldContain("public XRDataBuffer? PackedTriangleBuffer => _packedTriangleBuffer;");
        gpuMeshBvhSource.ShouldContain("program.BindBuffer(morton, 3);");
        gpuMeshBvhSource.ShouldContain("program.BindBuffer(_packedTriangleBuffer!, 4);");

        packShader.ShouldContain("uint sourceTriangleIndex = MortonData[sortedIndex * 2u + 1u];");
        packShader.ShouldContain("uvec4 TriangleIndices[];");
        packShader.ShouldContain("PackedTriangles[sortedIndex].extra = uvec4(sourceTriangleIndex, sourceTriangleIndex, 0u, 0u);");

        pickSource.ShouldContain("BvhRaycastRequest");
        pickSource.ShouldContain("TriangleBuffer = bvh.PackedTriangleBuffer");
        pickSource.ShouldContain("GpuMeshBvhPickCandidate");
        pickSource.ShouldContain("DispatchLatestGpuMeshBvhPick");
        pickSource.ShouldContain("FinishGpuMeshBvhPick(state, generation)");
        pickSource.ShouldContain("DispatchQueued");
        pickSource.ShouldContain("RaycastInFlight");
        pickSource.ShouldContain("state.Candidate is { IsComplete: false } pending");
        pickSource.ShouldNotContain("forceRebuild: mesh.HasGpuMeshBvhRefreshRequest");
        pickSource.ShouldNotContain("QueueGpuMeshBvhInteractionRefresh");

        // The pick setting is a boolean toggle and the CPU triangle-test fallback was removed.
        pickSource.ShouldContain("GpuMeshBvhClickPickEnabled");
        pickSource.ShouldNotContain("EnsureGpuMeshBvhClickPickCpuBvh");
        pickSource.ShouldNotContain("EMeshBvhClickPickMode");

        // The GPU readback resolves the exact face/edge/vertex for the requested hit mode.
        pickSource.ShouldContain("TryBuildPickResult(candidate.HitMode");

        string uploadRay = Slice(
            pickSource,
            "public void UploadRay(Vector3 origin, Vector3 direction, float maxDistance)",
            "[StructLayout(LayoutKind.Sequential)]",
            StringComparison.Ordinal);

        uploadRay.ShouldContain("RayBuffer.PushData();");
        uploadRay.ShouldNotContain("HitBuffer.PushData();");

        glDataBufferSource.ShouldContain("Api.GetNamedBufferParameter(id, BufferPNameARB.Size, out int allocatedSize);");
        glDataBufferSource.ShouldContain("if (allocatedSize > 0)");
        glDataBufferSource.ShouldContain("RecreateBuffer();");

        visualSceneSource.ShouldContain("BvhRaycasts.ProcessDispatches();");
        visualSceneSource.ShouldContain("BvhRaycasts.ProcessCompletions();");
        visualSceneSource.ShouldNotContain("BvhRaycasts.SetEnabled(false, \"initial settings disabled\")");
        visualSceneSource.ShouldNotContain("BvhRaycasts.SetEnabled(false, \"disabled by settings\")");

        raycastDispatcherSource.ShouldContain("public bool Enqueue(BvhRaycastRequest request)");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uRayCount\", request.RayCount);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uRootIndex\", request.RootNodeIndex);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uPacketWidth\", packetWidth);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uUsePacketMode\", request.UsePacketMode ? 1u : 0u);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uAnyHitMode\", request.AnyHit ? 1u : 0u);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uMaxStackDepth\", request.MaxStackDepth ?? DefaultStackLimit);");
        raycastDispatcherSource.ShouldNotContain("program.Uniform(\"uRayCount\", (int)request.RayCount);");
        raycastDispatcherSource.ShouldNotContain("program.Uniform(\"uMaxStackDepth\", (int)(request.MaxStackDepth ?? DefaultStackLimit));");

        string preferencesSource = ReadWorkspaceFile("XRENGINE/Settings/EditorPreferences.cs");
        preferencesSource.ShouldContain("public bool GpuMeshBvhClickPickEnabled");
        preferencesSource.ShouldNotContain("enum EMeshBvhClickPickMode");

        editorSource.ShouldContain("RegisterPendingGpuSelection(gpuCandidate)");
        editorSource.ShouldContain("data is GpuMeshBvhPickCandidate gpuCandidate");
    }

    private enum CustomUiEnumTestValue
    {
        All,
        LeavesOnly,
        InternalOnly,
    }

    private static Triangle CreateIntersectingTriangle(
        MathBvhQueryVolume query,
        MathBvhQueryShape shape)
    {
        if (shape != MathBvhQueryShape.Raycast)
        {
            Vector3 center = shape switch
            {
                MathBvhQueryShape.Sphere => query.Sphere.Center,
                MathBvhQueryShape.Frustum =>
                    (query.Frustum.LeftBottomNear + query.Frustum.RightTopNear +
                     query.Frustum.LeftBottomFar + query.Frustum.RightTopFar) * 0.25f,
                _ => query.Box.Center,
            };
            return new Triangle(
                center + Vector3.UnitX * 0.05f,
                center + Vector3.UnitZ * 0.05f,
                center - (Vector3.UnitX + Vector3.UnitZ) * 0.05f);
        }

        Vector3 direction = Vector3.Normalize(query.Raycast.End - query.Raycast.Start);
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitX));
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(direction, tangent));
        Vector3 rayCenter = (query.Raycast.Start + query.Raycast.End) * 0.5f;
        return new Triangle(
            rayCenter + tangent * 0.4f,
            rayCenter - tangent * 0.4f,
            rayCenter + bitangent * 0.4f);
    }

    private static string Slice(string source, string startToken, string endToken, StringComparison comparison)
    {
        string normalized = source.Replace("\r\n", "\n");
        int start = normalized.IndexOf(startToken, comparison);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find start token '{startToken}'.");

        int end = normalized.IndexOf(endToken, start + startToken.Length, comparison);
        end.ShouldBeGreaterThan(start, $"Could not find end token '{endToken}' after '{startToken}'.");

        return normalized[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = ResolveWorkspaceRoot();
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n");
    }

    private static string ResolveWorkspaceRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")) ||
                File.Exists(Path.Combine(directory, "XRENGINE.sln")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../"));
    }
}
