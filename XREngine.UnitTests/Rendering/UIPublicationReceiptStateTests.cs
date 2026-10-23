using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIPublicationReceiptStateTests
{
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
