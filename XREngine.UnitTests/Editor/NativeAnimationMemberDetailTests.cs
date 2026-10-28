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
            // Member detail rows must not reuse command row IDs, or a click runs a command.
            ulong lastCommand = (ulong)typeof(AnimationPanel).GetField("LastCommandRowId",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.That(rows.Where(row => row.Text.StartsWith("Selected: ", StringComparison.Ordinal))
                .Select(row => row.Id), Has.All.GreaterThan(lastCommand));
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

            // The key timeline lists lane rows and selected-member details together.
            typeof(AnimationPanel).GetField("_graphView",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, false);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            var rows = (List<(ulong Id, string Text)>)typeof(AnimationPanel)
                .GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;
            Assert.That(rows.Any(row => row.Text.StartsWith("  Lane 1:", StringComparison.Ordinal)), Is.True);
            Assert.That(rows.Any(row => row.Text.StartsWith("Selected: ", StringComparison.Ordinal)), Is.True);
            Assert.That(rows.Select(row => row.Id), Is.Unique);
        }
        finally
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, previousAsset);
            node.Destroy(true);
        }
    }

    [Test]
    public void HoveredKeyUpdatesItsRowInPlaceWithoutEndingEdits()
    {
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        PropAnimFloat animation = new(2, false, true);
        FloatKeyframe key = new() { Second = 0.5f, OutValue = 3 };
        animation.Keyframes.Add(key);
        AnimationMember member = new("Weight", EAnimationMemberType.Property, animation);
        AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group, member))
        {
            LengthInSeconds = 2
        };
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, clip);
            typeof(AnimationPanel).GetMethod("RefreshRows", Instance)!.Invoke(panel, null);
            var rows = (List<(ulong Id, string Text)>)typeof(AnimationPanel)
                .GetField("_rows", Instance)!.GetValue(panel)!;
            Assert.That(rows.Single(row => row.Id == 67).Text, Is.EqualTo("Hovered key: none"));
            object epoch = typeof(AnimationPanel).GetField("_authoringEpoch", Instance)!.GetValue(panel)!;
            int count = rows.Count;

            MethodInfo setHover = typeof(AnimationPanel).GetMethod("SetHoveredKey", Instance)!;
            setHover.Invoke(panel, [member, key]);
            Assert.That(rows.Single(row => row.Id == 67).Text,
                Does.StartWith("Hovered key: Weight | 0.5 s"));
            Assert.That(rows.Count, Is.EqualTo(count));
            Assert.That(typeof(AnimationPanel).GetField("_authoringEpoch", Instance)!.GetValue(panel),
                Is.EqualTo(epoch));

            setHover.Invoke(panel, [null, null]);
            Assert.That(rows.Single(row => row.Id == 67).Text, Is.EqualTo("Hovered key: none"));
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
