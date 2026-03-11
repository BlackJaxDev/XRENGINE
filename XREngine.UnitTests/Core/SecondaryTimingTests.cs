using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Scene.Mesh;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Timers;

namespace XREngine.UnitTests.Core;

public sealed class SecondaryTimingTests
{
    [Test]
    public void LightComponent_TimeSinceLastMovementSeconds_UsesTickDelta()
    {
        long baseTicks = Stopwatch.Frequency * 60L * 60L * 24L;
        long laterTicks = baseTicks + Stopwatch.Frequency * 3L / 2L;

        LightComponent.TimeSinceLastMovementSeconds(laterTicks, baseTicks).ShouldBe(1.5f, 0.000001f);
    }

    [Test]
    public void RenderableMesh_ShouldReuseSkinnedBounds_UsesTickWindow()
    {
        long baseTicks = Stopwatch.Frequency * 60L * 60L * 24L;
        long withinWindow = baseTicks + Stopwatch.Frequency * 4L;
        long outsideWindow = baseTicks + Stopwatch.Frequency * 6L;

        RenderableMesh.ShouldReuseSkinnedBounds(withinWindow, baseTicks).ShouldBeTrue();
        RenderableMesh.ShouldReuseSkinnedBounds(outsideWindow, baseTicks).ShouldBeFalse();
    }

    [Test]
    public void OvrLipSyncComponent_HasRecentAudioData_UsesTickWindow()
    {
        long baseTicks = Stopwatch.Frequency * 60L * 60L * 8L;
        long freshTicks = baseTicks + Stopwatch.Frequency / 10L;
        long staleTicks = baseTicks + Stopwatch.Frequency / 4L;

        OVRLipSyncComponent.HasRecentAudioData(freshTicks, baseTicks).ShouldBeTrue();
        OVRLipSyncComponent.HasRecentAudioData(staleTicks, baseTicks).ShouldBeFalse();
    }

    [Test]
    public void HumanoidComponent_ShouldAttemptRebind_UsesTickThreshold()
    {
        long nowTicks = Stopwatch.Frequency * 120L;
        long nextRebindTicks = nowTicks + 1L;

        HumanoidComponent.ShouldAttemptRebind(false, nowTicks, nowTicks).ShouldBeTrue();
        HumanoidComponent.ShouldAttemptRebind(false, nowTicks, nextRebindTicks).ShouldBeFalse();
        HumanoidComponent.ShouldAttemptRebind(true, nowTicks, nowTicks).ShouldBeFalse();
    }

    [Test]
    public void VrikSolver_ShouldSendBaseline_UsesTickInterval()
    {
        long baseTicks = Stopwatch.Frequency * 60L;
        long beforeThreshold = baseTicks + Stopwatch.Frequency - 1L;
        long atThreshold = baseTicks + Stopwatch.Frequency;

        VRIKSolverComponent.ShouldSendBaseline(true, beforeThreshold, baseTicks).ShouldBeTrue();
        VRIKSolverComponent.ShouldSendBaseline(false, beforeThreshold, baseTicks).ShouldBeFalse();
        VRIKSolverComponent.ShouldSendBaseline(false, atThreshold, baseTicks).ShouldBeTrue();
    }

    [Test]
    public void HierarchyPanel_IsDoubleClick_UsesTickThreshold()
    {
        var node = new SceneNode();
        long baseTicks = Stopwatch.Frequency * 30L;
        long withinThreshold = baseTicks + EngineTimer.SecondsToStopwatchTicks(0.3f);
        long outsideThreshold = baseTicks + EngineTimer.SecondsToStopwatchTicks(0.6f);

        HierarchyPanel.IsDoubleClick(node, node, withinThreshold, baseTicks, 0.45f).ShouldBeTrue();
        HierarchyPanel.IsDoubleClick(node, node, outsideThreshold, baseTicks, 0.45f).ShouldBeFalse();
        HierarchyPanel.IsDoubleClick(node, new SceneNode(), withinThreshold, baseTicks, 0.45f).ShouldBeFalse();
    }
}