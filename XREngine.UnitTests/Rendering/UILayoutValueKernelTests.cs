using System.Collections.Frozen;
using System.Numerics;
using System.Threading;
using NUnit.Framework;
using XREngine.Rendering;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UILayoutValueKernelTests
{
    [Test, NonParallelizable]
    public void InterruptedScrollApplyPreservesSettledExtentAndReappliesCachedPage()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIScrollableTransform scroll = new()
        {
            Parent = canvas, Width = 100, Height = 100, MaxAnchor = Vector2.Zero
        };
        UIBoundableTransform content = new()
        {
            Parent = scroll, Width = 240, Height = 100, MaxAnchor = Vector2.Zero
        };
        try
        {
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            ulong firstGeneration = canvas.CurrentLayoutGeneration;
            scroll.ContentExtent.X.ShouldBe(240);
            scroll.MaxScrollOffset.X.ShouldBe(140);

            bool interrupted = false;
            content.PropertyChanged += (_, change) =>
            {
                if (interrupted || change.PropertyName != nameof(UITransform.ActualLocalBottomLeftTranslation))
                    return;
                interrupted = true;
                scroll.ScrollOffset = Vector2.Zero;
            };
            scroll.ScrollOffset = new Vector2(96, 0);
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() =>
            {
                canvas.PumpLayoutResults();
                return interrupted;
            }, TimeSpan.FromSeconds(5)).ShouldBeTrue();

            // Candidate A wrote a child placement before its callback rejected
            // publication. Pre-capture must retain settled limits.
            scroll.ContentExtent.X.ShouldBe(240);
            scroll.MaxScrollOffset.X.ShouldBe(140);

            SpinWait.SpinUntil(() =>
            {
                canvas.UpdateLayoutAsync();
                canvas.PumpLayoutResults();
                return canvas.CurrentLayoutGeneration > firstGeneration && !canvas.IsLayoutInvalidated;
            }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
            content.ActualLocalBottomLeftTranslation.X.ShouldBe(0);
            scroll.ContentExtent.X.ShouldBe(240);
            scroll.MaxScrollOffset.X.ShouldBe(140);
        }
        finally
        {
            content.Destroy(true);
            scroll.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void ScrollExtentUsesPlacementOffsetOfAppliedChildGeometry()
    {
        UIScrollableTransform scroll = new();
        UIBoundableTransform content = new() { Parent = scroll };
        UILayoutNodeOutput contentOutput = new()
        {
            ActualSize = new Vector2(240, 100),
            DesiredSize = new Vector2(240, 100)
        };
        Vector2 viewport = new(100, 100);

        content.ApplyCalculatedLayout(contentOutput, Vector2.Zero);
        scroll.SynchronizeAppliedScroll(viewport, Vector2.Zero);
        scroll.ContentExtent.X.ShouldBe(240);
        scroll.MaxScrollOffset.X.ShouldBe(140);

        // Authored input advances while the child still shows the old accepted
        // placement. Pre-capture synchronization must not add 96 to its extent.
        scroll.ScrollOffset = new Vector2(96, 0);
        scroll.SynchronizeScroll(viewport);
        scroll.ContentExtent.X.ShouldBe(240);
        scroll.MaxScrollOffset.X.ShouldBe(140);

        // After the new result applies, use its scroll offset as the basis for
        // recovering logical content coordinates from the child's placement.
        content.ApplyCalculatedLayout(contentOutput, new Vector2(-96, 0));
        scroll.SynchronizeAppliedScroll(viewport, new Vector2(96, 0));
        scroll.ContentExtent.X.ShouldBe(240);
        scroll.MaxScrollOffset.X.ShouldBe(140);

        contentOutput.ActualSize = new Vector2(100, 240);
        contentOutput.LocalTranslation = new Vector2(0, -140);
        content.ApplyCalculatedLayout(contentOutput, Vector2.Zero);
        scroll.SynchronizeAppliedScroll(viewport, Vector2.Zero);
        scroll.ScrollOffset = new Vector2(0, 96);
        scroll.SynchronizeScroll(viewport);
        scroll.ContentExtent.Y.ShouldBe(240);
        scroll.MaxScrollOffset.Y.ShouldBe(140);

        content.ApplyCalculatedLayout(contentOutput, new Vector2(0, 96));
        scroll.SynchronizeAppliedScroll(viewport, new Vector2(0, 96));
        scroll.ContentExtent.Y.ShouldBe(240);
        scroll.MaxScrollOffset.Y.ShouldBe(140);
    }

    [Test]
    public void ScrollbarMetricsComeFromAcceptedScrollableOutput()
    {
        UILayoutNodeInput[] nodes =
        [
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Scrollable,
                Parent = -1,
                ChildCount = 1,
                Width = 100,
                Height = 50,
                MaxAnchor = Vector2.Zero,
                OperatorValues = new(12, 34, 80, 120)
            },
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Boundable,
                Parent = 0,
                Width = 300,
                Height = 300,
                MaxAnchor = Vector2.Zero
            }
        ];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 1)];

        UILayoutNodeOutput[] result = UILayoutValueKernel.Run(nodes, children,
            new BoundingRectangleF(0, 0, 100, 50));

        result[0].AcceptedScrollOffset.ShouldBe(new Vector2(12, 34));
        result[0].AcceptedMaxScrollOffset.ShouldBe(new Vector2(80, 120));
        result[1].AcceptedScrollOffset.ShouldBe(Vector2.Zero);
        result[1].PlacementTranslation.ShouldBe(new Vector2(-12, 34));
    }

    [Test]
    public void ListSeparatesRawIntrinsicSizeFromDesiredOuterSize()
    {
        UILayoutNodeInput[] nodes =
        [
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Canvas,
                Parent = -1,
                FirstChild = 0,
                ChildCount = 1,
                Width = 100,
                Height = 100,
                MaxAnchor = Vector2.Zero
            },
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.List,
                Parent = 0,
                FirstChild = 1,
                ChildCount = 1,
                MaxAnchor = Vector2.Zero,
                OperatorFlags = 1,
                OperatorValues = new(float.NaN, 0, 0, 0)
            },
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Boundable,
                Parent = 1,
                Width = 10,
                Height = 20,
                MinWidth = 30,
                Margins = new(2, 0, 2, 0),
                MaxAnchor = Vector2.Zero
            }
        ];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 1), new(2, 0, 0, 1, 1)];

        UILayoutNodeOutput[] result = UILayoutValueKernel.Run(nodes, children,
            new BoundingRectangleF(0, 0, 100, 100));

        result[1].RawSize.X.ShouldBe(10f);
        result[1].DesiredSize.X.ShouldBe(34f);
        result[1].ActualSize.X.ShouldBe(10f);
    }

    [Test]
    public void DualSplitKeepsAllocationAndPlacementSeparate()
    {
        UILayoutNodeInput[] nodes =
        [
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.DualSplit,
                Parent = -1,
                FirstChild = 0,
                ChildCount = 2,
                Width = 100,
                Height = 40,
                MaxAnchor = Vector2.Zero,
                OperatorValues = new(0.5f, float.NaN, 4, 0)
            },
            default(UILayoutNodeInput) with { Operator = UILayoutOperator.Boundable, Parent = 0, MaxAnchor = Vector2.One },
            default(UILayoutNodeInput) with { Operator = UILayoutOperator.Boundable, Parent = 0, MaxAnchor = Vector2.One }
        ];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 1), new(2, 0, 0, 1, 1)];

        UILayoutNodeOutput[] result = UILayoutValueKernel.Run(nodes, children,
            new BoundingRectangleF(0, 0, 100, 40));

        result[0].ActualSize.ShouldBe(new Vector2(100, 40));
        result[1].AllocatedBounds.Width.ShouldBe(50f);
        result[2].AllocatedBounds.Width.ShouldBe(50f);
        result[2].PlacementTranslation.X.ShouldBe(54f);
        result[2].LocalTranslation.X.ShouldBe(0f);
    }

    [Test]
    public void MeasureCacheReusesVisualOnlyEditAndRejectsLayoutEdit()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform child = new() { Parent = canvas, Width = 10, Height = 10, MaxAnchor = Vector2.Zero };
        try
        {
            UILayoutSnapshot before = UILayoutSnapshot.Capture(canvas, []);
            UILayoutNodeOutput[] previous = UILayoutValueKernel.Run(before, null, null);
            previous[1].RawSize = new(777, 777);

            child.RecordUIChange(XREngine.Rendering.UI.State.UIChangeDomain.Visual);
            UILayoutSnapshot visual = UILayoutSnapshot.Capture(canvas, []);
            UILayoutNodeOutput[] reused = UILayoutValueKernel.Run(visual, before, previous);
            reused[1].RawSize.X.ShouldBe(777f);

            child.Width = 25;
            UILayoutSnapshot changed = UILayoutSnapshot.Capture(canvas, []);
            UILayoutNodeOutput[] measured = UILayoutValueKernel.Run(changed, visual, reused);
            measured[1].RawSize.X.ShouldBe(25f);
        }
        finally
        {
            child.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void PendingCapturesRebaseBothSiblingEditsAgainstAcceptedOutput()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform first = new() { Parent = canvas, Width = 10, Height = 10 };
        UIBoundableTransform second = new() { Parent = canvas, Width = 20, Height = 10 };
        try
        {
            List<UITransform> acceptedTargets = [];
            UILayoutSnapshot accepted = UILayoutSnapshot.Capture(canvas, acceptedTargets);
            UILayoutOutputPageView acceptedOutput = UILayoutValueKernel.RunPaged(accepted, null, null);
            UIElementChange[] changes = new UIElementChange[canvas.ChangeJournal.Capacity];
            canvas.ChangeJournal.TryDrain(changes, out _).ShouldBeTrue();

            first.Width = 25;
            canvas.ChangeJournal.TryDrain(changes, out UIChangeBatch firstBatch).ShouldBeTrue();
            List<UITransform> firstTargets = [];
            UILayoutSnapshot firstPending = UILayoutSnapshot.CaptureIncremental(canvas,
                accepted, acceptedTargets, firstTargets, changes.AsSpan(0, firstBatch.Count).ToArray());

            second.Width = 35;
            canvas.ChangeJournal.TryDrain(changes, out UIChangeBatch secondBatch).ShouldBeTrue();
            UILayoutSnapshot latest = UILayoutSnapshot.CaptureIncremental(canvas,
                firstPending, firstTargets, [], changes.AsSpan(0, secondBatch.Count).ToArray());
            UILayoutOutputPageView result = UILayoutValueKernel.RunPaged(latest, accepted, acceptedOutput);

            result[1].RawSize.X.ShouldBe(25f);
            result[2].RawSize.X.ShouldBe(35f);
        }
        finally
        {
            second.Destroy(true);
            first.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void IncrementalCaptureReusesStableTopologyAndRefreshesChangedValues()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform child = new() { Parent = canvas, Width = 10, Height = 10 };
        try
        {
            List<UITransform> beforeTargets = [];
            UILayoutSnapshot before = UILayoutSnapshot.Capture(canvas, beforeTargets);
            child.Width = 25;
            List<UITransform> afterTargets = [];
            UILayoutSnapshot after = UILayoutSnapshot.CaptureIncremental(canvas, before, beforeTargets, afterTargets);
            after.Children.ShouldBe(before.Children);
            after.Nodes[1].Width.ShouldBe(25f);
            afterTargets[1].ShouldBeSameAs(child);
            before.Nodes[1].Width.ShouldBe(10f);
        }
        finally
        {
            child.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void JournalCaptureSharesStableTopologyMetadataAndUpdatesOnlyDirtyBranch()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform first = new() { Parent = canvas, Width = 10, Height = 10 };
        UIBoundableTransform second = new() { Parent = canvas, Width = 20, Height = 20 };
        try
        {
            List<UITransform> beforeTargets = [];
            UILayoutSnapshot before = UILayoutSnapshot.Capture(canvas, beforeTargets);
            UIElementChange[] changes = new UIElementChange[canvas.ChangeJournal.Capacity];
            canvas.ChangeJournal.TryDrain(changes, out _).ShouldBeTrue();

            first.Width = 25;
            canvas.ChangeJournal.TryDrain(changes, out var batch).ShouldBeTrue();
            List<UITransform> afterTargets = [];
            UILayoutSnapshot after = UILayoutSnapshot.CaptureIncremental(canvas, before,
                beforeTargets, afterTargets, changes.AsSpan(0, batch.Count).ToArray());

            after.SharesTopologyMetadataWith(before).ShouldBeTrue();
            after.Children.ShouldBeSameAs(before.Children);
            after.Nodes[1].Width.ShouldBe(25f);
            after.Nodes[2].ShouldBe(before.Nodes[2]);
            second.Width.ShouldBe(20f);
        }
        finally
        {
            second.Destroy(true);
            first.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void ArrangeCacheReusesUnchangedSubtreeForVisualEdit()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform child = new() { Parent = canvas, Width = 10, Height = 10 };
        try
        {
            List<UITransform> beforeTargets = [];
            UILayoutSnapshot before = UILayoutSnapshot.Capture(canvas, beforeTargets);
            UILayoutNodeOutput[] prior = UILayoutValueKernel.Run(before, null, null);
            prior[1].LocalTranslation = new(123, 456);
            child.RecordUIChange(XREngine.Rendering.UI.State.UIChangeDomain.Visual);
            UILayoutSnapshot after = UILayoutSnapshot.CaptureIncremental(canvas, before, beforeTargets, []);
            UILayoutNodeOutput[] reused = UILayoutValueKernel.Run(after, before, prior);
            reused[1].LocalTranslation.ShouldBe(new Vector2(123, 456));
        }
        finally
        {
            child.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void OutputPageBuilderCopiesOnlyEditedPageAndKeepsPriorViewImmutable()
    {
        UILayoutNodeOutput[] values = new UILayoutNodeOutput[768];
        values[600].RawSize = new Vector2(10, 20);
        UILayoutOutputPageView before = UILayoutOutputPageView.CopyFrom(values);
        UILayoutOutputPageBuilder next = new(values.Length, before);
        next.Edit(600).RawSize = new Vector2(30, 40);
        UILayoutOutputPageView after = next.Freeze();

        after.SharesPageWith(before, 0).ShouldBeTrue();
        after.SharesPageWith(before, 300).ShouldBeTrue();
        after.SharesPageWith(before, 600).ShouldBeFalse();
        after.CopiedNodes.ShouldBe(256);
        before[600].RawSize.ShouldBe(new Vector2(10, 20));
        after[600].RawSize.ShouldBe(new Vector2(30, 40));
    }

    [Test]
    public void PartialOutputPageRoundTripsWithoutOverrun()
    {
        UILayoutNodeOutput[] values = new UILayoutNodeOutput[257];
        values[256].RawSize = new Vector2(7, 9);

        UILayoutOutputPageView view = UILayoutOutputPageView.CopyFrom(values);

        view.ToArray().ShouldBe(values);
        view[256].RawSize.ShouldBe(new Vector2(7, 9));
    }

    [Test]
    public void InputValuePagesCopyOnlyEditedPage()
    {
        uint[] values = new uint[768];
        values[600] = 1;
        UILayoutValuePageView<uint> before = UILayoutValuePageView<uint>.CopyFrom(values);
        UILayoutValuePageBuilder<uint> builder = new(before);
        builder.Edit(600) = 2;
        UILayoutValuePageView<uint> after = builder.Freeze();

        after.SharesPageWith(before, 0).ShouldBeTrue();
        after.SharesPageWith(before, 300).ShouldBeTrue();
        after.SharesPageWith(before, 600).ShouldBeFalse();
        before[600].ShouldBe(1u);
        after[600].ShouldBe(2u);
    }

    [Test]
    public void LeafEditSharesUnchangedInputAndOutputPages()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        List<UIBoundableTransform> leaves = [];
        try
        {
            for (int i = 0; i < 520; i++)
                leaves.Add(new UIBoundableTransform
                {
                    Parent = canvas, Width = 10, Height = 10, MaxAnchor = Vector2.Zero
                });
            List<UITransform> beforeTargets = [];
            UILayoutSnapshot before = UILayoutSnapshot.Capture(canvas, beforeTargets);
            UILayoutOutputPageView prior = UILayoutValueKernel.RunPaged(before, null, null);
            UIElementChange[] journal = new UIElementChange[canvas.ChangeJournal.Capacity];
            canvas.ChangeJournal.TryDrain(journal, out _).ShouldBeTrue();

            leaves[^1].Width = 30;
            canvas.ChangeJournal.TryDrain(journal, out UIChangeBatch changes).ShouldBeTrue();
            List<UITransform> afterTargets = [];
            UILayoutSnapshot after = UILayoutSnapshot.CaptureIncremental(canvas, before,
                beforeTargets, afterTargets, journal.AsSpan(0, changes.Count).ToArray());
            UILayoutOutputPageView result = UILayoutValueKernel.RunPaged(after, before, prior);

            after.Nodes.SharesPageWith(before.Nodes, 300).ShouldBeTrue();
            result.SharesPageWith(prior, 300).ShouldBeTrue();
            result[520].RawSize.X.ShouldBe(30);
            prior[520].RawSize.X.ShouldBe(10);
            result.VisitedNodes.ShouldBeLessThan(16);
            result.CopiedNodes.ShouldBeLessThanOrEqualTo(512);
        }
        finally
        {
            foreach (UIBoundableTransform leaf in leaves)
                leaf.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void CancelledWorkerStopsBeforeTraversal()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        try
        {
            UILayoutSnapshot snapshot = UILayoutSnapshot.Capture(canvas, []);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                UILayoutValueKernel.Run(snapshot, null, null, cancellation.Token));
        }
        finally
        {
            canvas.Destroy(true);
        }
    }

    [Test]
    public void SerialAndSnapshotWorkerEntryUseTheSameKernel()
    {
        UICanvasTransform canvas = new() { Width = 200, Height = 100 };
        UIBoundableTransform child = new() { Parent = canvas, Width = 30, Height = 20 };
        try
        {
            UILayoutSnapshot snapshot = UILayoutSnapshot.Capture(canvas, []);
            UILayoutNodeOutput[] serial = UILayoutValueKernel.Run(snapshot.Nodes, snapshot.Children, snapshot.RootBounds);
            UILayoutNodeOutput[] worker = UILayoutValueKernel.Run(snapshot, null, null);
            worker.ShouldBe(serial);
        }
        finally
        {
            child.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void PublicMeasureAndArrangeUseAcceptedCanvasLayout()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform child = new()
        {
            Parent = canvas, Width = 25, Height = 10, MaxAnchor = Vector2.Zero
        };
        try
        {
            Should.Throw<InvalidOperationException>(() => child.Measure(new Vector2(100, 100)));
            child.ActualSize.ShouldBe(Vector2.Zero);
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();

            child.Measure(new Vector2(100, 100)).ShouldBe(new Vector2(25, 10));
            child.Arrange(new BoundingRectangleF(0, 0, 1, 1));
            child.ActualSize.ShouldBe(new Vector2(25, 10));
        }
        finally
        {
            child.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void DetachedMeasureUsesBoundedValueKernelWithoutApplyingGeometry()
    {
        UIBoundableTransform detached = new()
        {
            Width = 25, Height = 10, MaxAnchor = Vector2.Zero
        };
        try
        {
            detached.Measure(new Vector2(100, 100)).ShouldBe(new Vector2(25, 10));
            detached.ActualSize.ShouldBe(Vector2.Zero);
            Should.Throw<InvalidOperationException>(() =>
                detached.Arrange(new BoundingRectangleF(0, 0, 100, 100)));
        }
        finally { detached.Destroy(true); }
    }

    [Test]
    public void WarmIdleCanvasRequestsAllocateNoManagedStorage()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        try
        {
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            ulong generation = canvas.CurrentLayoutGeneration;
            for (int index = 0; index < 100; index++)
            {
                canvas.UpdateLayoutAsync();
                canvas.PumpLayoutResults();
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1000; index++)
            {
                canvas.UpdateLayoutAsync();
                canvas.PumpLayoutResults();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            allocated.ShouldBe(0);
            canvas.CurrentLayoutGeneration.ShouldBe(generation);
        }
        finally { canvas.Destroy(true); }
    }

    [Test, NonParallelizable]
    public void CompletedUnpumpedPageKeepsQuotaUntilOwnerRetires()
    {
        int pages = UILayoutPageLease.RetainedPages;
        long bytes = UILayoutPageLease.RetainedWorkingBytes;
        UILayoutPageLease lease = UILayoutPageLease.TryAcquire()!;
        lease.ShouldNotBeNull();
        try
        {
            lease.StartWorker();
            lease.FinishWorker();
            lease.IsReleased.ShouldBeFalse();
            UILayoutPageLease.RetainedPages.ShouldBe(pages + 1);
            UILayoutPageLease.RetainedWorkingBytes.ShouldBe(bytes + UILayoutSnapshot.MaxWorkingBytes);
            lease.RetireOwner();
            lease.IsReleased.ShouldBeTrue();
            UILayoutPageLease.RetainedPages.ShouldBe(pages);
            UILayoutPageLease.RetainedWorkingBytes.ShouldBe(bytes);
        }
        finally
        {
            lease.FinishWorker();
            lease.RetireOwner();
        }
    }

    [Test, NonParallelizable]
    public void CanceledRunningPageReleasesOnlyAfterWorkerStopsOnce()
    {
        int pages = UILayoutPageLease.RetainedPages;
        long bytes = UILayoutPageLease.RetainedWorkingBytes;
        UILayoutPageLease lease = UILayoutPageLease.TryAcquire()!;
        lease.ShouldNotBeNull();
        try
        {
            lease.StartWorker();
            lease.RetireOwner();
            lease.RetireOwner();
            lease.IsReleased.ShouldBeFalse();
            UILayoutPageLease.RetainedPages.ShouldBe(pages + 1);
            lease.FinishWorker();
            lease.FinishWorker();
            lease.IsReleased.ShouldBeTrue();
            UILayoutPageLease.RetainedPages.ShouldBe(pages);
            UILayoutPageLease.RetainedWorkingBytes.ShouldBe(bytes);
        }
        finally
        {
            lease.FinishWorker();
            lease.RetireOwner();
        }
    }

    [Test, NonParallelizable]
    public void LayoutStartCallbackCannotDispatchASecondWorkerOrResurrectCanceledWork()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform child = new() { Parent = canvas, Width = 10, Height = 10 };
        int starts = 0;
        try
        {
            canvas.LayoutingStarted += _ =>
            {
                starts++;
                child.Width = 20;
                canvas.UpdateLayoutAsync();
                canvas.CancelLayoutJob();
            };
            canvas.UpdateLayoutAsync();

            starts.ShouldBe(1);
            canvas.IsUpdatingLayout.ShouldBeFalse();
            canvas.IsLayoutInvalidated.ShouldBeTrue();
            canvas.CurrentLayoutOutputs.Length.ShouldBe(0);
            canvas.PumpLayoutResults().ShouldBeFalse();
        }
        finally
        {
            child.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test, NonParallelizable]
    public void NestedCanvasTransitionRejectsCallbackDispatch()
    {
        UICanvasTransform root = new() { Width = 100, Height = 100 };
        UICanvasTransform canvas = new() { Width = 50, Height = 50 };
        try
        {
            canvas.LayoutingStarted += _ => canvas.Parent = root;
            canvas.UpdateLayoutAsync();

            canvas.IsNestedCanvas.ShouldBeTrue();
            canvas.IsUpdatingLayout.ShouldBeFalse();
            canvas.CurrentLayoutOutputs.Length.ShouldBe(0);
            canvas.PumpLayoutResults().ShouldBeFalse();
        }
        finally
        {
            canvas.Destroy(true);
            root.Destroy(true);
        }
    }

    [Test, NonParallelizable]
    public void LayoutStartCallbackFailureRetainsInvalidation()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        try
        {
            canvas.LayoutingStarted += _ => throw new InvalidOperationException("layout callback");
            canvas.UpdateLayoutAsync();

            canvas.IsLayoutInvalidated.ShouldBeTrue();
            canvas.IsUpdatingLayout.ShouldBeFalse();
            canvas.CurrentLayoutOutputs.Length.ShouldBe(0);
        }
        finally
        {
            canvas.Destroy(true);
        }
    }

    [Test, NonParallelizable]
    public void LayoutFinishCallbackCancellationDoesNotPublishOrRestart()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        int finished = 0;
        try
        {
            canvas.LayoutingFinished += _ =>
            {
                finished++;
                canvas.CancelLayoutJob();
            };
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() =>
            {
                canvas.PumpLayoutResults();
                return finished != 0;
            }, TimeSpan.FromSeconds(5)).ShouldBeTrue();

            canvas.IsUpdatingLayout.ShouldBeFalse();
            canvas.IsLayoutInvalidated.ShouldBeTrue();
            canvas.CurrentLayoutOutputs.Length.ShouldBe(0);
        }
        finally
        {
            canvas.Destroy(true);
        }
    }

    [Test, NonParallelizable]
    public void CapacityRefusalKeepsDirtyInputsForTheNextAdmission()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform child = new()
        {
            Width = 20, Height = 10, MinAnchor = Vector2.Zero, MaxAnchor = Vector2.Zero, Parent = canvas
        };
        List<UILayoutPageLease> held = [];
        try
        {
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            while (UILayoutPageLease.TryAcquire() is { } lease)
                held.Add(lease);
            child.Width = 37;
            canvas.UpdateLayoutAsync();
            canvas.IsLayoutInvalidated.ShouldBeTrue();
            canvas.ChangeJournal.PendingCount.ShouldBeGreaterThan(0);
            foreach (UILayoutPageLease lease in held)
                lease.RetireOwner();
            held.Clear();

            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            child.ActualWidth.ShouldBe(37);
            canvas.IsLayoutInvalidated.ShouldBeFalse();
        }
        finally
        {
            foreach (UILayoutPageLease lease in held)
                lease.RetireOwner();
            canvas.Destroy(true);
        }
    }

    [Test]
    public void ApplyingDerivedGeometryPreservesAuthoredTransformDependencies()
    {
        UIBoundableTransform transform = new();
        try
        {
            var before = transform.ChangeRevisions;
            UILayoutNodeOutput output = new()
            {
                ActualSize = new Vector2(70, 20),
                LocalTranslation = new Vector2(11, 12),
            };
            transform.ApplyCalculatedLayout(output, Vector2.Zero);
            transform.RefreshAxisAlignedRegion();
            transform.ChangeRevisions.Transform.ShouldBe(before.Transform);
            transform.ActualSize.ShouldBe(output.ActualSize);
            transform.Scale = new Vector3(2);
            transform.ChangeRevisions.Transform.ShouldBeGreaterThan(before.Transform);
        }
        finally { transform.Destroy(true); }
    }

    [Test, NonParallelizable]
    public void OwnerPumpDrainsVisualJournalWithoutStartingLayout()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        try
        {
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            canvas.RecordUIChange(XREngine.Rendering.UI.State.UIChangeDomain.Visual);
            canvas.ChangeJournal.PendingCount.ShouldBeGreaterThan(0);

            canvas.UpdateLayoutAsync();

            canvas.ChangeJournal.PendingCount.ShouldBe(0);
            canvas.IsLayoutInvalidated.ShouldBeFalse();
        }
        finally
        {
            canvas.Destroy(true);
        }
    }

    [Test]
    public void DependencyCheckAllowsVisualEditButRejectsGeometryAndDetach()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        UIBoundableTransform child = new() { Parent = canvas, Width = 10, Height = 10 };
        try
        {
            List<UITransform> targets = [];
            UILayoutSnapshot snapshot = UILayoutSnapshot.Capture(canvas, targets);
            UILayoutCanvasSession.DependenciesMatch(canvas, snapshot, targets).ShouldBeTrue();

            child.RecordUIChange(XREngine.Rendering.UI.State.UIChangeDomain.Visual);
            UILayoutCanvasSession.DependenciesMatch(canvas, snapshot, targets).ShouldBeTrue();

            child.Width = 20;
            UILayoutCanvasSession.DependenciesMatch(canvas, snapshot, targets).ShouldBeFalse();

            List<UITransform> afterSizeTargets = [];
            UILayoutSnapshot afterSize = UILayoutSnapshot.Capture(canvas, afterSizeTargets);
            child.Parent = null;
            UILayoutCanvasSession.DependenciesMatch(canvas, afterSize, afterSizeTargets).ShouldBeFalse();
        }
        finally
        {
            child.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test]
    public void FontMetricMutationRejectsCapturedLayoutBeforeQueuedUiNotification()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        FontGlyphSet font = new();
        try
        {
            List<UITransform> targets = [];
            UILayoutSnapshot captured = UILayoutSnapshot.Capture(canvas, targets);
            UILayoutCanvasSession.DependenciesMatch(canvas, captured, targets).ShouldBeTrue();

            font.Glyphs = new Dictionary<string, FontGlyphSet.Glyph>();

            UILayoutCanvasSession.DependenciesMatch(canvas, captured, targets).ShouldBeFalse();
        }
        finally
        {
            font.Destroy(true);
            canvas.Destroy(true);
        }
    }

    [Test, NonParallelizable]
    public void BackendReplacementRejectsEveryCapturedCanvasGeneration()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        IUITextShapingBackend? original = UITextShapingBackendRegistry.Current;
        try
        {
            List<UITransform> targets = [];
            UILayoutSnapshot captured = UILayoutSnapshot.Capture(canvas, targets);
            UILayoutCanvasSession.DependenciesMatch(canvas, captured, targets).ShouldBeTrue();
            UITextShapingBackendRegistry.Current = new UITextMutatingShapingBackend(_ => { });
            UILayoutCanvasSession.DependenciesMatch(canvas, captured, targets).ShouldBeFalse();
        }
        finally
        {
            UITextShapingBackendRegistry.Current = original;
            canvas.Destroy(true);
        }
    }

    [Test, NonParallelizable]
    public void StaleShapingFaultDoesNotBlockNewerPendingText()
    {
        using UITestShaderScope scope = new();
        XREngine.Scene.SceneNode root = new("Shaping fault canvas", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        UICanvasTransform canvas = (UICanvasTransform)root.Transform;
        canvas.Width = 100;
        canvas.Height = 40;
        XREngine.Scene.SceneNode child = new(root) { Name = "Text" };
        child.SetTransform<UIBoundableTransform>();
        FontGlyphSet font = new()
        {
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                ["A"] = new(new Vector2(8), Vector2.Zero),
                ["B"] = new(new Vector2(8), Vector2.Zero)
            },
            Atlas = new XRTexture2D(8, 8, XREngine.Data.Rendering.EPixelInternalFormat.Rgba8,
                XREngine.Data.Rendering.EPixelFormat.Rgba, XREngine.Data.Rendering.EPixelType.UnsignedByte, true),
            LayoutEmSize = 8
        };
        UIRuneMetrics metric = new(Vector2.Zero, new(8, 8), 9, new(0, 0, 8, 8));
        font.InstallRuntimeShapingFace(new UIFontFaceSnapshot([1], 0, 8,
            new Dictionary<uint, UIRuneMetrics> { [51] = metric }));
        IUITextShapingBackend? original = UITextShapingBackendRegistry.Current;
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        try
        {
            UITextShapingBackendRegistry.Current = new UITextMutatingShapingBackend(request =>
            {
                if (request.Text != "A")
                    return;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The stale worker was not released.");
                throw new InvalidOperationException("Old text failed after a newer edit.");
            });
            UITextComponent text = child.AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            text.UseShapedText = true;
            canvas.UpdateLayoutAsync();
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();

            text.Text = "B";
            canvas.UpdateLayoutAsync();
            release.Set();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            canvas.IsLayoutInvalidated.ShouldBeFalse();
            canvas.CurrentLayoutOutputs.Span[1].TextResult.ShouldNotBeNull()
                .Glyphs.Span[0].GlyphId.ShouldBe(51u);
        }
        finally
        {
            release.Set();
            UITextShapingBackendRegistry.Current = original;
            canvas.CancelLayoutJob();
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void CanvasDpiChangeRejectsCapturedLayout()
    {
        UICanvasTransform canvas = new() { Width = 100, Height = 100 };
        try
        {
            List<UITransform> targets = [];
            UILayoutSnapshot snapshot = UILayoutSnapshot.Capture(canvas, targets);
            canvas.DpiScale = 2;

            UILayoutCanvasSession.DependenciesMatch(canvas, snapshot, targets).ShouldBeFalse();
            canvas.IsLayoutInvalidated.ShouldBeTrue();
        }
        finally
        {
            canvas.Destroy(true);
        }
    }

    [Test]
    public void TextDpiScaleChangesAutoSizedLayoutGeometry()
    {
        var metrics = new Dictionary<int, UIRuneMetrics>
        {
            ['A'] = new(Vector2.Zero, new(8, 10), 9, new(0, 0, 8, 10))
        }.ToFrozenDictionary();
        UITextLayoutInput text = new("A", metrics, 1, 1, 10, new(16, 16), 10,
            FontGlyphSet.EWrapMode.None, 0, 0, EHorizontalAlignment.Left,
            EVerticalAlignment.Bottom, Vector4.Zero);
        UILayoutNodeInput[] nodes =
        [
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Boundable,
                Parent = -1,
                MaxAnchor = Vector2.Zero,
                TextInput = text
            }
        ];
        BoundingRectangleF bounds = new(0, 0, 100, 100);
        UILayoutNodeOutput[] normal = UILayoutValueKernel.Run(nodes, [], bounds);
        nodes[0] = nodes[0] with { TextInput = text with { DpiScale = 2 } };
        UILayoutNodeOutput[] scaled = UILayoutValueKernel.Run(nodes, [], bounds);

        scaled[0].RawSize.ShouldBe(normal[0].RawSize * 2);
        scaled[0].ActualSize.ShouldBe(normal[0].ActualSize * 2);
    }

    [Test]
    public void NarrowListMeasuresWrappedTextBeforePlacingFollowingItem()
    {
        var metrics = new Dictionary<int, UIRuneMetrics>
        {
            ['A'] = new(Vector2.Zero, new(8, 10), 9, new(0, 0, 8, 10))
        }.ToFrozenDictionary();
        UITextLayoutInput wrapped = new("AAAAA", metrics, 1, 1, 10, new(16, 16), 10,
            FontGlyphSet.EWrapMode.Character, 0, 0, EHorizontalAlignment.Left,
            EVerticalAlignment.Bottom, Vector4.Zero);
        UILayoutNodeInput[] nodes =
        [
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.List, Parent = -1, FirstChild = 0,
                ChildCount = 2, Width = 20, Height = 100, MaxAnchor = Vector2.Zero,
                OperatorValues = new(float.NaN, 0, 0, 0)
            },
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Boundable, Parent = 0,
                MaxAnchor = Vector2.One, TextInput = wrapped
            },
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Boundable, Parent = 0,
                MaxAnchor = Vector2.One, TextInput = wrapped with { Text = "A" }
            }
        ];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 1), new(2, 0, 0, 1, 1)];

        UILayoutNodeOutput[] result = UILayoutValueKernel.Run(nodes, children,
            new BoundingRectangleF(0, 0, 100, 100));

        result[1].DesiredSize.Y.ShouldBeGreaterThan(10);
        result[1].RawSize.Y.ShouldBe(result[1].TextResult!.IntrinsicSize.Y, 0.01f);
        (result[2].PlacementTranslation.Y + result[2].ActualSize.Y)
            .ShouldBeLessThanOrEqualTo(result[1].PlacementTranslation.Y + 0.01f);
    }

    [Test]
    public void TabSelectionAndHiddenAncestorProduceEffectiveVisibility()
    {
        UILayoutNodeInput[] nodes =
        [
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Tab,
                Parent = -1,
                FirstChild = 0,
                ChildCount = 2,
                Width = 100,
                Height = 20,
                MaxAnchor = Vector2.Zero,
                OperatorFlags = 1,
                Hidden = true
            },
            default(UILayoutNodeInput) with { Operator = UILayoutOperator.Boundable, Parent = 0 },
            default(UILayoutNodeInput) with { Operator = UILayoutOperator.Boundable, Parent = 0 }
        ];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 1), new(2, 0, 0, 1, 1)];

        UILayoutNodeOutput[] result = UILayoutValueKernel.Run(nodes, children,
            new BoundingRectangleF(0, 0, 100, 20));

        result[0].Visible.ShouldBeFalse();
        result[1].Visible.ShouldBeFalse();
        result[2].Visible.ShouldBeFalse();

        nodes[0] = nodes[0] with { Hidden = false };
        UILayoutNodeOutput[] selected = UILayoutValueKernel.Run(nodes, children,
            new BoundingRectangleF(0, 0, 100, 20));
        selected[1].Visible.ShouldBeFalse();
        selected[2].Visible.ShouldBeTrue();
    }
}
