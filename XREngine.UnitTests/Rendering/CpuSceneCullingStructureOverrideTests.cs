using NUnit.Framework;
using Shouldly;
using System;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class CpuSceneCullingStructureOverrideTests
{
    [Test]
    public void EffectiveSettings_UsesProjectOverrideForCpuSceneCullingStructure()
    {
        var originalGameSettings = Engine.GameSettings;
        var originalStructure = Engine.Rendering.Settings.CpuSceneCullingStructure;
        string? originalEnv = Environment.GetEnvironmentVariable("XRE_CPU_SCENE_CULLING_STRUCTURE");

        try
        {
            Environment.SetEnvironmentVariable("XRE_CPU_SCENE_CULLING_STRUCTURE", null);
            Engine.GameSettings = new GameStartupSettings();
            Engine.Rendering.Settings.CpuSceneCullingStructure = ECpuSceneCullingStructure.Octree;

            Engine.GameSettings.CpuSceneCullingStructureOverride =
                new OverrideableSetting<ECpuSceneCullingStructure>(ECpuSceneCullingStructure.Bvh, true);

            Engine.Rendering.Settings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Octree);
            Engine.EffectiveSettings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Bvh);

            Environment.SetEnvironmentVariable("XRE_CPU_SCENE_CULLING_STRUCTURE", "Octree");
            Engine.EffectiveSettings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Octree);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XRE_CPU_SCENE_CULLING_STRUCTURE", originalEnv);
            Engine.Rendering.Settings.CpuSceneCullingStructure = originalStructure;
            Engine.GameSettings = originalGameSettings;
        }
    }
}
