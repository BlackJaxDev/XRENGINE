using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedGeometryDatabaseContractTests
{
    [Test]
    public void GeometryLayouts_ArePackFourAndStd430Compatible()
    {
        Marshal.SizeOf<AdvancedBufferReference>().ShouldBe(32);
        Marshal.SizeOf<AdvancedGeometryRecord>().ShouldBe(256);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.CurrentVertexData)).ShouldBe(0);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.PreviousVertexData)).ShouldBe(32);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.IndexData)).ShouldBe(64);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.MeshletData)).ShouldBe(96);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.FallbackGeometry)).ShouldBe(128);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.VertexLayoutId)).ShouldBe(160);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.BoundsSphere)).ShouldBe(176);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.BoundsMin)).ShouldBe(192);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.BoundsMax)).ShouldBe(208);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.MaterialSectionFirst)).ShouldBe(224);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.PrimitiveTopology)).ShouldBe(232);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.CookedLayoutVersion)).ShouldBe(248);
        OffsetOf<AdvancedGeometryRecord>(nameof(AdvancedGeometryRecord.Flags)).ShouldBe(252);
    }

    [Test]
    public void StaticGeometry_UsesSceneOwnedImmutableVertexAndIndexArenas()
    {
        AdvancedGeometryDatabase database = CreateDatabase();
        byte[] vertices = CreateTriangleVertices();
        uint[] indices = [0u, 1u, 2u];
        AdvancedGeometryRegistration registration = CreateTriangleRegistration();

        database.TryAddStatic(vertices, indices, registration, out AdvancedGpuHandle handle).ShouldBeTrue();
        database.TryGet(handle, out AdvancedGeometryRecord record).ShouldBeTrue();

        record.Source.ShouldBe(EAdvancedGeometrySource.Static);
        record.Residency.ShouldBe(EAdvancedGeometryResidency.Resident);
        record.CurrentVertexData.IsValid.ShouldBeTrue();
        record.PreviousVertexData.ShouldBe(record.CurrentVertexData);
        record.IndexData.IsValid.ShouldBeTrue();
        record.VertexCount.ShouldBe(3u);
        record.IndexCount.ShouldBe(3u);
        record.PrimitiveTopology.ShouldBe(EPrimitiveType.Triangles);
        record.VertexLayoutId.ShouldBe(registration.VertexLayoutId);
        record.MaterialSectionCount.ShouldBe(1u);
        database.StaticVertexArena.Data.SequenceEqual(vertices).ShouldBeTrue();
        MemoryMarshal.Cast<byte, uint>(database.IndexArena.Data).SequenceEqual(indices).ShouldBeTrue();
    }

    [Test]
    public void PreSkinnedGeometry_PublishesDistinctCurrentAndPreviousFrameSources()
    {
        AdvancedGeometryDatabase database = CreateDatabase();
        byte[] current = CreateTriangleVertices();
        byte[] previous = CreateTriangleVertices();
        previous[0] = 99;

        database.TryAddPreSkinned(
            current,
            previous,
            [0u, 1u, 2u],
            CreateTriangleRegistration(),
            out AdvancedGpuHandle handle).ShouldBeTrue();
        database.TryGet(handle, out AdvancedGeometryRecord record).ShouldBeTrue();

        record.Source.ShouldBe(EAdvancedGeometrySource.PreSkinnedCurrentAndPrevious);
        record.CurrentVertexData.Buffer.ShouldBe(database.PreSkinnedCurrentArena.BufferHandle);
        record.PreviousVertexData.Buffer.ShouldBe(database.PreSkinnedPreviousArena.BufferHandle);
        record.CurrentVertexData.Buffer.ShouldNotBe(record.PreviousVertexData.Buffer);
        database.PreSkinnedCurrentArena.Data[0].ShouldBe(current[0]);
        database.PreSkinnedPreviousArena.Data[0].ShouldBe(previous[0]);
    }

    [Test]
    public void MeshletLocalGeometry_DoesNotChangeGeometryOrPrimitiveIdentity()
    {
        AdvancedGeometryDatabase database = CreateDatabase();
        AdvancedGeometryRegistration registration = CreateTriangleRegistration() with
        {
            MeshletFirst = 7u,
            MeshletCount = 1u,
        };
        byte[] meshlet = new byte[16];

        database.TryAddMeshletLocal(
            CreateTriangleVertices(),
            [0u, 1u, 2u],
            meshlet,
            meshletStride: 16u,
            registration,
            out AdvancedGpuHandle handle).ShouldBeTrue();
        database.TryGet(handle, out AdvancedGeometryRecord record).ShouldBeTrue();

        record.Source.ShouldBe(EAdvancedGeometrySource.MeshletLocal);
        record.MeshletData.IsValid.ShouldBeTrue();
        record.MeshletFirst.ShouldBe(7u);
        record.MeshletCount.ShouldBe(1u);
        record.VertexCount.ShouldBe(3u);
        record.IndexCount.ShouldBe(3u);
        record.PrimitiveTopology.ShouldBe(EPrimitiveType.Triangles);
    }

    [Test]
    public void IncompatibleCookedGeometryVersion_IsRejectedWithoutArenaWrites()
    {
        AdvancedGeometryDatabase database = CreateDatabase();
        AdvancedGeometryRegistration incompatible = CreateTriangleRegistration() with
        {
            CookedLayoutVersion = AdvancedGeometryCookedLayout.CurrentVersion + 1u,
        };

        database.TryAddStatic(
            CreateTriangleVertices(),
            [0u, 1u, 2u],
            incompatible,
            out AdvancedGpuHandle handle).ShouldBeFalse();

        handle.ShouldBe(AdvancedGpuHandle.Invalid);
        database.Records.Count.ShouldBe(0u);
        database.StaticVertexArena.CountBytes.ShouldBe(0u);
        database.IndexArena.CountBytes.ShouldBe(0u);
    }

    [Test]
    public void MissingGeometry_ExplicitlySkipsOrResolvesResidentFallback()
    {
        AdvancedGeometryDatabase database = CreateDatabase();
        AdvancedGeometryRegistration registration = CreateTriangleRegistration();
        database.TryAddMissing(registration, AdvancedGpuHandle.Invalid, out AdvancedGpuHandle skipped).ShouldBeTrue();
        database.TryGet(skipped, out AdvancedGeometryRecord skippedRecord).ShouldBeTrue();
        skippedRecord.Residency.ShouldBe(EAdvancedGeometryResidency.Missing);
        skippedRecord.MissingBehavior.ShouldBe(EAdvancedMissingGeometryBehavior.SkipDraw);
        database.TryResolveVisibilityGeometry(skipped, out _).ShouldBeFalse();

        database.TryAddStatic(
            CreateTriangleVertices(),
            [0u, 1u, 2u],
            registration,
            out AdvancedGpuHandle fallback).ShouldBeTrue();
        database.TryAddMissing(registration, fallback, out AdvancedGpuHandle missing).ShouldBeTrue();
        database.TryGet(missing, out AdvancedGeometryRecord missingRecord).ShouldBeTrue();
        missingRecord.MissingBehavior.ShouldBe(EAdvancedMissingGeometryBehavior.UseFallback);
        missingRecord.FallbackGeometry.ShouldBe(fallback);

        database.TryResolveVisibilityGeometry(missing, out AdvancedGeometryRecord resolved).ShouldBeTrue();
        resolved.IsResident.ShouldBeTrue();
        resolved.CurrentVertexData.IsValid.ShouldBeTrue();
    }

    [Test]
    public void ImmutableArena_GrowsAndInvalidatesOnlyAtExplicitBoundary()
    {
        AdvancedImmutableByteArena arena = new(bufferIndex: 9u, capacityBytes: 4u);
        arena.TryAppend([1, 2, 3, 4], 1u, out AdvancedBufferReference first).ShouldBeTrue();
        arena.TryAppend([5], 1u, out _).ShouldBeFalse();

        arena.GrowAtBoundary(8u);
        arena.TryAppend([5], 1u, out AdvancedBufferReference second).ShouldBeTrue();
        second.Buffer.ShouldBe(first.Buffer);
        AdvancedGpuHandle oldGeneration = arena.BufferHandle;

        arena.ResetAtBoundary();
        arena.CountBytes.ShouldBe(0u);
        arena.BufferHandle.Index.ShouldBe(oldGeneration.Index);
        arena.BufferHandle.Generation.ShouldNotBe(oldGeneration.Generation);
        first.Buffer.ShouldNotBe(arena.BufferHandle);
    }

    internal static byte[] CreateTriangleVertices()
        => new byte[3 * 12];

    internal static AdvancedGeometryRegistration CreateTriangleRegistration()
        => AdvancedGeometryRegistration.Create(
            vertexCount: 3u,
            indexCount: 3u,
            vertexStride: 12u,
            primitiveTopology: EPrimitiveType.Triangles,
            vertexLayoutId: 0x1122334455667788ul,
            boundsSphere: new Vector4(0f, 0f, 0f, 1f),
            boundsMin: new Vector4(-1f, -1f, -1f, 0f),
            boundsMax: new Vector4(1f, 1f, 1f, 0f));

    private static AdvancedGeometryDatabase CreateDatabase()
        => new(
            recordCapacity: 8u,
            staticVertexCapacityBytes: 4096u,
            indexCapacityBytes: 4096u,
            preSkinnedCurrentCapacityBytes: 4096u,
            preSkinnedPreviousCapacityBytes: 4096u,
            meshletCapacityBytes: 4096u);

    private static int OffsetOf<T>(string fieldName) where T : struct
        => Marshal.OffsetOf<T>(fieldName).ToInt32();
}
