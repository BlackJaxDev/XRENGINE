using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class Phase524bCaptureReadinessTests
{
    [Test]
    public void Stable_RejectsGlobalImportDecodeAndUploadWork()
    {
        ImportedTextureStreamingTelemetry telemetry = default;
        Phase524bCaptureReadinessDiagnostics.IsStable(telemetry, [], out _).ShouldBeTrue();

        Phase524bCaptureReadinessDiagnostics.IsStable(
            telemetry with { ActiveImportScopes = 1 },
            [],
            out string importReason).ShouldBeFalse();
        importReason.ShouldContain("imports=1");

        Phase524bCaptureReadinessDiagnostics.IsStable(
            telemetry with { ActiveGpuUploadCount = 1 },
            [],
            out string uploadReason).ShouldBeFalse();
        uploadReason.ShouldContain("gpuUploads=1");
    }

    [Test]
    public void Stable_RequiresEveryVisibleTextureGenerationAndDesiredResidency()
    {
        ImportedTextureStreamingTextureTelemetry stableVisible = default(ImportedTextureStreamingTextureTelemetry) with
        {
            TextureName = "Albedo",
            IsVisible = true,
            PreviewReady = true,
            ResidentMaxDimension = 1024,
            DesiredResidentMaxDimension = 1024,
            CurrentPageCoverage = 1.0f,
            DesiredPageCoverage = 1.0f,
            ResidentGeneration = 4,
            PublishedGeneration = 4,
        };

        Phase524bCaptureReadinessDiagnostics.IsStable(
            default,
            [stableVisible],
            out _).ShouldBeTrue();

        ImportedTextureStreamingTextureTelemetry previewOnly = stableVisible with
        {
            ResidentMaxDimension = 64,
            DesiredResidentMaxDimension = 1024,
        };
        Phase524bCaptureReadinessDiagnostics.IsStable(
            default,
            [previewOnly],
            out string residencyReason).ShouldBeFalse();
        residencyReason.ShouldContain("resident=64/1024");

        ImportedTextureStreamingTextureTelemetry unpublished = stableVisible with
        {
            ResidentGeneration = 5,
            PublishedGeneration = 4,
        };
        Phase524bCaptureReadinessDiagnostics.IsStable(
            default,
            [unpublished],
            out string generationReason).ShouldBeFalse();
        generationReason.ShouldContain("generation=4/5");
    }

    [Test]
    public void Stable_IgnoresIncompleteTexturesThatAreNotVisible()
    {
        ImportedTextureStreamingTextureTelemetry backgroundTexture = default(ImportedTextureStreamingTextureTelemetry) with
        {
            TextureName = "NotInView",
            IsVisible = false,
            PreviewReady = false,
            HasPendingTransition = false,
            ResidentMaxDimension = 0,
            DesiredResidentMaxDimension = 2048,
        };

        Phase524bCaptureReadinessDiagnostics.IsStable(
            default,
            [backgroundTexture],
            out _).ShouldBeTrue();
    }

    [Test]
    public void StrictSpsBoundaryCapture_WaitsForReadinessBeforeMotionCadenceStarts()
    {
        string source = File.ReadAllText(Path.Combine(
            FindWorkspaceRoot(),
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.StrictSpsBoundaryCapture.cs"));
        int readinessGate = source.IndexOf(
            "Phase524bCaptureReadinessDiagnostics.IsReady(out string readinessReason)",
            StringComparison.Ordinal);
        int cadenceAdvance = source.IndexOf(
            "int successfulFrameIndex = _strictSpsBoundarySuccessfulFrameCount++;",
            StringComparison.Ordinal);
        int deterministicMotionStart = source.IndexOf(
            "Phase524bTemporalScenarioDiagnostics.SetBoundaryCaptureMotionIndex(0);",
            StringComparison.Ordinal);

        readinessGate.ShouldBeGreaterThanOrEqualTo(0);
        deterministicMotionStart.ShouldBeGreaterThan(readinessGate);
        cadenceAdvance.ShouldBeGreaterThan(readinessGate);
        cadenceAdvance.ShouldBeGreaterThan(deterministicMotionStart);
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XRENGINE.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the XRENGINE workspace root.");
    }
}
