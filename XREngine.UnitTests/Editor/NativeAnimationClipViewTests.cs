using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeAnimationClipViewTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private UITestShaderScope _shaders = null!;
    private object? _previousSelection;
    private SceneNode _root = null!;
    private AnimationPanel _panel = null!;
    private AnimationClip _clip = null!;
    private AnimationClip _other = null!;
    private AnimationMember _member = null!;
    private FloatKeyframe _key = null!;

    [SetUp]
    public void SetUp()
    {
        _shaders = new UITestShaderScope();
        _previousSelection = NativeAssetSelection.SelectedTarget;
        (_clip, _member, _key) = CreateClip("Clip");
        (_other, _, _) = CreateClip("Other");
        SelectClip(_clip);
        _root = new SceneNode("Tools", new UIBoundableTransform());
        _root.SetWorldContext(new TestWorldContext());
        _root.NewChild<AnimationPanel>(out _panel);
        Refresh();
    }

    [TearDown]
    public void TearDown()
    {
        SelectClip(_previousSelection);
        _root?.Destroy(true);
        _shaders?.Dispose();
    }

    [Test]
    public void ReturningToAClipRestoresItsView()
    {
        SetView(_member, _key, zoom: 4f, windowStart: 0.5f);

        SelectClip(null);
        Refresh();
        Assert.That(Get<AnimationMember?>("_selectedMember"), Is.Null);
        SelectClip(_clip);
        Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(Get<AnimationMember?>("_selectedMember"), Is.SameAs(_member));
            Assert.That(Get<Keyframe?>("_selectedKey"), Is.SameAs(_key));
            Assert.That(Get<float>("_plotZoom"), Is.EqualTo(4f));
            Assert.That(Get<float>("_plotWindowStart"), Is.EqualTo(0.5f));
        });
    }

    [Test]
    public void AnotherClipStartsWithADefaultView()
    {
        SetView(_member, _key, zoom: 4f, windowStart: 0.5f);

        SelectClip(_other);
        Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(Get<AnimationMember?>("_selectedMember"), Is.Null);
            Assert.That(Get<Keyframe?>("_selectedKey"), Is.Null);
            Assert.That(Get<float>("_plotZoom"), Is.EqualTo(1f));
            Assert.That(Get<float>("_plotWindowStart"), Is.EqualTo(0f));
        });
    }

    [Test]
    public void RemovedKeyIsNotRestored()
    {
        SetView(_member, _key, zoom: 2f, windowStart: 0f);
        SelectClip(null);
        Refresh();
        ((PropAnimFloat)_member.Animation!).Keyframes.Remove(_key);

        SelectClip(_clip);
        Refresh();

        Assert.That(Get<AnimationMember?>("_selectedMember"), Is.SameAs(_member));
        Assert.That(Get<Keyframe?>("_selectedKey"), Is.Null);
    }

    [Test]
    public void MemoryKeepsTheNewestEntriesAndRejectsAReconstructedClip()
    {
        NativeAnimationClipViewMemory memory = new();
        AnimationClip[] clips = new AnimationClip[NativeAnimationClipViewMemory.Capacity + 1];
        for (int index = 0; index < clips.Length; index++)
        {
            clips[index] = CreateClip("Clip " + index).Clip;
            memory.Remember(clips[index], new(null, null, index, 0, 1, 0));
        }

        Assert.That(memory.Count, Is.EqualTo(NativeAnimationClipViewMemory.Capacity));
        Assert.That(memory.TryTake(clips[0], out _), Is.False, "The oldest entry goes first.");
        Assert.That(memory.TryTake(clips[^1], out NativeAnimationClipView newest), Is.True);
        Assert.That(newest.MemberPage, Is.EqualTo(clips.Length - 1));
        Assert.That(memory.TryTake(clips[^1], out _), Is.False, "A taken entry is removed.");

        memory.Remember(clips[1], default);
        clips[1].Destroy(true);
        Assert.That(memory.TryTake(clips[1], out _), Is.False);
    }

    private void SetView(AnimationMember member, Keyframe key, float zoom, float windowStart)
    {
        Set("_selectedMember", member);
        Set("_selectedKey", key);
        Set("_plotZoom", zoom);
        Set("_plotWindowStart", windowStart);
    }

    private void Refresh()
        => typeof(AnimationPanel).GetMethod("RefreshRows", Fields)!.Invoke(_panel, null);

    private T Get<T>(string field) => (T)typeof(AnimationPanel).GetField(field, Fields)!.GetValue(_panel)!;

    private void Set(string field, object? value) => typeof(AnimationPanel).GetField(field, Fields)!.SetValue(_panel, value);

    private static (AnimationClip Clip, AnimationMember Member, FloatKeyframe Key) CreateClip(string name)
    {
        PropAnimFloat animation = new(3, false, true);
        FloatKeyframe key = new(0.75f, 2f, 2f, 0f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(key);
        AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
        AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group, member))
            { Name = name, LengthInSeconds = 3 };
        return (clip, member, key);
    }

    private static void SelectClip(object? clip)
        => typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
            .SetValue(null, clip);
}
