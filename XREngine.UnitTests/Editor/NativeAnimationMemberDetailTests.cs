using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeAnimationMemberDetailTests
{
    [Test]
    public void MethodDetailsBoundTextWithoutCallingUserToString()
    {
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        AnimationMember member = new("Apply", EAnimationMemberType.Method)
        {
            MethodArguments = [new NativeAnimationThrowingDetail(), new string('x', 500)],
            DefaultValue = new NativeAnimationThrowingDetail()
        };
        AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group,
            member)) { LengthInSeconds = 2 };
        try
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, clip);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            typeof(AnimationPanel).GetField("_selectedMember",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, member);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            var rows = (List<(ulong Id, string Text)>)typeof(AnimationPanel)
                .GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;
            Assert.That(rows.Any(row => row.Text.Contains("<NativeAnimationThrowingDetail>")), Is.True);
            Assert.That(rows.First(row => row.Id == 101).Text.Length, Is.LessThan(160));
        }
        finally
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, previousAsset);
            node.Destroy(true);
        }
    }

    [Test]
    public void TimelineIncludesSelectedMemberAheadOfPageLanes()
    {
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        AnimationMember root = new("Root", EAnimationMemberType.Group);
        for (int index = 0; index < 8; index++)
            root.Children.Add(new AnimationMember($"Member{index}",
                EAnimationMemberType.Property, new PropAnimFloat(2, false, true)));
        AnimationClip clip = new(root) { LengthInSeconds = 2 };
        AnimationMember selected = root.Children[7];
        try
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, clip);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            typeof(AnimationPanel).GetField("_selectedMember",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, selected);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            var lanes = (List<AnimationMember>)typeof(AnimationPanel)
                .GetField("_visiblePlotMembers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;
            Assert.That(lanes[0], Is.SameAs(selected));
            Assert.That(lanes.Count, Is.EqualTo(8));
        }
        finally
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, previousAsset);
            node.Destroy(true);
        }
    }

    [Test]
    public void SelectedMethodShowsBoundedArgumentsAndAttachedAnimation()
    {
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        AnimationMember member = new("Apply", EAnimationMemberType.Method,
            new PropAnimFloat(2, false, true))
        {
            MethodArguments = new object?[40],
            AnimatedMethodArgumentIndex = 3,
            CacheReturnValue = true
        };
        member.MethodArguments[3] = 4f;
        AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group,
            member)) { LengthInSeconds = 2 };
        try
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, clip);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            typeof(AnimationPanel).GetField("_selectedMember",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, member);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            var rows = (List<(ulong Id, string Text)>)typeof(AnimationPanel)
                .GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;

            Assert.Multiple(() =>
            {
                Assert.That(rows.Any(row => row.Text.Contains("argument 3 is animated")), Is.True);
                Assert.That(rows.Any(row => row.Text.Contains("Attached animation: PropAnimFloat")), Is.True);
                Assert.That(rows.Any(row => row.Text.Contains("8 more method arguments")), Is.True);
                Assert.That(rows.Count(row => row.Id is >= 100 and < 132), Is.EqualTo(32));
            });
        }
        finally
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, previousAsset);
            node.Destroy(true);
        }
    }
}
