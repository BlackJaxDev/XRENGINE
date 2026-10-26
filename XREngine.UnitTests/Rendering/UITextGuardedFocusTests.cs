using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextGuardedFocusTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public void FocusPublishesTheDraftAndLossPublishesFormatting(bool guarded)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.PreText = "[";
            input.PostText = "]";
            input.Text = "draft";
            input.CursorPosition = 4;
            input.SelectionAnchor = 1;
            long epoch = input.FocusEpoch;
            int gained = 0, lost = 0;
            input.GotFocus += _ => gained++;
            input.LostFocus += _ => lost++;
            if (guarded) Assert.That(input.SetFocusGuarded(true, static () => true), Is.True);
            else input.IsFocused = true;
            Assert.That(input.TextComponent.Text, Is.EqualTo("draft"));
            if (guarded) Assert.That(input.SetFocusGuarded(false, static () => true), Is.True);
            else input.IsFocused = false;
            Assert.That(input.TextComponent.Text, Is.EqualTo("[draft]"));
            Assert.That(input.CursorPosition, Is.EqualTo(4));
            Assert.That(input.SelectionAnchor, Is.EqualTo(1));
            Assert.That(input.FocusEpoch, Is.EqualTo(epoch + 2));
            Assert.That(gained, Is.EqualTo(1));
            Assert.That(lost, Is.EqualTo(1));
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FocusObserverDisplayEditCannotBecomeTheOldRequestDisplay(bool afterWrite)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "draft";
            UITextComponent display = input.TextComponent;
            int gained = 0;
            input.GotFocus += _ => gained++;
            if (afterWrite)
                input.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(UITextInputComponent.IsFocused))
                        display.Text = "newer display";
                };
            else
                input.PropertyChanging += (_, change) =>
                {
                    if (change.PropertyName == nameof(UITextInputComponent.IsFocused))
                        display.Text = "newer display";
                };

            Assert.That(input.SetFocusGuarded(true, static () => true), Is.False);
            Assert.That(display.Text, Is.EqualTo("newer display"));
            Assert.That(gained, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void LossPrechangeProofFailureStopsTheOldFocusEffects()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        XRPropertyChangingEventHandler? rejectLoss = null;
        try
        {
            input.PreText = "[";
            input.Text = "draft";
            input.IsFocused = true;
            long epoch = input.FocusEpoch;
            bool current = true;
            int lost = 0;
            input.LostFocus += _ => lost++;
            rejectLoss = (_, change) =>
            {
                if (change.PropertyName != nameof(UITextInputComponent.IsFocused)) return;
                current = false;
                input.TextComponent.Text = "newer display";
            };
            input.PropertyChanging += rejectLoss;

            Assert.That(input.SetFocusGuarded(false, () => current), Is.False);
            Assert.That(input.IsFocused, Is.True);
            Assert.That(input.FocusEpoch, Is.EqualTo(epoch));
            Assert.That(input.TextComponent.Text, Is.EqualTo("newer display"));
            Assert.That(lost, Is.Zero);
        }
        finally
        {
            input.PropertyChanging -= rejectLoss;
            root.Destroy(true);
        }
        Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DisplayPrechangeNewerInputTextCannotBeOverwritten(bool losingFocus)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.PreText = "[";
            input.PostText = "]";
            input.Text = "draft";
            if (losingFocus) input.IsFocused = true;
            UITextComponent display = input.TextComponent;
            bool nested = false;
            int focusEvents = 0;
            input.GotFocus += _ => focusEvents++;
            input.LostFocus += _ => focusEvents++;
            display.PropertyChanging += (_, change) =>
            {
                if (nested || change.PropertyName != nameof(UITextComponent.Text)) return;
                nested = true;
                input.Text = "newer draft";
            };

            Assert.Throws<InvalidOperationException>(() =>
                input.SetFocusGuarded(!losingFocus, static () => true));
            Assert.That(nested, Is.True);
            Assert.That(input.Text, Is.EqualTo("newer draft"));
            Assert.That(display.Text, Is.EqualTo("newer draft"));
            Assert.That(focusEvents, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisplayPrechangeCaretEditStopsTheOldDisplayWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.PreText = "[";
            input.Text = "draft";
            UITextComponent display = input.TextComponent;
            string? previousDisplay = display.Text;
            display.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextComponent.Text))
                    input.CursorPosition = 2;
            };

            Assert.That(input.SetFocusGuarded(true, static () => true), Is.False);
            Assert.That(input.CursorPosition, Is.EqualTo(2));
            Assert.That(display.Text, Is.EqualTo(previousDisplay));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisplayLifetimeReplacementStopsOldFocusPublication()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.PreText = "[";
            input.Text = "draft";
            UITextComponent display = input.TextComponent;
            string? previousDisplay = display.Text;
            display.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextComponent.Text)) display.Generate();
            };

            Assert.Throws<InvalidOperationException>(() =>
                input.SetFocusGuarded(true, static () => true));
            Assert.That(display.Text, Is.EqualTo(previousDisplay));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void GotFocusCallbackKeepsItsNewerTextAndRejectsOldContinuation()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "draft";
            input.GotFocus += _ => input.Text = "callback draft";

            Assert.That(input.SetFocusGuarded(true, static () => true), Is.False);
            Assert.That(input.Text, Is.EqualTo("callback draft"));
            Assert.That(input.TextComponent.Text, Is.EqualTo("callback draft"));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisplayObserverExceptionKeepsItsOriginalIdentity()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.PreText = "[";
            input.Text = "draft";
            InvalidOperationException expected = new("display observer failed");
            input.TextComponent.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextComponent.Text)) throw expected;
            };

            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                input.SetFocusGuarded(true, static () => true)), Is.SameAs(expected));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void GuardedFocusLossStillCancelsComposition()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            root.IsActiveSelf = true;
            input.IsFocused = true;
            Assert.That(input.BeginComposition(), Is.True);
            Assert.That(input.UpdateComposition("draft", 5), Is.True);

            Assert.That(input.SetFocusGuarded(false, static () => true), Is.True);
            Assert.That(input.IsComposing, Is.False);
            Assert.That(input.Text, Is.EqualTo("seed"));
            Assert.That(input.TextComponent.Text, Is.EqualTo("seed"));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void GuardedFocusLossStillCommitsTheCurrentBindingDraft()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            UITextLifetimeBinding binding = new("seed");
            input.Binding = binding;
            input.CommitOnSubmit = true;
            root.IsActiveSelf = true;
            input.IsFocused = true;
            input.Text = "committed draft";

            Assert.That(input.SetFocusGuarded(false, static () => true), Is.True);
            Assert.That(binding.Value, Is.EqualTo("committed draft"));
            Assert.That(input.Text, Is.EqualTo("committed draft"));
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateInput(out UITextInputComponent input)
    {
        SceneNode root = new("Guarded focus text", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode field = root.NewChild("Input");
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        input = field.AddComponent<UITextInputComponent>()!;
        return root;
    }
}
