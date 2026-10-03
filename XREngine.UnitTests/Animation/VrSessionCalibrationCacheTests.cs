using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.VR;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

/// <summary>Checks that a saved body calibration follows one avatar object, not another copy of its prefab.</summary>
[TestFixture]
[NonParallelizable]
public sealed class VrSessionCalibrationCacheTests
{
    private static readonly MethodInfo SaveMethod = typeof(VRPlayerCharacterComponent)
        .GetMethod("SaveSessionCalibration", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo SignatureMethod = typeof(VRPlayerCharacterComponent)
        .GetMethod("AvatarSignature", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo HasCalibrationProperty = typeof(VRPlayerCharacterComponent)
        .GetProperty(nameof(VRPlayerCharacterComponent.HasCalibration))!;
    private static readonly FieldInfo CacheField = typeof(VRPlayerCharacterComponent)
        .GetField("SessionCalibrations", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Test]
    public void SavedCalibration_FollowsReparentedAvatarButNotReplacementOrChangedBindPose()
    {
        using var original = new SyntheticVrCalibrationRig();
        using var replacement = new SyntheticVrCalibrationRig();
        original.Capture().Success.ShouldBeTrue();

        SceneNode playerNode = new(original.SceneRoot, "Player", new Transform());
        VRPlayerCharacterComponent player = playerNode.AddComponent<VRPlayerCharacterComponent>()!;
        player.HumanoidComponent = original.Humanoid;
        player.IKSolver = original.Solver;
        player.InitializeRig();
        HasCalibrationProperty.SetValue(player, true);
        SaveMethod.Invoke(player, null);

        var cache = (ConditionalWeakTable<SceneNode, Dictionary<VrAvatarCalibrationKey, VrSessionCalibration>>)
            CacheField.GetValue(null)!;
        VrAvatarCalibrationKey originalSignature = Signature(player);
        SavedFor(cache, original.AvatarRoot, originalSignature).ShouldBeTrue();

        SceneNode temporaryVrFoot = new(original.SceneRoot, "Temporary VR Foot", new Transform());
        original.AvatarRoot.Transform.SetParent(temporaryVrFoot.Transform, false, EParentAssignmentMode.Immediate);
        SavedFor(cache, original.AvatarRoot, Signature(player)).ShouldBeTrue();

        player.HumanoidComponent = replacement.Humanoid;
        player.IKSolver = replacement.Solver;
        player.InitializeRig();
        VrAvatarCalibrationKey replacementSignature = Signature(player);
        replacementSignature.ShouldBe(originalSignature);
        SavedFor(cache, replacement.AvatarRoot, replacementSignature).ShouldBeFalse();

        player.HumanoidComponent = original.Humanoid;
        player.IKSolver = original.Solver;
        player.InitializeRig();
        SavedFor(cache, original.AvatarRoot, Signature(player)).ShouldBeTrue();
        TransformBase head = original.Humanoid.Head.Node!.Transform;
        head.BindMatrix *= Matrix4x4.CreateTranslation(0.0f, 0.02f, 0.0f);
        SavedFor(cache, original.AvatarRoot, Signature(player)).ShouldBeFalse();
    }

    private static VrAvatarCalibrationKey Signature(VRPlayerCharacterComponent player)
        => (VrAvatarCalibrationKey)SignatureMethod.Invoke(player, null)!;

    private static bool SavedFor(
        ConditionalWeakTable<SceneNode, Dictionary<VrAvatarCalibrationKey, VrSessionCalibration>> cache,
        SceneNode avatar,
        VrAvatarCalibrationKey signature)
        => cache.TryGetValue(avatar, out Dictionary<VrAvatarCalibrationKey, VrSessionCalibration>? entries) &&
            entries.ContainsKey(signature);
}
