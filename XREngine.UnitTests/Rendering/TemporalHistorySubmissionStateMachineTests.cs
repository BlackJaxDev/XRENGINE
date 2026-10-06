using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Drives the CPU temporal-history submission state machine through the same
/// stage, seal, record, and accept calls that the strict-stereo Vulkan path uses.
/// No graphics device is necessary because the state machine compares only
/// backend-neutral image identities.
/// </summary>
[TestFixture]
public sealed class TemporalHistorySubmissionStateMachineTests
{
    private const uint StereoLayerMask = 0b11u;

    [Test]
    public void AcceptCandidate_PublishesRecordedCandidateOnlyOnceAndOnlyAfterAcceptance()
    {
        Harness harness = new();
        Matrix4x4 left = Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f);
        Matrix4x4 right = Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f);

        TemporalHistorySubmissionCandidate recorded = harness.RecordFrame(left, right, authoredHistoryReady: false);

        // Recording confirms native coverage but publishes nothing.
        harness.Uniforms.TryRead(out _).ShouldBeFalse();
        harness.HistoryReady.ShouldBeFalse();
        harness.Submission.HasAcceptedResources.ShouldBeFalse();
        harness.Submission.Recorded.ShouldBeTrue();
        harness.Submission.Candidate.Token.ShouldBe(recorded.Token);

        VPRC_TemporalAccumulationPass.AcceptCandidate(in recorded).ShouldBeTrue();

        harness.AssertPublished(left, right);
        harness.HistoryReady.ShouldBeTrue();
        harness.Submission.HasAcceptedResources.ShouldBeTrue();
        harness.Submission.AcceptedResources.ShouldBe(harness.Writes);
        harness.Submission.Candidate.IsValid.ShouldBeFalse();
        harness.Submission.Recorded.ShouldBeFalse();

        // A second acceptance of the same token (for example, a finally backstop) publishes nothing.
        VPRC_TemporalAccumulationPass.AcceptCandidate(in recorded).ShouldBeFalse();
        harness.AssertPublished(left, right);
        harness.HistoryReady.ShouldBeTrue();
        harness.Submission.HasAcceptedResources.ShouldBeTrue();
    }

    [Test]
    public void DiscardCandidate_RejectedAttemptKeepsPreviousAcceptedSeedAndMatrices()
    {
        Harness harness = new();
        Matrix4x4 acceptedLeft = Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f);
        Matrix4x4 acceptedRight = Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f);
        harness.AcceptFrame(acceptedLeft, acceptedRight, authoredHistoryReady: false);
        ulong acceptedEpoch = harness.Submission.Epoch;

        TemporalHistorySubmissionCandidate rejected = harness.RecordFrame(
            Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(4.0f, 0.0f, 0.0f),
            authoredHistoryReady: true);
        VPRC_TemporalAccumulationPass.DiscardCandidate(in rejected);

        harness.Submission.Candidate.IsValid.ShouldBeFalse();
        harness.Submission.Recorded.ShouldBeFalse();
        harness.Submission.Epoch.ShouldBe(acceptedEpoch);
        harness.Submission.HasAcceptedResources.ShouldBeTrue();
        harness.Submission.AcceptedResources.ShouldBe(harness.Writes);
        harness.HistoryReady.ShouldBeTrue();
        harness.AssertPublished(acceptedLeft, acceptedRight);

        // A released token cannot publish later.
        VPRC_TemporalAccumulationPass.AcceptCandidate(in rejected).ShouldBeFalse();
        harness.AssertPublished(acceptedLeft, acceptedRight);

        // The retained accepted seed still seals the next history-ready attempt.
        Matrix4x4 nextLeft = Matrix4x4.CreateTranslation(5.0f, 0.0f, 0.0f);
        Matrix4x4 nextRight = Matrix4x4.CreateTranslation(6.0f, 0.0f, 0.0f);
        harness.AcceptFrame(nextLeft, nextRight, authoredHistoryReady: true);
        harness.AssertPublished(nextLeft, nextRight);
    }

    [Test]
    public void StrictCapture_IssuesNewTokenOnlyAfterOutstandingCandidateClears()
    {
        Harness harness = new();
        TemporalHistorySubmissionCandidate outstanding = harness.RecordFrame(
            Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f),
            authoredHistoryReady: false);

        // A second authoring attempt is blocked while the first candidate is in flight.
        using (TemporalHistoryCaptureScope blocked = VPRC_TemporalAccumulationPass.EnterStrictSpsCapture(harness.Instance))
        {
            harness.TryBeginStrictCapture().ShouldBeFalse();
            blocked.HasTemporalAttemptOrBlocked.ShouldBeTrue();
            harness.Submission.Candidate.Token.ShouldBe(outstanding.Token);
        }

        harness.Submission.Candidate.Token.ShouldBe(outstanding.Token);
        harness.Submission.Recorded.ShouldBeTrue();
        VPRC_TemporalAccumulationPass.AcceptCandidate(in outstanding).ShouldBeTrue();

        using (VPRC_TemporalAccumulationPass.EnterStrictSpsCapture(harness.Instance))
        {
            harness.TryBeginStrictCapture().ShouldBeTrue();
            ulong nextToken = harness.Submission.Candidate.Token;
            nextToken.ShouldNotBe(0UL);
            nextToken.ShouldNotBe(outstanding.Token);
        }

        // An authored attempt that no recorder took is released when its scope ends.
        harness.Submission.Candidate.IsValid.ShouldBeFalse();
        harness.Submission.Staged.ShouldBeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InvalidateCandidateForDeviceLoss_InvalidatesHistoryOwnedByLostBackend(bool lostCandidateIsPending)
    {
        Harness harness = new();
        TemporalHistorySubmissionCandidate accepted = harness.AcceptFrame(
            Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f),
            authoredHistoryReady: false);
        TemporalHistorySubmissionCandidate pending = harness.RecordFrame(
            Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(4.0f, 0.0f, 0.0f),
            authoredHistoryReady: true);
        ulong epochBeforeLoss = harness.Submission.Epoch;

        // A lost device that owns neither the pending attempt nor the accepted images changes nothing.
        TemporalHistorySubmissionCandidate unrelated = pending with
        {
            Token = pending.Token + 100UL,
            WriteResources = Harness.CreateWrites(new object()),
        };
        VPRC_TemporalAccumulationPass.InvalidateCandidateForDeviceLoss(in unrelated);
        harness.Submission.Epoch.ShouldBe(epochBeforeLoss);
        harness.Submission.HasAcceptedResources.ShouldBeTrue();
        harness.Submission.Candidate.Token.ShouldBe(pending.Token);
        harness.HistoryReady.ShouldBeTrue();

        TemporalHistorySubmissionCandidate lost = lostCandidateIsPending ? pending : accepted;
        VPRC_TemporalAccumulationPass.InvalidateCandidateForDeviceLoss(in lost);

        harness.Submission.Epoch.ShouldNotBe(epochBeforeLoss);
        harness.Submission.HasAcceptedResources.ShouldBeFalse();
        harness.HistoryReady.ShouldBeFalse();
        harness.AssertPublished(Matrix4x4.Identity, Matrix4x4.Identity, historyReady: false);

        // The in-flight attempt can no longer publish its matrices.
        VPRC_TemporalAccumulationPass.AcceptCandidate(in pending).ShouldBeFalse();
        harness.Submission.Candidate.IsValid.ShouldBeFalse();
        harness.HistoryReady.ShouldBeFalse();
        harness.AssertPublished(Matrix4x4.Identity, Matrix4x4.Identity, historyReady: false);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(-1)]
    public void CameraCutOrReset_InvalidatesOutstandingCandidateToken(int resetEyeIndex)
    {
        Harness harness = new();
        Matrix4x4 acceptedLeft = Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f);
        Matrix4x4 acceptedRight = Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f);
        harness.AcceptFrame(acceptedLeft, acceptedRight, authoredHistoryReady: false);
        TemporalHistorySubmissionCandidate pending = harness.RecordFrame(
            Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(4.0f, 0.0f, 0.0f),
            authoredHistoryReady: true);
        ulong epochBeforeReset = harness.Submission.Epoch;

        // -1 is a full history reset; 0 and 1 are per-eye camera cuts.
        if (resetEyeIndex < 0)
            harness.ResetHistory();
        else
            harness.ResetEyeHistory(resetEyeIndex);

        harness.Submission.Epoch.ShouldNotBe(epochBeforeReset);
        harness.Submission.HasAcceptedResources.ShouldBeFalse();

        VPRC_TemporalAccumulationPass.AcceptCandidate(in pending).ShouldBeFalse();
        harness.Submission.Candidate.IsValid.ShouldBeFalse();
        harness.AssertPublished(acceptedLeft, acceptedRight);
    }

    /// <summary>
    /// Owns one stereo temporal state and the fixed history images it records into.
    /// </summary>
    private sealed class Harness
    {
        private static readonly Type TemporalStateType = typeof(VPRC_TemporalAccumulationPass)
            .GetNestedType("TemporalState", BindingFlags.NonPublic)
            .ShouldNotBeNull();

        private readonly object _state;
        private readonly XRFrameBuffer _historyTarget = new();
        private readonly XRTexture2D _depthRead = new();

        public Harness()
        {
            ConstructorInfo constructor = TemporalStateType
                .GetConstructor([typeof(VPRC_TemporalAccumulationPass.TemporalViewKey).MakeByRefType()])
                .ShouldNotBeNull();
            _state = constructor.Invoke([new VPRC_TemporalAccumulationPass.TemporalViewKey(1, 2, 3, -1, -1, 0)]);
            Writes = CreateWrites(new object());

            VPRC_TemporalAccumulationPass.TemporalHistoryGenerationTracker generation = Generation;
            generation.BeginFrame(
                new VPRC_TemporalAccumulationPass.TemporalHistoryProfile(
                    64u, 64u, 64u, 64u, EAntiAliasingMode.Taa, EVrTemporalHistoryPolicy.StereoArrayLayer),
                expectedLayerCount: 2u);
            generation.RecordCurrentMatrices(StereoLayerMask);
        }

        public XRRenderPipelineInstance Instance { get; } = new();

        public TemporalHistoryResourceSet Writes { get; }

        public TemporalHistorySubmissionState Submission
            => GetStateProperty<TemporalHistorySubmissionState>("Submission");

        public TemporalUniformDataSnapshot Uniforms
            => GetStateProperty<TemporalUniformDataSnapshot>("UniformSnapshot");

        public bool HistoryReady
            => (bool)TemporalStateType.GetField("HistoryReady").ShouldNotBeNull().GetValue(_state)!;

        private VPRC_TemporalAccumulationPass.TemporalHistoryGenerationTracker Generation
            => GetStateProperty<VPRC_TemporalAccumulationPass.TemporalHistoryGenerationTracker>("HistoryGeneration");

        public static TemporalHistoryResourceSet CreateWrites(object backendOwner)
            => new(
                new TemporalHistoryResourceIdentity(backendOwner, 0x10UL, 1UL, 0x1u, 0u, 1u, 0u, 2u),
                new TemporalHistoryResourceIdentity(backendOwner, 0x20UL, 1UL, 0x2u, 0u, 1u, 0u, 2u),
                default,
                default);

        /// <summary>Records one frame and accepts it as a native submission would.</summary>
        public TemporalHistorySubmissionCandidate AcceptFrame(Matrix4x4 left, Matrix4x4 right, bool authoredHistoryReady)
        {
            TemporalHistorySubmissionCandidate recorded = RecordFrame(left, right, authoredHistoryReady);
            VPRC_TemporalAccumulationPass.AcceptCandidate(in recorded).ShouldBeTrue();
            return recorded;
        }

        /// <summary>
        /// Authors, stages, transfers, seals, and records one stereo attempt. The
        /// candidate stays pending until a test accepts, discards, or invalidates it.
        /// </summary>
        public TemporalHistorySubmissionCandidate RecordFrame(Matrix4x4 left, Matrix4x4 right, bool authoredHistoryReady)
        {
            using (VPRC_TemporalAccumulationPass.EnterStrictSpsCapture(Instance))
            {
                TryBeginStrictCapture().ShouldBeTrue();
                Stage(left, right, authoredHistoryReady);

                VPRC_TemporalAccumulationPass.TryGetStagedCandidate(
                    Instance, out TemporalHistorySubmissionCandidate staged).ShouldBeTrue();
                TemporalHistoryResourceSet writes = Writes;
                VPRC_TemporalAccumulationPass.TrySealCandidate(
                    in staged,
                    in writes,
                    in writes,
                    out TemporalHistorySubmissionCandidate sealedCandidate,
                    out bool requiresReauthor).ShouldBeTrue();
                requiresReauthor.ShouldBeFalse();
                VPRC_TemporalAccumulationPass.TryConfirmRecordedCandidate(
                    in sealedCandidate,
                    StereoLayerMask,
                    StereoLayerMask,
                    tsrColorLayerMask: 0u,
                    metadataLayerMask: 0u,
                    exposureLayerMask: 0u,
                    temporalResolveRecorded: false,
                    tsrResolveRecorded: false,
                    out TemporalHistorySubmissionCandidate recorded).ShouldBeTrue();
                return recorded;
            }
        }

        public bool TryBeginStrictCapture()
            => (bool)GetPrivateStaticMethod("TryBeginStrictHistoryCapture", typeof(XRRenderPipelineInstance), TemporalStateType)
                .Invoke(null, [Instance, _state])!;

        public void ResetHistory()
            => GetPrivateStaticMethod("ResetHistory", TemporalStateType).Invoke(null, [_state]);

        public void ResetEyeHistory(int eyeIndex)
            => GetPrivateStaticMethod("ResetEyeHistory", TemporalStateType, typeof(int)).Invoke(null, [_state, eyeIndex]);

        public void AssertPublished(Matrix4x4 left, Matrix4x4 right, bool historyReady = true)
        {
            Uniforms.TryRead(out VPRC_TemporalAccumulationPass.TemporalUniformData data).ShouldBeTrue();
            data.PrevViewProjectionUnjittered.ShouldBe(left);
            data.RightEyePrevViewProjectionUnjittered.ShouldBe(right);
            data.HistoryReady.ShouldBe(historyReady);
            data.LeftEyeHistoryReady.ShouldBe(historyReady);
            data.RightEyeHistoryReady.ShouldBe(historyReady);
        }

        /// <summary>Mirrors the begin-bind, target-registration, and staging steps of strict capture.</summary>
        private void Stage(Matrix4x4 left, Matrix4x4 right, bool authoredHistoryReady)
        {
            VPRC_TemporalAccumulationPass.TemporalHistoryGenerationTracker generation = Generation;
            VPRC_TemporalAccumulationPass.TemporalHistoryCoverage coverage = default;
            coverage.Begin(2u, requiresTsrColor: false);
            coverage.RecordColorAndDepth(StereoLayerMask, StereoLayerMask);

            TemporalHistorySubmissionState submission = Submission;
            submission.Coverage = coverage;
            submission.MatrixLayerMask = generation.CurrentMatrixLayerMask;
            submission.LeftEye = CreateEye(left);
            submission.RightEye = CreateEye(right);
            submission.Candidate = submission.Candidate with
            {
                ProfileGeneration = generation.ProfileGeneration,
                LeftEyeResetGeneration = generation.LeftEyeResetGeneration,
                RightEyeResetGeneration = generation.RightEyeResetGeneration,
                ExpectedLayerMask = generation.ExpectedLayerMask,
                CompleteLayerMask = coverage.CompleteLayerMask & generation.CurrentMatrixLayerMask,
                AuthoredHistoryReady = authoredHistoryReady,
                ColorHistoryTarget = _historyTarget,
                DepthHistoryTarget = _historyTarget,
                DepthReadTexture = _depthRead,
            };
            submission.Staged = true;
        }

        private static TemporalHistoryEyeSnapshot CreateEye(Matrix4x4 viewProjection)
            => new(
                Vector2.Zero,
                DepthZeroToOne: true,
                ReversedDepth: false,
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                viewProjection,
                viewProjection,
                Matrix4x4.Identity,
                Vector3.Zero,
                Vector3.UnitZ);

        private T GetStateProperty<T>(string name)
            => (T)TemporalStateType.GetProperty(name).ShouldNotBeNull().GetValue(_state)!;

        private static MethodInfo GetPrivateStaticMethod(string name, params Type[] parameterTypes)
            => typeof(VPRC_TemporalAccumulationPass)
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, parameterTypes)
                .ShouldNotBeNull();
    }
}
