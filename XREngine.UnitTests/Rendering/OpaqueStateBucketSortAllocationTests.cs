using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpaqueStateBucketSortAllocationTests
{
    private const int Pass = (int)EDefaultRenderPass.OpaqueDeferred;
    private const int MaterialCount = 3;
    private const int RenderersPerMaterial = 3;
    private const int CommandsPerRenderer = 2;

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
    public void Sort_InterleavedBuckets_AllocatesNothingAfterWarmUp()
    {
        // Several materials with several mesh renderers each, inserted interleaved. The sort then reorders runs at
        // more than one level of the key.
        XRMeshRenderer[,] renderers = new XRMeshRenderer[MaterialCount, RenderersPerMaterial];
        for (int material = 0; material < MaterialCount; material++)
        {
            XRMaterial shared = new();
            for (int renderer = 0; renderer < RenderersPerMaterial; renderer++)
                renderers[material, renderer] = new XRMeshRenderer(null, shared);
        }

        List<RenderCommandMesh3D> commands = [];
        for (int renderer = 0; renderer < RenderersPerMaterial; renderer++)
        {
            for (int copy = 0; copy < CommandsPerRenderer; copy++)
            {
                for (int material = 0; material < MaterialCount; material++)
                    commands.Add(CreateCommand(renderers[material, renderer], copy));
            }
        }

        SnapshotSortedRenderCommandCollection collection = new(new OpaqueStateBucketRenderCommandSorter());

        for (int frame = 0; frame < 8; frame++)
            FillAndSort(collection, commands);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 16; frame++)
            FillAndSort(collection, commands);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
    }

    private static void FillAndSort(SnapshotSortedRenderCommandCollection collection, List<RenderCommandMesh3D> commands)
    {
        collection.Clear();
        for (int i = 0; i < commands.Count; i++)
            collection.Add(commands[i], i, commands.Count - i);

        int count = 0;
        foreach (RenderCommand _ in collection)
            count++;
        if (count != commands.Count)
            throw new InvalidOperationException("The collection lost commands.");
    }

    private static RenderCommandMesh3D CreateCommand(XRMeshRenderer renderer, int copy)
        => new(Pass)
        {
            Mesh = renderer,
            WorldMatrix = Matrix4x4.Identity,
            WorldCullingVolumeOverride = new AABB(
                new Vector3(copy, -1.0f, -1.0f),
                new Vector3(copy + 1.0f, 1.0f, 1.0f)),
        };
}
