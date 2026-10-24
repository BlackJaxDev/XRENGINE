using NUnit.Framework;
using Shouldly;
using System.Numerics;
using System.Reflection;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIRecordStoreTests
{
    [Test]
    public void ReplacingOnePageSharesOtherPagesUntilEachOwnerReleases()
    {
        int claims = 0;
        int[] initial = Enumerable.Range(1, 600).ToArray();
        UIRecordStore<int> first = UIRecordStore<int>.Create(initial,
            _ => claims++, _ => claims--);
        first.PageCount.ShouldBe(3);
        claims.ShouldBe(600);
        first.Retain();

        int[] changed = Enumerable.Repeat(700, 256).ToArray();
        UIRecordStore<int> second = first.WithReplacedPage(1, changed);
        claims.ShouldBe(856);
        second[0].ShouldBe(1);
        second[256].ShouldBe(700);
        second[512].ShouldBe(513);
        first[256].ShouldBe(257);
        first.GetPage(0).ToArray().ShouldBe(second.GetPage(0).ToArray());

        first.Release();
        claims.ShouldBe(856);
        first.Release();
        claims.ShouldBe(600);
        second.Release();
        claims.ShouldBe(0);
    }

    [Test]
    public void FailedPageRetainUnwindsNewAndSharedClaims()
    {
        int claims = 0;
        int[] initial = Enumerable.Range(1, 600).ToArray();
        UIRecordStore<int> store = UIRecordStore<int>.Create(initial,
            value =>
            {
                if (value == 999)
                    throw new InvalidOperationException("Retain rejected");
                claims++;
            }, _ => claims--);
        try
        {
            int[] changed = Enumerable.Repeat(700, 256).ToArray();
            changed[4] = 999;
            Should.Throw<InvalidOperationException>(() =>
                store.WithReplacedPage(1, changed));
            claims.ShouldBe(600);
            store[256].ShouldBe(257);
        }
        finally { store.Release(); }
        claims.ShouldBe(0);
    }

    [Test]
    public void EmptyAndPartialPagesKeepExactLength()
    {
        UIRecordStore<int> empty = UIRecordStore<int>.Create(ReadOnlySpan<int>.Empty);
        empty.Length.ShouldBe(0);
        empty.PageCount.ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => empty.GetPage(0));
        empty.Release();

        UIRecordStore<int> partial = UIRecordStore<int>.Create([1, 2, 3]);
        partial.Length.ShouldBe(3);
        partial.GetPage(0).Length.ShouldBe(3);
        Should.Throw<ArgumentOutOfRangeException>(() =>
            partial.WithReplacedPage(0, [1, 2]));
        partial.Release();
    }

    [Test]
    public void MappedPacketKeepsSharedStoreAfterSourceRetires()
    {
        XRTexture2D texture = new(1, 1, EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, true);
        texture.Mipmaps[0].DataBytes = [1, 2, 3, 4];
        UITextureSnapshot.TryAcquire(texture, out UITextureSnapshot? snapshot,
            out string? reason).ShouldBeTrue(reason);
        UIRenderRecord record = new()
        {
            RenderInfo = null!,
            MembershipId = 1,
            Identity = default,
            WorldMatrix = Matrix4x4.Identity,
            Bounds = Vector4.One,
            RenderPass = 0,
            ZIndex = 0,
            Material = null,
            TextureSnapshot = snapshot,
        };
        UIPublication source = new(1, 1, 1, 1, 1,
            ReadOnlyMemory<UILayoutNodeOutput>.Empty,
            ReadOnlyMemory<UIElementIdentity>.Empty, [], [record],
            new BoundingRectangleF(Vector2.Zero, new Vector2(100)),
            Matrix4x4.Identity, Matrix4x4.Identity);
        snapshot!.Release();
        UIRecordStore<UIRenderRecord> store = source.RenderRecordStore;
        UIPublication mapped = source.WithOutputMapping(Matrix4x4.Identity,
            Matrix4x4.Identity, ECanvasDrawSpace.Screen, source.CanvasBounds,
            Matrix4x4.Identity, Matrix4x4.Identity, 0, 1);
        mapped.RenderRecordStore.ShouldBeSameAs(store);

        texture.Mipmaps[0].DataBytes = [5, 6, 7, 8];
        texture.Mipmaps[0].Invalidate();
        UITextureSnapshot.TryAcquire(texture, out UITextureSnapshot? replacement,
            out reason).ShouldBeTrue(reason);
        replacement.ShouldNotBeSameAs(snapshot);
        replacement!.Release();

        source.Release();
        store.ResourcesRetired.ShouldBeFalse();
        snapshot.Retain();
        snapshot.Release();
        mapped.Release();
        store.ResourcesRetired.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => snapshot.Retain());
    }

    [Test]
    public void TerminalCleanupClosesStoreBeforeReleaseCallback()
    {
        UIRecordStore<int>? store = null;
        bool retainRejected = false;
        bool readRejected = false;
        bool replaceRejected = false;
        store = UIRecordStore<int>.Create([1], _ => { }, _ =>
        {
            UIRecordStore<int> owned = store ?? throw new InvalidOperationException("The store was not assigned.");
            retainRejected = Should.Throw<ObjectDisposedException>(owned.Retain) is not null;
            readRejected = Should.Throw<ObjectDisposedException>(() => owned.GetPage(0)) is not null;
            replaceRejected = Should.Throw<ObjectDisposedException>(() =>
                owned.WithReplacedPage(0, [2])) is not null;
        });

        MethodInfo retire = typeof(UIRecordStore<int>).GetMethod("RetireAbandoned",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        retire.Invoke(store, null);
        GC.SuppressFinalize(store);
        retainRejected.ShouldBeTrue();
        readRejected.ShouldBeTrue();
        replaceRejected.ShouldBeTrue();
        store.ResourcesRetired.ShouldBeTrue();
    }

    [Test]
    public void MappedPacketKeepsPagedHitNavigationAcrossSourceRetirement()
    {
        UIHitRecord[] hits = new UIHitRecord[258];
        for (int index = 0; index < hits.Length; index++)
        {
            Vector2 position = new(index * 20, 0);
            hits[index] = new UIHitRecord(null!, index + 1,
                new UIElementIdentity(index + 1, 1), 0,
                Matrix4x4.CreateTranslation(-position.X, 0, 0),
                new Vector2(10), new BoundingRectangleF(position, new Vector2(10)),
                null, index, -1, index + 1, 0, 0, index,
                index is 0 or 257, false);
        }
        UIPublication source = new(1, 1, 1, 1, 1,
            ReadOnlyMemory<UILayoutNodeOutput>.Empty,
            ReadOnlyMemory<UIElementIdentity>.Empty, hits, [],
            new BoundingRectangleF(Vector2.Zero, new Vector2(5200)),
            Matrix4x4.Identity, Matrix4x4.Identity);
        UIPublication mapped = source.WithOutputMapping(Matrix4x4.Identity,
            Matrix4x4.Identity, ECanvasDrawSpace.Screen, source.CanvasBounds,
            Matrix4x4.Identity, Matrix4x4.Identity, 0, 1);
        UIRecordStore<UIHitRecord> store = source.HitRecordStore;
        source.Release();

        store.ResourcesRetired.ShouldBeFalse();
        mapped.GetHitRecordPage(0).Length.ShouldBe(256);
        mapped.GetHitRecordPage(1).Length.ShouldBe(2);
        UIFocusNavigation.FindTabTarget(mapped, 1, false).ShouldBe(257);
        UIFocusNavigation.FindDirectionalTarget(mapped, 1, Vector2.UnitX).ShouldBe(257);

        mapped.Release();
        store.ResourcesRetired.ShouldBeTrue();
    }

    [Test]
    public void RangeRetainsOnlyIntersectingLeavesAfterStoreRetires()
    {
        int[] claims = new int[3];
        int[] records = Enumerable.Range(0, 768).ToArray();
        UIRecordStore<int> store = UIRecordStore<int>.Create(records,
            value => claims[value / 256]++, value => claims[value / 256]--);
        UIRecordRange<int> range = UIRecordRange<int>.Create(store, 240, 32);
        store.Release();

        claims.ShouldBe(new[] { 256, 256, 0 });
        range.PageCount.ShouldBe(2);
        range.GetPage(0).ToArray().ShouldBe(Enumerable.Range(240, 16).ToArray());
        range.GetPage(1).ToArray().ShouldBe(Enumerable.Range(256, 16).ToArray());
        range.Release();
        claims.ShouldBe(new[] { 0, 0, 0 });
    }

    [Test]
    public void FailedSecondLeafRetainUnwindsFirstLeafClaim()
    {
        UIRecordStore<int> store = UIRecordStore<int>.Create(Enumerable.Range(0, 512).ToArray());
        UIRecordStoreNode<int> first = store.GetLeaf(0);
        UIRecordStoreNode<int> second = store.GetLeaf(1);
        FieldInfo claimsField = typeof(UIRecordStoreNode<int>).GetField("_claims",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        int firstClaims = (int)claimsField.GetValue(first)!;
        claimsField.SetValue(second, int.MaxValue);
        try
        {
            Should.Throw<OverflowException>(() => UIRecordRange<int>.Create(store, 0, 512));
            ((int)claimsField.GetValue(first)!).ShouldBe(firstClaims);
        }
        finally
        {
            claimsField.SetValue(second, 1);
            store.Release();
        }
    }

    [Test]
    public void SpliceSharesUntouchedPagesAndCopiesOnlyTouchedPages()
    {
        bool countersWereEnabled = UIRuntimeCounters.Enabled;
        UIRuntimeCounters.Enabled = true;
        int claims = 0;
        UIRecordStore<int> store = UIRecordStore<int>.Create(Enumerable.Range(0, 2560).ToArray(),
            _ => claims++, _ => claims--);
        UIRecordStore<int>? spliced = null;
        try
        {
            long copied = UIRuntimeCounters.Read(UIWorkCounter.PublicationRecordCellsCopied);
            // Remove one record from page 3 and insert two records into page 7.
            spliced = store.Splice([new UIRecordSplice(800, 1, 0, 0), new UIRecordSplice(1800, 0, 0, 2)],
                [-1, -2]);

            (UIRuntimeCounters.Read(UIWorkCounter.PublicationRecordCellsCopied) - copied)
                .ShouldBeLessThanOrEqualTo(2 * UIRecordStore<int>.PageSize + 2);
            spliced.Length.ShouldBe(2561);
            spliced.HasUniformPages.ShouldBeFalse();
            // Page 7 grew past one page and split, so later pages move by one.
            for (int page = 0; page < 7; page++)
                ReferenceEquals(spliced.GetLeaf(page), store.GetLeaf(page)).ShouldBe(page != 3);
            spliced.PageCount.ShouldBe(11);
            spliced.GetLeaf(9).ShouldBeSameAs(store.GetLeaf(8));
            spliced.GetLeaf(10).ShouldBeSameAs(store.GetLeaf(9));
            List<int> expected = Enumerable.Range(0, 2560).ToList();
            expected.RemoveAt(800);
            expected.InsertRange(1799, [-1, -2]);
            AssertSame(spliced, expected);
            store[800].ShouldBe(800);
            claims.ShouldBe(2560 + 255 + 258);
        }
        finally
        {
            spliced?.Release();
            store.Release();
            UIRuntimeCounters.Enabled = countersWereEnabled;
        }
        claims.ShouldBe(0);
    }

    [Test]
    public void RepeatedSplicesMatchAReferenceList()
    {
        Random random = new(1234);
        List<int> expected = Enumerable.Range(0, 1500).ToList();
        int claims = 0;
        UIRecordStore<int> store = UIRecordStore<int>.Create(expected.ToArray(),
            _ => claims++, _ => claims--);
        int nextValue = 10_000;
        try
        {
            for (int step = 0; step < 200; step++)
            {
                int editCount = random.Next(1, 4);
                List<UIRecordSplice> edits = [];
                List<int> inserted = [];
                int cursor = 0;
                for (int edit = 0; edit < editCount && cursor <= expected.Count; edit++)
                {
                    int start = random.Next(cursor, Math.Min(expected.Count, cursor + 600) + 1);
                    int remove = random.Next(0, Math.Min(expected.Count - start, 40) + 1);
                    int add = random.Next(0, 40);
                    edits.Add(new UIRecordSplice(start, remove, inserted.Count, add));
                    for (int index = 0; index < add; index++)
                        inserted.Add(nextValue++);
                    cursor = start + remove;
                }
                for (int edit = edits.Count - 1; edit >= 0; edit--)
                {
                    UIRecordSplice splice = edits[edit];
                    expected.RemoveRange(splice.OldStart, splice.OldCount);
                    expected.InsertRange(splice.OldStart, inserted.GetRange(splice.NewStart, splice.NewCount));
                }
                UIRecordStore<int> next = store.Splice([.. edits], [.. inserted]);
                store.Release();
                store = next;
                claims.ShouldBe(expected.Count);
                if (step % 20 == 0)
                    AssertSame(store, expected);
            }
            AssertSame(store, expected);
            // Short pages are packed again before they double the page count.
            store.PageCount.ShouldBeLessThanOrEqualTo((store.Length + 255) / 256 * 2 + 4);

            int page = store.PageCount / 2;
            int[] replaced = store.GetPage(page).ToArray();
            replaced[0] = -7;
            UIRecordStore<int> patched = store.WithReplacedPages([page], [replaced]);
            patched[store.GetPageStart(page)].ShouldBe(-7);
            patched.Release();
        }
        finally { store.Release(); }
        claims.ShouldBe(0);
    }

    [Test]
    public void SpliceCanEmptyAndRefillAStore()
    {
        UIRecordStore<int> store = UIRecordStore<int>.Create([1, 2, 3]);
        UIRecordStore<int> empty = store.Splice([new UIRecordSplice(0, 3, 0, 0)], []);
        UIRecordStore<int> refilled = empty.Splice([new UIRecordSplice(0, 0, 0, 2)], [7, 8]);
        UIRecordStore<int> same = refilled.Splice([], []);
        try
        {
            empty.Length.ShouldBe(0);
            empty.PageCount.ShouldBe(0);
            AssertSame(refilled, [7, 8]);
            same.ShouldBeSameAs(refilled);
            Should.Throw<ArgumentOutOfRangeException>(() =>
                store.Splice([new UIRecordSplice(2, 0, 0, 0), new UIRecordSplice(1, 0, 0, 0)], []));
            Should.Throw<ArgumentOutOfRangeException>(() =>
                store.Splice([new UIRecordSplice(0, 4, 0, 0)], []));
        }
        finally
        {
            same.Release();
            refilled.Release();
            empty.Release();
            store.Release();
        }
    }

    [Test]
    public void RangeOverSplicedPagesReadsExactRecords()
    {
        UIRecordStore<int> store = UIRecordStore<int>.Create(Enumerable.Range(0, 1024).ToArray());
        UIRecordStore<int> spliced = store.Splice([new UIRecordSplice(300, 100, 0, 0)], []);
        UIRecordRange<int> range = UIRecordRange<int>.Create(spliced, 200, 500);
        UIRecordRange<int> shared = range.Share();
        try
        {
            List<int> expected = Enumerable.Range(0, 1024).ToList();
            expected.RemoveRange(300, 100);
            for (int index = 0; index < 500; index++)
                shared[index].ShouldBe(expected[200 + index]);
            int[] copy = new int[500];
            range.CopyTo(copy);
            copy.ShouldBe(expected.GetRange(200, 500).ToArray());
        }
        finally
        {
            shared.Release();
            range.Release();
            spliced.Release();
            store.Release();
        }
    }

    private static void AssertSame(UIRecordStore<int> store, IReadOnlyList<int> expected)
    {
        store.Length.ShouldBe(expected.Count);
        int[] copy = new int[store.Length];
        store.CopyTo(copy);
        copy.ShouldBe(expected.ToArray());
        for (int index = 0; index < expected.Count; index += 37)
            store[index].ShouldBe(expected[index]);
        int total = 0;
        for (int page = 0; page < store.PageCount; page++)
        {
            store.GetPageStart(page).ShouldBe(total);
            store.GetPage(page).Length.ShouldBeGreaterThan(0);
            total += store.GetPageLength(page);
        }
        total.ShouldBe(expected.Count);
    }
}
