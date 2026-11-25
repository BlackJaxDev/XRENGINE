using XREngine.Components;
using NUnit.Framework;
using Shouldly;
using XREngine.Input.Devices;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextMultilineSubmitTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void EnterAddsNewlineCtrlEnterCommitsAndEscapeRestoresDraft()
    {
        SceneNode root = new("Multiline input", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        try
        {
            _ = root.AddComponent<UICanvasComponent>()!;
            SceneNode field = new(root);
            field.SetTransform<UIBoundableTransform>();
            field.AddComponent<UITextComponent>();
            UITextInputComponent input = field.AddComponent<UITextInputComponent>()!;
            UIValueState<string> binding = new("Start");
            input.Binding = binding;
            input.SingleLineMode = false;
            input.CommitOnSubmit = true;
            root.IsActiveSelf = true;
            input.IsFocused = true;
            input.CursorPosition = input.Text.Length;
            input.SelectionAnchor = input.CursorPosition;
            int submits = 0;
            int cancels = 0;
            int writes = 0;
            input.Submitted += _ => submits++;
            input.Cancelled += _ => cancels++;
            binding.ValueChanged += _ => writes++;
            UITestTreeKeyRegistration keys = new();
            input.RegisterInput(keys);

            keys.Press(EKey.Enter).ShouldBeTrue();
            input.Text.ShouldBe("Start" + Environment.NewLine);
            binding.Value.ShouldBe("Start");
            keys.Press(EKey.ControlLeft).ShouldBeTrue();
            keys.Press(EKey.Enter).ShouldBeTrue();
            keys.Release(EKey.ControlLeft).ShouldBeTrue();
            binding.Value.ShouldBe("Start" + Environment.NewLine);
            submits.ShouldBe(1);
            writes.ShouldBe(1);

            input.UserAddText("Draft");
            binding.Value.ShouldBe("Start" + Environment.NewLine);
            keys.Press(EKey.Escape).ShouldBeTrue();
            input.Text.ShouldBe(binding.Value);
            cancels.ShouldBe(1);
            writes.ShouldBe(1);

            input.BeginComposition().ShouldBeTrue();
            input.UpdateComposition("preedit", 7).ShouldBeTrue();
            keys.Press(EKey.Escape).ShouldBeTrue();
            input.IsComposing.ShouldBeFalse();
            input.Text.ShouldBe(binding.Value);
            cancels.ShouldBe(1);
            writes.ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }
}
