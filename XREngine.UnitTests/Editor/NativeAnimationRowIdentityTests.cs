using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Animation;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeAnimationRowIdentityTests
{
    [Test]
    public void ProjectedRowsFormatOnlyBoundVisibleItems()
    {
        NativeTextList rows = new();
        int formatted = 0;
        rows.ReplaceProjected([(11, string.Empty), (12, "Fixed")], index =>
        {
            formatted++;
            return $"Projected {index}";
        });
        Assert.That(rows.Count, Is.EqualTo(2));
        Assert.That(formatted, Is.Zero);
        UIBoundableTransform row = rows.CreateRow();
        try
        {
            rows.BindRow(row, 0, 11, 1);
            Assert.That(formatted, Is.EqualTo(1));
            rows.UnbindRow(row, 11, 1);
            rows.BindRow(row, 1, 12, 2);
            Assert.That(formatted, Is.EqualTo(1));
        }
        finally { row.SceneNode?.Destroy(true); }
    }

    [Test]
    public void LargeMemberTreeProjectsOnePageAndIdleTickKeepsItsRevision()
    {
        SceneNode[] previousSelection = Selection.SceneNodes;
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        AnimationMember root = new("Root");
        for (int index = 0; index < 300; index++)
            root.Children.Add(new AnimationMember($"Member{index}"));
        AnimationClip clip = new(root) { LengthInSeconds = 2 };
        try
        {
            Selection.Clear();
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, clip);
            typeof(AnimationPanel).GetMethod("RefreshRows",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            NativeTextList projected = (NativeTextList)typeof(AnimationPanel)
                .GetField("_projectedRows", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;
            Assert.That(projected.Count, Is.LessThan(200));
            ulong revision = projected.Revision;
            typeof(AnimationPanel).GetField("_lastRefresh",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, DateTime.MinValue);
            typeof(AnimationPanel).GetMethod("RefreshTick",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            Assert.That(projected.Revision, Is.EqualTo(revision),
                "An unchanged idle clip must not rebuild all member rows.");
        }
        finally
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, previousAsset);
            try { node.Destroy(true); }
            finally { Selection.SceneNodes = previousSelection; }
        }
    }

    [Test]
    public void KeyDraftRequiresCurrentClipAndExactOwningMember()
    {
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        PropAnimFloat firstTrack = new(2, false, true);
        PropAnimFloat secondTrack = new(2, false, true);
        AnimationMember first = new("First", EAnimationMemberType.Property,
            firstTrack);
        AnimationMember second = new("Second", EAnimationMemberType.Property,
            secondTrack);
        AnimationMember root = new("Root", EAnimationMemberType.Group,
            first, second);
        AnimationClip clip = new(root) { LengthInSeconds = 2 };
        FloatKeyframe key = new();
        secondTrack.Keyframes.Add(key);
        FieldInfo displayed = typeof(AnimationPanel).GetField("_displayedClip",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo member = typeof(AnimationPanel).GetField("_selectedMember",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo selectedKey = typeof(AnimationPanel).GetField("_selectedKey",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo draft = typeof(AnimationPanel).GetMethod("TryGetCurrentKeyDraft",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, clip);
            displayed.SetValue(panel, clip);
            selectedKey.SetValue(panel, key);
            member.SetValue(panel, first);
            Assert.That(draft.Invoke(panel, [null, null, null]), Is.False);
            member.SetValue(panel, second);
            Assert.That(draft.Invoke(panel, [null, null, null]), Is.True);
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, new AnimationClip());
            Assert.That(draft.Invoke(panel, [null, null, null]), Is.False);
        }
        finally
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, previousAsset);
            node.Destroy(true);
        }
    }
    [Test]
    public void PathSourceRebuildDoesNotReuseRetiredRowIds()
    {
        SceneNode[] previous = Selection.SceneNodes;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        MethodInfo identify = typeof(AnimationPanel).GetMethod("IdFor",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo rebuild = typeof(AnimationPanel).GetMethod("RebuildCandidates",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Selection.Clear();
            object first = new();
            ulong retired = (ulong)identify.Invoke(panel, [first])!;
            retired.ShouldBeGreaterThan(79UL);
            rebuild.Invoke(panel, null);
            ulong replacement = (ulong)identify.Invoke(panel, [new object()])!;
            replacement.ShouldBeGreaterThan(retired);
            ulong reintroduced = (ulong)identify.Invoke(panel, [first])!;
            reintroduced.ShouldBeGreaterThan(replacement);
        }
        finally
        {
            try { node.Destroy(true); }
            finally { Selection.SceneNodes = previous; }
        }
    }
}
