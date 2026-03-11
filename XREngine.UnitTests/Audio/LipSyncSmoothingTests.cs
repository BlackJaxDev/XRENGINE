using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Audio;

public sealed class LipSyncSmoothingTests
{
    [TestCase(1.0f, 10.0f, 1.0f)]
    [TestCase(0.5f, 0.5f, 0.25f)]
    [TestCase(0.016f, 12.0f, 0.192f)]
    [TestCase(-1.0f, 10.0f, 0.0f)]
    public void Audio2Face3DComponent_GetSmoothingFactor_ClampsToUnitInterval(float deltaSeconds, float smoothingSpeed, float expected)
    {
        Audio2Face3DComponent.GetSmoothingFactor(deltaSeconds, smoothingSpeed).ShouldBe(expected, 0.000001f);
    }

    [TestCase(1.0f, 10.0f, 1.0f)]
    [TestCase(0.5f, 0.5f, 0.25f)]
    [TestCase(0.016f, 10.0f, 0.16f)]
    [TestCase(-1.0f, 10.0f, 0.0f)]
    public void OVRLipSyncComponent_GetSmoothingFactor_ClampsToUnitInterval(float deltaSeconds, float smoothingSpeed, float expected)
    {
        OVRLipSyncComponent.GetSmoothingFactor(deltaSeconds, smoothingSpeed).ShouldBe(expected, 0.000001f);
    }
}