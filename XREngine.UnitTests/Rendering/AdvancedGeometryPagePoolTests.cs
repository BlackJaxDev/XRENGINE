using System;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The geometry page pool maps the pages of registered geometries to pool slots. These tests check
/// the slot and page table reservation, the deferred release of retired geometries until their
/// publication is acknowledged, and the reuse of freed page table entries.
/// </summary>
[TestFixture]
public sealed class AdvancedGeometryPagePoolTests
{
    [Test]
    public void TryReserve_GivesEachPageASlotAndAContiguousTableRange()
    {
        AdvancedGeometryPagePool pool = CreatePool(8);
        MeshletPageSet first = CreatePages(3);
        MeshletPageSet second = CreatePages(2);

        pool.TryReserve(first, out AdvancedGeometryPagePool.PageRange firstRange).ShouldBeTrue();
        pool.TryReserve(second, out AdvancedGeometryPagePool.PageRange secondRange).ShouldBeTrue();

        firstRange.ShouldBe(new AdvancedGeometryPagePool.PageRange(0u, 3));
        secondRange.ShouldBe(new AdvancedGeometryPagePool.PageRange(3u, 2));
        pool.UsedSlotCount.ShouldBe(5);
        pool.PageTableLength.ShouldBe(5);

        uint[] table = [];
        int length = pool.CapturePageTable(ref table, out _);
        length.ShouldBe(5);
        byte[]?[] images = new byte[]?[pool.SlotCount];
        pool.CaptureSlotImages(images);
        for (int page = 0; page < 3; ++page)
            images[table[page]].ShouldBeSameAs(first.PageImages[page]);
        for (int page = 0; page < 2; ++page)
            images[table[3 + page]].ShouldBeSameAs(second.PageImages[page]);
        table.AsSpan(0, length).ToArray().ShouldBeUnique();
    }

    [Test]
    public void TryReserve_WithoutEnoughFreeSlots_ChangesNothing()
    {
        AdvancedGeometryPagePool pool = CreatePool(4);
        pool.TryReserve(CreatePages(3), out _).ShouldBeTrue();
        ulong version = pool.Version;

        pool.TryReserve(CreatePages(2), out _).ShouldBeFalse();

        pool.UsedSlotCount.ShouldBe(3);
        pool.PageTableLength.ShouldBe(3);
        pool.Version.ShouldBe(version);
    }

    [Test]
    public void TryReserve_RejectsAnImageLargerThanASlot()
    {
        AdvancedGeometryPagePool pool = CreatePool(4);
        MeshletPageSet pages = new(null!, [new byte[AdvancedGeometryPagePool.SlotBytes + 4]], default);

        pool.TryReserve(pages, out _).ShouldBeFalse();
        pool.UsedSlotCount.ShouldBe(0);
    }

    [Test]
    public void RetireGeometry_KeepsSlotsUntilThePublicationIsAcknowledged()
    {
        AdvancedGeometryPagePool pool = CreatePool(4);
        AdvancedGpuHandle geometry = new(1u, 1u);
        pool.TryReserve(CreatePages(3), out AdvancedGeometryPagePool.PageRange range).ShouldBeTrue();
        pool.Bind(geometry, range);

        pool.RetireGeometry(geometry, publicationGeneration: 10u);

        uint[] table = [];
        int length = pool.CapturePageTable(ref table, out _);
        for (int page = 0; page < length; ++page)
            table[page].ShouldBe(AdvancedGeometryPagePool.NotResident);
        pool.UsedSlotCount.ShouldBe(3);
        pool.TryReserve(CreatePages(2), out _).ShouldBeFalse();

        pool.ReclaimAcknowledged(9u).ShouldBe(0);
        pool.UsedSlotCount.ShouldBe(3);

        pool.ReclaimAcknowledged(10u).ShouldBe(1);
        pool.UsedSlotCount.ShouldBe(0);
        pool.PageTableLength.ShouldBe(0);
        byte[]?[] images = new byte[]?[pool.SlotCount];
        pool.CaptureSlotImages(images);
        images.ShouldAllBe(image => image == null);
    }

    [Test]
    public void ReleaseGeometry_FreesSlotsAtOnce()
    {
        AdvancedGeometryPagePool pool = CreatePool(4);
        AdvancedGpuHandle geometry = new(2u, 1u);
        pool.TryReserve(CreatePages(4), out AdvancedGeometryPagePool.PageRange range).ShouldBeTrue();
        pool.Bind(geometry, range);

        pool.ReleaseGeometry(geometry);

        pool.UsedSlotCount.ShouldBe(0);
        pool.TryReserve(CreatePages(4), out _).ShouldBeTrue();
    }

    [Test]
    public void FreedRanges_AreReusedAndMerge()
    {
        AdvancedGeometryPagePool pool = CreatePool(16);
        pool.TryReserve(CreatePages(2), out AdvancedGeometryPagePool.PageRange a).ShouldBeTrue();
        pool.TryReserve(CreatePages(2), out AdvancedGeometryPagePool.PageRange b).ShouldBeTrue();
        pool.TryReserve(CreatePages(2), out AdvancedGeometryPagePool.PageRange c).ShouldBeTrue();

        pool.Release(a);
        pool.Release(b);
        // The two freed ranges merge, so a range of four pages fits before the third range.
        pool.TryReserve(CreatePages(4), out AdvancedGeometryPagePool.PageRange merged).ShouldBeTrue();
        merged.FirstPage.ShouldBe(0u);
        pool.PageTableLength.ShouldBe(6);

        // A free range at the end of the table shortens the table.
        pool.Release(c);
        pool.PageTableLength.ShouldBe(4);
    }

    [Test]
    public void Version_ChangesWithTheTable()
    {
        AdvancedGeometryPagePool pool = CreatePool(4);
        AdvancedGpuHandle geometry = new(3u, 1u);
        ulong initial = pool.Version;

        pool.TryReserve(CreatePages(1), out AdvancedGeometryPagePool.PageRange range).ShouldBeTrue();
        pool.Bind(geometry, range);
        ulong reserved = pool.Version;
        reserved.ShouldNotBe(initial);

        pool.RetireGeometry(geometry, 5u);
        ulong retired = pool.Version;
        retired.ShouldNotBe(reserved);

        pool.ReclaimAcknowledged(5u);
        pool.Version.ShouldNotBe(retired);
    }

    [Test]
    public void EnsureConfigured_KeepsTheFirstSlotCount()
    {
        AdvancedGeometryPagePool pool = new();
        pool.SlotCount.ShouldBe(0);
        pool.TryReserve(CreatePages(1), out _).ShouldBeFalse();

        pool.EnsureConfigured(4);
        pool.EnsureConfigured(64);

        pool.SlotCount.ShouldBe(4);
        pool.PoolBytes.ShouldBe(4L * AdvancedGeometryPagePool.SlotBytes);
    }

    private static AdvancedGeometryPagePool CreatePool(int slotCount)
    {
        AdvancedGeometryPagePool pool = new();
        pool.EnsureConfigured(slotCount);
        return pool;
    }

    /// <summary>
    /// Creates a page set with small distinct page images. The pool reads only the images, so the
    /// page table is left out.
    /// </summary>
    private static MeshletPageSet CreatePages(int pageCount)
    {
        byte[][] images = new byte[pageCount][];
        for (int page = 0; page < pageCount; ++page)
            images[page] = new byte[64];
        return new MeshletPageSet(null!, images, default);
    }
}
