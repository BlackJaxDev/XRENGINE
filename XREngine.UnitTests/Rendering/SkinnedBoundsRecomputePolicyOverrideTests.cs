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
        var originalPolicy = RuntimeEngine.Rendering.Settings.SkinnedBoundsRecomputePolicy;
        var originalAllowInitial = RuntimeEngine.Rendering.Settings.AllowInitialSkinnedBoundsBuildWhenNever;

        try
        {
            Engine.GameSettings = new GameStartupSettings();
            RuntimeEngine.Rendering.Settings.SkinnedBoundsRecomputePolicy = ESkinnedBoundsRecomputePolicy.Selective;
            RuntimeEngine.Rendering.Settings.AllowInitialSkinnedBoundsBuildWhenNever = true;

            Engine.GameSettings.SkinnedBoundsRecomputePolicyOverride =
                new OverrideableSetting<ESkinnedBoundsRecomputePolicy>(ESkinnedBoundsRecomputePolicy.Never, true);
            Engine.GameSettings.AllowInitialSkinnedBoundsBuildWhenNeverOverride =
                new OverrideableSetting<bool>(false, true);

            RuntimeEngine.Rendering.Settings.SkinnedBoundsRecomputePolicy.ShouldBe(ESkinnedBoundsRecomputePolicy.Selective);
            Engine.EffectiveSettings.SkinnedBoundsRecomputePolicy.ShouldBe(ESkinnedBoundsRecomputePolicy.Never);
            Engine.EffectiveSettings.AllowInitialSkinnedBoundsBuildWhenNever.ShouldBeFalse();
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.SkinnedBoundsRecomputePolicy = originalPolicy;
            RuntimeEngine.Rendering.Settings.AllowInitialSkinnedBoundsBuildWhenNever = originalAllowInitial;
            Engine.GameSettings = originalGameSettings;
        }
    }
}