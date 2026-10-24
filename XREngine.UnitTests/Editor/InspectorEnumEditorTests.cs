using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks enum dispatch and exact property ownership when a row is recycled.</summary>
[TestFixture, NonParallelizable]
public sealed class InspectorEnumEditorTests
{
    [Test]
    public void OrdinaryEnumUsesComboAndRebindDoesNotWritePreviousProperty()
    {
        using UITestShaderScope scope = new();
        Undo.ClearHistory();
        InspectorEnumTestTarget target = new() { First = EVisibility.Hidden };
        SceneNode canvas = new("Enum canvas", new UICanvasTransform()) { IsActiveSelf = false };
        SceneNode node = new(canvas, "Enum row");
        node.SetTransform<UIBoundableTransform>();
        try
        {
            PropertyInfo first = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.First))!;
            PropertyInfo second = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.Second))!;
            InspectorPropertyEditors.CreateNew(first.PropertyType)!(node, first, [target]);
            UIComboBoxComponent combo = node.GetComponent<UIComboBoxComponent>()!;
            Assert.That(combo, Is.Not.Null);
            Assert.That(node.GetComponent<UITextInputComponent>(), Is.Null);
            Assert.That(combo.AccessibilityValue, Is.EqualTo(nameof(EVisibility.Hidden)));

            combo.CancelForRecycle();
            Assert.That(combo.Popup, Is.Null);
            NativeUIElements.RebindPropertyEditor(node, second, [target]);
            Assert.That(combo.Popup, Is.Not.Null);
            Assert.That(combo.AccessibilityValue, Is.EqualTo(nameof(EVisibility.Collapsed)));
            IUIComboOptionSource choices = combo.VirtualOptions!;
            ulong visibleId = 0;
            for (int index = 0; index < choices.Count; index++)
                if (choices.GetOption(index).Label == nameof(EVisibility.Visible))
                    visibleId = choices.GetOption(index).Id;
            Assert.That(combo.Select(visibleId), Is.True);
            Assert.That(target.Second, Is.EqualTo(EVisibility.Visible));
            Assert.That(target.First, Is.EqualTo(EVisibility.Hidden));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(target.Second, Is.EqualTo(EVisibility.Collapsed));

            NativeUIElements.RebindPropertyEditor(node, null, null);
            Assert.Multiple(() =>
            {
                Assert.That(combo.Binding, Is.Null);
                Assert.That(combo.VirtualOptions, Is.Null);
                Assert.That(combo.IsReadOnly, Is.True);
            });
        }
        finally { canvas.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void GetterAndControlCallbacksKeepTheLatestQueuedProperty()
    {
        using UITestShaderScope scope = new();
        SceneNode canvas = new("Enum callback canvas", new UICanvasTransform()) { IsActiveSelf = false };
        SceneNode node = new(canvas, "Enum row");
        node.SetTransform<UIBoundableTransform>();
        InspectorEnumTestTarget target = new() { First = EVisibility.Hidden };
        try
        {
            PropertyInfo first = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.First))!;
            PropertyInfo second = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.Second))!;
            InspectorPropertyEditors.CreateNew(first.PropertyType)!(node, first, [target]);
            InspectorEnumEditorComponent editor = node.GetComponent<InspectorEnumEditorComponent>()!;
            UIComboBoxComponent combo = node.GetComponent<UIComboBoxComponent>()!;
            editor.Bind(null, null);
            target.OnRead = () => editor.Bind(second, [target]);
            editor.Bind(first, [target]);
            Assert.That(combo.AccessibilityValue, Is.EqualTo(nameof(EVisibility.Collapsed)));
            Assert.That(node.FirstChild!.GetComponent<UITextComponent>()!.Text,
                Is.EqualTo(nameof(EVisibility.Collapsed)));
            target.OnRead = null;

            bool reentered = false;
            combo.PropertyChanging += (_, change) =>
            {
                if (reentered || change.PropertyName != nameof(combo.IsReadOnly)) return;
                reentered = true;
                editor.Bind(second, [target]);
            };
            editor.Bind(null, null);
            Assert.That(reentered, Is.True);
            Assert.That(combo.IsReadOnly, Is.False);
            Assert.That(combo.AccessibilityValue, Is.EqualTo(nameof(EVisibility.Collapsed)));

            EditorEnumPropertyBinding replacement = new(first, [target]);
            combo.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(combo.Binding) || combo.Binding is not null) return;
                combo.VirtualOptions = replacement;
                combo.Binding = replacement;
            };
            editor.Destroy(true);
            Assert.That(combo.Binding, Is.SameAs(replacement));
            Assert.That(combo.VirtualOptions, Is.SameAs(replacement));
        }
        finally { canvas.Destroy(true); }
    }

    [TestCase(nameof(UIComboBoxComponent.Binding))]
    [TestCase(nameof(UIComboBoxComponent.VirtualOptions))]
    public void HelperRetirementDuringControlPrechangeDetachesPendingOwner(string propertyName)
    {
        using UITestShaderScope scope = new();
        SceneNode canvas = new("Enum retirement canvas", new UICanvasTransform()) { IsActiveSelf = false };
        SceneNode node = new(canvas, "Enum row");
        node.SetTransform<UIBoundableTransform>();
        try
        {
            InspectorEnumTestTarget target = new();
            PropertyInfo first = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.First))!;
            InspectorPropertyEditors.CreateNew(first.PropertyType)!(node, first, [target]);
            InspectorEnumEditorComponent editor = node.GetComponent<InspectorEnumEditorComponent>()!;
            UIComboBoxComponent combo = node.GetComponent<UIComboBoxComponent>()!;
            editor.Bind(null, null);
            combo.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == propertyName)
                    editor.Destroy(true);
            };
            editor.Bind(first, [target]);
            Assert.That(editor.IsDestroyed, Is.True);
            Assert.That(combo.Binding, Is.Null);
            Assert.That(combo.VirtualOptions, Is.Null);
        }
        finally { canvas.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VetoedOrThrowingClearCannotKeepRetiredPropertyWritable(bool throws)
    {
        using UITestShaderScope scope = new();
        SceneNode canvas = new("Enum veto canvas", new UICanvasTransform()) { IsActiveSelf = false };
        SceneNode node = new(canvas, "Enum row");
        node.SetTransform<UIBoundableTransform>();
        InspectorEnumTestTarget target = new() { First = EVisibility.Hidden };
        UIComboBoxComponent? combo = null;
        XRPropertyChangingEventHandler? handler = null;
        try
        {
            PropertyInfo first = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.First))!;
            InspectorPropertyEditors.CreateNew(first.PropertyType)!(node, first, [target]);
            InspectorEnumEditorComponent editor = node.GetComponent<InspectorEnumEditorComponent>()!;
            combo = node.GetComponent<UIComboBoxComponent>()!;
            IUIValueBinding<ulong?> previous = combo.Binding!;
            handler = (_, change) =>
            {
                if (change.PropertyName != nameof(combo.Binding)) return;
                if (throws) throw new InvalidOperationException("Injected clear failure.");
                ((XRPropertyChangingEventArgs<IUIValueBinding<ulong?>?>)change).AllowChange = false;
            };
            combo.PropertyChanging += handler;
            Assert.Throws<AggregateException>(() => editor.Bind(null, null));
            Assert.Throws<ObjectDisposedException>(() => previous.SetValue(1));
            Assert.That(target.First, Is.EqualTo(EVisibility.Hidden));
            combo.PropertyChanging -= handler;
            handler = null;
            editor.Bind(first, [target]);
            Assert.That(combo.Binding, Is.Not.SameAs(previous));
            Assert.That(combo.AccessibilityValue, Is.EqualTo(nameof(EVisibility.Hidden)));
        }
        finally
        {
            if (combo is not null && handler is not null)
                combo.PropertyChanging -= handler;
            canvas.Destroy(true);
        }
    }

    [Test]
    public void GetterRetirementDuringWriteStopsBeforeTheOldSetter()
    {
        using UITestShaderScope scope = new();
        Undo.ClearHistory();
        SceneNode canvas = new("Enum write canvas", new UICanvasTransform()) { IsActiveSelf = false };
        SceneNode node = new(canvas, "Enum row");
        node.SetTransform<UIBoundableTransform>();
        InspectorEnumTestTarget target = new() { First = EVisibility.Hidden };
        try
        {
            PropertyInfo first = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.First))!;
            PropertyInfo second = typeof(InspectorEnumTestTarget).GetProperty(nameof(target.Second))!;
            InspectorPropertyEditors.CreateNew(first.PropertyType)!(node, first, [target]);
            InspectorEnumEditorComponent editor = node.GetComponent<InspectorEnumEditorComponent>()!;
            UIComboBoxComponent combo = node.GetComponent<UIComboBoxComponent>()!;
            IUIValueBinding<ulong?> previous = combo.Binding!;
            target.OnRead = () =>
            {
                target.OnRead = null;
                editor.Bind(second, [target]);
            };
            Assert.Throws<ObjectDisposedException>(() => previous.SetValue(1));
            Assert.That(target.First, Is.EqualTo(EVisibility.Hidden));
            Assert.That(combo.AccessibilityValue, Is.EqualTo(nameof(EVisibility.Collapsed)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { canvas.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void FlagsEnumUsesTextForCombinedNamedValues()
    {
        using UITestShaderScope scope = new();
        SceneNode canvas = new("Flags canvas", new UICanvasTransform()) { IsActiveSelf = false };
        SceneNode node = new(canvas, "Flags row");
        node.SetTransform<UIBoundableTransform>();
        try
        {
            PropertyInfo property = typeof(InspectorEnumTestTarget).GetProperty(nameof(InspectorEnumTestTarget.Flags))!;
            InspectorPropertyEditors.CreateNew(property.PropertyType)!(node, property, [new InspectorEnumTestTarget()]);
            Assert.That(node.Transform.Children.Any(child =>
                child.SceneNode?.GetComponent<UITextInputComponent>() is not null), Is.True);
            Assert.That(node.GetComponent<UIComboBoxComponent>(), Is.Null);
        }
        finally { canvas.Destroy(true); }
    }
}
