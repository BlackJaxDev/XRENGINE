using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class NativeAnimationChannelAdmissionTests
{
    [Test]
    public void SelectionAbaAndReentrantOrVetoedTextRefreshCannotEditWrongKey()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode canvas = new("Canvas", new UICanvasTransform()) { IsActiveSelf = false };
        canvas.SetWorldContext(new TestWorldContext());
        canvas.AddComponent<UICanvasComponent>();
        SceneNode node = canvas.NewChild("Animation");
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        NativeAnimationRebindingFloat animation = new();
        FloatKeyframe key = new(0.5f, 1f, 0f, EVectorInterpType.Linear);
        FloatKeyframe nextKey = new(1f, 7f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(key, nextKey);
        AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
        AnimationClip clip = new(member) { LengthInSeconds = 2 };
        AnimationClip other = new() { LengthInSeconds = 2 };
        PropertyInfo selected = typeof(NativeAssetSelection)
            .GetProperty(nameof(NativeAssetSelection.SelectedTarget))!;
        MethodInfo publish = typeof(NativeAssetSelection)
            .GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)!;
        FieldInfo selectedMember = typeof(AnimationPanel)
            .GetField("_selectedMember", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo selectedKey = typeof(AnimationPanel)
            .GetField("_selectedKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            selected.SetValue(null, clip);
            canvas.IsActiveSelf = true;
            selectedMember.SetValue(panel, member);
            selectedKey.SetValue(panel, key);
            UITextInputComponent input = (UITextInputComponent)typeof(AnimationPanel)
                .GetField("_valueInput", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;
            input.Text = "9";
            bool rebound = false;
            animation.OnEnumerate = () =>
            {
                if (rebound) return;
                rebound = true;
                publish.Invoke(null, [other]);
                publish.Invoke(null, [clip]);
                selectedMember.SetValue(panel, member);
                selectedKey.SetValue(panel, key);
            };
            typeof(AnimationPanel).GetMethod("SubmitValue",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [input]);
            Assert.That(rebound, Is.True);
            Assert.That(key.OutValue, Is.EqualTo(1f));
            Assert.That(Undo.CanUndo, Is.False);

            animation.OnEnumerate = null;
            input.Text = "stale";
            bool changedDuringWrite = false;
            input.PropertyChanging += (_, change) =>
            {
                if (changedDuringWrite || change.PropertyName != nameof(input.Text))
                    return;
                changedDuringWrite = true;
                selectedKey.SetValue(panel, nextKey);
                typeof(AnimationPanel).GetMethod("RefreshValueInput",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            };
            typeof(AnimationPanel).GetMethod("RefreshValueInput",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            Assert.That(changedDuringWrite, Is.True);
            Assert.That(input.Text, Is.EqualTo("7"));

            selectedKey.SetValue(panel, key);
            input.Text = "stale";
            bool submittedDuringRefresh = false;
            input.PropertyChanging += (_, change) =>
            {
                if (submittedDuringRefresh || change.PropertyName != nameof(input.Text))
                    return;
                submittedDuringRefresh = true;
                typeof(AnimationPanel).GetMethod("SubmitValue",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [input]);
            };
            typeof(AnimationPanel).GetMethod("RefreshValueInput",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            Assert.That(submittedDuringRefresh, Is.True);
            Assert.That(key.OutValue, Is.EqualTo(1f));
            Assert.That(input.Text, Is.EqualTo("1"));

            selectedKey.SetValue(panel, nextKey);
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(input.Text) &&
                    change is XRPropertyChangingEventArgs<string> text &&
                    text.NewValue == "7")
                    text.AllowChange = false;
            };
            typeof(AnimationPanel).GetMethod("RefreshValueInput",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            Assert.That(input.Text, Is.EqualTo("1"));
            typeof(AnimationPanel).GetMethod("SubmitValue",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [input]);
            Assert.That(nextKey.OutValue, Is.EqualTo(7f));
            Assert.That(Undo.CanUndo, Is.False);

            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(input.Text) &&
                    change is XRPropertyChangingEventArgs<string> text &&
                    text.NewValue == "7")
                    throw new InvalidOperationException("text observer failed");
            };
            Assert.Throws<TargetInvocationException>(() =>
                typeof(AnimationPanel).GetMethod("RefreshValueInput",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null));
            Assert.That(typeof(AnimationPanel).GetField("_valueInputRefreshPending",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel), Is.True);
        }
        finally
        {
            animation.OnEnumerate = null;
            selected.SetValue(null, previousAsset);
            canvas.Destroy(true);
            Undo.ClearHistory();
        }
    }
}
