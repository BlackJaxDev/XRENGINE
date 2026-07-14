using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class StereoRenderedOutputMetricsTests
{
    [Test]
    public void BloomMetrics_ControlledEyesMatchMonoEnergyAndCentroidThresholds()
    {
        const int width = 8;
        const int height = 4;
        float[] mono = CreateRgba(width, height);
        float[] left = CreateRgba(width, height);
        float[] right = CreateRgba(width, height);
        SetPixel(mono, width, 2, 1, new Vector3(4.0f, 2.0f, 1.0f));
        SetPixel(left, width, 2, 1, new Vector3(4.0f, 2.0f, 1.0f));
        SetPixel(right, width, 2, 1, new Vector3(4.0f, 2.0f, 1.0f));

        var monoMetrics = StereoRenderedOutputMetrics.MeasureBloom(mono, width, height);
        var leftMetrics = StereoRenderedOutputMetrics.MeasureBloom(left, width, height);
        var rightMetrics = StereoRenderedOutputMetrics.MeasureBloom(right, width, height);

        StereoRenderedOutputMetrics.RelativeDifference(leftMetrics.LuminanceEnergy, monoMetrics.LuminanceEnergy)
            .ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxBloomRelativeEnergyDelta);
        StereoRenderedOutputMetrics.RelativeDifference(rightMetrics.LuminanceEnergy, monoMetrics.LuminanceEnergy)
            .ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxBloomRelativeEnergyDelta);
        Vector2.Distance(leftMetrics.NormalizedCentroid, monoMetrics.NormalizedCentroid)
            .ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxBloomCentroidDistance);
        Vector2.Distance(rightMetrics.NormalizedCentroid, monoMetrics.NormalizedCentroid)
            .ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxBloomCentroidDistance);
    }

    [Test]
    public void BloomIndependence_EyeSpecificRegionsHaveNoCrossEyeLeakage()
    {
        const int width = 8;
        const int height = 4;
        float[] left = CreateRgba(width, height);
        float[] right = CreateRgba(width, height);
        SetPixel(left, width, 1, 1, Vector3.One);
        SetPixel(right, width, 6, 2, Vector3.One);

        double leftLeakage = StereoRenderedOutputMetrics.MeasureOutsideRegionEnergyFraction(left, width, height, 0, 0, 4, 4);
        double rightLeakage = StereoRenderedOutputMetrics.MeasureOutsideRegionEnergyFraction(right, width, height, 4, 0, 4, 4);

        leftLeakage.ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxCrossEyeLeakageFraction);
        rightLeakage.ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxCrossEyeLeakageFraction);
    }

    [Test]
    public void BloomThresholds_AllowBoundedNearFieldBinocularDisparity()
    {
        Vector2 leftCentroid = new(0.4215f, 0.2437f);
        Vector2 rightCentroid = new(0.3767f, 0.2474f);

        Vector2.Distance(leftCentroid, rightCentroid)
            .ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxBloomCentroidDistance);
        leftCentroid.X.ShouldBeInRange(
            StereoRenderedOutputThresholds.MinBloomCentroidX,
            StereoRenderedOutputThresholds.MaxBloomCentroidX);
        rightCentroid.X.ShouldBeInRange(
            StereoRenderedOutputThresholds.MinBloomCentroidX,
            StereoRenderedOutputThresholds.MaxBloomCentroidX);
        leftCentroid.Y.ShouldBeInRange(
            StereoRenderedOutputThresholds.MinBloomCentroidY,
            StereoRenderedOutputThresholds.MaxBloomCentroidY);
        rightCentroid.Y.ShouldBeInRange(
            StereoRenderedOutputThresholds.MinBloomCentroidY,
            StereoRenderedOutputThresholds.MaxBloomCentroidY);
    }

    [Test]
    public void VelocityMetrics_DistinguishStaticAndScriptedMotion()
    {
        const int width = 4;
        const int height = 2;
        float[] staticVelocity = CreateRgba(width, height);
        float[] movingVelocity = CreateRgba(width, height);
        movingVelocity[0] = 0.025f;
        movingVelocity[1] = -0.01f;

        var staticMetrics = StereoRenderedOutputMetrics.MeasureVelocity(staticVelocity, width, height);
        var movingMetrics = StereoRenderedOutputMetrics.MeasureVelocity(movingVelocity, width, height);

        staticMetrics.MaxMagnitude.ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxStaticVelocityMagnitude);
        movingMetrics.MaxMagnitude.ShouldBeGreaterThanOrEqualTo(StereoRenderedOutputThresholds.MinMovingVelocityMagnitude);
        movingMetrics.NonZeroSampleCount.ShouldBe(1);
        movingMetrics.MeanVector.X.ShouldBeGreaterThan(0.0f);
        movingMetrics.MeanVector.Y.ShouldBeLessThan(0.0f);

        RenderedOutputCaptureMetrics capture = StereoRenderedOutputMetrics.MeasureCapture(
            movingVelocity,
            width,
            height);
        capture.VelocityMeanX.ShouldBeGreaterThan(0.0f);
        capture.VelocityMeanY.ShouldBeLessThan(0.0f);
        capture.VelocityMagnitudeFingerprint.Length.ShouldBe(256);
        capture.VelocityMagnitudeFingerprint.ShouldContain(value => value > 0.0);
    }

    [Test]
    public void SharpnessAndConvergence_MonoEquivalentCohortMeetsFixedThresholds()
    {
        const int width = 8;
        const int height = 4;
        float[] mono = CreateVerticalStep(width, height);
        float[] stereoEye = (float[])mono.Clone();
        var monoSharpness = StereoRenderedOutputMetrics.MeasureEdgeSharpness(mono, width, height);
        var stereoSharpness = StereoRenderedOutputMetrics.MeasureEdgeSharpness(stereoEye, width, height);

        float sharpnessRatio = stereoSharpness.MaxGradient / Math.Max(monoSharpness.MaxGradient, float.Epsilon);
        sharpnessRatio.ShouldBeGreaterThanOrEqualTo(StereoRenderedOutputThresholds.MinStaticEdgeSharpnessRatioToMono);
        StereoRenderedOutputMetrics.RootMeanSquareError(stereoEye, mono)
            .ShouldBeLessThanOrEqualTo(StereoRenderedOutputThresholds.MaxTemporalConvergenceRmse);
    }

    [Test]
    public void DiagnosticCapturePaths_UseBothLayersAndConfiguredAgentRunRoot()
    {
        string runRoot = Path.Combine("Build", "_AgentValidation", "20260713-stereo-contract");
        string expected = Path.Combine(runRoot, "mcp-captures");

        DefaultPipelineDiagnosticCapture.ResolveLayerCount(stereo: true).ShouldBe(2);
        DefaultPipelineDiagnosticCapture.ResolveLayerCount(stereo: false).ShouldBe(1);
        DefaultPipelineDiagnosticCapture.ResolveOutputDirectory(null, runRoot).ShouldBe(expected);
        DefaultPipelineDiagnosticCapture.ResolveOutputDirectory("custom-captures", runRoot).ShouldBe("custom-captures");
        DefaultPipelineDiagnosticCapture.ResolveTemporalScenarioOutputPath(
                "DefaultPipelineSps",
                EPhase524bTemporalSample.StaticPoseSettled,
                "07_Velocity",
                1)
            .ShouldEndWith("DefaultPipelineSps_Temporal_StaticPoseSettled_07_Velocity_layer1.png");
    }

    [Test]
    public void SmaaStereoShaderVariant_EnablesArraySamplingAtAllocationTime()
    {
        string source = VPRC_SMAA.ResolveShaderCode("#version 450\nuniform sampler2D Source;", stereo: true);

        source.ShouldContain("#define XRE_STEREO 1");
        source.ShouldStartWith("#version 450");
        VPRC_SMAA.ResolveShaderCode(source, stereo: false).ShouldBe(source);
    }

    private static float[] CreateRgba(int width, int height)
        => new float[checked(width * height * 4)];

    private static float[] CreateVerticalStep(int width, int height)
    {
        float[] rgba = CreateRgba(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = width / 2; x < width; x++)
                SetPixel(rgba, width, x, y, Vector3.One);
        }
        return rgba;
    }

    private static void SetPixel(float[] rgba, int width, int x, int y, Vector3 rgb)
    {
        int offset = (y * width + x) * 4;
        rgba[offset] = rgb.X;
        rgba[offset + 1] = rgb.Y;
        rgba[offset + 2] = rgb.Z;
        rgba[offset + 3] = 1.0f;
    }
}
