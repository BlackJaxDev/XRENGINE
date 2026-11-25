using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextGuardedSelectionTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void ReversedSelectionUsesGraphemeBoundariesAndOneIntent()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "a\u0301b";
            ulong before = input.CaretWriteIntentRevision;
            int checks = 0;
            Assert.That(input.SetSelectionGuarded(1, 3, 0, _ => checks++), Is.True);
            Assert.That(input.CursorPosition, Is.Zero);
            Assert.That(input.SelectionAnchor, Is.EqualTo(3));
            Assert.That(input.SelectionStart, Is.Zero);
            Assert.That(input.SelectionEnd, Is.EqualTo(3));
            Assert.That(input.CaretWriteIntentRevision, Is.EqualTo(before + 1));
            Assert.That(checks, Is.GreaterThan(2));
            Assert.That(input.SetSelectionGuarded(0, 3, 0, static _ => { }), Is.False);
            Assert.That(input.CaretWriteIntentRevision, Is.EqualTo(before + 2));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void InitialOwnerFailureLeavesBothFieldsUntouched()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "long seed";
            input.CursorPosition = 8;
            input.SelectionAnchor = 6;
            InvalidOperationException failure = new("old receipt");
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(2, 1, failure, static error => throw error)),
                Is.SameAs(failure));
            Assert.That(input.CursorPosition, Is.EqualTo(8));
            Assert.That(input.SelectionAnchor, Is.EqualTo(6));
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CaretPrechangeAttemptSupersedesGuardedSelection(bool restoreOriginal)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            input.CursorPosition = 4;
            input.SelectionAnchor = 3;
            bool nested = false;
            input.PropertyChanging += (_, change) =>
            {
                if (nested || change.PropertyName != nameof(UITextInputComponent.CursorPosition)) return;
                nested = true;
                input.CursorPosition = restoreOriginal ? 4 : 1;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(2, 0, 0, static _ => { }));
            Assert.That(input.CursorPosition, Is.EqualTo(restoreOriginal ? 4 : 1));
            Assert.That(input.SelectionAnchor, Is.EqualTo(3));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void AnchorPrechangeCanReplaceCursorWithoutStaleAnchorCommit()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.SelectionAnchor))
                    input.CursorPosition = 1;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, static _ => { }));
            Assert.That(input.CursorPosition, Is.EqualTo(1));
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VetoRejectsWithoutWritingTheRejectedField(bool vetoAnchor)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == (vetoAnchor
                    ? nameof(UITextInputComponent.SelectionAnchor)
                    : nameof(UITextInputComponent.CursorPosition)))
                    ((XRPropertyChangingEventArgs<int>)change).AllowChange = false;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, static _ => { }));
            Assert.That(input.CursorPosition, Is.EqualTo(vetoAnchor ? 3 : 0));
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void TextCallbackAttemptCancelsLaterSelectionWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.CursorPosition))
                    input.Text = "newer";
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, static _ => { }));
            Assert.That(input.Text, Is.EqualTo("newer"));
            Assert.That(input.CursorPosition, Is.Zero);
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EqualTextOrDisplayAttemptCancelsSelection(bool writeDisplay)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            UITextComponent display = input.TextComponent;
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName != nameof(UITextInputComponent.CursorPosition)) return;
                if (writeDisplay) display.Text = display.Text;
                else input.Text = input.Text;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, static _ => { }));
            Assert.That(input.Text, Is.EqualTo("seed"));
            Assert.That(input.CursorPosition, Is.Zero);
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisplayCallbackAttemptCancelsLaterSelectionWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            UITextComponent display = input.TextComponent;
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.CursorPosition))
                    display.Text = "newer display";
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, static _ => { }));
            Assert.That(display.Text, Is.EqualTo("newer display"));
            Assert.That(input.CursorPosition, Is.Zero);
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DetachmentDuringCaretPrechangeStopsTheFieldWrite(bool detachDisplay)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        SceneNode field = input.SceneNode!;
        UITextComponent display = input.TextComponent;
        try
        {
            input.Text = "seed";
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.CursorPosition))
                    Assert.That(field.DetachComponent(detachDisplay ? display : input), Is.True);
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, static _ => { }));
            Assert.That(input.CursorPosition, Is.Zero);
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally
        {
            if (detachDisplay) display.Destroy(true);
            else input.Destroy(true);
            root.Destroy(true);
        }
    }

    [Test]
    public void QueueingOwnerDuringCaretPrechangeStopsTheFieldWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            SceneNode field = input.SceneNode!;
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.CursorPosition))
                    field.Destroy(false);
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, static _ => { }));
            Assert.That(input.CursorPosition, Is.Zero);
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ChangedObserverFailureKeepsCommittedCaretAndDoesNotWriteAnchor()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            bool current = true;
            input.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.CursorPosition)) current = false;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetSelectionGuarded(3, 2, 0, _ =>
                {
                    if (!current) throw new InvalidOperationException("owner changed");
                }));
            Assert.That(input.CursorPosition, Is.EqualTo(3));
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateInput(out UITextInputComponent input)
    {
        SceneNode root = new("Guarded selection", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode field = root.NewChild("Input");
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        input = field.AddComponent<UITextInputComponent>()!;
        return root;
    }
}
