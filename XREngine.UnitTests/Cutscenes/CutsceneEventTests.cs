using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Editor.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneEventTests
{
    private sealed class FakeResolver(params SceneNode[] nodes) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id)
        {
            foreach (SceneNode node in nodes)
                if (node.ID == id)
                    return node;
            return null;
        }

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>A component with plain members for the property track tests.</summary>
    public sealed class ProbeComponent : XRComponent
    {
        public float Intensity { get; set; } = 2f;

        public Vector3 Tint { get; set; } = new(1, 1, 1);

        public Quaternion Spin { get; set; } = Quaternion.Identity;

        public float Range = 5f;

        public ProbeSettings Settings { get; } = new();

        public string Label { get; set; } = "Label";
    }

    /// <summary>A nested object that a member path walks through.</summary>
    public sealed class ProbeSettings
    {
        public float Exposure { get; set; } = 1f;
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static CutsceneEvent Event(double seconds, string key, string payload = "")
        => new() { Time = T(seconds), Key = key, Payload = payload };

    private static (CutscenePlayer Player, List<string> Fired, CutsceneBindingTable Table) MakeEventRig(
        double duration,
        ECutsceneEventSeekPolicy policy,
        params CutsceneEvent[] events)
    {
        CutsceneAsset asset = new() { Duration = T(duration) };
        EventTrack track = new() { Name = "Events", SeekPolicy = policy };
        track.Events.AddRange(events);
        asset.Tracks.Add(track);

        List<CutsceneCompileDiagnostic> diagnostics = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, diagnostics);
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver());
        table.Diagnostics.ShouldBeEmpty();
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));

        List<string> fired = [];
        player.EventFired += e => fired.Add($"{e.Event.Key}{e.Event.Payload}@{e.Event.Time.ToSeconds():0.###}");
        return (player, fired, table);
    }

    private static void SeekAndTick(CutscenePlayer player, double seconds, ESeekIntent intent = ESeekIntent.Jump)
    {
        player.Seek(T(seconds), intent);
        player.Tick(0.0);
    }

    [Test]
    public void Play_FiresEachCrossedEventOnce_IncludingAtAFrameBoundary()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(
            4, ECutsceneEventSeekPolicy.Skip,
            Event(1.25, "c"), Event(0, "a"), Event(0.5, "b"), Event(1.0, "d"), Event(4, "end"));

        player.Play();
        player.Tick(0.5);
        fired.ShouldBe(["a@0", "b@0.5"]);

        player.Tick(0.5);
        fired.ShouldBe(["a@0", "b@0.5", "d@1"]);

        player.Tick(0.5);
        fired.ShouldBe(["a@0", "b@0.5", "d@1", "c@1.25"]);

        // The end of the range is inclusive, and playback pauses there, so later ticks fire nothing.
        player.Tick(10);
        player.Tick(10);
        fired.ShouldBe(["a@0", "b@0.5", "d@1", "c@1.25", "end@4"]);
    }

    [Test]
    public void Play_AfterPause_DoesNotFireTheEventAtThePausePositionAgain()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(4, ECutsceneEventSeekPolicy.Skip, Event(1, "a"));

        player.Play();
        player.Tick(1);
        player.Pause();
        player.Tick(1);
        player.Play();
        player.Tick(1);

        fired.ShouldBe(["a@1"]);
    }

    [Test]
    public void Seek_WithSkip_FiresNothing()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(10, ECutsceneEventSeekPolicy.Skip, Event(1, "a"), Event(2, "b"));

        SeekAndTick(player, 5);
        fired.ShouldBeEmpty();

        // Playback from the target fires only the events after the target.
        player.Play();
        player.Tick(1);
        fired.ShouldBeEmpty();
    }

    [Test]
    public void Seek_WithFireLatest_FiresTheLastCrossedEventOfEachKey()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(
            10, ECutsceneEventSeekPolicy.FireLatest,
            Event(1, "music", "1"), Event(2, "door", "1"), Event(3, "music", "2"), Event(4, "door", "2"), Event(6, "music", "3"));

        SeekAndTick(player, 5);

        fired.ShouldBe(["music2@3", "door2@4"]);
    }

    [Test]
    public void Seek_WithFireAll_FiresTheCrossedEventsInTimeOrder()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(
            10, ECutsceneEventSeekPolicy.FireAll,
            Event(3, "c"), Event(1, "a"), Event(9, "late"), Event(2, "b"), Event(2, "b", "2"));

        SeekAndTick(player, 5);

        fired.ShouldBe(["a@1", "b@2", "b2@2", "c@3"]);
    }

    [Test]
    public void Seek_ToTheSamePosition_FiresNothing()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(10, ECutsceneEventSeekPolicy.FireAll, Event(0, "a"), Event(1, "b"));

        SeekAndTick(player, 0);
        fired.ShouldBeEmpty();

        // The event at zero has not fired yet, so playback from zero fires it.
        player.Play();
        player.Tick(1);
        fired.ShouldBe(["a@0", "b@1"]);
    }

    [Test]
    public void BackwardSeek_FiresNothing_AndPlaybackFiresTheEventsAgain()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(10, ECutsceneEventSeekPolicy.FireAll, Event(1, "a"), Event(2, "b"));

        player.Play();
        player.Tick(3);
        fired.ShouldBe(["a@1", "b@2"]);

        SeekAndTick(player, 1);
        fired.ShouldBe(["a@1", "b@2"]);

        player.Tick(1.5);
        fired.ShouldBe(["a@1", "b@2", "a@1", "b@2"]);
    }

    [Test]
    public void Scrub_FiresNothing_EvenWithFireAll()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(10, ECutsceneEventSeekPolicy.FireAll, Event(1, "a"), Event(2, "b"));

        player.BeginScrub();
        for (int i = 1; i <= 8; i++)
        {
            player.UpdateScrub(T(i * 0.5));
            player.Tick(0.016);
        }
        player.EndScrub();

        fired.ShouldBeEmpty();
    }

    [Test]
    public void Scrub_FiresByThePolicy_WhenTheTrackOptsIn()
    {
        (CutscenePlayer player, List<string> fired, CutsceneBindingTable table) = MakeEventRig(10, ECutsceneEventSeekPolicy.FireAll, Event(1, "a"), Event(2, "b"));
        ((EventTrack)table.GetTrack(0)).FireDuringScrub = true;

        player.BeginScrub();
        player.UpdateScrub(T(1.5));
        player.Tick(0.016);
        player.UpdateScrub(T(0.5));
        player.Tick(0.016);
        player.EndScrub();

        fired.ShouldBe(["a@1"]);
    }

    [Test]
    public void ReversePlay_FiresNothing()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(10, ECutsceneEventSeekPolicy.Skip, Event(1, "a"), Event(2, "b"), Event(3, "c"));

        SeekAndTick(player, 5);
        player.SetRate(-1);
        player.Play();
        for (int i = 0; i < 10; i++)
            player.Tick(0.5);

        player.Position.ShouldBe(CutsceneTime.Zero);
        fired.ShouldBeEmpty();
    }

    [Test]
    public void Finished_IsRaisedByState_WhenASkipGoesToTheEnd()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(10, ECutsceneEventSeekPolicy.Skip, Event(10, "end"));
        int finished = 0;
        player.Finished += () => finished++;

        player.Play();
        player.Tick(1);
        finished.ShouldBe(0);

        SeekAndTick(player, 10);
        finished.ShouldBe(1);
        fired.ShouldBeEmpty();

        // The position stays at the end, so later ticks do not raise it again.
        player.Tick(1);
        player.Tick(1);
        finished.ShouldBe(1);
        player.State.ShouldBe(ECutsceneTransportState.Paused);

        // A new pass raises it again.
        SeekAndTick(player, 2);
        player.Play();
        player.Tick(20);
        finished.ShouldBe(2);
    }

    [Test]
    public void Finished_IsNotRaisedByAScrubOrAStop()
    {
        (CutscenePlayer player, _, _) = MakeEventRig(10, ECutsceneEventSeekPolicy.Skip);
        int finished = 0;
        player.Finished += () => finished++;

        player.BeginScrub();
        player.UpdateScrub(T(10));
        player.Tick(0.016);
        player.EndScrub();
        player.Stop();

        finished.ShouldBe(0);
    }

    [Test]
    public void Preview_FiresOnlyTheTracksThatOptIn()
    {
        (CutscenePlayer player, List<string> fired, CutsceneBindingTable table) = MakeEventRig(10, ECutsceneEventSeekPolicy.Skip, Event(1, "gameplay"));
        player.IsPreview = true;
        player.Play();
        player.Tick(2);
        fired.ShouldBeEmpty();

        ((EventTrack)table.GetTrack(0)).FireInPreview = true;
        SeekAndTick(player, 0);
        player.Tick(2);
        fired.ShouldBe(["gameplay@1"]);
    }

    [Test]
    public void PreviewHost_FiresOnlyTheTracksThatOptIn()
    {
        CutsceneAsset asset = new() { Duration = T(10) };
        EventTrack gameplay = new() { Name = "Gameplay" };
        gameplay.Events.Add(Event(1, "gameplay"));
        EventTrack authoring = new() { Name = "Authoring", FireInPreview = true };
        authoring.Events.Add(Event(1, "authoring"));
        asset.Tracks.Add(gameplay);
        asset.Tracks.Add(authoring);

        CutscenePreviewHost host = new(new FakeResolver());
        List<string> fired = [];
        host.EventFired += e => fired.Add(e.Event.Key);

        host.Begin(asset);
        host.Tick(0.0);
        host.Play();
        host.Tick(2.0);
        host.End();

        fired.ShouldBe(["authoring"]);
    }

    [Test]
    public void Play_WithoutCrossedEvents_DoesNotAllocate()
    {
        (CutscenePlayer player, List<string> fired, _) = MakeEventRig(1000, ECutsceneEventSeekPolicy.FireAll, Event(999, "late"));
        player.Play();
        player.Tick(0.01);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            player.Tick(0.01);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
        fired.ShouldBeEmpty();
    }

    // Property tracks.

    private sealed class PropertyRig : IDisposable
    {
        public required CutscenePlayer Player { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required ProbeComponent Probe { get; init; }
        public required List<CutsceneCompileDiagnostic> CompileDiagnostics { get; init; }

        public void Dispose() => Table.Dispose();
    }

    private static PropertyRig MakePropertyRig(double duration, params PropertyTrack[] tracks)
    {
        SceneNode node = new("Probe", new Transform());
        ProbeComponent probe = node.AddComponent<ProbeComponent>()!;

        CutsceneAsset asset = new() { Duration = T(duration) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        asset.Tracks.AddRange(tracks);

        List<CutsceneCompileDiagnostic> compile = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, compile);
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        return new PropertyRig
        {
            Player = new CutscenePlayer(compiled, table, new CutsceneStateSnapshot(table)),
            Table = table,
            Probe = probe,
            CompileDiagnostics = compile,
        };
    }

    private static PropertyTrack Track(string member, params PropertyTrackClip[] clips)
    {
        PropertyTrack track = new() { Name = member, BindingSlot = 0, ComponentType = nameof(ProbeComponent), MemberPath = member };
        track.Clips.AddRange(clips);
        return track;
    }

    private static PropertyTrackClip FloatClip(double start, double duration, float from, float to)
        => new()
        {
            Start = T(start),
            Duration = T(duration),
            FloatCurve = new CutsceneFloatCurve { Times = [T(0), T(duration)], Values = [from, to] },
        };

    [Test]
    public void PropertyTrack_WritesFloatVectorAndRotationMembers()
    {
        PropertyTrackClip tint = new()
        {
            Start = T(0),
            Duration = T(4),
            Vector3Curve = new CutsceneVector3Curve { Times = [T(0), T(4)], Values = [Vector3.Zero, new Vector3(4, 8, 12)] },
        };
        Quaternion end = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        PropertyTrackClip spin = new()
        {
            Start = T(0),
            Duration = T(4),
            QuaternionCurve = new CutsceneQuaternionCurve { Times = [T(0), T(4)], Values = [Quaternion.Identity, end] },
        };

        using PropertyRig rig = MakePropertyRig(
            4,
            Track(nameof(ProbeComponent.Intensity), FloatClip(0, 4, 0, 8)),
            Track(nameof(ProbeComponent.Tint), tint),
            Track(nameof(ProbeComponent.Spin), spin),
            Track(nameof(ProbeComponent.Range), FloatClip(0, 4, 10, 20)),
            Track("Settings.Exposure", FloatClip(0, 4, 0, 4)));

        SeekAndTick(rig.Player, 2);

        rig.Probe.Intensity.ShouldBe(4f, 1e-4f);
        Vector3.Distance(rig.Probe.Tint, new Vector3(2, 4, 6)).ShouldBeLessThan(1e-4f);
        MathF.Abs(Quaternion.Dot(rig.Probe.Spin, Quaternion.Slerp(Quaternion.Identity, end, 0.5f))).ShouldBe(1f, 1e-4f);
        rig.Probe.Range.ShouldBe(15f, 1e-4f);
        rig.Probe.Settings.Exposure.ShouldBe(2f, 1e-4f);
        rig.Player.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void PropertyTrack_BlendsFromTheOriginalValue_BelowFullWeight()
    {
        PropertyTrackClip clip = FloatClip(0, 4, 10, 10);
        clip.BlendIn = T(2);
        using PropertyRig rig = MakePropertyRig(4, Track(nameof(ProbeComponent.Intensity), clip));

        SeekAndTick(rig.Player, 0.5);

        // Weight 0.25 blends from the original value 2 toward 10.
        rig.Probe.Intensity.ShouldBe(4f, 1e-4f);
    }

    [Test]
    public void PropertyTrack_NormalizesWeights_AboveOne()
    {
        using PropertyRig rig = MakePropertyRig(
            4,
            Track(nameof(ProbeComponent.Intensity), FloatClip(0, 4, 10, 10)),
            Track(nameof(ProbeComponent.Intensity), FloatClip(0, 4, 20, 20)));

        SeekAndTick(rig.Player, 1);

        rig.Probe.Intensity.ShouldBe(15f, 1e-4f);
    }

    [Test]
    public void PropertyTrack_GivesTheOriginalValueBack_WhenNoClipIsActive()
    {
        using PropertyRig rig = MakePropertyRig(10, Track(nameof(ProbeComponent.Intensity), FloatClip(1, 2, 7, 7)));

        SeekAndTick(rig.Player, 0.5);
        rig.Probe.Intensity.ShouldBe(2f);

        SeekAndTick(rig.Player, 2);
        rig.Probe.Intensity.ShouldBe(7f);

        SeekAndTick(rig.Player, 5);
        rig.Probe.Intensity.ShouldBe(2f);
    }

    [Test]
    public void PropertyTrack_Stop_RestoresOrKeepsByTheFinishBehavior()
    {
        PropertyTrack restored = Track(nameof(ProbeComponent.Intensity), FloatClip(0, 4, 9, 9));
        PropertyTrack kept = Track(nameof(ProbeComponent.Range), FloatClip(0, 4, 30, 30));
        kept.WhenFinished = ECutsceneFinishBehavior.KeepState;
        using PropertyRig rig = MakePropertyRig(4, restored, kept);

        SeekAndTick(rig.Player, 1);
        rig.Probe.Intensity.ShouldBe(9f);
        rig.Probe.Range.ShouldBe(30f);

        rig.Player.Stop();
        rig.Probe.Intensity.ShouldBe(2f);
        rig.Probe.Range.ShouldBe(30f);

        // The kept value is the original value of the next run.
        rig.Probe.Range = 40f;
        SeekAndTick(rig.Player, 1);
        rig.Player.StopAndRestoreAll();
        rig.Probe.Range.ShouldBe(40f);
    }

    [Test]
    public void PropertyTrack_SharedMember_RestoresIfAnyTrackRestores()
    {
        PropertyTrack kept = Track(nameof(ProbeComponent.Intensity), FloatClip(0, 4, 9, 9));
        kept.WhenFinished = ECutsceneFinishBehavior.KeepState;
        PropertyTrack restored = Track(nameof(ProbeComponent.Intensity), FloatClip(2, 2, 9, 9));
        using PropertyRig rig = MakePropertyRig(4, kept, restored);

        SeekAndTick(rig.Player, 3);
        rig.Probe.Intensity.ShouldBe(9f);

        rig.Player.Stop();
        rig.Probe.Intensity.ShouldBe(2f);
    }

    [Test]
    public void PropertyTrack_ReportsAMissingOrUnsupportedMember_Once()
    {
        PropertyTrack missing = Track("Missing", FloatClip(0, 4, 1, 1));
        PropertyTrack unsupported = Track(nameof(ProbeComponent.Label), FloatClip(0, 4, 1, 1));
        PropertyTrack noComponent = Track(nameof(ProbeComponent.Intensity), FloatClip(0, 4, 1, 1));
        noComponent.ComponentType = "NoSuchComponent";
        using PropertyRig rig = MakePropertyRig(4, missing, unsupported, noComponent);

        for (int i = 0; i < 5; i++)
            SeekAndTick(rig.Player, 1 + i * 0.1);

        rig.Player.Diagnostics.Count.ShouldBe(3);
        rig.Player.Diagnostics.ShouldContain(d => d.TrackIndex == 0 && d.Message.Contains("Missing"));
        rig.Player.Diagnostics.ShouldContain(d => d.TrackIndex == 1 && d.Message.Contains("Label"));
        rig.Player.Diagnostics.ShouldContain(d => d.TrackIndex == 2 && d.Message.Contains("NoSuchComponent"));
        rig.Probe.Label.ShouldBe("Label");
    }

    [Test]
    public void PropertyTrack_Evaluation_DoesNotAllocate()
    {
        PropertyTrackClip tint = new()
        {
            Start = T(0),
            Duration = T(100),
            Vector3Curve = new CutsceneVector3Curve { Times = [T(0), T(100)], Values = [Vector3.Zero, Vector3.One] },
        };
        using PropertyRig rig = MakePropertyRig(
            100,
            Track(nameof(ProbeComponent.Intensity), FloatClip(0, 100, 0, 1)),
            Track(nameof(ProbeComponent.Tint), tint));

        SeekAndTick(rig.Player, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            SeekAndTick(rig.Player, 1 + i * 0.05);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }
}
