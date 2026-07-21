using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainQualityTransitionTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void TierTransitionPreservesNormalizedCadenceProgress()
    {
        var component = new PhysicsChainComponent
        {
            UpdateRate = 60.0f,
        };
        SetField(component, "_time", 0.0125f);
        SetField(component, "_qualityCadenceProgress", 0.75f);
        SetField(component, "_qualityPhaseInitialized", true);

        component.SetEffectiveQualityTier(PhysicsChainQualityTier.Hz30);

        GetField<float>(component, "_qualityCadenceProgress").ShouldBe(0.75f, 0.00001f);
        GetField<float>(component, "_time").ShouldBe(0.025f, 0.00001f);
    }

    [Test]
    public void BecomingVisibleWakesSleepingChainAndStartsInteractionGrace()
    {
        var component = new PhysicsChainComponent
        {
            RecentInteractionQualityFrameCount = 12,
        };
        component.SetRuntimeVisibility(false);
        SetField(component, "_isRuntimeSleeping", true);

        component.SetRuntimeVisibility(true);

        component.IsRuntimeSleeping.ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.RelevanceChanged);
        GetField<int>(component, "_recentInteractionQualityFramesRemaining").ShouldBe(12);
    }

    private static void SetField<T>(PhysicsChainComponent component, string name, T value)
    {
        FieldInfo field = typeof(PhysicsChainComponent).GetField(name, InstanceFlags)
            ?? throw new MissingFieldException(typeof(PhysicsChainComponent).FullName, name);
        field.SetValue(component, value);
    }

    private static T GetField<T>(PhysicsChainComponent component, string name)
    {
        FieldInfo field = typeof(PhysicsChainComponent).GetField(name, InstanceFlags)
            ?? throw new MissingFieldException(typeof(PhysicsChainComponent).FullName, name);
        return (T)field.GetValue(component)!;
    }
}
