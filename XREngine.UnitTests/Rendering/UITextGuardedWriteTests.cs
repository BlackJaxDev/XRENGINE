using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextGuardedWriteTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void SuccessfulInputWritePublishesExactlyTheCurrentTextAndDisplay()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            int checks = 0;
            Assert.That(input.SetTextGuarded("hello", 0, _ => checks++), Is.True);
            Assert.That(checks, Is.GreaterThan(1));
            Assert.That(input.Text, Is.EqualTo("hello"));
            Assert.That(input.TextComponent.Text, Is.EqualTo("hello"));
            Assert.That(input.CursorPosition, Is.Zero);
            Assert.That(input.SelectionAnchor, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void InitialValidatorFailureCannotWriteInputOrDisplay()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            UITextComponent display = input.TextComponent;
            string? priorDisplay = display.Text;
            InvalidOperationException expected = new("no longer current");
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("late", expected, static failure => throw failure)),
                Is.SameAs(expected));
            Assert.That(input.Text, Is.Empty);
            Assert.That(display.Text, Is.EqualTo(priorDisplay));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void QueuedOwnerNodeRejectsInputAndStandaloneDisplayWrites()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            SceneNode field = input.SceneNode!;
            UITextComponent display = input.TextComponent;
            string? priorDisplay = display.Text;
            field.Destroy(false);
            Assert.That(field.IsDestroyQueued, Is.True);
            Assert.That(input.IsDestroyed, Is.False);
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("late", 0, static _ => { }));
            Assert.Throws<InvalidOperationException>(() =>
                display.SetTextGuarded("late", 0, static _ => { }));
            Assert.That(input.Text, Is.Empty);
            Assert.That(display.Text, Is.EqualTo(priorDisplay));
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangingDetachmentRejectsInputWriteWithoutClearingComponentNode(bool detachDisplay)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        SceneNode field = input.SceneNode!;
        UITextComponent display = input.TextComponent;
        XRComponent target = detachDisplay ? display : input;
        string? priorDisplay = display.Text;
        try
        {
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName != nameof(UITextInputComponent.Text)) return;
                Assert.That(field.DetachComponent(target), Is.True);
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("stale", 0, static _ => { }));
            Assert.That(input.Text, Is.Empty);
            Assert.That(display.Text, Is.EqualTo(priorDisplay));
            Assert.That(target.SceneNode, Is.SameAs(field));
            Assert.That(field.Components, Does.Not.Contain(target));
        }
        finally
        {
            if (detachDisplay) display.Destroy(true);
            else input.Destroy(true);
            root.Destroy(true);
        }
    }

    [Test]
    public void StandaloneDisplayChangingDetachmentRejectsItsOwnFieldWrite()
    {
        SceneNode root = CreateInput(out _);
        SceneNode node = root.NewChild("Standalone display");
        UITextComponent display = node.AddComponent<UITextComponent>()!;
        try
        {
            display.Text = "before";
            display.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextComponent.Text))
                    Assert.That(node.DetachComponent(display), Is.True);
            };
            Assert.Throws<InvalidOperationException>(() =>
                display.SetTextGuarded("stale", 0, static _ => { }));
            Assert.That(display.Text, Is.EqualTo("before"));
            Assert.That(display.SceneNode, Is.SameAs(node));
            Assert.That(node.Components, Does.Not.Contain(display));
        }
        finally
        {
            display.Destroy(true);
            root.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EqualContentDifferentReferenceVetoRejectsGuardedWrite(bool displayOnly)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "same";
            UITextComponent display = input.TextComponent;
            string candidate = new("same".ToCharArray());
            if (displayOnly)
            {
                string? original = display.Text;
                Assert.That(ReferenceEquals(original, candidate), Is.False);
                display.PropertyChanging += (_, change) =>
                {
                    if (change.PropertyName == nameof(UITextComponent.Text))
                        ((XRPropertyChangingEventArgs<string?>)change).AllowChange = false;
                };
                Assert.Throws<InvalidOperationException>(() =>
                    display.SetTextGuarded(candidate, 0, static _ => { }));
                Assert.That(display.Text, Is.SameAs(original));
            }
            else
            {
                string original = input.Text;
                Assert.That(ReferenceEquals(original, candidate), Is.False);
                input.PropertyChanging += (_, change) =>
                {
                    if (change.PropertyName == nameof(UITextInputComponent.Text))
                        ((XRPropertyChangingEventArgs<string>)change).AllowChange = false;
                };
                Assert.Throws<InvalidOperationException>(() =>
                    input.SetTextGuarded(candidate, 0, static _ => { }));
                Assert.That(input.Text, Is.SameAs(original));
            }
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SuppressedNoOpInputWriteDoesNotProjectAStaleSiblingDisplay()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "same";
            UITextComponent display = input.TextComponent;
            display.Text = "independent";
            ulong previous = input.TextWriteIntentRevision;
            using (XRBase.SuppressPropertyNotifications())
                Assert.That(input.SetTextGuarded(input.Text, 0, static _ => { }), Is.False);
            Assert.That(input.TextWriteIntentRevision, Is.GreaterThan(previous));
            Assert.That(input.Text, Is.EqualTo("same"));
            Assert.That(display.Text, Is.EqualTo("independent"));
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangingNestedWriteRetainsNewerIntentEvenAfterValueReturns(bool restoreOld)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            bool nested = false;
            input.PropertyChanging += (_, change) =>
            {
                if (nested || change.PropertyName != nameof(UITextInputComponent.Text)) return;
                nested = true;
                input.Text = "newer";
                if (restoreOld) input.Text = "seed";
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("stale", 0, static _ => { }));
            Assert.That(input.Text, Is.EqualTo(restoreOld ? "seed" : "newer"));
            Assert.That(input.TextComponent.Text, Is.EqualTo(input.Text));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void EqualValueNestedAttemptAlsoInvalidatesTheOldIntent()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            ulong before = input.TextWriteIntentRevision;
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.Text)) input.Text = "seed";
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("stale", 0, static _ => { }));
            Assert.That(input.Text, Is.EqualTo("seed"));
            Assert.That(input.TextWriteIntentRevision, Is.GreaterThan(before + 1));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ChangedNestedWriteCannotBeOverwrittenByOldDisplayContinuation()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            bool nested = false;
            input.PropertyChanged += (_, change) =>
            {
                if (nested || change.PropertyName != nameof(UITextInputComponent.Text)) return;
                nested = true;
                input.Text = "newer";
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("stale", 0, static _ => { }));
            Assert.That(input.Text, Is.EqualTo("newer"));
            Assert.That(input.TextComponent.Text, Is.EqualTo("newer"));
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangingVetoNeverCommitsTheRequestedInputText(bool rejectCaller)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            bool current = true;
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName != nameof(UITextInputComponent.Text)) return;
                ((XRPropertyChangingEventArgs<string>)change).AllowChange = false;
                current = !rejectCaller;
            };
            if (rejectCaller)
                Assert.Throws<InvalidOperationException>(() =>
                    input.SetTextGuarded("vetoed", 0, _ =>
                    {
                        if (!current) throw new InvalidOperationException("owner changed");
                    }));
            else
                Assert.Throws<InvalidOperationException>(() =>
                    input.SetTextGuarded("vetoed", 0, static _ => { }));
            Assert.That(input.Text, Is.Empty);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReparentInChangingRejectsOldOwnerBeforeTextFieldWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            SceneNode field = input.SceneNode!;
            SceneNode foreign = root.NewChild("Foreign");
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.Text))
                    field.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("stale", root, owner =>
                {
                    if (!ReferenceEquals(field.Parent, owner))
                        throw new InvalidOperationException("reparented");
                }));
            Assert.That(input.Text, Is.Empty);
            Assert.That(field.Parent, Is.SameAs(foreign));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DestroyInChangingRejectsTheInputWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.Text)) input.Destroy(true);
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("stale", input, target =>
                {
                    if (target.IsDestroyed) throw new InvalidOperationException("destroyed");
                }));
            Assert.That(input.IsDestroyed, Is.True);
            Assert.That(input.Text, Is.Empty);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CursorChangingCanInvalidateTextPublicationBeforeSiblingDisplayWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "long seed";
            input.CursorPosition = 8;
            input.SelectionAnchor = 6;
            string? priorDisplay = input.TextComponent.Text;
            bool current = true;
            input.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.CursorPosition)) current = false;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("short", 0, _ =>
                {
                    if (!current) throw new InvalidOperationException("caret owner changed");
                }));
            Assert.That(input.Text, Is.EqualTo("short"));
            Assert.That(input.CursorPosition, Is.EqualTo(8));
            Assert.That(input.SelectionAnchor, Is.EqualTo(6));
            Assert.That(input.TextComponent.Text, Is.EqualTo(priorDisplay));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CursorChangingNestedWritePreservesTheNewerCaretAndStopsDisplayPublication()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "long seed";
            input.CursorPosition = 8;
            string? priorDisplay = input.TextComponent.Text;
            bool nested = false;
            input.PropertyChanging += (_, change) =>
            {
                if (nested || change.PropertyName != nameof(UITextInputComponent.CursorPosition)) return;
                nested = true;
                input.CursorPosition = 1;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("short", 0, static _ => { }));
            Assert.That(nested, Is.True);
            Assert.That(input.Text, Is.EqualTo("short"));
            Assert.That(input.CursorPosition, Is.EqualTo(1));
            Assert.That(input.TextComponent.Text, Is.EqualTo(priorDisplay));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void FreshNoOpAfterCaretRejectionRepairsCaretAndDisplayWithoutAnotherTextEvent()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "long seed";
            input.CursorPosition = 8;
            input.SelectionAnchor = 6;
            UITextComponent display = input.TextComponent;
            string? priorDisplay = display.Text;
            int textNotifications = 0;
            input.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.Text)) textNotifications++;
            };
            bool current = true;
            XRPropertyChangingEventHandler rejectCaret = (_, change) =>
            {
                if (change.PropertyName == nameof(UITextInputComponent.CursorPosition)) current = false;
            };
            input.PropertyChanging += rejectCaret;
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("short", 0, _ =>
                {
                    if (!current) throw new InvalidOperationException("caret owner changed");
                }));
            Assert.That(input.Text, Is.EqualTo("short"));
            Assert.That(input.CursorPosition, Is.EqualTo(8));
            Assert.That(input.SelectionAnchor, Is.EqualTo(6));
            Assert.That(display.Text, Is.EqualTo(priorDisplay));
            Assert.That(textNotifications, Is.EqualTo(1));

            input.PropertyChanging -= rejectCaret;
            Assert.That(input.SetTextGuarded(input.Text, 0, static _ => { }), Is.False);
            Assert.That(input.CursorPosition, Is.EqualTo(5));
            Assert.That(input.SelectionAnchor, Is.EqualTo(5));
            Assert.That(display.Text, Is.EqualTo("short"));
            Assert.That(textNotifications, Is.EqualTo(1));
        }
        finally { root.Destroy(true); }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void InputChangedCallbackDisplayIntentCannotBeOverwritten(int mode)
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            input.Text = "seed";
            UITextComponent display = input.TextComponent;
            bool nested = false;
            input.PropertyChanged += (_, change) =>
            {
                if (nested || change.PropertyName != nameof(UITextInputComponent.Text)) return;
                nested = true;
                switch (mode)
                {
                    case 0:
                        display.Text = "newer";
                        break;
                    case 1:
                        display.Text = "away";
                        display.Text = "seed";
                        break;
                    default:
                        display.Text = "seed";
                        break;
                }
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("candidate", 0, static _ => { }));
            Assert.That(nested, Is.True);
            Assert.That(input.Text, Is.EqualTo("candidate"));
            Assert.That(display.Text, Is.EqualTo(mode == 0 ? "newer" : "seed"));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SiblingDisplayChangingCanInvalidateBeforeItsFieldWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            UITextComponent display = input.TextComponent;
            string? priorDisplay = display.Text;
            bool current = true;
            display.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextComponent.Text)) current = false;
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("requested", 0, _ =>
                {
                    if (!current) throw new InvalidOperationException("display owner changed");
                }));
            Assert.That(input.Text, Is.EqualTo("requested"));
            Assert.That(display.Text, Is.EqualTo(priorDisplay));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ThrowingCallbackLeavesNoActiveAdmissionForTheNextWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            bool once = true;
            input.PropertyChanging += (_, change) =>
            {
                if (!once || change.PropertyName != nameof(UITextInputComponent.Text)) return;
                once = false;
                throw new InvalidOperationException("observer failure");
            };
            Assert.Throws<InvalidOperationException>(() =>
                input.SetTextGuarded("first", 0, static _ => { }));
            Assert.That(input.SetTextGuarded("second", 0, static _ => { }), Is.True);
            Assert.That(input.Text, Is.EqualTo("second"));
            Assert.That(input.TextComponent.Text, Is.EqualTo("second"));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SuppressedNotificationsStillCheckTheGuardAndPermitTheWrite()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            using (XRBase.SuppressPropertyNotifications())
            {
                Assert.Throws<InvalidOperationException>(() =>
                    input.SetTextGuarded("stale", 0,
                        static _ => throw new InvalidOperationException("stale")));
                Assert.That(input.Text, Is.Empty);
                Assert.That(input.SetTextGuarded("accepted", 0, static _ => { }), Is.True);
            }
            Assert.That(input.Text, Is.EqualTo("accepted"));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisplayGuardChecksCallbackIntentAndCanRecoverAfterFailure()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            UITextComponent display = input.TextComponent;
            display.Text = "seed";
            bool nested = false;
            display.PropertyChanging += (_, change) =>
            {
                if (nested || change.PropertyName != nameof(UITextComponent.Text)) return;
                nested = true;
                display.Text = "newer";
            };
            Assert.Throws<InvalidOperationException>(() =>
                display.SetTextGuarded("stale", 0, static _ => { }));
            Assert.That(display.Text, Is.EqualTo("newer"));
            Assert.That(display.SetTextGuarded("accepted", 0, static _ => { }), Is.True);
            Assert.That(display.Text, Is.EqualTo("accepted"));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisplayGuardRejectsChangedObserverOwnershipLoss()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            UITextComponent display = input.TextComponent;
            bool current = true;
            display.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UITextComponent.Text)) current = false;
            };
            Assert.Throws<InvalidOperationException>(() =>
                display.SetTextGuarded("written", 0, _ =>
                {
                    if (!current) throw new InvalidOperationException("owner changed");
                }));
            Assert.That(display.Text, Is.EqualTo("written"));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void EqualValueDisplayAttemptAdvancesIntentRevision()
    {
        SceneNode root = CreateInput(out UITextInputComponent input);
        try
        {
            UITextComponent display = input.TextComponent;
            display.Text = "same";
            ulong before = display.TextWriteIntentRevision;
            display.Text = "same";
            Assert.That(display.TextWriteIntentRevision, Is.GreaterThan(before));
            Assert.That(display.SetTextGuarded("same", 0, static _ => { }), Is.False);
            Assert.That(display.TextWriteIntentRevision, Is.GreaterThan(before + 1));
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateInput(out UITextInputComponent input)
    {
        SceneNode root = new("Guarded text", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode field = root.NewChild("Input");
        field.SetTransform<UIBoundableTransform>();
        field.AddComponent<UITextComponent>();
        input = field.AddComponent<UITextInputComponent>()!;
        return root;
    }
}
