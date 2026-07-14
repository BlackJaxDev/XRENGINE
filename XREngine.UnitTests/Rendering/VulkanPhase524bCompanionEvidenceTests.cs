using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanPhase524bCompanionEvidenceTests
{
    [Test]
    public void ValidatorMatchesStereoBloomExtentEvidenceAcrossOneLogLine()
    {
        string validator = ReadWorkspaceFile("Tools/Validate-VulkanPhase524b.ps1");

        validator.ShouldContain(
            "$bloomPattern = \"\\[PostProcessDiag\\] Fullscreen[^\\r\\n]*Bloom[^\\r\\n]*destinationExtent=$([regex]::Escape($bloomExtent))[^\\r\\n]*(attachmentLayers|layers)=2[^\\r\\n]*viewMask=0x3\"");
        validator.ShouldNotContain("Fullscreen[^\\\\r\\\\n]*Bloom");
    }

    [Test]
    public void StrictSpsFrameOutputPacing_IdentifiesOneConcreteMultiviewExternalTarget()
    {
        FrameOutputPacingDecision pacing = OpenXRAPI.CreateOpenXrStereoFrameOutputPacing(
            renderFrameId: 41UL,
            externalImageSlot: 2);

        pacing.FrameId.ShouldBe(41UL);
        pacing.OutputKind.ShouldBe(EFrameOutputKind.OpenXREyeSubmit);
        pacing.ViewKind.ShouldBe(EVrOutputViewKind.LeftEye);
        pacing.Request.FrameId.ShouldBe(41UL);
        pacing.Request.OutputClass.ShouldBe(ERenderOutputClass.XrCritical);
        pacing.Request.Target.TargetClass.ShouldBe(ERenderOutputTargetClass.RuntimeExternalImage);
        pacing.Request.Target.ViewMask.ShouldBe(0x3u);
        pacing.Request.Target.ExternalImageSlot.ShouldBe(2);
    }

    [Test]
    [NonParallelizable]
    public void Phase524bTsrScaleOverride_AcceptsOnlyFiniteValidationRange()
    {
        string name = XREngineEnvironmentVariables.VulkanPhase524bTsrResolutionScale;
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "0.67");
            OpenXRAPI.ResolvePhase524bTsrRenderScaleOverride().ShouldBe(0.67f);

            Environment.SetEnvironmentVariable(name, "0.49");
            OpenXRAPI.ResolvePhase524bTsrRenderScaleOverride().ShouldBeNull();

            Environment.SetEnvironmentVariable(name, "NaN");
            OpenXRAPI.ResolvePhase524bTsrRenderScaleOverride().ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Test]
    public void DesktopRejectionInjection_ArmsFromHistoryThenRejectsExactlyOnce()
    {
        var injection = new Phase524bDesktopRejectionInjection();

        injection.Observe(true, true, true, 1.25, "history").Action
            .ShouldBe(EPhase524bDesktopRejectionAction.Armed);

        Phase524bDesktopRejectionDecision rejection =
            injection.Observe(true, true, true, 1.5, "current");
        rejection.Action.ShouldBe(EPhase524bDesktopRejectionAction.Reject);
        rejection.Exposure.ShouldBe(1.5);
        rejection.ExposureHistory.ShouldBe(1.25);

        injection.Observe(true, true, true, 2.0, "later").Action
            .ShouldBe(EPhase524bDesktopRejectionAction.Wait);
    }

    [Test]
    public void DesktopRejectionEvidence_RequiresLegalPolicyNumericValuesAndDesktopOwner()
    {
        var evidence = new OpenXrSmokeDesktopRejectionEvidence
        {
            Injected = true,
            Observed = true,
            Policy = "PresentLastCompletedContent",
            PresentedLastCompletedImage = true,
            PresentAccepted = true,
            PipelineName = "DefaultPipelineDesktop",
            PipelineInstanceId = 41,
            OutputId = 7,
            RenderFrameId = 99,
            Exposure = 1.1,
            ExposureHistory = 1.0,
            ExposureFinite = true,
            ExposureHistoryFinite = true,
            ExposureNonZeroRequired = true,
            ExposureHistoryNonZeroRequired = true,
            ExposureOwnerMatchesDesktop = true,
        };

        OpenXrSmokePhase524bEvidenceValidator.ValidateDesktopRejection(evidence, required: true)
            .ShouldBeEmpty();

        evidence.ClearedTargetPublished = true;
        evidence.ExposureHistory = double.NaN;
        OpenXrSmokePhase524bEvidenceValidator.ValidateDesktopRejection(evidence, required: true)
            .Length.ShouldBeGreaterThanOrEqualTo(2);
    }

    [TestCase(false, 1.0, 896u, 1007u)]
    [TestCase(true, 0.67, 600u, 674u)]
    public void TsrResolutionCohort_ValidatesNativeAndSeparateSubNativeShapes(
        bool subNative,
        double scale,
        uint internalWidth,
        uint internalHeight)
    {
        OpenXrSmokeOutputLedgerEntry[] outputs =
        [
            new()
            {
                OutputId = 12,
                PipelineInstanceId = 21,
                TargetClass = "RuntimeExternalImage",
                DisplayWidth = 896,
                DisplayHeight = 1007,
                InternalWidth = internalWidth,
                InternalHeight = internalHeight,
                LayerCount = 2,
                ViewMask = 0x3,
                Rendered = true,
            },
        ];

        OpenXrSmokePhase524bEvidenceValidator.ValidateTsrResolutionCohort(
                scale,
                scale,
                outputs,
                expectSubNative: subNative)
            .ShouldBeEmpty();

        OpenXrSmokePhase524bEvidenceValidator.ValidateTsrResolutionCohort(
                scale,
                scale,
                outputs,
                expectSubNative: !subNative)
            .ShouldNotBeEmpty();
    }

    [Test]
    public void TemporalScenarioMatrix_AcceptsCompletePredeclaredRenderedOutputEvidence()
    {
        (OpenXrSmokeTemporalScenarioDefinition[] matrix, string[] stages, OpenXrSmokeCaptureLedgerEntry[] captures) =
            CreateValidTemporalScenarioEvidence();

        OpenXrSmokePhase524bEvidenceValidator.ValidateTemporalScenarioMatrix(matrix, stages, captures)
            .ShouldBeEmpty();
    }

    [Test]
    public void TemporalScenarioMatrix_RejectsMissingStaticDirectionalAndDisocclusionEvidence()
    {
        (OpenXrSmokeTemporalScenarioDefinition[] matrix, string[] stages, OpenXrSmokeCaptureLedgerEntry[] captures) =
            CreateValidTemporalScenarioEvidence();
        OpenXrSmokeCaptureLedgerEntry staticVelocity = captures.Single(capture =>
            capture.TemporalSample == nameof(EPhase524bTemporalSample.StaticPoseSettled) &&
            capture.Stage == "07_Velocity" && capture.LayerIndex == 0);
        staticVelocity.VelocityMaxMagnitude = 0.1f;
        OpenXrSmokeCaptureLedgerEntry movingVelocity = captures.Single(capture =>
            capture.TemporalSample == nameof(EPhase524bTemporalSample.HeadTranslationActive) &&
            capture.Stage == "07_Velocity" && capture.LayerIndex == 0);
        movingVelocity.VelocityMeanX = 0.01f;
        OpenXrSmokeCaptureLedgerEntry revealed = captures.Single(capture =>
            capture.TemporalSample == nameof(EPhase524bTemporalSample.DisocclusionRevealed) &&
            capture.Stage == "13c_MonoTsrReference" && capture.LayerIndex == 1);
        OpenXrSmokeCaptureLedgerEntry occluded = captures.Single(capture =>
            capture.TemporalSample == nameof(EPhase524bTemporalSample.DisocclusionOccluded) &&
            capture.Stage == "13c_MonoTsrReference" && capture.LayerIndex == 1);
        revealed.LuminanceFingerprint = (double[])occluded.LuminanceFingerprint.Clone();

        string[] failures = OpenXrSmokePhase524bEvidenceValidator.ValidateTemporalScenarioMatrix(
            matrix,
            stages,
            captures[..^1]);

        failures.ShouldContain(failure => failure.Contains("static-zero velocity", StringComparison.Ordinal));
        failures.ShouldContain(failure => failure.Contains("NegativeX", StringComparison.Ordinal));
        failures.ShouldContain(failure => failure.Contains("Disocclusion layer 1", StringComparison.Ordinal));
        failures.ShouldContain(failure => failure.Contains("capture ledger contains", StringComparison.OrdinalIgnoreCase));
    }

    private static (
        OpenXrSmokeTemporalScenarioDefinition[] Matrix,
        string[] Stages,
        OpenXrSmokeCaptureLedgerEntry[] Captures) CreateValidTemporalScenarioEvidence()
    {
        string[] stages = ["07_Velocity", "09_BloomMip1", "13c_MonoTsrReference", "14_TsrOutput"];
        Phase524bTemporalSampleDefinition[] definitions =
            Phase524bTemporalScenarioDiagnostics.Definitions.ToArray();
        OpenXrSmokeTemporalScenarioDefinition[] matrix = definitions.Select(static definition => new OpenXrSmokeTemporalScenarioDefinition
        {
            Scenario = definition.Scenario.ToString(),
            Sample = definition.Sample.ToString(),
            VelocityOracle = definition.VelocityOracle.ToString(),
            CaptureStartFrame = definition.CaptureStartFrame,
            CaptureEndFrame = definition.CaptureEndFrame,
            RequiresTemporalConvergence = definition.RequiresTemporalConvergence,
            IsDisocclusionBaseline = definition.IsDisocclusionBaseline,
            IsDisocclusionResult = definition.IsDisocclusionResult,
        }).ToArray();
        var captures = new List<OpenXrSmokeCaptureLedgerEntry>(definitions.Length * stages.Length * 2);
        for (int definitionIndex = 0; definitionIndex < definitions.Length; definitionIndex++)
        {
            Phase524bTemporalSampleDefinition definition = definitions[definitionIndex];
            for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
            {
                string stage = stages[stageIndex];
                for (int layer = 0; layer < 2; layer++)
                {
                    double baseValue = definition.Sample switch
                    {
                        EPhase524bTemporalSample.DisocclusionOccluded => 0.10,
                        EPhase524bTemporalSample.DisocclusionRevealed => 0.20,
                        _ => 0.30 + (definitionIndex * 0.01),
                    };
                    double[] luminanceFingerprint = Enumerable.Repeat(
                        baseValue + (layer * 0.00001),
                        256).ToArray();
                    double[] velocityFingerprint = new double[256];
                    bool moving = definition.VelocityOracle != EPhase524bVelocityOracle.Zero;
                    if (moving)
                        velocityFingerprint[layer] = 0.01;
                    float direction = definition.VelocityOracle == EPhase524bVelocityOracle.NegativeX
                        ? -1.0f
                        : 1.0f;

                    captures.Add(new OpenXrSmokeCaptureLedgerEntry
                    {
                        PipelineName = "DefaultPipelineSps",
                        OutputRole = "TemporalScenarioRenderedOutput",
                        Stage = stage,
                        LayerIndex = layer,
                        ExpectedLayerCount = 2,
                        ViewMask = 0x3u,
                        TemporalScenario = definition.Scenario.ToString(),
                        TemporalSample = definition.Sample.ToString(),
                        VelocityOracle = definition.VelocityOracle.ToString(),
                        TemporalSequenceFrame = definition.CaptureStartFrame,
                        RenderFrameId = (ulong)(100 + definitionIndex),
                        Width = 64,
                        Height = 64,
                        LengthBytes = 1024,
                        LuminanceEnergy = 100.0 + layer,
                        BloomCentroidX = 0.5f + (layer * 0.0005f),
                        BloomCentroidY = 0.5f,
                        VelocityMeanX = moving ? direction * 0.001f : 0.0f,
                        VelocityMaxMagnitude = moving ? 0.01f : 0.0f,
                        EdgeMaxGradient = 1.0f,
                        LuminanceFingerprintWidth = 16,
                        LuminanceFingerprintHeight = 16,
                        LuminanceFingerprint = luminanceFingerprint,
                        VelocityMagnitudeFingerprintWidth = 16,
                        VelocityMagnitudeFingerprintHeight = 16,
                        VelocityMagnitudeFingerprint = velocityFingerprint,
                    });
                }
            }
        }
        return (matrix, stages, [.. captures]);
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
            {
                string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
                return File.ReadAllText(path).Replace("\r\n", "\n");
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
