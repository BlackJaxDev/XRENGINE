using NUnit.Framework;
using System.Numerics;
using System.Reflection;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextLifetimeTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void DirectDestroyCancelsCompositionAndRetiresBindingDelivery()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextLifetimeBinding binding = new("seed");
        input.Binding = binding;
        root.IsActiveSelf = true;
        try
        {
            binding.ListenerCount.ShouldBe(1);
            Action<string> delayed = binding.CaptureDelivery().ShouldNotBeNull();
            input.IsFocused = true;
            input.BeginComposition().ShouldBeTrue();
            input.UpdateComposition("draft", 5).ShouldBeTrue();
            long epoch = input.FocusEpoch;

            input.Destroy(true);

            input.FocusEpoch.ShouldBeGreaterThan(epoch);
            input.Binding.ShouldBeNull();
            input.IsComposing.ShouldBeFalse();
            input.Text.ShouldBe("seed");
            binding.Value.ShouldBe("seed");
            binding.ListenerCount.ShouldBe(0);
            delayed("late");
            input.Text.ShouldBe("seed");
            input.UserAddText("late");
            binding.Value.ShouldBe("seed");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DeactivateAndReactivateOwnOneExactSubscription()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextLifetimeBinding binding = new("initial");
        input.Binding = binding;
        try
        {
            binding.ListenerCount.ShouldBe(0);
            root.IsActiveSelf = true;
            binding.ListenerCount.ShouldBe(1);
            root.IsActiveSelf = false;
            binding.ListenerCount.ShouldBe(0);
            root.IsActiveSelf = true;
            binding.ListenerCount.ShouldBe(1);
            input.Destroy(true);
            binding.ListenerCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DestroyCompletesWhenBindingUnsubscribeThrows()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextLifetimeBinding binding = new("initial");
        input.Binding = binding;
        root.IsActiveSelf = true;
        try
        {
            binding.ThrowOnRemove = true;
            Should.Throw<InvalidOperationException>(() => input.Destroy(true));
            input.Binding.ShouldBeNull();
            binding.ListenerCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BindingReplacementDuringWriteKeepsNewSource()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextLifetimeBinding old = new("old");
        UITextLifetimeBinding replacement = new("replacement");
        input.Binding = old;
        root.IsActiveSelf = true;
        old.ValueChanged += _ => input.Binding = replacement;
        try
        {
            input.UserReplaceText("typed", 5, 5).ShouldBeTrue();
            input.Binding.ShouldBeSameAs(replacement);
            input.Text.ShouldBe("replacement");
            old.ListenerCount.ShouldBe(1); // The test's replacement callback remains.
            replacement.ListenerCount.ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ThrowingAddAfterReplacementKeepsOnlyLatestHandler()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextLifetimeBinding old = new("old");
        UITextLifetimeBinding replacement = new("new");
        root.IsActiveSelf = true;
        old.OnAdd = () => input.Binding = replacement;
        old.ThrowOnAdd = true;
        try
        {
            Should.Throw<InvalidOperationException>(() => input.Binding = old);
            input.Binding.ShouldBeSameAs(replacement);
            old.ListenerCount.ShouldBe(0);
            replacement.ListenerCount.ShouldBe(1);
            replacement.SetValue("latest");
            input.Text.ShouldBe("latest");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RemovalCallbackDestroyStopsOuterBindingChange()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextLifetimeBinding old = new("old");
        UITextLifetimeBinding replacement = new("new");
        input.Binding = old;
        root.IsActiveSelf = true;
        old.OnRemove = () => input.Destroy(true);
        try
        {
            input.Binding = replacement;
            input.Binding.ShouldBeNull();
            replacement.ListenerCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ActivationDuringUnsubscribeReattachesAfterDeactivation()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextLifetimeBinding binding = new("value");
        input.Binding = binding;
        root.IsActiveSelf = true;
        binding.OnRemove = () => root.IsActiveSelf = true;
        try
        {
            root.IsActiveSelf = false;
            root.IsActiveSelf.ShouldBeTrue();
            binding.ListenerCount.ShouldBe(1);
            binding.SetValue("reactivated");
            input.Text.ShouldBe("reactivated");
        }
        finally
        {
            binding.OnRemove = null;
            root.Destroy(true);
        }
    }

    [Test]
    public void LatePropertyAndCaretCallbacksDoNotTouchDestroyedSibling()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        UITextComponent sibling = input.TextComponent;
        input.Text = "before";
        root.IsActiveSelf = true;
        try
        {
            input.Destroy(true);
            string? before = sibling.Text;
            input.Text = "late";
            sibling.Text.ShouldBe(before);
            input.SetCursorPositionWithLocalPoint(Vector3.Zero);
            input.TrySetCursorPositionWithLayout(Vector3.Zero, null!).ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PropertyCallbackDestroyDoesNotRecreateTextSibling()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        SceneNode field = input.SceneNode!;
        UITextComponent sibling = input.TextComponent;
        root.IsActiveSelf = true;
        input.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(UITextInputComponent.Text))
                return;
            sibling.Destroy(true);
            input.Destroy(true);
        };
        try
        {
            input.Text = "destroy";
            input.IsDestroyed.ShouldBeTrue();
            field.GetComponent<UITextComponent>().ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DirectComponentDestroyQueuesOnlyOwnedOverlayNodes()
    {
        SceneNode root = CreateRoot();
        UITextInputComponent input = CreateInput(root);
        SceneNode field = input.SceneNode!;
        root.IsActiveSelf = true;
        try
        {
            MethodInfo place = typeof(UITextInputComponent).GetMethod("PlaceOverlay",
                BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
            object?[] arguments = [null, new Vector4(1, 2, 3, 4),
                "Text caret", ColorF4.White, 20];
            place.Invoke(input, arguments);
            UIBoundableTransform ownedTransform = ((UIBoundableTransform?)arguments[0]).ShouldNotBeNull();
            SceneNode owned = ownedTransform.SceneNode.ShouldNotBeNull();
            SceneNode foreign = new(input.TextComponent.SceneNode!) { Name = "Foreign overlay" };
            UIBoundableTransform foreignTransform = foreign.SetTransform<UIBoundableTransform>();
            typeof(UITextInputComponent).GetField("_caretOverlay",
                BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull().SetValue(input, foreignTransform);

            input.Destroy(true);

            owned.IsDestroyQueued.ShouldBeTrue();
            foreign.IsDestroyQueued.ShouldBeFalse();
            foreign.IsDestroyed.ShouldBeFalse();
            owned.Parent.ShouldBeSameAs(field);
            XRObjectBase.ProcessPendingDestructions();
            owned.IsDestroyed.ShouldBeTrue();
            foreign.IsDestroyed.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateRoot()
    {
        SceneNode root = new("Text lifetime", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        return root;
    }

    private static UITextInputComponent CreateInput(SceneNode root)
    {
        SceneNode field = root.NewChild("Input");
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        return field.AddComponent<UITextInputComponent>()!;
    }
}
