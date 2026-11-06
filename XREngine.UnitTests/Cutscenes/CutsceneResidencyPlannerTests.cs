using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneResidencyPlannerTests
{
    private const double Frame = 1.0 / 60.0;

    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = new("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = new("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = new("00000000-0000-0000-0000-00000000000e");

    [Test]
    public void Update_SortsRequestsByTierThenDeadlineThenCoverageDescending()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B, C, D],
            [
                Bucket(0.0, 0.25, new ResidencyEntry(0, 2, 0.2f), new ResidencyEntry(1, 2, 0.8f)),
                Bucket(0.5, 0.75, new ResidencyEntry(3, 2, 0.9f)),
                Bucket(1.0, 1.25, new ResidencyEntry(2, 1, 0.5f)),
            ]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);
        backend.SetLevel(A, EResidencyLevel.Coarse);
        backend.SetLevel(B, EResidencyLevel.Coarse);
        backend.SetLevel(C, EResidencyLevel.Proxy);
        backend.SetLevel(D, EResidencyLevel.Coarse);

        planner.Update(CutsceneTime.Zero, 1.0, null, Frame);

        ResidencyRequest[] upper = backend.UpperRequests.Select(r => r.Request).ToArray();
        upper.Select(r => r.AssetId).ShouldBe([C, B, A, D]);
        upper.Select(r => r.TargetLevel).ShouldBe([1, 2, 2, 2]);
        upper[0].Deadline.ShouldBe(S(1.0));
        upper[1].Deadline.ShouldBe(CutsceneTime.Zero);
        upper[3].Deadline.ShouldBe(S(0.5));
        upper[1].ScreenCoverage.ShouldBe(0.8f);
    }

    [Test]
    public void Open_RequestsProxyForEachAssetInFirstNeededOrder()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B],
            [
                Bucket(0.0, 0.25, new ResidencyEntry(1, 2, 0.5f)),
                Bucket(3.0, 3.25, new ResidencyEntry(0, 2, 0.5f)),
            ]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);

        backend.Requests.Select(r => r.Request.AssetId).ShouldBe([B, A]);
        backend.Requests.ShouldAllBe(r => r.Request.TargetLevel == 0);
        planner.PendingProxyCount.ShouldBe(2);

        backend.Complete(backend.Requests[0].Ticket);
        planner.Update(CutsceneTime.Zero, 0.0, null, Frame);
        planner.PendingProxyCount.ShouldBe(1);
    }

    [Test]
    public void Update_Jump_CancelsOlderTicketsAndRequestsWithNewGeneration()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B],
            [
                Bucket(0.0, 0.25, new ResidencyEntry(0, 1, 0.5f)),
                Bucket(10.0, 10.25, new ResidencyEntry(0, 1, 0.5f), new ResidencyEntry(1, 1, 0.5f)),
            ]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);
        List<Guid> completed = [];
        planner.LoadCompleted += (asset, _) => completed.Add(asset);

        planner.Update(CutsceneTime.Zero, 1.0, null, Frame);
        (ResidencyTicket oldTicket, ResidencyRequest oldRequest) = backend.UpperRequests.Single();
        oldRequest.AssetId.ShouldBe(A);
        oldRequest.Generation.ShouldBe(0);

        planner.Update(S(10.0), 1.0, ESeekIntent.Jump, Frame);

        planner.Generation.ShouldBe(1);
        backend.Cancelled.ShouldContain(oldTicket);
        ResidencyRequest[] newRequests = backend.UpperRequests.Skip(1).Select(r => r.Request).ToArray();
        newRequests.Select(r => r.AssetId).ShouldBe([A, B], ignoreOrder: true);
        newRequests.ShouldAllBe(r => r.Generation == 1);

        // A late result of the older generation can enter the cache, but it runs no callback.
        backend.Complete(oldTicket);
        int requestCount = backend.Requests.Count;
        planner.Update(S(10.0), 1.0, null, Frame);
        completed.ShouldBeEmpty();
        backend.Requests.Count.ShouldBe(requestCount);
    }

    [Test]
    public void Update_Scrub_SendsNothingAboveProxyUntilDwell()
    {
        CutsceneResidencyManifest manifest = new([A], [Bucket(5.0, 5.25, new ResidencyEntry(0, 2, 0.5f))]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);

        planner.Update(S(5.0), 0.0, ESeekIntent.Scrub, Frame);
        planner.Update(S(5.1), 0.0, ESeekIntent.Scrub, 0.1);
        planner.Update(S(5.2), 0.0, ESeekIntent.Scrub, 0.1);
        backend.UpperRequests.ShouldBeEmpty();

        planner.Update(S(5.2), 0.0, null, 0.05);
        planner.Update(S(5.2), 0.0, null, 0.05);
        backend.UpperRequests.ShouldBeEmpty();

        planner.Update(S(5.2), 0.0, null, 0.05);
        (ResidencyTicket ticket, ResidencyRequest request) = backend.UpperRequests.Single();
        request.AssetId.ShouldBe(A);
        request.TargetLevel.ShouldBe(1);

        // A new drag closes the gate and cancels the work that did not complete.
        planner.Update(S(5.0), 0.0, ESeekIntent.Scrub, Frame);
        backend.Cancelled.ShouldContain(ticket);
    }

    [Test]
    public void Update_Jump_SkipsDwellGate()
    {
        CutsceneResidencyManifest manifest = new([A], [Bucket(5.0, 5.25, new ResidencyEntry(0, 2, 0.5f))]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);

        planner.Update(S(5.1), 0.0, ESeekIntent.Jump, Frame);

        backend.UpperRequests.Single().Request.AssetId.ShouldBe(A);
    }

    [Test]
    public void Update_LookAheadFollowsDirectionAndScalesWithRate()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B, C],
            [
                Bucket(7.0, 7.25, new ResidencyEntry(1, 1, 0.5f)),
                Bucket(13.0, 13.25, new ResidencyEntry(0, 1, 0.5f)),
                Bucket(18.0, 18.25, new ResidencyEntry(2, 1, 0.5f)),
            ]);

        CutsceneResidencyPlanner forward = Create(manifest, out FakeResidencyBackend forwardBackend);
        forward.Update(S(10.0), 1.0, null, Frame);
        forwardBackend.UpperRequests.Select(r => r.Request.AssetId).ShouldBe([A]);

        CutsceneResidencyPlanner reverse = Create(manifest, out FakeResidencyBackend reverseBackend);
        reverse.Update(S(10.0), -1.0, null, Frame);
        reverseBackend.UpperRequests.Select(r => r.Request.AssetId).ShouldBe([B]);

        CutsceneResidencyPlanner fast = Create(manifest, out FakeResidencyBackend fastBackend);
        fast.Update(S(10.0), 2.0, null, Frame);
        fastBackend.UpperRequests.Select(r => r.Request.AssetId).ShouldBe([A, C]);
    }

    [Test]
    public void Update_RateChange_FlipsWindow()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B],
            [
                Bucket(7.0, 7.25, new ResidencyEntry(1, 1, 0.5f)),
                Bucket(13.0, 13.25, new ResidencyEntry(0, 1, 0.5f)),
            ]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);

        planner.Update(S(10.0), 1.0, null, Frame);
        ResidencyTicket forwardTicket = backend.UpperRequests.Single().Ticket;

        planner.Update(S(10.0), -1.0, null, Frame);

        backend.Cancelled.ShouldContain(forwardTicket);
        backend.UpperRequests.Last().Request.AssetId.ShouldBe(B);
    }

    [Test]
    public void Close_CancelsAllTicketsAndLateCompletionRunsNoCallback()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B],
            [Bucket(0.0, 0.25, new ResidencyEntry(0, 1, 0.5f), new ResidencyEntry(1, 1, 0.5f))]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);
        List<(Guid Asset, int Level)> completed = [];
        planner.LoadCompleted += (asset, level) => completed.Add((asset, level));

        ResidencyTicket proxyA = backend.Requests[0].Ticket;
        ResidencyTicket proxyB = backend.Requests[1].Ticket;
        planner.Update(CutsceneTime.Zero, 1.0, null, Frame);
        ResidencyTicket upperA = backend.UpperRequests.Single(r => r.Request.AssetId == A).Ticket;
        ResidencyTicket upperB = backend.UpperRequests.Single(r => r.Request.AssetId == B).Ticket;

        backend.Complete(upperA);
        planner.Update(CutsceneTime.Zero, 1.0, null, Frame);
        completed.ShouldBe([(A, 1)]);

        planner.Close();

        planner.IsClosed.ShouldBeTrue();
        backend.Cancelled.ShouldBe([upperB, upperA, proxyA, proxyB], ignoreOrder: true);

        int requestCount = backend.Requests.Count;
        int cancelCount = backend.Cancelled.Count;
        backend.Complete(upperB);
        backend.Complete(proxyA);
        planner.Update(S(1.0), 1.0, ESeekIntent.Jump, Frame);
        planner.Close();

        completed.Count.ShouldBe(1);
        backend.Requests.Count.ShouldBe(requestCount);
        backend.Cancelled.Count.ShouldBe(cancelCount);
    }

    [Test]
    public void Close_FromCompletionHandler_StopsTheRemainingCallbacks()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B],
            [Bucket(0.0, 0.25, new ResidencyEntry(0, 1, 0.5f), new ResidencyEntry(1, 1, 0.5f))]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);
        int callbacks = 0;
        planner.LoadCompleted += (_, _) =>
        {
            callbacks++;
            planner.Close();
        };

        backend.Complete(backend.Requests[0].Ticket);
        backend.Complete(backend.Requests[1].Ticket);
        planner.Update(CutsceneTime.Zero, 1.0, null, Frame);

        callbacks.ShouldBe(1);
        planner.IsClosed.ShouldBeTrue();
        backend.UpperRequests.ShouldBeEmpty();
    }

    [Test]
    public void Update_WithNoChange_AllocatesNothingAndSendsNothing()
    {
        Guid[] assets = [A, B, C, D];
        ResidencyBucket[] buckets = new ResidencyBucket[80];
        for (int b = 0; b < buckets.Length; b++)
            buckets[b] = Bucket(b * 0.25, (b + 1) * 0.25, new ResidencyEntry(b % assets.Length, 2, 0.5f));
        CutsceneResidencyManifest manifest = new(assets, buckets);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);

        RunPlayFrames(planner);
        planner.Update(CutsceneTime.FromSeconds(0.1), 0.0, ESeekIntent.Scrub, Frame);
        RunScrubFrames(planner);
        planner.Update(CutsceneTime.FromSeconds(0.1), 1.0, ESeekIntent.Jump, Frame);
        RunPlayFrames(planner);

        int requestCount = backend.Requests.Count;
        int cancelCount = backend.Cancelled.Count;
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunPlayFrames(planner);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
        backend.Requests.Count.ShouldBe(requestCount);
        backend.Cancelled.Count.ShouldBe(cancelCount);
    }

    [Test]
    public void Evict_TakesUpperLevelsFarthestFromPlayheadAndNeverProxy()
    {
        CutsceneResidencyManifest manifest = new(
            [A, B, C, D, E],
            [
                Bucket(0.0, 0.25, new ResidencyEntry(0, 1, 0.5f)),
                Bucket(2.0, 2.25, new ResidencyEntry(1, 1, 0.5f)),
                Bucket(4.0, 4.25, new ResidencyEntry(2, 1, 0.5f)),
                Bucket(6.0, 6.25, new ResidencyEntry(3, 1, 0.5f)),
                Bucket(8.0, 8.25, new ResidencyEntry(4, 1, 0.5f)),
            ]);
        CutsceneResidencyPlanner planner = Create(manifest, out FakeResidencyBackend backend);
        ResidencyTicket[] proxies = backend.Requests.Select(r => r.Ticket).ToArray();
        foreach (ResidencyTicket proxy in proxies)
            backend.Complete(proxy);

        planner.Update(CutsceneTime.Zero, 2.0, null, Frame);
        Dictionary<Guid, ResidencyTicket> upper = backend.UpperRequests.ToDictionary(r => r.Request.AssetId, r => r.Ticket);
        upper.Count.ShouldBe(5);
        foreach (ResidencyTicket ticket in upper.Values)
            backend.Complete(ticket);
        planner.Update(CutsceneTime.Zero, 2.0, null, Frame);
        backend.Cancelled.ShouldBeEmpty();

        planner.Evict(2).ShouldBe(2);
        backend.Cancelled.ShouldBe([upper[E], upper[D]]);
        backend.GetLevel(E).ShouldBe(EResidencyLevel.Proxy);

        planner.Evict(10).ShouldBe(3);
        backend.Cancelled.ShouldBe([upper[E], upper[D], upper[C], upper[B], upper[A]]);
        backend.Cancelled.ShouldNotContain(t => proxies.Contains(t));
        planner.Evict(10).ShouldBe(0);
    }

    [Test]
    public void PlaybackWait_HoldsAtCutUntilTargetIsResident()
    {
        CutsceneResidencyPlanner planner = CreateCutPlanner(out FakeResidencyBackend backend);

        planner.Update(S(0.5), 1.0, null, Frame);
        planner.Update(S(1.01), 1.0, null, Frame);
        planner.IsHoldingPlayback.ShouldBeTrue();
        planner.PlaybackHoldTime.ShouldBe(S(1.0));

        planner.Update(S(1.0), 1.0, null, 0.1);
        planner.IsHoldingPlayback.ShouldBeTrue();

        backend.SetLevel(B, EResidencyLevel.Target);
        planner.Update(S(1.0), 1.0, null, 0.1);
        planner.IsHoldingPlayback.ShouldBeFalse();
        planner.PlaybackWaitTimeoutCount.ShouldBe(0);

        planner.Update(S(1.02), 1.0, null, Frame);
        planner.IsHoldingPlayback.ShouldBeFalse();
    }

    [Test]
    public void PlaybackWait_ReleasesAtMaximumWait()
    {
        CutsceneResidencyPlanner planner = CreateCutPlanner(out _);

        planner.Update(S(0.5), 1.0, null, Frame);
        planner.Update(S(1.01), 1.0, null, Frame);
        planner.Update(S(1.0), 1.0, null, 0.2);
        planner.Update(S(1.0), 1.0, null, 0.2);
        planner.IsHoldingPlayback.ShouldBeTrue();

        planner.Update(S(1.0), 1.0, null, 0.2);
        planner.IsHoldingPlayback.ShouldBeFalse();
        planner.PlaybackWaitTimeoutCount.ShouldBe(1);

        planner.Update(S(1.03), 1.0, null, Frame);
        planner.IsHoldingPlayback.ShouldBeFalse();
    }

    [Test]
    public void PlaybackWait_NeverHoldsForJumpScrubReverseOrWhenOff()
    {
        CutsceneResidencyPlanner jump = CreateCutPlanner(out _);
        jump.Update(S(0.5), 1.0, null, Frame);
        jump.Update(S(1.5), 1.0, ESeekIntent.Jump, Frame);
        jump.IsHoldingPlayback.ShouldBeFalse();
        jump.Update(S(1.6), 1.0, null, Frame);
        jump.IsHoldingPlayback.ShouldBeFalse();

        CutsceneResidencyPlanner scrub = CreateCutPlanner(out _);
        scrub.Update(S(0.5), 1.0, null, Frame);
        scrub.Update(S(1.5), 1.0, ESeekIntent.Scrub, Frame);
        scrub.IsHoldingPlayback.ShouldBeFalse();

        CutsceneResidencyPlanner reverse = CreateCutPlanner(out _);
        reverse.Update(S(2.5), -1.0, null, Frame);
        reverse.Update(S(0.9), -1.0, null, Frame);
        reverse.IsHoldingPlayback.ShouldBeFalse();

        CutsceneResidencyPlanner off = CreateCutPlanner(out _);
        off.PlaybackWaitEnabled = false;
        off.Update(S(0.5), 1.0, null, Frame);
        off.Update(S(1.01), 1.0, null, Frame);
        off.IsHoldingPlayback.ShouldBeFalse();

        CutsceneResidencyPlanner held = CreateCutPlanner(out _);
        held.Update(S(0.5), 1.0, null, Frame);
        held.Update(S(1.01), 1.0, null, Frame);
        held.IsHoldingPlayback.ShouldBeTrue();
        held.Update(S(1.5), 1.0, ESeekIntent.Jump, Frame);
        held.IsHoldingPlayback.ShouldBeFalse();
    }

    [Test]
    public void Manifest_RejectsOverlappingBucketsAndUnknownAssets()
    {
        Should.Throw<ArgumentException>(() => new CutsceneResidencyManifest(
            [A],
            [Bucket(0.0, 1.0), Bucket(0.5, 1.5)]));
        Should.Throw<ArgumentException>(() => new CutsceneResidencyManifest(
            [A],
            [Bucket(0.0, 1.0, new ResidencyEntry(1, 1, 0.5f))]));
        Should.Throw<ArgumentException>(() => new CutsceneResidencyManifest([A, A], []));
    }

    private static void RunPlayFrames(CutsceneResidencyPlanner planner)
    {
        // The playhead moves inside one bucket, and the window edges stay inside their buckets.
        for (int k = 0; k < 1000; k++)
            planner.Update(new CutsceneTime(CutsceneTime.FromSeconds(0.1).Flicks + k * 70_560L), 1.0, null, Frame);
    }

    private static void RunScrubFrames(CutsceneResidencyPlanner planner)
    {
        for (int k = 0; k < 100; k++)
            planner.Update(CutsceneTime.FromSeconds(0.1), 0.0, null, Frame);
    }

    private static CutsceneResidencyPlanner CreateCutPlanner(out FakeResidencyBackend backend)
    {
        CutsceneResidencyManifest manifest = new(
            [A, B],
            [
                Bucket(0.0, 1.0, new ResidencyEntry(0, 1, 0.5f)),
                new ResidencyBucket(S(1.0), S(2.0), [new ResidencyEntry(1, 2, 0.5f)], startsAtCut: true),
            ]);
        CutsceneResidencyPlanner planner = Create(manifest, out backend);
        planner.PlaybackWaitEnabled = true;
        planner.PlaybackWaitMaximumSeconds = 0.5;
        return planner;
    }

    private static CutsceneResidencyPlanner Create(CutsceneResidencyManifest manifest, out FakeResidencyBackend backend)
    {
        backend = new FakeResidencyBackend();
        CutsceneResidencyPlanner planner = new(manifest, backend);
        backend.Planner = planner;
        planner.Open();
        return planner;
    }

    private static ResidencyBucket Bucket(double start, double end, params ResidencyEntry[] entries)
        => new(S(start), S(end), entries);

    private static CutsceneTime S(double seconds)
        => CutsceneTime.FromSeconds(seconds);
}
