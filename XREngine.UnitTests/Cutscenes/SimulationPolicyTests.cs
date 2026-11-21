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
        using Rig rig = MakeRig([new SimulationControlTrack { Name = "Hair" }], withShots: true);
        rig.Player.Simulation.TargetResolver = (_, targets) => targets.Add(target);

        rig.Player.Play();
        for (int i = 0; i < 20; i++)
            rig.Player.Tick(0.05);
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
        long after = GC.GetAllocatedBytesForCurrentThread();
        (after - before).ShouldBe(0);

        // The router runs the discontinuity handler at each scrub step, so the scrub measurement includes it. The scrub
        // stays inside the first shot: a camera claim at a cut allocates inside the viewport, outside the cutscene code.
        XRViewport viewport = CreateViewport();
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);
        for (int i = 0; i < 20; i++)
            SeekAndTick(rig.Player, 0.1 + (i % 9) * 0.2, ESeekIntent.Scrub);

        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
            SeekAndTick(rig.Player, 0.1 + (i % 9) * 0.2, ESeekIntent.Scrub);
        after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
        router.DiscontinuityHandler.HandledCount.ShouldBeGreaterThan(500);
        router.Detach();
    }

    [Test]
    public void Jump_DuringBeginScrub_ReleasesTheHold()
    {
        FakeTarget target = new();
        using Rig rig = MakeFakeRig(target);
        rig.Player.BeginScrub();
        rig.Player.UpdateScrub(T(2.0));
        rig.Player.Tick(0);
        target.Held.ShouldBeTrue();

        SeekAndTick(rig.Player, 4.0);

        target.Held.ShouldBeFalse();
        target.HoldEnds.ShouldBe(1);
        target.Resets.ShouldBe(2);
        rig.Player.Simulation.IsHeld.ShouldBeFalse();
    }

    [Test]
    public void ChainReset_OnAGpuChain_DispatchesTheResetStateWithAZeroTimeStep()
    {
        RecordingGpuBridge bridge = new();
        using IDisposable lease = RuntimePhysicsChainRendering.Install(bridge);
        (SceneNode root, PhysicsChainComponent chain, object last) = MakeChain(useGpu: true);
        try
        {
            chain.ResetSimulationToCurrentPose();
            chain.BeginWorldLateTick().ShouldBeFalse();

            bridge.Dispatches.ShouldBe(1);
            bridge.LastLoopCount.ShouldBe(1);
            bridge.LastTimeVar.ShouldBe(0.0f);
            int firstVersion = bridge.LastParticleStateVersion;

            // A second reset, as in a scrub hold, dispatches again with a new particle state version and the reset positions.
            SetParticlePosition(last, new Vector3(5, 5, 5));
            chain.ResetSimulationToCurrentPose();
            chain.BeginWorldLateTick().ShouldBeFalse();

            bridge.Dispatches.ShouldBe(2);
            bridge.LastParticleStateVersion.ShouldNotBe(firstVersion);
            Vector3 expected = ParticleTransformPosition(last);
            Vector3.Distance(bridge.LastParticlePositions[^1], expected).ShouldBeLessThan(1e-4f);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void ChainReset_WithWeightZero_DropsTheRequestAndKeepsTheBones()
    {
        (SceneNode root, PhysicsChainComponent chain, object last) = MakeChain(useGpu: false);
        try
        {
            chain.BlendWeight = 0.0f;
            SetParticlePosition(last, new Vector3(5, 5, 5));
            chain.ResetSimulationToCurrentPose();

            chain.BeginWorldLateTick().ShouldBeFalse();

            chain.IsSimulationResetPending.ShouldBeFalse();
            GetParticlePosition(last).ShouldBe(new Vector3(5, 5, 5));
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void EmitterHold_DoesNotChangeIsSimulating_AndKeepsPauseDuringTheHold()
    {
        SceneNode node = new("Sparks", new Transform());
        ParticleEmitterComponent emitter = node.AddComponent<ParticleEmitterComponent>()!;
        ParticleEmitterSimulationTarget target = new(emitter);
        try
        {
            target.SetHeld(true);
            emitter.IsSimulationHeld.ShouldBeTrue();
            emitter.IsSimulating.ShouldBeTrue();

            emitter.Pause();
            target.SetHeld(false);

            emitter.IsSimulationHeld.ShouldBeFalse();
            emitter.IsSimulating.ShouldBeFalse();
        }
        finally
        {
            node.Destroy(true);
        }
    }

    [Test]
    public void EmitterHold_ResumeDuringTheHold_RegistersNoSecondTick()
    {
        SceneNode node = new("Sparks", new Transform());
        ParticleEmitterComponent emitter = node.AddComponent<ParticleEmitterComponent>()!;
        ParticleEmitterSimulationTarget target = new(emitter);
        try
        {
            emitter.Pause();
            int ticksBefore = TickCount(emitter);
            target.SetHeld(true);

            emitter.Resume();
            int ticksAfterResume = TickCount(emitter);
            emitter.Resume();

            TickCount(emitter).ShouldBe(ticksAfterResume);
            ticksAfterResume.ShouldBe(ticksBefore + 1);
            emitter.IsSimulating.ShouldBeTrue();
            emitter.IsSimulationHeld.ShouldBeTrue();

            target.SetHeld(false);
            emitter.IsSimulating.ShouldBeTrue();
            emitter.IsSimulationHeld.ShouldBeFalse();
            TickCount(emitter).ShouldBe(ticksAfterResume);
        }
        finally
        {
            node.Destroy(true);
        }
    }

    [Test]
    public void EmitterHold_ActivationDuringTheHold_RegistersTheTickAndKeepsTheHold()
    {
        SceneNode node = new("Sparks", new Transform());
        ParticleEmitterComponent emitter = node.AddComponent<ParticleEmitterComponent>()!;
        ParticleEmitterSimulationTarget target = new(emitter);
        try
        {
            target.SetHeld(true);
            int ticksBefore = TickCount(emitter);

            // A node outside a world has no renderer, so the GPU set-up of the activation cannot run here. The test runs the
            // deactivation and the tick decision of the activation.
            InvokeLifecycle(emitter, "OnComponentDeactivated");
            TickCount(emitter).ShouldBe(ticksBefore);
            InvokeLifecycle(emitter, "RegisterUpdateTickIfSimulating");

            // The hold does not change IsSimulating, so the activation registers the update tick as normal.
            TickCount(emitter).ShouldBe(ticksBefore + 1);
            emitter.IsSimulating.ShouldBeTrue();
            emitter.IsSimulationHeld.ShouldBeTrue();

            target.SetHeld(false);
            emitter.IsSimulationHeld.ShouldBeFalse();
            emitter.IsSimulating.ShouldBeTrue();
        }
        finally
        {
            node.Destroy(true);
        }
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
    public void AutoExposureReset_AppliesOnceToTheFirstFrameWithTheNewEpoch()
    {
        ColorGradingSettings settings = new() { ExposureTransitionSpeed = 0.5f };
        float normal = 1.0f - MathF.Exp(-0.5f * 0.01f);
        settings.ComputeGpuAutoExposureBlend(0.01f).ShouldBe(normal, 1e-6f);

        settings.ResetAutoExposureHistory(minimumFrameCameraEpoch: 5UL);
        settings.IsAutoExposureResetPending.ShouldBeTrue();

        // A frame captured before the discontinuity renders after the reset. It does not use the reset.
        settings.BeginGpuAutoExposureDispatch(4UL, out long requests).ShouldBeFalse();
        settings.ComputeGpuAutoExposureBlend(0.01f).ShouldBe(normal, 1e-6f);
        settings.EndGpuAutoExposureDispatch(success: true, requests);
        settings.IsAutoExposureResetPending.ShouldBeTrue();

        // A failed dispatch of a new frame keeps the reset for the next frame.
        settings.BeginGpuAutoExposureDispatch(5UL, out requests).ShouldBeTrue();
        settings.ComputeGpuAutoExposureBlend(0.01f).ShouldBe(1.0f);
        settings.EndGpuAutoExposureDispatch(success: false, requests);
        settings.IsAutoExposureResetPending.ShouldBeTrue();

        // The first successful dispatch of a new frame uses the reset, and it is used only once.
        settings.BeginGpuAutoExposureDispatch(6UL, out requests).ShouldBeTrue();
        settings.ComputeGpuAutoExposureBlend(0.01f).ShouldBe(1.0f);
        settings.EndGpuAutoExposureDispatch(success: true, requests);
        settings.IsAutoExposureResetPending.ShouldBeFalse();
        settings.ComputeGpuAutoExposureBlend(0.01f).ShouldBe(normal, 1e-6f);

        settings.BeginGpuAutoExposureDispatch(6UL, out requests).ShouldBeFalse();
        settings.EndGpuAutoExposureDispatch(success: true, requests);
    }

    [Test]
    public void WarmUp_AfterAParentMoveInTheSameTick_UsesTheNewEmitterPosition()
    {
        SceneNode parent = new("Rig", new Transform());
        SceneNode child = parent.NewChild("Sparks");
        ParticleEmitterComponent emitter = child.AddComponent<ParticleEmitterComponent>()!;
        try
        {
            ((Transform)child.Transform).Translation = new Vector3(0, 1, 0);
            parent.Transform.RecalculateMatrices();
            child.Transform.RecalculateMatrices();

            // The cutscene tick moves the parent and starts the warm-up before the transform flush.
            ((Transform)parent.Transform).Translation = new Vector3(10, 0, 0);
            emitter.Prewarm(0.1f, seed: 1, budgetMilliseconds: 10_000.0).ShouldBeTrue();

            Vector3 position = emitter.PrewarmWorldMatrix.Translation;
            Vector3.Distance(position, new Vector3(10, 1, 0)).ShouldBeLessThan(1e-4f);
        }
        finally
        {
            parent.Destroy(true);
        }
    }

    [Test]
    public void AutoExposureReset_TheSnapDecisionIsLocalToTheDispatchThread()
    {
        ColorGradingSettings settings = new() { ExposureTransitionSpeed = 0.5f };
        float normal = 1.0f - MathF.Exp(-0.5f * 0.01f);
        settings.ResetAutoExposureHistory(3UL);
        settings.BeginGpuAutoExposureDispatch(3UL, out long requests).ShouldBeTrue();

        // A concurrent dispatch of an old frame on another thread does not see the snap of this thread.
        float otherBlend = 0.0f;
        bool otherSnap = true;
        Thread other = new(() =>
        {
            otherSnap = settings.BeginGpuAutoExposureDispatch(2UL, out long otherRequests);
            otherBlend = settings.ComputeGpuAutoExposureBlend(0.01f);
            settings.EndGpuAutoExposureDispatch(success: true, otherRequests);
        });
        other.Start();
        other.Join();

        otherSnap.ShouldBeFalse();
        otherBlend.ShouldBe(normal, 1e-6f);
        settings.IsAutoExposureResetPending.ShouldBeTrue();
        settings.ComputeGpuAutoExposureBlend(0.01f).ShouldBe(1.0f);
        settings.EndGpuAutoExposureDispatch(success: true, requests);
        settings.IsAutoExposureResetPending.ShouldBeFalse();
    }

    [Test]
    public void AutoExposureReset_ALostCpuLuminanceResultTimesOut()
    {
        ColorGradingSettings settings = new();
        settings.ResetAutoExposureHistory(1UL);
        long start = 1_000_000L;
        long frequency = System.Diagnostics.Stopwatch.Frequency;

        settings.BeginCpuAutoExposureSnap(1UL, start, out long requests).ShouldBeTrue();

        // While the result is in flight, later frames do not start another snap request.
        settings.BeginCpuAutoExposureSnap(1UL, start + frequency / 2, out _).ShouldBeFalse();

        // The result never comes. After the timeout, a new request starts, and its result applies the reset.
        long late = start + (long)(frequency * (ColorGradingSettings.CpuAutoExposureSnapTimeoutSeconds + 0.1));
        settings.BeginCpuAutoExposureSnap(1UL, late, out long retry).ShouldBeTrue();
        retry.ShouldBe(requests);
        settings.EndCpuAutoExposureSnap(applied: true, retry);
        settings.IsAutoExposureResetPending.ShouldBeFalse();
        settings.BeginCpuAutoExposureSnap(1UL, late + frequency * 5, out _).ShouldBeFalse();
    }

    [Test]
    public void AutoExposureReset_ARequestDuringADispatchStaysPending()
    {
        ColorGradingSettings settings = new();
        settings.ResetAutoExposureHistory(1UL);
        settings.BeginGpuAutoExposureDispatch(1UL, out long requests).ShouldBeTrue();

        settings.ResetAutoExposureHistory(2UL);
        settings.EndGpuAutoExposureDispatch(success: true, requests);

        settings.IsAutoExposureResetPending.ShouldBeTrue();
        settings.BeginGpuAutoExposureDispatch(1UL, out requests).ShouldBeFalse();
        settings.EndGpuAutoExposureDispatch(success: true, requests);
        settings.BeginGpuAutoExposureDispatch(2UL, out requests).ShouldBeTrue();
        settings.EndGpuAutoExposureDispatch(success: true, requests);
        settings.IsAutoExposureResetPending.ShouldBeFalse();
    }

    private sealed class RecordingGpuBridge : IRuntimePhysicsChainRenderingBridge
    {
        public int Dispatches;
        public int LastLoopCount;
        public float LastTimeVar;
        public int LastParticleStateVersion;
        public Vector3[] LastParticlePositions = [];

        public PhysicsChainGpuBackendState BackendState => PhysicsChainGpuBackendState.Ready;
        public void Register(PhysicsChainComponent chain) { }
        public void Unregister(PhysicsChainComponent chain) { }

        public void Execute(PhysicsChainComponent chain, in PhysicsChainGpuDispatchSnapshot snapshot)
        {
            Dispatches++;
            LastLoopCount = snapshot.LoopCount;
            LastTimeVar = snapshot.TimeVar;
            LastParticleStateVersion = snapshot.ParticleStateVersion;
            LastParticlePositions = new Vector3[snapshot.Particles.Length];
            for (int i = 0; i < snapshot.Particles.Length; i++)
                LastParticlePositions[i] = snapshot.Particles[i].Position;
        }

        public void NotifyReadbackUnavailable(PhysicsChainComponent chain, string reason) { }
        public void InvalidateGpuDrivenRenderers(PhysicsChainComponent chain) { }
        public void RenderDebug(PhysicsChainComponent chain) { }
        public void RecordHierarchyRecalculationTicks(long ticks) { }
    }

    private static (SceneNode Root, PhysicsChainComponent Chain, object Last) MakeChain(bool useGpu)
    {
        SceneNode root = new("ChainRoot", new Transform());
        Transform rootBone = (Transform)root.Transform;
        Transform parent = rootBone;
        for (int i = 0; i < 3; i++)
        {
            Transform bone = new() { Parent = parent, Translation = new Vector3(0, -0.1f, 0) };
            parent = bone;
        }

        PhysicsChainComponent chain = root.AddComponent<PhysicsChainComponent>()!;
        chain.Root = rootBone;
        chain.UseGPU = useGpu;
        chain.SetupParticles();
        IList particles = ParticlesOf(chain);
        return (root, chain, particles[particles.Count - 1]!);
    }

    private static Vector3 ParticleTransformPosition(object particle)
    {
        object? transform = particle.GetType().GetProperty("Transform", InstanceFlags)!.GetValue(particle);
        if (transform is Transform t)
            return t.WorldTranslation;

        // An end particle has no transform. Its reset position is the end offset from its parent.
        return GetParticlePosition(particle);
    }

    private static int TickCount(XRComponent component)
    {
        FieldInfo field = typeof(XRWorldObjectBase).GetField("_tickCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int count = 0;
        foreach (object _ in (IEnumerable)field.GetValue(component)!)
            count++;
        return count;
    }

    private static void InvokeLifecycle(XRComponent component, string methodName)
        => component.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);

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
