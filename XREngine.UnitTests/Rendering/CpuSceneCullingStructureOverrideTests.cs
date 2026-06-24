using NUnit.Framework;
using Shouldly;
using System;
using XREngine.Data.Core;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Scene;

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
        string? originalEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure);

        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure, null);
            EffectiveSettingsEnvOverrides.ReloadForTests();
            Engine.GameSettings = new GameStartupSettings();
            Engine.Rendering.Settings.CpuSceneCullingStructure = ECpuSceneCullingStructure.Octree;

            Engine.GameSettings.CpuSceneCullingStructureOverride =
                new OverrideableSetting<ECpuSceneCullingStructure>(ECpuSceneCullingStructure.Bvh, true);

            Engine.Rendering.Settings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Octree);
            Engine.EffectiveSettings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Bvh);

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure, "Octree");
            EffectiveSettingsEnvOverrides.ReloadForTests();
            Engine.EffectiveSettings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Octree);
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure, originalEnv);
            EffectiveSettingsEnvOverrides.ReloadForTests();
            Engine.Rendering.Settings.CpuSceneCullingStructure = originalStructure;
            Engine.GameSettings = originalGameSettings;
        }
    }

    [Test]
    public void VisualScene3D_ApplyCpuSceneCullingStructurePreference_SwitchesGenericRenderTree()
    {
        VisualScene3D scene = new();

        scene.ApplyCpuSceneCullingStructurePreference(ECpuSceneCullingStructure.Bvh);
        scene.GenericRenderTree.ShouldBeOfType<CpuBvhRenderTree<RenderInfo3D>>();

        scene.ApplyCpuSceneCullingStructurePreference(ECpuSceneCullingStructure.Octree);
        scene.GenericRenderTree.ShouldBeOfType<Octree<RenderInfo3D>>();
    }
}
