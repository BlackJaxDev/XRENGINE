using XREngine.Components;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextDraftOwnerTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void ValidatorReplacementCannotCommitOldDraftToNewBindingOnSubmitOrBlur()
    {
        SceneNode root = CreateFocusedInput(out UITextRebindingValidator input,
            out UITextReentrantDraftBinding original);
        try
        {
            UITextReentrantDraftBinding replacement = new("replacement");
            int submits = 0;
            input.Submitted += _ => submits++;
            input.UserReplaceText("draft", 5, 5).ShouldBeTrue();
            input.OnValidate = () => input.Binding = replacement;

            input.UserSubmit();
            input.Binding.ShouldBeSameAs(replacement);
            input.Text.ShouldBe("replacement");
            original.Value.ShouldBe("original");
            replacement.Value.ShouldBe("replacement");
            submits.ShouldBe(0);

            input.IsFocused = false;
            replacement.Value.ShouldBe("replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CancelGetterReplacementRestoresOnlyTheLatestBinding()
    {
        SceneNode root = CreateFocusedInput(out UITextRebindingValidator input,
            out UITextReentrantDraftBinding original);
        try
        {
            UITextReentrantDraftBinding replacement = new("replacement");
            input.UserReplaceText("draft", 5, 5).ShouldBeTrue();
            original.OnGet = () =>
            {
                original.OnGet = null;
                input.Binding = replacement;
            };

            input.UserCancel();

            input.Binding.ShouldBeSameAs(replacement);
            input.Text.ShouldBe("replacement");
            original.Value.ShouldBe("original");
            replacement.Value.ShouldBe("replacement");
            input.IsFocused = false;
            replacement.Value.ShouldBe("replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SetterReplacementDoesNotMarkNewOwnerDraftCommitted()
    {
        SceneNode root = CreateFocusedInput(out UITextRebindingValidator input,
            out UITextReentrantDraftBinding original);
        try
        {
            UITextReentrantDraftBinding replacement = new("replacement");
            int submits = 0;
            input.Submitted += _ => submits++;
            input.UserReplaceText("draft", 5, 5).ShouldBeTrue();
            original.OnSet = () => input.Binding = replacement;

            input.UserSubmit();

            original.Value.ShouldBe("draft");
            input.Binding.ShouldBeSameAs(replacement);
            input.Text.ShouldBe("replacement");
            replacement.Value.ShouldBe("replacement");
            submits.ShouldBe(0);
            input.IsFocused = false;
            replacement.Value.ShouldBe("replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BlurValidatorReplacementCannotCommitOldDraftToNewBinding()
    {
        SceneNode root = CreateFocusedInput(out UITextRebindingValidator input,
            out UITextReentrantDraftBinding original);
        try
        {
            UITextReentrantDraftBinding replacement = new("replacement");
            input.UserReplaceText("draft", 5, 5).ShouldBeTrue();
            input.OnValidate = () => input.Binding = replacement;

            input.IsFocused = false;

            input.Binding.ShouldBeSameAs(replacement);
            input.Text.ShouldBe("replacement");
            original.Value.ShouldBe("original");
            replacement.Value.ShouldBe("replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ValidatorTargetReplacementCannotWriteOldDraftToNewPropertyTargets()
    {
        SceneNode root = new("Legacy draft ownership", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        try
        {
            _ = root.AddComponent<UICanvasComponent>()!;
            SceneNode field = new(root);
            field.SetTransform<UIBoundableTransform>();
            field.AddComponent<UITextComponent>();
            UITextRebindingValidator input = field.AddComponent<UITextRebindingValidator>()!;
            UITextDraftPropertyTarget original = new() { Value = "original" };
            UITextDraftPropertyTarget replacement = new() { Value = "replacement" };
            input.Property = typeof(UITextDraftPropertyTarget).GetProperty(nameof(UITextDraftPropertyTarget.Value));
            input.Targets = [original];
            input.Text = "original";
            input.SingleLineMode = false;
            input.CommitOnSubmit = true;
            root.IsActiveSelf = true;
            input.IsFocused = true;
            input.UserReplaceText("draft", 5, 5).ShouldBeTrue();
            input.OnValidate = () => input.Targets = [replacement];

            input.UserSubmit();
            original.Value.ShouldBe("original");
            replacement.Value.ShouldBe("replacement");
            input.IsFocused = false;
            replacement.Value.ShouldBe("replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ValidatorInPlaceTargetReplacementCannotCommitToNewObject()
    {
        SceneNode root = CreateLegacyInput<UITextRebindingValidator>(out UITextRebindingValidator input);
        try
        {
            UITextDraftPropertyTarget original = new() { Value = "original" };
            UITextDraftPropertyTarget replacement = new() { Value = "replacement" };
            object?[] targets = [original];
            input.Property = typeof(UITextDraftPropertyTarget).GetProperty(nameof(UITextDraftPropertyTarget.Value));
            input.Targets = targets;
            input.Text = "original";
            input.SingleLineMode = false;
            input.CommitOnSubmit = true;
            root.IsActiveSelf = true;
            input.IsFocused = true;
            input.UserReplaceText("draft", 5, 5).ShouldBeTrue();
            input.OnValidate = () => targets[0] = replacement;

            input.UserSubmit();
            input.IsFocused = false;

            original.Value.ShouldBe("original");
            replacement.Value.ShouldBe("replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PropertySetterChangingLaterTargetStopsRemainingTextWrites()
    {
        SceneNode root = CreateLegacyInput<UITextRebindingValidator>(out UITextRebindingValidator input);
        try
        {
            UITextDraftPropertyTarget first = new() { Value = "first" };
            UITextDraftPropertyTarget second = new() { Value = "second" };
            UITextDraftPropertyTarget replacement = new() { Value = "replacement" };
            object?[] targets = [first, second];
            input.Property = typeof(UITextDraftPropertyTarget).GetProperty(nameof(UITextDraftPropertyTarget.Value));
            input.Targets = targets;
            input.Text = "first";
            input.CommitOnSubmit = true;
            root.IsActiveSelf = true;
            input.IsFocused = true;
            input.UserReplaceText("draft", 5, 5).ShouldBeTrue();
            first.OnValueSet = () => targets[1] = replacement;

            input.UserSubmit();

            first.Value.ShouldBe("draft");
            second.Value.ShouldBe("second");
            replacement.Value.ShouldBe("replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PropertySetterChangingLaterTargetStopsRemainingNumericWrites()
    {
        SceneNode root = CreateLegacyInput<UIIntInputComponent>(out UIIntInputComponent input);
        try
        {
            UITextDraftPropertyTarget first = new() { Number = 1 };
            UITextDraftPropertyTarget second = new() { Number = 2 };
            UITextDraftPropertyTarget replacement = new() { Number = 3 };
            object?[] targets = [first, second];
            input.Property = typeof(UITextDraftPropertyTarget).GetProperty(nameof(UITextDraftPropertyTarget.Number));
            input.Targets = targets;
            input.Text = "8";
            first.OnNumberSet = () => targets[1] = replacement;
            root.IsActiveSelf = true;

            input.UserSubmit();

            first.Number.ShouldBe(8);
            second.Number.ShouldBe(2);
            replacement.Number.ShouldBe(3);
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateLegacyInput<T>(out T input) where T : UITextInputComponent, new()
    {
        SceneNode root = new("Legacy draft ownership", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        _ = root.AddComponent<UICanvasComponent>()!;
        SceneNode field = new(root);
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        input = field.AddComponent<T>()!;
        return root;
    }

    private static SceneNode CreateFocusedInput(out UITextRebindingValidator input,
        out UITextReentrantDraftBinding binding)
    {
        SceneNode root = new("Draft ownership", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        _ = root.AddComponent<UICanvasComponent>()!;
        SceneNode field = new(root);
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        input = field.AddComponent<UITextRebindingValidator>()!;
        binding = new UITextReentrantDraftBinding("original");
        input.Binding = binding;
        input.SingleLineMode = false;
        input.CommitOnSubmit = true;
        root.IsActiveSelf = true;
        input.IsFocused = true;
        return root;
    }
}
