using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedRenderPipelineIdentityTests
{
    [Test]
    public void RuntimeAssembly_ContainsOnlyAdvancedPipelineIdentity()
    {
        Type pipelineType = typeof(AdvancedRenderPipeline);

        pipelineType.FullName.ShouldBe("XREngine.Rendering.AdvancedRenderPipeline");
        pipelineType.Assembly
            .GetType("XREngine.Rendering.DefaultRenderPipeline" + "2", throwOnError: false)
            .ShouldBeNull();
    }

    [Test]
    [NonParallelizable]
    public void AdvancedPipelineSelector_UsesDedicatedModeEnvironmentVariable()
    {
        const string expectedVariable = "XRE_ADVANCED_RENDER_PIPELINE_MODE";
        const string formerBooleanVariable = "XRE_USE_ADVANCED_RENDER_" + "PIPELINE";
        const string formerVariable = "XRE_USE_PIPELINE_" + "V2";
        string variable = XREngineEnvironmentVariables.AdvancedRenderPipelineMode;
        string? previousValue = Environment.GetEnvironmentVariable(variable);
        string? previousBooleanValue = Environment.GetEnvironmentVariable(formerBooleanVariable);
        string? previousFormerValue = Environment.GetEnvironmentVariable(formerVariable);
        EAdvancedRenderPipelineMode previousMode =
            RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode;

        try
        {
            variable.ShouldBe(expectedVariable);
            RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode =
                EAdvancedRenderPipelineMode.Disabled;

            Environment.SetEnvironmentVariable(formerBooleanVariable, "1");
            Environment.SetEnvironmentVariable(formerVariable, "1");
            Environment.SetEnvironmentVariable(variable, null);
            EffectiveSettingsEnvOverrides.ReloadForTests();
            EngineRenderingSettingsApplication.AdvancedRenderPipelineMode
                .ShouldBe(EAdvancedRenderPipelineMode.Disabled);

            foreach (EAdvancedRenderPipelineMode mode in
                     Enum.GetValues<EAdvancedRenderPipelineMode>())
            {
                Environment.SetEnvironmentVariable(variable, mode.ToString());
                EffectiveSettingsEnvOverrides.ReloadForTests();
                EngineRenderingSettingsApplication.AdvancedRenderPipelineMode.ShouldBe(mode);
            }

            Environment.SetEnvironmentVariable(variable, "not-a-mode");
            EffectiveSettingsEnvOverrides.ReloadForTests();
            EngineRenderingSettingsApplication.AdvancedRenderPipelineMode
                .ShouldBe(EAdvancedRenderPipelineMode.Disabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousValue);
            Environment.SetEnvironmentVariable(formerBooleanVariable, previousBooleanValue);
            Environment.SetEnvironmentVariable(formerVariable, previousFormerValue);
            EffectiveSettingsEnvOverrides.ReloadForTests();
            RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode = previousMode;
        }
    }
}
