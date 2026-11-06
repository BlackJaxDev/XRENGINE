using System.Collections;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Components.Particles;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class SimulationPolicyTests
{
    private sealed class FakeResolver : ICutsceneBindingResolver
    {
        public readonly Dictionary<Guid, SceneNode> ById = [];

        public SceneNode? FindNodeById(Guid id) => ById.GetValueOrDefault(id);

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>Counts the calls of the evaluator. The counters are fields, so the fake does not allocate.</summary>
    private sealed class FakeTarget(bool supportsWarmUp = true) : ICutsceneSimulationTarget
    {
        public readonly object Identity = new();
        public int Resets;
        public int KeepHeldCalls;
        public int WarmUps;
        public int HoldStarts;
        public int HoldEnds;
        public bool Held;
        public int LastSeed = -1;
        public float LastWarmUpSeconds;
        public double LastWarmUpBudget;

        public object Source => Identity;

        public string Name => "fake simulation";

        public bool SupportsWarmUp { get; } = supportsWarmUp;

        public void ResetToCurrentPose(int seed)
        {
            Resets++;
            LastSeed = seed;
        }

        public void SetHeld(bool held)
        {
            Held = held;
            if (held)
                HoldStarts++;
            else
                HoldEnds++;
        }

        public void KeepHeld() => KeepHeldCalls++;

        public void WarmUp(float seconds, int seed, double budgetMilliseconds)
        {
            WarmUps++;
            LastSeed = seed;
            LastWarmUpSeconds = seconds;
            LastWarmUpBudget = budgetMilliseconds;
        }
    }

    private sealed class Rig : IDisposable
    {
        public required SceneNode Root { get; init; }
        public required SceneNode Simulated { get; init; }
        public required CutscenePlayer Player { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required CameraComponent[] Cameras { get; init; }

        public void Dispose()
        {
            Table.Dispose();
            Root.Destroy(true);
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static CameraComponent CreateCamera(SceneNode root, string name)
    {
        CameraComponent component = root.NewChild(name).AddComponent<CameraComponent>()!;
        XRCamera camera = new(component.Transform, new XRPerspectiveCameraParameters(60f, 1, 0.1f, 100));
        typeof(CameraComponent).GetField("_camera", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, new Lazy<XRCamera>(() => camera));
        return component;
    }

    private static XRViewport CreateViewport()
        => new(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false };

    /// <summary>
    /// Builds a 6 s cutscene. Slot 0 is the simulated node. Each entry of <paramref name="tracks"/> adds a simulation track on slot 0.
    /// With <paramref name="withShots"/>, two cameras in slots 1 and 2 have shots at 0-2 s and 2-6 s.
    /// </summary>
    private static Rig MakeRig(IEnumerable<SimulationControlTrack> tracks, bool withShots = false, Action<SceneNode>? setup = null)
    {
        SceneNode root = new("Root", new Transform());
        SceneNode simulated = root.NewChild("Hair");
        setup?.Invoke(simulated);

        FakeResolver resolver = new();
        CutsceneAsset asset = new() { Duration = T(6) };
        resolver.ById[simulated.ID] = simulated;
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = simulated.ID });

        CameraComponent[] cameras = [];
        if (withShots)
        {
            cameras = [CreateCamera(root, "Camera A"), CreateCamera(root, "Camera B")];
            for (int i = 0; i < cameras.Length; i++)
            {
                resolver.ById[cameras[i].SceneNode!.ID] = cameras[i].SceneNode!;
                asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = cameras[i].SceneNode!.ID });
            }

            CameraCutTrack cut = new() { Name = "Cuts", BindingSlot = 1 };
            cut.Clips.Add(new CameraCutClip { Start = T(0), Duration = T(2), CameraBindingSlot = 1 });
            cut.Clips.Add(new CameraCutClip { Start = T(2), Duration = T(4), CameraBindingSlot = 2 });
            asset.Tracks.Add(cut);
        }

        foreach (SimulationControlTrack track in tracks)
        {
            track.BindingSlot = 0;
            asset.Tracks.Add(track);
        }

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, resolver);
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));
        return new Rig { Root = root, Simulated = simulated, Player = player, Table = table, Cameras = cameras };
    }

    private static Rig MakeFakeRig(FakeTarget target, params SimulationControlTrack[] tracks)
    {
        Rig rig = MakeRig(tracks.Length == 0 ? [new SimulationControlTrack { Name = "Hair" }] : tracks);
        rig.Player.Simulation.TargetResolver = (_, targets) => targets.Add(target);
        return rig;
    }

    private static void SeekAndTick(CutscenePlayer player, double seconds, ESeekIntent intent = ESeekIntent.Jump)
    {
        player.Seek(T(seconds), intent);
        player.Tick(0);
    }

    [Test]
    public void Jump_ResetsTheChainOnce()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target, new SimulationControlTrack { Name = "Hair", Seed = 11 });

        SeekAndTick(rig.Player, 3.0);

        target.Resets.ShouldBe(1);
        target.LastSeed.ShouldBe(11);
        target.WarmUps.ShouldBe(0);
        target.HoldStarts.ShouldBe(0);
        rig.Player.Simulation.IsHeld.ShouldBeFalse();

        // Normal playback after the jump is not a discontinuity, so the simulation runs with no more resets.
        rig.Player.Play();
        rig.Player.Tick(0.1);
        rig.Player.Tick(0.1);
        target.Resets.ShouldBe(1);
        target.KeepHeldCalls.ShouldBe(0);
    }

    [Test]
    public void Jump_WithARealChain_RequestsOneResetForTheNextSimulationStep()
    {
        PhysicsChainComponent? chain = null;
        using Rig rig = MakeRig(
            [new SimulationControlTrack { Name = "Hair" }],
            setup: node => chain = node.AddComponent<PhysicsChainComponent>());

        chain!.IsSimulationResetPending.ShouldBeFalse();
        SeekAndTick(rig.Player, 3.0);

        chain.IsSimulationResetPending.ShouldBeTrue();
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Scrub_HoldsTheResetPoseAndResumesAtRateNearOne()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target, new SimulationControlTrack { Name = "Hair", Policy = ECutsceneSimulationPolicy.WarmUp, WarmUpSeconds = 0.5, WarmUpBudgetMilliseconds = 1.5, Seed = 3 });

        SeekAndTick(rig.Player, 1.0, ESeekIntent.Scrub);
        SeekAndTick(rig.Player, 1.5, ESeekIntent.Scrub);

        // Each scrub step resets, and the hold starts once. The warm-up waits for the release.
        target.Resets.ShouldBe(2);
        target.HoldStarts.ShouldBe(1);
        target.Held.ShouldBeTrue();
        target.WarmUps.ShouldBe(0);
        rig.Player.Simulation.IsHeld.ShouldBeTrue();

        // A paused frame with no seek keeps the reset pose.
        rig.Player.Tick(0.016);
        target.KeepHeldCalls.ShouldBe(1);

        // Playback at half rate is not near one, so the hold stays.
        rig.Player.SetRate(0.5);
        rig.Player.Play();
        rig.Player.Tick(0.016);
        target.KeepHeldCalls.ShouldBe(2);
        target.Held.ShouldBeTrue();

        rig.Player.SetRate(0.97);
        rig.Player.Tick(0.016);

        target.Held.ShouldBeFalse();
        target.HoldEnds.ShouldBe(1);
        target.WarmUps.ShouldBe(1);
        target.LastWarmUpSeconds.ShouldBe(0.5f);
        target.LastWarmUpBudget.ShouldBe(1.5);
        target.LastSeed.ShouldBe(3);
        target.Resets.ShouldBe(2);
        target.KeepHeldCalls.ShouldBe(2);
        rig.Player.Simulation.IsHeld.ShouldBeFalse();
    }

    [Test]
    public void Scrub_EndScrubReleasesTheHold()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target);

        rig.Player.BeginScrub();
        rig.Player.UpdateScrub(T(2.0));
        rig.Player.Tick(0);
        rig.Player.Tick(0);
        target.Held.ShouldBeTrue();
        target.KeepHeldCalls.ShouldBe(1);

        rig.Player.EndScrub();
        rig.Player.Tick(0);

        target.Held.ShouldBeFalse();
        target.HoldEnds.ShouldBe(1);
        target.KeepHeldCalls.ShouldBe(1);
    }

    [Test]
    public void Stop_ReleasesTheHoldAndResetsInTheNextTick()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target);
        SeekAndTick(rig.Player, 2.0, ESeekIntent.Scrub);
        target.Held.ShouldBeTrue();

        rig.Player.Stop();

        // The stop puts back the state of a held target at once, but the reset waits for the tick.
        target.Held.ShouldBeFalse();
        target.Resets.ShouldBe(1);
        rig.Player.Tick(0);
        target.Resets.ShouldBe(2);
        target.Held.ShouldBeFalse();
    }

    [Test]
    public void WarmUp_StopsAtItsBudgetAndContinuesNextFrame()
    {
        // The node is not in a world, so the emitter has no GPU buffers. The steps still advance the prewarm.
        SceneNode node = new("Sparks", new Transform());
        ParticleEmitterComponent emitter = node.AddComponent<ParticleEmitterComponent>()!;

        // A budget of zero runs exactly one step in each frame.
        emitter.Prewarm(1.0f, seed: 7, budgetMilliseconds: 0.0).ShouldBeFalse();
        int steps = (int)MathF.Ceiling(1.0f / ParticleEmitterComponent.PrewarmStepSeconds);
        emitter.IsPrewarming.ShouldBeTrue();
        emitter.PrewarmStepsRemaining.ShouldBe(steps - 1);

        emitter.ContinuePrewarm().ShouldBeFalse();
        emitter.PrewarmStepsRemaining.ShouldBe(steps - 2);

        for (int i = 0; i < steps - 3; i++)
            emitter.ContinuePrewarm().ShouldBeFalse();
        emitter.ContinuePrewarm().ShouldBeTrue();
        emitter.IsPrewarming.ShouldBeFalse();
        emitter.ContinuePrewarm().ShouldBeTrue();

        // A large budget completes the prewarm in the call.
        emitter.Prewarm(0.5f, seed: 7, budgetMilliseconds: 10_000.0).ShouldBeTrue();
        emitter.IsPrewarming.ShouldBeFalse();
        node.Destroy(true);
    }

    [Test]
    public void WarmUp_JumpStartsOneWarmUpWithTheTrackValues()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target, new SimulationControlTrack { Name = "Sparks", Policy = ECutsceneSimulationPolicy.WarmUp, WarmUpSeconds = 2.0, WarmUpBudgetMilliseconds = 0.75, Seed = 42 });

        SeekAndTick(rig.Player, 4.0);

        // The warm-up includes the reset, so the target gets no separate reset.
        target.WarmUps.ShouldBe(1);
        target.Resets.ShouldBe(0);
        target.LastWarmUpSeconds.ShouldBe(2.0f);
        target.LastWarmUpBudget.ShouldBe(0.75);
        target.LastSeed.ShouldBe(42);
    }

    [Test]
    public void WarmUp_OnATargetWithNoWarmUp_SettlesFromResetWithOneDiagnostic()
    {
        FakeTarget target = new(supportsWarmUp: false);
        using Rig rig = MakeFakeRig(target, new SimulationControlTrack { Name = "Hair", Policy = ECutsceneSimulationPolicy.WarmUp });

        SeekAndTick(rig.Player, 1.0);
        SeekAndTick(rig.Player, 2.0);

        target.WarmUps.ShouldBe(0);
        target.Resets.ShouldBe(2);
        rig.Player.Diagnostics.Count(d => d.Message.Contains("no warm-up", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public void OneDiscontinuity_GivesOneCallOfEachReset()
    {
        // Two tracks bind the same node, and the seek crosses a cut, so the tick has a jump and a cut.
        FakeTarget target = new();
        using Rig rig = MakeRig([new SimulationControlTrack { Name = "First" }, new SimulationControlTrack { Name = "Second" }], withShots: true);
        rig.Player.Simulation.TargetResolver = (_, targets) => targets.Add(target);
        XRViewport viewport = CreateViewport();
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);
        SeekAndTick(rig.Player, 1.0);
        target.Resets.ShouldBe(1);

        XRCamera next = rig.Cameras[1].Camera;
        ulong epochBefore = next.TemporalHistoryEpoch;
        long exposureBefore = next.AutoExposureResetCount;
        long handledBefore = router.DiscontinuityHandler.HandledCount;
        int discontinuities = 0;
        rig.Player.Discontinuity += _ => discontinuities++;

        SeekAndTick(rig.Player, 3.0);

        viewport.ActiveCamera.ShouldBeSameAs(next);
        discontinuities.ShouldBe(1);
        router.DiscontinuityHandler.HandledCount.ShouldBe(handledBefore + 1);
        next.TemporalHistoryEpoch.ShouldBe(epochBefore + 1UL);
        next.AutoExposureResetCount.ShouldBe(exposureBefore + 1);
        target.Resets.ShouldBe(2);
        rig.Player.Diagnostics.Count(d => d.Message.Contains("Only track", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public void KinematicWrite_HappensInTheNormalTickOnly()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target);

        // The seek call and the stop call only record the request.
        rig.Player.Seek(T(3.0), ESeekIntent.Jump);
        target.Resets.ShouldBe(0);
        rig.Player.Tick(0);
        target.Resets.ShouldBe(1);

        rig.Player.Stop();
        target.Resets.ShouldBe(1);
        rig.Player.Tick(0);
        target.Resets.ShouldBe(2);
    }

    [Test]
    public void KinematicWrite_WithARealChain_HappensInTheNormalTickOnly()
    {
        PhysicsChainComponent? chain = null;
        using Rig rig = MakeRig(
            [new SimulationControlTrack { Name = "Hair" }],
            setup: node => chain = node.AddComponent<PhysicsChainComponent>());

        rig.Player.Seek(T(3.0), ESeekIntent.Jump);
        chain!.IsSimulationResetPending.ShouldBeFalse();
        rig.Player.Tick(0);
        chain.IsSimulationResetPending.ShouldBeTrue();
    }

    [Test]
    public void PlayAndScrub_WithSimulationTrack_AllocateNothing()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target);
        rig.Player.Play();
        for (int i = 0; i < 20; i++)
            rig.Player.Tick(0.05);
        for (int i = 0; i < 20; i++)
            SeekAndTick(rig.Player, 0.1 + (i % 10) * 0.2, ESeekIntent.Scrub);
        rig.Player.Seek(T(0), ESeekIntent.Jump);
        rig.Player.Play();
        rig.Player.Tick(0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            if (rig.Player.State != ECutsceneTransportState.Playing)
            {
                rig.Player.Seek(CutsceneTime.Zero, ESeekIntent.Jump);
                rig.Player.Play();
            }
            rig.Player.Tick(0.05);
        }
        for (int i = 0; i < 500; i++)
            SeekAndTick(rig.Player, 0.1 + (i % 10) * 0.2, ESeekIntent.Scrub);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    [Test]
    public void ChainReset_AppliesAtTheNextSimulationStepAndReplacesIt()
    {
        SceneNode root = new("ChainRoot", new Transform());
        try
        {
            Transform rootBone = (Transform)root.Transform;
            Transform parent = rootBone;
            for (int i = 0; i < 3; i++)
            {
                Transform bone = new() { Parent = parent, Translation = new Vector3(0, -0.1f, 0) };
                parent = bone;
            }

            PhysicsChainComponent chain = root.AddComponent<PhysicsChainComponent>()!;
            chain.Root = rootBone;
            chain.SetupParticles();

            IList particles = ParticlesOf(chain);
            particles.Count.ShouldBeGreaterThan(1);
            object last = particles[particles.Count - 1]!;
            SetParticlePosition(last, new Vector3(5, 5, 5));

            chain.ResetSimulationToCurrentPose();
            chain.IsSimulationResetPending.ShouldBeTrue();

            chain.BeginWorldLateTick().ShouldBeFalse();

            chain.IsSimulationResetPending.ShouldBeFalse();
            GetParticlePosition(last).ShouldNotBe(new Vector3(5, 5, 5));
            Vector3 expected = ((Transform?)last.GetType().GetProperty("Transform")!.GetValue(last))?.WorldTranslation ?? GetParticlePosition(last);
            Vector3.Distance(GetParticlePosition(last), expected).ShouldBeLessThan(1e-4f);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void AutoExposureReset_GivesABlendOfOneUntilAnUpdateAppliesIt()
    {
        ColorGradingSettings settings = new() { ExposureTransitionSpeed = 0.5f };
        float normal = settings.ComputeGpuAutoExposureBlend(0.01f);
        normal.ShouldBe(1.0f - MathF.Exp(-0.5f * 0.01f), 1e-6f);
        settings.IsAutoExposureResetPending.ShouldBeFalse();

        settings.ResetAutoExposureHistory();

        settings.IsAutoExposureResetPending.ShouldBeTrue();
        settings.ComputeGpuAutoExposureBlend(0.01f).ShouldBe(1.0f);
        settings.ComputeGpuAutoExposureBlend(0.0f).ShouldBe(1.0f);
    }

    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static IList ParticlesOf(PhysicsChainComponent chain)
    {
        IList trees = (IList)typeof(PhysicsChainComponent).GetProperty("_particleTrees", InstanceFlags)!.GetValue(chain)!;
        object tree = trees[0]!;
        return (IList)tree.GetType().GetProperty("Particles", InstanceFlags)!.GetValue(tree)!;
    }

    private static Vector3 GetParticlePosition(object particle)
        => (Vector3)particle.GetType().GetProperty("Position", InstanceFlags)!.GetValue(particle)!;

    private static void SetParticlePosition(object particle, Vector3 position)
        => particle.GetType().GetProperty("Position", InstanceFlags)!.SetValue(particle, position);
}
