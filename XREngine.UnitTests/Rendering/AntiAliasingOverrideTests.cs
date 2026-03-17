using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AntiAliasingOverrideTests
{
    [Test]
    public void ApplyAntiAliasingPreference_DoesNotOverwriteEngineDefaults()
    {
        var originalUserSettings = Engine.UserSettings;
        var originalGameSettings = Engine.GameSettings;
        var originalAntiAliasingMode = Engine.Rendering.Settings.AntiAliasingMode;
        var originalMsaaSampleCount = Engine.Rendering.Settings.MsaaSampleCount;

        try
        {
            Engine.UserSettings = new UserSettings();
            Engine.GameSettings = new GameStartupSettings();
            Engine.Rendering.Settings.AntiAliasingMode = EAntiAliasingMode.Fxaa;
            Engine.Rendering.Settings.MsaaSampleCount = 4u;

            Engine.UserSettings.AntiAliasingModeOverride = new OverrideableSetting<EAntiAliasingMode>(EAntiAliasingMode.Taa, true);
            Engine.UserSettings.MsaaSampleCountOverride = new OverrideableSetting<uint>(8u, true);

            Engine.Rendering.ApplyAntiAliasingPreference();

            Engine.Rendering.Settings.AntiAliasingMode.ShouldBe(EAntiAliasingMode.Fxaa);
            Engine.Rendering.Settings.MsaaSampleCount.ShouldBe(4u);
            Engine.EffectiveSettings.AntiAliasingMode.ShouldBe(EAntiAliasingMode.Taa);
            Engine.EffectiveSettings.MsaaSampleCount.ShouldBe(8u);
        }
        finally
        {
            Engine.Rendering.Settings.AntiAliasingMode = originalAntiAliasingMode;
            Engine.Rendering.Settings.MsaaSampleCount = originalMsaaSampleCount;
            Engine.UserSettings = originalUserSettings;
            Engine.GameSettings = originalGameSettings;
        }
    }
}
