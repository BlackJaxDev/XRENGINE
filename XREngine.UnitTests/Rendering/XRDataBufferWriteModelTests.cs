using NUnit.Framework;
using Shouldly;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class XRDataBufferWriteModelTests
{
    [Test]
    public void WriterDispose_AutoCommitsDirtyRangeAndRevision()
    {
        XRDataBuffer buffer = CreateIntBuffer("AutoCommit");

        using (XRBufferWriter<int> writer = buffer.Alloc<int>(4, XRBufferWriteMode.Discard))
        {
            writer.Span[0] = 10;
            writer.Span[1] = 20;
        }

        buffer.Revision.ShouldBe(1ul);
        buffer.GetDataRawAtIndex<int>(0).ShouldBe(10);
        buffer.GetDataRawAtIndex<int>(1).ShouldBe(20);
        buffer.GetDirtyRangesSnapshot().ShouldBe([new XRBufferDirtyRange(0u, 16u)]);
    }

    [Test]
    public void WriterCancel_LeavesRevisionUnchanged()
    {
        XRDataBuffer buffer = CreateIntBuffer("Cancel");
        using (XRBufferWriter<int> writer = buffer.Alloc<int>(2))
        {
            writer.Span[0] = 7;
            writer.Cancel();
        }

        buffer.Revision.ShouldBe(0ul);
        buffer.GetDirtyRangesSnapshot().Length.ShouldBe(0);
    }

    [Test]
    public void EmptyWriter_DoesNotAdvanceRevision()
    {
        XRDataBuffer buffer = CreateIntBuffer("Empty");

        using (buffer.Alloc<int>(0u))
        {
        }

        buffer.Revision.ShouldBe(0ul);
        buffer.GetDirtyRangesSnapshot().Length.ShouldBe(0);
    }

    [Test]
    public void WriterExplicitCommit_MakesDisposeNoOp()
    {
        XRDataBuffer buffer = CreateIntBuffer("ExplicitCommit");
        XRBufferWriter<int> writer = buffer.Alloc<int>(1);
        writer.Span[0] = 42;
        writer.Commit();
        writer.Dispose();

        buffer.Revision.ShouldBe(1ul);
        buffer.GetDataRawAtIndex<int>(0).ShouldBe(42);
    }

    [Test]
    public void WriterExplicitCancel_MakesDisposeNoOp()
    {
        XRDataBuffer buffer = CreateIntBuffer("ExplicitCancel");
        XRBufferWriter<int> writer = buffer.Alloc<int>(1);
        writer.Cancel();
        writer.Dispose();

        buffer.Revision.ShouldBe(0ul);
    }

    [Test]
    public void WriterRequireExplicitCommit_ReportsDisposeWithoutDecision()
    {
        XRDataBuffer buffer = CreateIntBuffer("RequireExplicit");
        XRBufferWriteOptions options = XRBufferWriteOptions.FromBuffer(buffer) with
        {
            DisposeBehavior = XRBufferWriterDisposeBehavior.RequireExplicitCommit,
        };

        XRBufferWriter<int> writer = buffer.Alloc<int>(1, options);
        InvalidOperationException? ex = null;
        try
        {
            writer.Dispose();
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        ex.ShouldNotBeNull();
        buffer.Revision.ShouldBe(0ul);
    }

    [Test]
    public void DirtyRangeMerging_CollapsesToFullUploadPastThreshold()
    {
        XRDataBuffer buffer = CreateIntBuffer("DirtyCollapse");
        buffer.DirtyRangeCollapseThreshold = 2;

        using (XRBufferWriter<int> writer = buffer.Alloc<int>(16, XRBufferWriteMode.Scattered))
        {
            writer.MarkDirty(0, 1);
            writer.MarkDirty(2, 1);
            writer.MarkDirty(4, 1);
        }

        XRBufferDirtyRange range = buffer.GetDirtyRangesSnapshot().ShouldHaveSingleItem();
        range.OffsetBytes.ShouldBe(0u);
        range.LengthBytes.ShouldBe(buffer.ClientSideSource!.Length);
    }

    [Test]
    public void AppendWrite_UploadsOnlyTailRange()
    {
        XRDataBuffer buffer = CreateIntBuffer("Append");
        buffer.SetDataRaw<int>([1, 2]);

        using (XRBufferWriter<int> writer = buffer.Alloc<int>(2, XRBufferWriteMode.Append))
        {
            writer.Span[0] = 3;
            writer.Span[1] = 4;
        }

        buffer.GetDataRawAtIndex<int>(2).ShouldBe(3);
        buffer.GetDataRawAtIndex<int>(3).ShouldBe(4);
        buffer.GetDirtyRangesSnapshot().ShouldBe([new XRBufferDirtyRange(8u, 8u)]);
    }

    [Test]
    public void TypedBufferSetData_SkipsUnchangedContent()
    {
        XRDataBuffer<int> buffer = new("Typed", EBufferTarget.ShaderStorageBuffer);

        buffer.SetData([1, 2, 3]);
        buffer.Revision.ShouldBe(1ul);

        buffer.SetData([1, 2, 3]);
        buffer.Revision.ShouldBe(1ul);
    }

    [Test]
    public void RawUnmanagedSpan_RoundTripsStructPayloadWithoutMarshallingFallback()
    {
        PackedSample[] expected =
        [
            new PackedSample(7, 1.25f, 3),
            new PackedSample(8, 2.5f, 4),
        ];

        XRDataBuffer buffer = new("PackedSpan", EBufferTarget.ShaderStorageBuffer, integral: false)
        {
            PadEndingToVec4 = false,
        };

        buffer.SetDataRaw<PackedSample>(expected);
        buffer.GetDataRaw<PackedSample>(out PackedSample[] actual, validateTypes: false);

        buffer.ComponentType.ShouldBe(EComponentType.Struct);
        buffer.ElementSize.ShouldBe((uint)Unsafe.SizeOf<PackedSample>());
        actual.ShouldBe(expected);
    }

    [Test]
    public void RawUnmanagedIndexAccess_RoundTripsStructPayloadWithoutMarshallingFallback()
    {
        PackedSample expected = new(42, 3.75f, 9);
        XRDataBuffer buffer = new(
            "PackedIndex",
            EBufferTarget.ShaderStorageBuffer,
            elementCount: 2u,
            componentType: EComponentType.Struct,
            componentCount: (uint)Unsafe.SizeOf<PackedSample>(),
            normalize: false,
            integral: false)
        {
            PadEndingToVec4 = false,
        };

        PackedSample first = new(1, 0.5f, 2);
        buffer.SetDataRawAtIndex(0u, first);
        buffer.SetDataRawAtIndex(1u, expected);

        buffer.GetDataRawAtIndex<PackedSample>(0u).ShouldBe(first);
        buffer.GetDataRawAtIndex<PackedSample>(1u).ShouldBe(expected);
    }

    [Test]
    public void CommitDirtyElements_PreservesLayoutAndRecordsRange()
    {
        XRDataBuffer buffer = new("Float2", EBufferTarget.ShaderStorageBuffer, 4u, EComponentType.Float, 2u, false, false)
        {
            PadEndingToVec4 = false,
            DefaultMemoryPolicy = XRBufferMemoryPolicy.CpuToGpuDynamic,
        };

        buffer.SetVector2(2u, new Vector2(3.0f, 4.0f));
        buffer.CommitDirtyElements(2u, 1u);

        buffer.ComponentType.ShouldBe(EComponentType.Float);
        buffer.ComponentCount.ShouldBe(2u);
        buffer.Revision.ShouldBe(1ul);
        buffer.GetDirtyRangesSnapshot().ShouldBe([new XRBufferDirtyRange(16u, 8u)]);
    }

    [Test]
    public void PolicyResolver_MapsLegacyUsageAndBackendRoutes()
    {
        XRBufferPolicyResolver.FromUsage(EBufferUsage.StreamRead, false, 0, 0)
            .ShouldBe(XRBufferMemoryPolicy.GpuToCpuReadback);

        XRBufferPolicyResolver.ResolveOpenGL(
                XRBufferMemoryPolicy.CpuToGpuPersistentRing,
                EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Write,
                EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Write,
                uploadQueueEnabled: true,
                byteCount: 256)
            .ShouldBe(XRBufferResolvedRoute.PersistentMappedRing);

        XRBufferPolicyResolver.ResolveVulkan(
                XRBufferMemoryPolicy.GpuOnly,
                supportsPersistentRing: true,
                supportsDeviceLocal: true)
            .ShouldBe(XRBufferResolvedRoute.DeviceLocal);
    }

    [Test]
    public void ReadbackTicket_DoesNotExposeDataBeforeCompletion()
    {
        XRDataBuffer buffer = CreateIntBuffer("Readback");
        buffer.DefaultMemoryPolicy = XRBufferMemoryPolicy.GpuToCpuReadback;
        buffer.SetDataRaw<int>([123]);

        using XRBufferReadbackTicket ticket = buffer.RequestReadback(0u, 4u);

        ticket.Status.ShouldBe(XRBufferReadbackTicketStatus.Pending);
        ticket.TryGetSpan<int>(out _).ShouldBeFalse();
    }

    [Test]
    public void DeviceAddressQuery_ReportsDowngradeWhenUnsupported()
    {
        XRDataBuffer buffer = CreateIntBuffer("NoAddress");

        buffer.TryGetGpuAddress(out ulong address, out string reason).ShouldBeFalse();

        address.ShouldBe(0ul);
        reason.ShouldContain("No backend");
    }

    [Test]
    public void WriterGrowth_KeepsDescriptorBindingReady()
    {
        XRDataBuffer<int> buffer = new("DescriptorReady", EBufferTarget.ShaderStorageBuffer, 1u)
        {
            BindingIndexOverride = 3u,
            Resizable = true,
        };

        using (XRBufferWriter<int> writer = buffer.Alloc(32u, XRBufferWriteMode.Discard))
            writer.Span[^1] = 99;

        XRBufferStateSnapshot snapshot = buffer.GetStateSnapshot();
        buffer.ElementCount.ShouldBeGreaterThanOrEqualTo(32u);
        snapshot.IsDescriptorBindingReady.ShouldBeTrue();
        snapshot.HasPendingUpload.ShouldBeTrue();
    }

    private static XRDataBuffer CreateIntBuffer(string name)
        => new(name, EBufferTarget.ShaderStorageBuffer, false)
        {
            PadEndingToVec4 = false,
            DefaultMemoryPolicy = XRBufferMemoryPolicy.CpuToGpuDynamic,
        };

    private readonly record struct PackedSample(int Id, float Weight, byte Flags);
}
