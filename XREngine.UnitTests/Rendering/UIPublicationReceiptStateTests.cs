using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using XREngine.Components;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIPublicationReceiptStateTests
{
    [Test]
    public void SurfacePoolBackpressuresFourRetainedGenerationsWithoutResizingReaders()
    {
        using var pool = new UIRenderSurfacePool(EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
        UIRenderSurface[] retained = new UIRenderSurface[4];
        for (int index = 0; index < retained.Length; index++)
        {
            pool.TryReserve(16, 16, out UIRenderSurface? writer, out string? failure)
                .ShouldBeTrue(failure);
            writer.ShouldNotBeNull();
            ulong generation = writer.ReservationGeneration;
            writer.Retain(); // a packet or GPU artifact claim
            using var fence = new TestSurfaceFence { Status = EGpuFenceStatus.Signaled };
            pool.TryPublish(writer, generation, fence).ShouldBeTrue();
            writer.Release(); // writer claim
            retained[index] = writer;
        }
        pool.SlotCount.ShouldBe(4);
        pool.TryReserve(32, 32, out _, out string? refusal).ShouldBeFalse();
        refusal.ShouldNotBeNull();
        refusal.ShouldContain("Capacity");
        retained[0].Width.ShouldBe(16u);
        retained[0].Height.ShouldBe(16u);

        retained[1].Release();
        pool.TryReserve(32, 32, out UIRenderSurface? resized, out string? retryFailure)
            .ShouldBeTrue(retryFailure);
        resized.ShouldNotBeNull();
        resized.ShouldBeSameAs(retained[1]);
        resized.Width.ShouldBe(32u);
        resized.Height.ShouldBe(32u);
        retained[0].Width.ShouldBe(16u);
        resized.TryComplete(resized.ReservationGeneration - 1).ShouldBeFalse();
        resized.Release();
        retained[0].Release();
        retained[2].Release();
        retained[3].Release();
    }

    [Test]
    public void MappedPacketKeepsItsReservedSurfaceUntilTheLastReaderReleases()
    {
        using var pool = new UIRenderSurfacePool(EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
        pool.TryReserve(8, 8, out UIRenderSurface? writer, out string? failure)
            .ShouldBeTrue(failure);
        writer.ShouldNotBeNull();
        UIPublication source = new(1, 1, 1, 1, 1, default, default, [], [],
            new BoundingRectangleF(0, 0, 8, 8), Matrix4x4.Identity,
            Matrix4x4.Identity, offscreenSurface: writer);
        UIPublication mapped = source.WithOutputMapping(Matrix4x4.Identity,
            Matrix4x4.Identity, ECanvasDrawSpace.World,
            new BoundingRectangleF(0, 0, 8, 8), Matrix4x4.Identity,
            Matrix4x4.Identity, 0, 1);
        writer.Release();
        source.Release();
        writer.IsPoolOnly.ShouldBeFalse();
        mapped.Release();
        writer.IsPoolOnly.ShouldBeTrue();
    }

    [Test]
    public void OutputVisualVariantSharesHitGenerationButUsesDistinctRenderRecords()
    {
        UIPublication source = EmptyPublication(17);
        UIRenderRecord replacement = default(UIRenderRecord) with { MembershipId = 91 };
        UIPublication variant = source.WithOutputVariant([replacement], 31,
            Matrix4x4.Identity, Matrix4x4.Identity,
            new BoundingRectangleF(0, 0, 8, 8));
        variant.Generation.ShouldBe(source.Generation);
        variant.HitGeneration.ShouldBe(source.HitGeneration);
        variant.RenderGeneration.ShouldBe(31UL);
        variant.Renders.Length.ShouldBe(1);
        variant.TryGetRenderRecord(91, out _).ShouldBeTrue();
        source.Renders.Length.ShouldBe(0);
        source.Release();
        variant.Renders.Length.ShouldBe(1);
        variant.Release();
    }

    [Test]
    public void SurfaceGenerationRequiresSubmittedAndSignaledProducerFence()
    {
        using var pool = new UIRenderSurfacePool(EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
        pool.TryReserve(8, 8, out UIRenderSurface? writer, out _).ShouldBeTrue();
        writer.ShouldNotBeNull();
        ulong generation = writer.ReservationGeneration;
        writer.Generation.ShouldBe(generation);
        writer.CompletedGeneration.ShouldBe(0UL);
        using var fence = new TestSurfaceFence();
        pool.TryPublish(writer, generation, fence).ShouldBeFalse();
        fence.Status = EGpuFenceStatus.Failed;
        pool.TryPublish(writer, generation, fence).ShouldBeFalse();
        fence.Status = EGpuFenceStatus.Signaled;
        fence.Submission = EGpuFenceSubmissionStatus.AwaitingSubmission;
        pool.TryPublish(writer, generation, fence).ShouldBeFalse();
        fence.Submission = EGpuFenceSubmissionStatus.Submitted;
        pool.TryPublish(writer, generation, fence).ShouldBeTrue();
        writer.CompletedGeneration.ShouldBe(generation);
        writer.Release();
    }

    [Test]
    public void RetiredFailedProducerCannotCompleteAfterItsFenceWrapperIsRentedAgain()
    {
        bool retired = false;
        UIReceiptCompletionProbe probe = new(() => retired);
        using var fence = new TestSurfaceFence { Status = EGpuFenceStatus.Failed };
        probe.Seal(authored: true, fence: fence);
        probe.IsCompleted.ShouldBeFalse();
        retired = true;
        probe.IsRetired.ShouldBeTrue();
        probe.IsCompleted.ShouldBeFalse();
        fence.Dispose();
        fence.RentAgain(EGpuFenceStatus.Signaled);
        probe.IsCompleted.ShouldBeFalse();
    }

    [Test]
    public void CompletedOffscreenFrameAdvancesDespiteNewerUnfinishedAuthoring()
    {
        UIPublication first = EmptyPublication(1);
        UIPublication second = EmptyPublication(2);
        UIPublication newest = EmptyPublication(3);
        try
        {
            UICanvasComponent.CanPromoteOffscreenPublication(first, null, 1, true)
                .ShouldBeTrue();
            // The current authoring candidate could already be generation three.
            UICanvasComponent.CanPromoteOffscreenPublication(second, first, 1, true)
                .ShouldBeTrue();
            UICanvasComponent.CanPromoteOffscreenPublication(first, second, 1, true)
                .ShouldBeFalse();
            UICanvasComponent.CanPromoteOffscreenPublication(newest, second, 1, false)
                .ShouldBeFalse();
            UICanvasComponent.CanPromoteOffscreenPublication(newest, second, 2, true)
                .ShouldBeFalse();
        }
        finally { first.Release(); second.Release(); newest.Release(); }
    }

    [Test]
    public void CompletedSurfaceRejectsForeignRendererAndBackendGeneration()
    {
        using var pool = new UIRenderSurfacePool(EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
        pool.TryReserve(8, 8, out UIRenderSurface? writer, out _).ShouldBeTrue();
        writer.ShouldNotBeNull();
        object originalRenderer = new();
        object replacementRenderer = new();
        ulong generation = writer.ReservationGeneration;
        writer.TryComplete(generation, originalRenderer, 7).ShouldBeTrue();
        writer.IsCompletedFor(generation, originalRenderer, 7).ShouldBeTrue();
        writer.IsCompletedFor(generation, originalRenderer, 8).ShouldBeFalse();
        writer.IsCompletedFor(generation, replacementRenderer, 7).ShouldBeFalse();
        writer.Release();
    }

    [Test]
    public void SuccessfulAtomicOffscreenConsumerDoesNotRequireWindowPresentation()
    {
        UICanvasComponent.MustRejectWorldConsumer(completed: true,
            requiresAtomic: true).ShouldBeFalse();
        UICanvasComponent.MustRejectWorldConsumer(completed: false,
            requiresAtomic: true).ShouldBeTrue();
    }

    private sealed class TestSurfaceFence : XRGpuFence
    {
        public EGpuFenceStatus Status { get; set; } = EGpuFenceStatus.Pending;
        public EGpuFenceSubmissionStatus Submission { get; set; } = EGpuFenceSubmissionStatus.Submitted;
        public override EGpuFenceSubmissionStatus SubmissionStatus => Submission;
        public override long DiagnosticRentalId => _rentalId;
        private long _rentalId = 1;
        public void RentAgain(EGpuFenceStatus status)
        {
            _rentalId++;
            Status = status;
            ResetForReuse();
        }
        protected override EGpuFenceStatus PollCore() => Status;
        protected override void DisposeCore() { }
    }

    [Test]
    public void CollectedWorldCommandKeepsPacketUntilDiscardOrNextSwap()
    {
        const int pass = 1;
        var commands = new RenderCommandCollection(
            new Dictionary<int, IComparer<RenderCommand>?> { [pass] = null });
        UIPublication abandoned = EmptyPublication(1);
        commands.AddCPU(new ClaimedWorldCommand(pass, abandoned));
        abandoned.Release();
        abandoned.ResourcesRetired.ShouldBeFalse();
        commands.DiscardUpdatingCommands();
        abandoned.ResourcesRetired.ShouldBeTrue();

        UIPublication rendered = EmptyPublication(2);
        commands.AddCPU(new ClaimedWorldCommand(pass, rendered));
        commands.SwapBuffers();
        rendered.Release();
        rendered.ResourcesRetired.ShouldBeFalse();
        commands.DiscardUpdatingCommands();
        rendered.ResourcesRetired.ShouldBeFalse();
        commands.SwapBuffers();
        rendered.ResourcesRetired.ShouldBeTrue();
    }

    private static UIPublication EmptyPublication(ulong generation)
        => new(generation, 1, 1, 1, (long)generation, default, default, [], [],
            new BoundingRectangleF(0, 0, 100, 100), Matrix4x4.Identity, Matrix4x4.Identity);

    private sealed class ClaimedWorldCommand(int pass, UIPublication publication)
        : RenderCommandMethod3D(pass, static () => { }), IUiPublicationRenderCommand
    {
        public UIPublication Publication { get; } = publication;
    }

    [Test]
    public void MissingDrawRejectsTheWholeReceipt()
    {
        UIPublicationReceiptState state = new(2, requiresPresentation: true);
        state.TryQueueDraw().ShouldBeTrue();
        state.TrySeal(authoringSucceeded: true).ShouldBeFalse();
        state.TryClaim().ShouldBeFalse();
        state.TryAttachSubmission(1, 4, 10).ShouldBeFalse();
    }

    [Test]
    public void PlanRollbackReleasesOnlyBeforeArtifactReference()
    {
        UIPublicationReceiptState state = new(1, requiresPresentation: true);
        state.TryQueueDraw().ShouldBeTrue();
        state.TrySeal(authoringSucceeded: true).ShouldBeTrue();
        state.TryClaim().ShouldBeTrue();
        state.TryAttestPrepared(1).ShouldBeTrue();
        state.CanReleaseUnrecordedClaim.ShouldBeTrue();
        state.TryReferenceArtifact().ShouldBeTrue();
        state.CanReleaseUnrecordedClaim.ShouldBeFalse();
        state.CanRetire(nativeUseFinished: true).ShouldBeFalse();
        state.RetireArtifact();
        state.CanRetire(nativeUseFinished: false).ShouldBeFalse();
        state.CanRetire(nativeUseFinished: true).ShouldBeTrue();
    }

    [Test]
    public void EmptyReceiptStillNeedsRecordedArtifactAndExactPresentedImage()
    {
        UIPublicationReceiptState state = new(0, requiresPresentation: true);
        state.TrySeal(authoringSucceeded: true).ShouldBeTrue();
        state.TryClaim().ShouldBeTrue();
        state.TryAttestPrepared(0).ShouldBeTrue();
        state.TryAttachSubmission(2, 5, 12).ShouldBeFalse();
        state.TryReferenceArtifact().ShouldBeTrue();
        state.TryAttestRecorded(0).ShouldBeTrue();
        state.TryAttachSubmission(2, 5, 12).ShouldBeTrue();
        state.TryPresent(2, 6, 12).ShouldBeFalse();
        state.TryPresent(3, 5, 12).ShouldBeFalse();
        state.TryPresent(2, 5, 13).ShouldBeFalse();
        state.TryPresent(2, 5, 12).ShouldBeTrue();
    }

    [Test]
    public void ReplayRequiresAnAcceptedSourceAndRetainsItsExactFrame()
    {
        UIPublicationReceiptState state = new(1, requiresPresentation: true);
        state.TryQueueDraw().ShouldBeTrue();
        state.TrySeal(authoringSucceeded: true).ShouldBeTrue();
        state.TryClaim().ShouldBeTrue();
        state.TryAttestPrepared(1).ShouldBeTrue();
        state.TryReferenceArtifact().ShouldBeTrue();
        state.TryAttestRecorded(1).ShouldBeTrue();
        state.TryAttachSubmission(1, 7, 14).ShouldBeTrue();
        state.TryAttachReplay(1, 7, 14, 2, 7).ShouldBeFalse();
        state.TryPresent(1, 7, 14).ShouldBeTrue();
        state.TryAttachReplay(1, 7, 15, 2, 7).ShouldBeFalse();
        state.TryAttachReplay(1, 7, 14, 2, 7).ShouldBeTrue();
        state.TryPresent(2, 7, 14).ShouldBeTrue();
        state.TryAttachReplay(1, 7, 14, 3, 7).ShouldBeTrue();
        state.TryAttachReplay(1, 7, 14, 4, 7).ShouldBeTrue();
        state.TryAttachReplay(1, 7, 14, 5, 7).ShouldBeFalse();
        state.InvalidateImage(2, 7);
        state.TryPresent(2, 7, 14).ShouldBeFalse();
        state.TryPresent(1, 7, 14).ShouldBeTrue();
    }
}
