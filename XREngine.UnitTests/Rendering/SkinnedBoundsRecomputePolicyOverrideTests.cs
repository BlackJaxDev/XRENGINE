using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SkinnedBoundsRecomputePolicyOverrideTests
{
    [Test]
    public void EffectiveSettings_UsesProjectOverrideForSkinnedBoundsRecomputePolicy()
    {
        var originalGameSettings = Engine.GameSettings;
        var originalPolicy = Engine.Rendering.Settings.SkinnedBoundsRecomputePolicy;
        var originalAllowInitial = Engine.Rendering.Settings.AllowInitialSkinnedBoundsBuildWhenNever;

        try
        {
            Engine.GameSettings = new GameStartupSettings();
            Engine.Rendering.Settings.SkinnedBoundsRecomputePolicy = ESkinnedBoundsRecomputePolicy.Selective;
            Engine.Rendering.Settings.AllowInitialSkinnedBoundsBuildWhenNever = true;

            Engine.GameSettings.SkinnedBoundsRecomputePolicyOverride =
                new OverrideableSetting<ESkinnedBoundsRecomputePolicy>(ESkinnedBoundsRecomputePolicy.Never, true);
            Engine.GameSettings.AllowInitialSkinnedBoundsBuildWhenNeverOverride =
                new OverrideableSetting<bool>(false, true);

            Engine.Rendering.Settings.SkinnedBoundsRecomputePolicy.ShouldBe(ESkinnedBoundsRecomputePolicy.Selective);
            Engine.EffectiveSettings.SkinnedBoundsRecomputePolicy.ShouldBe(ESkinnedBoundsRecomputePolicy.Never);
            Engine.EffectiveSettings.AllowInitialSkinnedBoundsBuildWhenNever.ShouldBeFalse();
        }
        finally
        {
            Engine.Rendering.Settings.SkinnedBoundsRecomputePolicy = originalPolicy;
            Engine.Rendering.Settings.AllowInitialSkinnedBoundsBuildWhenNever = originalAllowInitial;
            Engine.GameSettings = originalGameSettings;
        }
    }
}