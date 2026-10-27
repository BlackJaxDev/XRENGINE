using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class BakeMotionSelectorTests
{
    private static readonly GaussianBakeBudget AvatarBudget = GaussianBakeBudget.For(GaussianBakeKind.Avatar);
    private static readonly GaussianBakeBudget WorldBudget = GaussianBakeBudget.For(GaussianBakeKind.World);

    [Test]
    public void ChooseForPrefab_RequestedClipWinsOverIdle()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out _);
        BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Idle", 4.0f));
        AnimationClipComponent wave = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f));
        List<string> warnings = [];

        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, "wave", AvatarBudget, warnings);

        choice.Source.ShouldBe("option");
        choice.ClipName.ShouldBe("Wave");
        choice.Players.ShouldHaveSingleItem().ShouldBeSameAs(wave);
        choice.DurationSeconds.ShouldBe(2.0f);
        choice.FrameCount.ShouldBe(64);
        choice.FrameRate.ShouldBe(32.0f, 1e-4f);
        warnings.ShouldBeEmpty();
    }

    [Test]
    public void ChooseForPrefab_IdleCaseInsensitiveWinsOverFirst()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out _);
        BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f));
        AnimationClipComponent idle = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("IDLE", 4.0f));
        List<string> warnings = [];

        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, null, AvatarBudget, warnings);

        choice.Source.ShouldBe("idle");
        choice.ClipName.ShouldBe("IDLE");
        choice.Players.ShouldHaveSingleItem().ShouldBeSameAs(idle);
        choice.DurationSeconds.ShouldBe(4.0f);
        choice.FrameCount.ShouldBe(120);
        choice.FrameRate.ShouldBe(30.0f, 1e-4f);
        warnings.ShouldBeEmpty();
    }

    [Test]
    public void ChooseForPrefab_UnknownRequested_WarnsAndFallsBack()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out _);
        BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f));
        AnimationClipComponent idle = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Idle", 4.0f));
        List<string> warnings = [];

        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, "Dance", AvatarBudget, warnings);

        warnings.ShouldBe(["clip-not-found:Dance"]);
        choice.Source.ShouldBe("idle");
        choice.Players.ShouldHaveSingleItem().ShouldBeSameAs(idle);
    }

    [Test]
    public void ChooseForPrefab_NoClips_IsStillFrame()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out SceneNode bone);
        BakeMotionTestScenes.AddStaticCube(bone);
        List<string> warnings = [];

        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, null, AvatarBudget, warnings);

        choice.Source.ShouldBe("none");
        choice.ClipName.ShouldBeNull();
        choice.Players.ShouldBeEmpty();
        choice.FrameCount.ShouldBe(1);
        choice.DurationSeconds.ShouldBe(0.0f);
        choice.FrameRate.ShouldBe(AvatarBudget.FrameRate);
        warnings.ShouldBeEmpty();
    }

    [Test]
    public void ChooseForPrefab_TwelveSecondClip_TruncatesTo10s()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out _);
        BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Long", 12.0f));
        List<string> warnings = [];

        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, null, AvatarBudget, warnings);

        choice.Source.ShouldBe("first");
        choice.DurationSeconds.ShouldBe(10.0f);
        choice.FrameCount.ShouldBe(304);
        choice.FrameRate.ShouldBe(30.4f, 1e-4f);
        warnings.ShouldBe(["clip-truncated"]);
    }

    [Test]
    public void Choose_ZeroLengthClip_IsSkipped()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out _);
        AnimationClipComponent empty = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Idle", 0.0f));
        AnimationClipComponent wave = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f));
        List<string> warnings = [];

        BakeMotionChoice prefabChoice = BakeMotionSelector.ChooseForPrefab(root, null, AvatarBudget, warnings);
        BakeMotionChoice worldChoice = BakeMotionSelector.ChooseForWorld(CreateWorld(root), WorldBudget);

        prefabChoice.Source.ShouldBe("first");
        prefabChoice.Players.ShouldHaveSingleItem().ShouldBeSameAs(wave);
        warnings.ShouldBe(["clip-unplayable:Idle"]);
        worldChoice.Players.ShouldNotContain(empty);
        worldChoice.Players.ShouldHaveSingleItem().ShouldBeSameAs(wave);
    }

    [Test]
    public void Choose_HumanoidClipWithoutHumanoidComponent_IsSkipped()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out _);
        AnimationClipComponent humanoid = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Idle", 2.0f, humanoid: true));
        AnimationClipComponent wave = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f));
        root.GetComponent<HumanoidComponent>().ShouldBeNull();
        List<string> warnings = [];

        BakeMotionChoice prefabChoice = BakeMotionSelector.ChooseForPrefab(root, null, AvatarBudget, warnings);
        BakeMotionChoice worldChoice = BakeMotionSelector.ChooseForWorld(CreateWorld(root), WorldBudget);

        prefabChoice.Source.ShouldBe("first");
        prefabChoice.ClipName.ShouldBe("Wave");
        prefabChoice.Players.ShouldHaveSingleItem().ShouldBeSameAs(wave);
        warnings.ShouldBe(["clip-unplayable:Idle"]);
        worldChoice.Players.ShouldNotContain(humanoid);
        worldChoice.Players.ShouldHaveSingleItem().ShouldBeSameAs(wave);
    }

    [Test]
    public void ChooseForPrefab_RequestedNameOnTwoClips_FirstInTraversalOrderWins()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out SceneNode bone);
        AnimationClipComponent first = BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Wave", 2.0f));
        BakeMotionTestScenes.AddClip(bone, BakeMotionTestScenes.CreateClip("wave", 3.0f));
        List<string> warnings = [];

        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, "WAVE", AvatarBudget, warnings);

        choice.Source.ShouldBe("option");
        choice.Players.ShouldHaveSingleItem().ShouldBeSameAs(first);
        warnings.ShouldBeEmpty();
    }

    [Test]
    public void ChooseForPrefab_StateMachineClip_PlaysThroughTemporaryComponent()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out SceneNode bone);
        BakeMotionTestScenes.AddStaticCube(bone);
        AnimStateMachineComponent machine = root.AddComponent<AnimStateMachineComponent>().ShouldNotBeNull();
        AnimationClip idle = BakeMotionTestScenes.CreateClip("Idle", 2.0f, endTranslationX: 4.0f);
        machine.StateMachine.Layers.Add(new AnimLayer(new AnimState(idle, "IdleState")));
        int componentCount = root.Components.Count;
        List<string> warnings = [];

        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, null, AvatarBudget, warnings);
        BakeMotionChoice again = BakeMotionSelector.ChooseForPrefab(root, null, AvatarBudget, warnings);

        choice.Source.ShouldBe("idle");
        AnimationClipComponent player = choice.Players.ShouldHaveSingleItem();
        player.Animation.ShouldBeSameAs(idle);
        player.SceneNode.ShouldBeSameAs(root);
        root.Components.Count.ShouldBe(componentCount);
        again.Source.ShouldBe("idle");
        again.ClipName.ShouldBe("Idle");
        warnings.ShouldBeEmpty();

        DeformedSurface start = BakeFrameSampler.EvaluateAt(root, choice, 0.0f);
        DeformedSurface end = BakeFrameSampler.EvaluateAt(root, choice, 1.0f);
        (end.Positions[0] - start.Positions[0]).ShouldBe(2.0f, 1e-3f);
    }

    [Test]
    public void ChooseForWorld_EveryClipPlaysForMaxDuration()
    {
        SceneNode first = BakeMotionTestScenes.CreateRig(out _);
        AnimationClipComponent idle = BakeMotionTestScenes.AddClip(first, BakeMotionTestScenes.CreateClip("Idle", 4.0f));
        SceneNode second = BakeMotionTestScenes.CreateRig(out _);
        AnimationClipComponent spin = BakeMotionTestScenes.AddClip(second, BakeMotionTestScenes.CreateClip("Spin", 3.0f));

        BakeMotionChoice choice = BakeMotionSelector.ChooseForWorld(CreateWorld(first, second), WorldBudget);

        choice.Source.ShouldBe("world");
        choice.ClipName.ShouldBeNull();
        choice.Players.ShouldBe([idle, spin]);
        choice.DurationSeconds.ShouldBe(WorldBudget.MaxDurationSeconds);
        choice.FrameCount.ShouldBe(304);
        choice.FrameRate.ShouldBe(30.4f, 1e-4f);
    }

    [Test]
    public void ChooseForWorld_NoClips_IsStillFrame()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out SceneNode bone);
        BakeMotionTestScenes.AddStaticCube(bone);

        BakeMotionChoice choice = BakeMotionSelector.ChooseForWorld(CreateWorld(root), WorldBudget);

        choice.Source.ShouldBe("none");
        choice.Players.ShouldBeEmpty();
        choice.FrameCount.ShouldBe(1);
    }

    [Test]
    public void ForEachFrame_WorldPlayers_EvaluateAtTimeModuloLength()
    {
        SceneNode root = BakeMotionTestScenes.CreateRig(out SceneNode bone);
        BakeMotionTestScenes.AddStaticCube(bone);
        BakeMotionTestScenes.AddClip(root, BakeMotionTestScenes.CreateClip("Spin", 3.0f, endTranslationX: 3.0f));
        BakeMotionChoice choice = BakeMotionSelector.ChooseForWorld(CreateWorld(root), WorldBudget);

        DeformedSurface atZero = BakeFrameSampler.EvaluateAt(root, choice, 0.0f);
        DeformedSurface atSevenAndHalf = BakeFrameSampler.EvaluateAt(root, choice, 7.5f);

        // 7.5 s mod 3 s = 1.5 s, where the clip moves the bone by 1.5 along X.
        (atSevenAndHalf.Positions[0] - atZero.Positions[0]).ShouldBe(1.5f, 1e-3f);
        atZero.Positions[1].ShouldBe(atSevenAndHalf.Positions[1], 1e-4f);
    }

    private static XRWorld CreateWorld(params SceneNode[] roots)
        => new("BakeMotionSelectorWorld", new XRScene("BakeMotionSelectorScene", roots));
}
