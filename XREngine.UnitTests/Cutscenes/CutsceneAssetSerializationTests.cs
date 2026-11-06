using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Cutscenes;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneAssetSerializationTests
{
    private static readonly long BigFlicks = (long)int.MaxValue * 3 + 7;

    private static CutsceneClip Fill(CutsceneClip clip)
    {
        clip.Start = new CutsceneTime(BigFlicks);
        clip.Duration = new CutsceneTime(BigFlicks + 11);
        clip.ClipOffset = new CutsceneTime(BigFlicks + 13);
        clip.Speed = 1.5;
        clip.BlendIn = new CutsceneTime(BigFlicks + 17);
        clip.BlendOut = new CutsceneTime(BigFlicks + 19);
        clip.Extrapolation = ECutsceneClipExtrapolation.Loop;
        return clip;
    }

    private static CutsceneAsset CreateSample()
    {
        CutsceneAsset asset = new()
        {
            Name = "Sample",
            Duration = new CutsceneTime(BigFlicks * 2),
            ForceRestoreAll = true,
        };
        asset.Bindings.Add(new CutsceneBindingReference
        {
            Kind = ECutsceneBindingKind.PrefabNode,
            ObjectId = Guid.NewGuid(),
            NodePath = "Root/Child",
            PrefabAssetId = Guid.NewGuid(),
            PrefabNodeId = Guid.NewGuid(),
            SlotName = "Hero",
        });
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "Cam" });

        ActivationTrack activation = new() { Name = "Act", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.KeepState };
        activation.Clips.Add(Fill(new ActivationClip()));
        asset.Tracks.Add(activation);

        TransformTrack transform = new() { Name = "Xf", BindingSlot = 1 };
        TransformTrackClip xf = (TransformTrackClip)Fill(new TransformTrackClip());
        xf.Position = new CutsceneVector3Curve
        {
            Interpolation = ECutsceneCurveInterpolation.Step,
            Times = [new CutsceneTime(BigFlicks), new CutsceneTime(BigFlicks * 2)],
            Values = [new Vector3(1, 2, 3), new Vector3(4, 5, 6)],
            DefaultValue = new Vector3(9, 9, 9),
        };
        xf.Rotation = new CutsceneQuaternionCurve
        {
            Times = [new CutsceneTime(BigFlicks)],
            Values = [Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f)],
        };
        xf.Scale = new CutsceneVector3Curve { Times = [CutsceneTime.Zero], Values = [Vector3.One] };
        transform.Clips.Add(xf);
        asset.Tracks.Add(transform);

        CameraCutTrack cut = new() { Name = "Cut" };
        CameraCutClip cutClip = (CameraCutClip)Fill(new CameraCutClip());
        cutClip.CameraBindingSlot = 4;
        cut.Clips.Add(cutClip);
        asset.Tracks.Add(cut);

        CameraPropertyTrack prop = new() { Name = "Prop", BindingSlot = 2 };
        CameraPropertyClip propClip = (CameraPropertyClip)Fill(new CameraPropertyClip());
        propClip.FieldOfView = new CutsceneFloatCurve
        {
            Interpolation = ECutsceneCurveInterpolation.Linear,
            Times = [new CutsceneTime(BigFlicks), new CutsceneTime(BigFlicks * 3)],
            Values = [60f, 90f],
            DefaultValue = 45f,
        };
        prop.Clips.Add(propClip);
        asset.Tracks.Add(prop);

        EventTrack events = new() { Name = "Events", SeekPolicy = ECutsceneEventSeekPolicy.FireLatest, FireInPreview = true, FireDuringScrub = true };
        events.Events.Add(new CutsceneEvent { Time = new CutsceneTime(BigFlicks), Key = "door", Payload = "open" });
        asset.Tracks.Add(events);

        PropertyTrack member = new() { Name = "Member", BindingSlot = 1, ComponentType = "LightComponent", MemberPath = "Settings.Intensity" };
        PropertyTrackClip memberClip = (PropertyTrackClip)Fill(new PropertyTrackClip());
        memberClip.FloatCurve = new CutsceneFloatCurve { Times = [new CutsceneTime(BigFlicks)], Values = [3f] };
        memberClip.Vector3Curve = new CutsceneVector3Curve { Times = [new CutsceneTime(BigFlicks)], Values = [new Vector3(1, 2, 3)] };
        memberClip.QuaternionCurve = new CutsceneQuaternionCurve { Times = [new CutsceneTime(BigFlicks)], Values = [Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.25f)] };
        member.Clips.Add(memberClip);
        asset.Tracks.Add(member);
        return asset;
    }

    private static void AssertClip(CutsceneClip expected, CutsceneClip actual)
    {
        actual.GetType().ShouldBe(expected.GetType());
        actual.Start.ShouldBe(expected.Start);
        actual.Duration.ShouldBe(expected.Duration);
        actual.ClipOffset.ShouldBe(expected.ClipOffset);
        actual.Speed.ShouldBe(expected.Speed);
        actual.BlendIn.ShouldBe(expected.BlendIn);
        actual.BlendOut.ShouldBe(expected.BlendOut);
        actual.Extrapolation.ShouldBe(expected.Extrapolation);
        actual.Start.Flicks.ShouldBeGreaterThan(int.MaxValue);
    }

    private static void AssertEquivalent(CutsceneAsset expected, CutsceneAsset actual)
    {
        actual.Duration.ShouldBe(expected.Duration);
        actual.ForceRestoreAll.ShouldBe(expected.ForceRestoreAll);

        actual.Bindings.Count.ShouldBe(expected.Bindings.Count);
        for (int i = 0; i < expected.Bindings.Count; i++)
        {
            CutsceneBindingReference e = expected.Bindings[i], a = actual.Bindings[i];
            a.Kind.ShouldBe(e.Kind);
            a.ObjectId.ShouldBe(e.ObjectId);
            a.NodePath.ShouldBe(e.NodePath);
            a.PrefabAssetId.ShouldBe(e.PrefabAssetId);
            a.PrefabNodeId.ShouldBe(e.PrefabNodeId);
            a.SlotName.ShouldBe(e.SlotName);
        }

        actual.Tracks.Count.ShouldBe(expected.Tracks.Count);
        for (int i = 0; i < expected.Tracks.Count; i++)
        {
            CutsceneTrack e = expected.Tracks[i], a = actual.Tracks[i];
            a.GetType().ShouldBe(e.GetType());
            a.Name.ShouldBe(e.Name);
            a.BindingSlot.ShouldBe(e.BindingSlot);
            a.WhenFinished.ShouldBe(e.WhenFinished);
            a.Clips.Count.ShouldBe(e.Clips.Count);
            for (int c = 0; c < e.Clips.Count; c++)
                AssertClip(e.Clips[c], a.Clips[c]);
        }

        TransformTrackClip xe = (TransformTrackClip)expected.Tracks[1].Clips[0];
        TransformTrackClip xa = (TransformTrackClip)actual.Tracks[1].Clips[0];
        xa.Position.Interpolation.ShouldBe(xe.Position.Interpolation);
        xa.Position.Times.ShouldBe(xe.Position.Times);
        xa.Position.Values.ShouldBe(xe.Position.Values);
        xa.Position.DefaultValue.ShouldBe(xe.Position.DefaultValue);
        xa.Rotation.Times.ShouldBe(xe.Rotation.Times);
        xa.Rotation.Values.ShouldBe(xe.Rotation.Values);
        xa.Scale.Values.ShouldBe(xe.Scale.Values);

        ((CameraCutClip)actual.Tracks[2].Clips[0]).CameraBindingSlot.ShouldBe(4);

        CameraPropertyClip pe = (CameraPropertyClip)expected.Tracks[3].Clips[0];
        CameraPropertyClip pa = (CameraPropertyClip)actual.Tracks[3].Clips[0];
        pa.FieldOfView.Times.ShouldBe(pe.FieldOfView.Times);
        pa.FieldOfView.Values.ShouldBe(pe.FieldOfView.Values);
        pa.FieldOfView.DefaultValue.ShouldBe(45f);

        EventTrack ea = (EventTrack)actual.Tracks[4];
        ea.SeekPolicy.ShouldBe(ECutsceneEventSeekPolicy.FireLatest);
        ea.FireInPreview.ShouldBeTrue();
        ea.FireDuringScrub.ShouldBeTrue();
        ea.Events.Count.ShouldBe(1);
        ea.Events[0].Time.ShouldBe(new CutsceneTime(BigFlicks));
        ea.Events[0].Key.ShouldBe("door");
        ea.Events[0].Payload.ShouldBe("open");

        PropertyTrack ma = (PropertyTrack)actual.Tracks[5];
        ma.ComponentType.ShouldBe("LightComponent");
        ma.MemberPath.ShouldBe("Settings.Intensity");
        PropertyTrackClip me = (PropertyTrackClip)expected.Tracks[5].Clips[0];
        PropertyTrackClip mc = (PropertyTrackClip)ma.Clips[0];
        mc.FloatCurve.Values.ShouldBe(me.FloatCurve.Values);
        mc.Vector3Curve.Values.ShouldBe(me.Vector3Curve.Values);
        mc.QuaternionCurve.Values.ShouldBe(me.QuaternionCurve.Values);
    }

    [Test]
    public void YamlSerializer_RoundTrips_CutsceneAsset()
    {
        CutsceneAsset original = CreateSample();
        string yaml = AssetManager.Serializer.Serialize(original, typeof(CutsceneAsset));
        CutsceneAsset? clone = AssetManager.Deserializer.Deserialize<CutsceneAsset>(yaml);
        clone.ShouldNotBeNull();
        AssertEquivalent(original, clone!);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_CutsceneAsset()
    {
        CutsceneAsset original = CreateSample();
        byte[] payload = CookedBinarySerializer.Serialize(original);
        CutsceneAsset? clone = CookedBinarySerializer.Deserialize(typeof(CutsceneAsset), payload) as CutsceneAsset;
        clone.ShouldNotBeNull();
        AssertEquivalent(original, clone!);
    }

    [Test]
    public void Defaults_MatchContract()
    {
        CutsceneAsset asset = new();
        asset.ForceRestoreAll.ShouldBeFalse();
        asset.Tracks.ShouldBeEmpty();
        asset.Bindings.ShouldBeEmpty();
        new ActivationTrack().WhenFinished.ShouldBe(ECutsceneFinishBehavior.RestoreState);
        new ActivationClip().Speed.ShouldBe(1.0);
        new ActivationClip().Extrapolation.ShouldBe(ECutsceneClipExtrapolation.None);
        new EventTrack().SeekPolicy.ShouldBe(ECutsceneEventSeekPolicy.Skip);
        new EventTrack().FireInPreview.ShouldBeFalse();
        new EventTrack().FireDuringScrub.ShouldBeFalse();
    }
}
