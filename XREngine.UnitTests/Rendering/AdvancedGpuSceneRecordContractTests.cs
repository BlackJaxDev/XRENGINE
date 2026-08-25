using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedGpuSceneRecordContractTests
{
    [Test]
    public void CanonicalRecordLayouts_ArePackFourAndGpuUploadable()
    {
        Marshal.SizeOf<AdvancedGpuHandle>().ShouldBe(8);
        Marshal.SizeOf<AdvancedGpuHandleLookup>().ShouldBe(8);
        Marshal.SizeOf<AdvancedGpuHandleRemap>().ShouldBe(16);
        Marshal.SizeOf<AdvancedDrawRecord>().ShouldBe(80);
        Marshal.SizeOf<AdvancedInstanceRecord>().ShouldBe(224);
        Marshal.SizeOf<AdvancedTransformRecord>().ShouldBe(80);
        Marshal.SizeOf<AdvancedDeformationRecord>().ShouldBe(48);
        Marshal.SizeOf<AdvancedRenderStateRecord>().ShouldBe(32);
        Marshal.SizeOf<AdvancedEditorIdentityRecord>().ShouldBe(32);

        OffsetOf<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.Instance)).ShouldBe(0);
        OffsetOf<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.Geometry)).ShouldBe(8);
        OffsetOf<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.Material)).ShouldBe(16);
        OffsetOf<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.EditorIdentity)).ShouldBe(40);
        OffsetOf<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.CurrentTransform)).ShouldBe(48);
        OffsetOf<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.PreviousTransform)).ShouldBe(56);
        OffsetOf<AdvancedDrawRecord>(nameof(AdvancedDrawRecord.PrimitiveSection)).ShouldBe(64);

        OffsetOf<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.CurrentWorld)).ShouldBe(0);
        OffsetOf<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.PreviousWorld)).ShouldBe(64);
        OffsetOf<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.BoundsSphere)).ShouldBe(128);
        OffsetOf<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.Animation)).ShouldBe(176);
        OffsetOf<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.VisibilityFlags)).ShouldBe(192);
        OffsetOf<AdvancedInstanceRecord>(nameof(AdvancedInstanceRecord.CurrentFrameSlot)).ShouldBe(208);
    }

    [Test]
    public void LogicalLookupImage_ResolvesCurrentHandlesAndRejectsStaleGenerations()
    {
        AdvancedGpuRecordTable<uint> table = new(3u);
        table.TryAdd(10u, out AdvancedGpuHandle first).ShouldBeTrue();
        table.TryAdd(20u, out AdvancedGpuHandle stale).ShouldBeTrue();
        table.TryRemoveImmediatelyBeforePublication(stale).ShouldBeTrue();
        table.ClearPublishedRemaps();
        table.TryAdd(30u, out AdvancedGpuHandle replacement).ShouldBeTrue();

        Span<AdvancedGpuHandleLookup> lookups =
            stackalloc AdvancedGpuHandleLookup[4];
        table.CopyLogicalLookups(lookups, out int lookupCount).ShouldBeTrue();

        lookupCount.ShouldBe(3);
        lookups[0].ShouldBe(AdvancedGpuHandleLookup.Invalid);
        lookups[(int)first.Index].Generation.ShouldBe(first.Generation);
        lookups[(int)first.Index].DenseIndex.ShouldBe(0u);
        lookups[(int)replacement.Index].Generation.ShouldBe(replacement.Generation);
        lookups[(int)replacement.Index].DenseIndex.ShouldBe(1u);
        replacement.Index.ShouldBe(stale.Index);
        replacement.Generation.ShouldNotBe(stale.Generation);

        table.LogicalLookupDirtyRange.ShouldBe(
            new AdvancedGpuDirtyRange(0u, 3u));
        table.ClearLogicalLookupDirtyRange();
        table.LogicalLookupDirtyRange.ShouldBe(AdvancedGpuDirtyRange.Empty);
    }

    [Test]
    public void GenerationalTable_ReusesSlotAndRejectsStaleGeneration()
    {
        AdvancedGpuRecordTable<uint> table = new(1u);
        table.TryAdd(11u, out AdvancedGpuHandle stale).ShouldBeTrue();
        stale.Index.ShouldBe(1u);
        stale.Generation.ShouldBe(1u);

        table.TryRemoveImmediatelyBeforePublication(stale).ShouldBeTrue();
        table.TryGet(stale, out _).ShouldBeFalse();
        table.TryRemoveImmediatelyBeforePublication(stale).ShouldBeFalse();

        table.ClearPublishedRemaps();
        table.TryAdd(22u, out AdvancedGpuHandle current).ShouldBeTrue();
        current.Index.ShouldBe(stale.Index);
        current.Generation.ShouldNotBe(stale.Generation);
        table.TryGet(current, out uint value).ShouldBeTrue();
        value.ShouldBe(22u);
    }

    [Test]
    public void GenerationalTable_DoesNotGrowOutsideExplicitBoundary()
    {
        AdvancedGpuRecordTable<uint> table = new(1u);
        table.TryAdd(1u, out _).ShouldBeTrue();
        table.TryAdd(2u, out AdvancedGpuHandle rejected).ShouldBeFalse();
        rejected.ShouldBe(AdvancedGpuHandle.Invalid);

        table.GrowAtBoundary(4u);
        table.Capacity.ShouldBe(4u);
        table.TryAdd(2u, out AdvancedGpuHandle accepted).ShouldBeTrue();
        accepted.IsValid.ShouldBeTrue();
    }

    [Test]
    public void GenerationalTable_CompactionPublishesRemovalAndMovementRemaps()
    {
        AdvancedGpuRecordTable<uint> table = new(4u);
        table.TryAdd(10u, out AdvancedGpuHandle first).ShouldBeTrue();
        table.TryAdd(20u, out AdvancedGpuHandle removed).ShouldBeTrue();
        table.TryAdd(30u, out AdvancedGpuHandle moved).ShouldBeTrue();
        table.ClearDirtyRange();
        table.ClearPublishedRemaps();

        table.TryRemoveImmediatelyBeforePublication(removed).ShouldBeTrue();
        table.IsPacked.ShouldBeFalse();
        table.Compact().ShouldBe(1);
        table.IsPacked.ShouldBeTrue();
        table.Count.ShouldBe(2u);
        table.PhysicalHighWater.ShouldBe(2u);
        table.PublishedRemaps.Length.ShouldBe(2);

        ReadOnlySpan<AdvancedGpuHandleRemap> remaps = table.PublishedRemaps;
        remaps[0].Handle.ShouldBe(removed);
        remaps[0].PreviousDenseIndex.ShouldBe(1u);
        remaps[0].CurrentDenseIndex.ShouldBe(AdvancedGpuHandleRemap.InvalidDenseIndex);
        remaps[1].Handle.ShouldBe(moved);
        remaps[1].PreviousDenseIndex.ShouldBe(2u);
        remaps[1].CurrentDenseIndex.ShouldBe(1u);

        Span<uint> dependentDenseIndices = stackalloc uint[] { 0u, 1u, 2u };
        table.ApplyPublishedRemaps(dependentDenseIndices);
        dependentDenseIndices[0].ShouldBe(0u);
        dependentDenseIndices[1].ShouldBe(AdvancedGpuHandleRemap.InvalidDenseIndex);
        dependentDenseIndices[2].ShouldBe(1u);

        table.TryGet(first, out uint firstValue).ShouldBeTrue();
        firstValue.ShouldBe(10u);
        table.TryGet(moved, out uint movedValue).ShouldBeTrue();
        movedValue.ShouldBe(30u);
        table.TryGetDenseIndex(moved, out uint movedDenseIndex).ShouldBeTrue();
        movedDenseIndex.ShouldBe(1u);

        table.DirtyRange.ShouldBe(new AdvancedGpuDirtyRange(1u, 2u));
        table.PhysicalRecords.Length.ShouldBe(2);
        table.PhysicalHandles[1].ShouldBe(moved);
        table.PhysicalOccupancy[0].ShouldBe((byte)1);
        table.PhysicalOccupancy[1].ShouldBe((byte)1);
    }

    [Test]
    public void GenerationalTable_WarmedMutationAndCompactionAllocateNoManagedMemory()
    {
        ExerciseStructuralMutation(new AdvancedGpuRecordTable<uint>(4u));

        AdvancedGpuRecordTable<uint> measured = new(4u);
        measured.TryAdd(1u, out _).ShouldBeTrue();
        measured.TryAdd(2u, out AdvancedGpuHandle removed).ShouldBeTrue();
        measured.TryAdd(3u, out _).ShouldBeTrue();
        measured.ClearDirtyRange();
        measured.ClearPublishedRemaps();
        Span<uint> dependentIndices = stackalloc uint[] { 0u, 1u, 2u };

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool didRemove = measured.TryRemoveImmediatelyBeforePublication(removed);
        int moves = measured.Compact();
        measured.ApplyPublishedRemaps(dependentIndices);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        didRemove.ShouldBeTrue();
        moves.ShouldBe(1);
        allocated.ShouldBe(0L);
    }

    [Test]
    public void Database_CompactsEverySceneTableAndPublishesDependentRemaps()
    {
        AdvancedGpuSceneDatabase database = CreateDatabase();
        database.BeginStructuralUpdate();

        CreateHole(database.Draws, out AdvancedGpuHandle movedDraw);
        CreateHole(database.Instances, out AdvancedGpuHandle movedInstance);
        CreateHole(database.Transforms, out AdvancedGpuHandle movedTransform);
        CreateHole(database.Deformations, out AdvancedGpuHandle movedDeformation);
        CreateHole(database.RenderStates, out AdvancedGpuHandle movedRenderState);
        CreateHole(database.EditorIdentities, out AdvancedGpuHandle movedEditorIdentity);
        CreateHole(database.Geometry.Records, out AdvancedGpuHandle movedGeometry);

        database.CompactAndPublishRemaps().ShouldBe(7);

        AssertPublishedMove(database.Draws, movedDraw);
        AssertPublishedMove(database.Instances, movedInstance);
        AssertPublishedMove(database.Transforms, movedTransform);
        AssertPublishedMove(database.Deformations, movedDeformation);
        AssertPublishedMove(database.RenderStates, movedRenderState);
        AssertPublishedMove(database.EditorIdentities, movedEditorIdentity);
        AssertPublishedMove(database.Geometry.Records, movedGeometry);
    }

    [Test]
    public void Database_ResolvesDrawDependenciesAndCreatesManagedIdentityFreeSnapshot()
    {
        AdvancedGpuSceneDatabase database = CreateDatabase();
        AdvancedGeometryRegistration geometryRegistration = AdvancedGeometryDatabaseContractTests.CreateTriangleRegistration();
        database.Geometry.TryAddStatic(
            AdvancedGeometryDatabaseContractTests.CreateTriangleVertices(),
            [0u, 1u, 2u],
            geometryRegistration,
            out AdvancedGpuHandle geometry).ShouldBeTrue();

        AdvancedInstanceRecord instanceRecord = new()
        {
            CurrentWorld = Matrix4x4.CreateTranslation(1f, 2f, 3f),
            PreviousWorld = Matrix4x4.Identity,
            BoundsSphere = new Vector4(0f, 0f, 0f, 1f),
            VisibilityFlags = EAdvancedInstanceVisibilityFlags.Enabled,
            CurrentFrameSlot = 2u,
            PreviousFrameSlot = 1u,
        };
        database.Instances.TryAdd(instanceRecord, out AdvancedGpuHandle instance).ShouldBeTrue();
        database.Transforms.TryAdd(
            new AdvancedTransformRecord { World = instanceRecord.CurrentWorld, FrameSlot = 2u },
            out AdvancedGpuHandle currentTransform).ShouldBeTrue();
        database.Transforms.TryAdd(
            new AdvancedTransformRecord { World = instanceRecord.PreviousWorld, FrameSlot = 1u },
            out AdvancedGpuHandle previousTransform).ShouldBeTrue();
        database.RenderStates.TryAdd(
            new AdvancedRenderStateRecord { StateClass = 4u },
            out AdvancedGpuHandle renderState).ShouldBeTrue();
        database.EditorIdentities.TryAdd(
            new AdvancedEditorIdentityRecord { StableInstanceId = 1234ul, SelectionId = 77u },
            out AdvancedGpuHandle editorIdentity).ShouldBeTrue();
        database.Deformations.TryAdd(
            new AdvancedDeformationRecord
            {
                SourceGeometry = geometry,
                CurrentGeometry = geometry,
                PreviousGeometry = geometry,
                CurrentFrameSlot = 2u,
                PreviousFrameSlot = 1u,
            },
            out AdvancedGpuHandle deformation).ShouldBeTrue();

        AdvancedGpuHandle material = new(9u, 3u);
        AdvancedDrawRecord drawRecord = new()
        {
            Instance = instance,
            Geometry = geometry,
            Material = material,
            Deformation = deformation,
            RenderState = renderState,
            EditorIdentity = editorIdentity,
            CurrentTransform = currentTransform,
            PreviousTransform = previousTransform,
        };
        database.Draws.TryAdd(drawRecord, out AdvancedGpuHandle draw).ShouldBeTrue();

        database.TryResolveDraw(draw, out AdvancedResolvedDrawRecords resolved).ShouldBeTrue();
        resolved.Material.ShouldBe(material);
        resolved.EditorIdentity.StableInstanceId.ShouldBe(1234ul);
        resolved.Geometry.IsResident.ShouldBeTrue();
        resolved.HasDeformation.ShouldBe(1u);
        resolved.Instance.CurrentFrameSlot.ShouldBe(2u);
        resolved.Instance.PreviousFrameSlot.ShouldBe(1u);

        database.TryCreateDrawDependencySnapshot(draw, out AdvancedDrawDependencySnapshot snapshot).ShouldBeTrue();
        snapshot.Draw.ShouldBe(draw);
        snapshot.Instance.ShouldBe(instance);
        snapshot.Geometry.ShouldBe(geometry);
        snapshot.Material.ShouldBe(material);
        snapshot.GeometryResidency.ShouldBe(EAdvancedGeometryResidency.Resident);
        snapshot.DrawDenseIndex.ShouldBe(0u);

        database.EditorIdentities.TryRemoveImmediatelyBeforePublication(editorIdentity).ShouldBeTrue();
        database.TryResolveDraw(draw, out _).ShouldBeFalse();
    }

    private static void ExerciseStructuralMutation(AdvancedGpuRecordTable<uint> table)
    {
        table.TryAdd(1u, out _);
        table.TryAdd(2u, out AdvancedGpuHandle removed);
        table.TryAdd(3u, out _);
        table.ClearPublishedRemaps();
        table.TryRemoveImmediatelyBeforePublication(removed);
        table.Compact();
        Span<uint> dependentIndices = stackalloc uint[] { 0u, 1u, 2u };
        table.ApplyPublishedRemaps(dependentIndices);
    }

    private static void CreateHole<T>(
        AdvancedGpuRecordTable<T> table,
        out AdvancedGpuHandle movedHandle)
        where T : unmanaged
    {
        T value = default;
        table.TryAdd(value, out _).ShouldBeTrue();
        table.TryAdd(value, out AdvancedGpuHandle removed).ShouldBeTrue();
        table.TryAdd(value, out movedHandle).ShouldBeTrue();
        table.TryRemoveImmediatelyBeforePublication(removed).ShouldBeTrue();
    }

    private static void AssertPublishedMove<T>(
        AdvancedGpuRecordTable<T> table,
        AdvancedGpuHandle movedHandle)
        where T : unmanaged
    {
        table.PublishedRemaps.Length.ShouldBe(2);
        table.PublishedRemaps[1].Handle.ShouldBe(movedHandle);
        table.PublishedRemaps[1].PreviousDenseIndex.ShouldBe(2u);
        table.PublishedRemaps[1].CurrentDenseIndex.ShouldBe(1u);
        table.TryGetDenseIndex(movedHandle, out uint denseIndex).ShouldBeTrue();
        denseIndex.ShouldBe(1u);
    }

    private static AdvancedGpuSceneDatabase CreateDatabase()
        => new(new AdvancedGpuSceneCapacityProfile(
            DrawRecords: 8u,
            InstanceRecords: 8u,
            TransformRecords: 16u,
            DeformationRecords: 8u,
            RenderStateRecords: 8u,
            EditorIdentityRecords: 8u,
            GeometryRecords: 8u,
            StaticVertexBytes: 4096u,
            IndexBytes: 4096u,
            PreSkinnedCurrentBytes: 4096u,
            PreSkinnedPreviousBytes: 4096u,
            MeshletBytes: 4096u));

    private static int OffsetOf<T>(string fieldName) where T : struct
        => Marshal.OffsetOf<T>(fieldName).ToInt32();
}
