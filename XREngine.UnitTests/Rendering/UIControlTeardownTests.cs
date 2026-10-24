using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIControlTeardownTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp]
    public void SetUp() => _shaderScope = new UITestShaderScope();

    [TearDown]
    public void TearDown() => _shaderScope.Dispose();

    [Test]
    public void DestroyingOpenMenuDetachesCanvasInputWithoutDestroyingItsNode()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode menuNode = menu.SceneNode;
            menu.Show(new Vector2(8, 80), new ContextMenuItem("Action", () => { }));
            input.SubscriberCount.ShouldBe(4);

            menu.Destroy(true);

            menu.IsDestroyed.ShouldBeTrue();
            menuNode.IsDestroyed.ShouldBeFalse();
            input.SubscriberCount.ShouldBe(0);
            Should.NotThrow(input.RaiseEscape);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void DismissCallbackCanDestroyMenuWhileAnotherShowIsPending()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(new Vector2(8, 80), new ContextMenuItem("First", () => { }));
            menu.Dismissed += () => menu.Destroy(true);

            Should.NotThrow(() => menu.Show(new Vector2(12, 70),
                new ContextMenuItem("Second", () => { })));

            menu.IsDestroyed.ShouldBeTrue();
            input.SubscriberCount.ShouldBe(0);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void DismissCallbackOpeningAnotherMenuKeepsTheLatestRequest()
    {
        SceneNode root = CreateCanvas(out UITestCanvasInputProbe input);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            menu.Show(new Vector2(8, 80), new ContextMenuItem("First", () => { }));
            int nestedShows = 0;
            void OnDismissed()
            {
                nestedShows++;
                menu.Show(new Vector2(18, 70), new ContextMenuItem("Nested", () => { }));
            }
            menu.Dismissed += OnDismissed;
            try
            {
                menu.Show(new Vector2(12, 60), new ContextMenuItem("Stale", () => { }));
            }
            finally
            {
                menu.Dismissed -= OnDismissed;
            }

            nestedShows.ShouldBe(1);
            menu.IsOpen.ShouldBeTrue();
            input.SubscriberCount.ShouldBe(4);
            menu.SceneNode.Transform.Children.Count.ShouldBe(1);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void DestroyingTooltipReleasesTargetHelpAndPopupCallback()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode targetNode = new(root) { Name = "Save" };
            targetNode.SetTransform<UIBoundableTransform>();
            UIButtonComponent target = targetNode.AddComponent<UIButtonComponent>()!;
            UITooltipComponent tooltip = targetNode.AddComponent<UITooltipComponent>()!;
            UIContextMenuComponent popup = CreateMenu(root);
            tooltip.Popup = popup;
            tooltip.Text = "Save the document";
            target.AccessibilityDescription.ShouldBe("Save the document");

            tooltip.Destroy(true);

            target.AccessibilityDescription.ShouldBeNull();
            target.IsFocused = true;
            popup.IsOpen.ShouldBeFalse();
            tooltip.IsDestroyed.ShouldBeTrue();
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void DestroyingComboDetachesBindingAndOpeningCannotUseDestroyedPopup()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode comboNode = new(root) { Name = "Combo" };
            comboNode.SetTransform<UIBoundableTransform>();
            UIComboBoxComponent combo = comboNode.AddComponent<UIComboBoxComponent>()!;
            UIContextMenuComponent popup = CreateMenu(root);
            UIValueState<ulong?> binding = new(1);
            combo.SetOptions([new(1, "One"), new(2, "Two")]);
            combo.Binding = binding;
            combo.Popup = popup;
            combo.Opening += source => source.Destroy(true);

            combo.Open().ShouldBeFalse();
            combo.IsDestroyed.ShouldBeTrue();
            popup.IsOpen.ShouldBeFalse();
            binding.SetValue(2);
            combo.SelectedId.ShouldBe(1UL);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void PopupReplacementStopsWhenDismissalDestroysCombo()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode comboNode = new(root) { Name = "Combo" };
            comboNode.SetTransform<UIBoundableTransform>();
            UIComboBoxComponent combo = comboNode.AddComponent<UIComboBoxComponent>()!;
            UIContextMenuComponent previous = CreateMenu(root);
            UIContextMenuComponent replacement = CreateMenu(root);
            combo.SetOptions([new(1, "One")]);
            combo.Popup = previous;
            combo.Open().ShouldBeTrue();
            previous.Dismissed += () => combo.Destroy(true);

            combo.Popup = replacement;

            combo.IsDestroyed.ShouldBeTrue();
            combo.Popup.ShouldBeNull();
            SubscriberCount(replacement, nameof(UIContextMenuComponent.Dismissed))
                .ShouldBe(0);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void BindingReplacementDoesNotReinstallReferenceAfterPrechangeDestroy()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode comboNode = new(root) { Name = "Combo" };
            comboNode.SetTransform<UIBoundableTransform>();
            UIComboBoxComponent combo = comboNode.AddComponent<UIComboBoxComponent>()!;
            UIValueState<ulong?> replacement = new(1);
            combo.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UIComboBoxComponent.Binding))
                    combo.Destroy(true);
            };

            combo.Binding = replacement;

            combo.IsDestroyed.ShouldBeTrue();
            combo.Binding.ShouldBeNull();
            SubscriberCount(replacement, nameof(UIValueState<ulong?>.ValueChanged))
                .ShouldBe(0);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void TooltipReplacementCannotSubscribeAfterOwnerDestroy()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode ownerNode = new(root) { Name = "Owner" };
            ownerNode.SetTransform<UIBoundableTransform>();
            UITooltipComponent tooltip = ownerNode.AddComponent<UITooltipComponent>()!;
            UIContextMenuComponent replacementPopup = CreateMenu(root);
            tooltip.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UITooltipComponent.Popup))
                    tooltip.Destroy(true);
            };

            tooltip.Popup = replacementPopup;

            tooltip.IsDestroyed.ShouldBeTrue();
            tooltip.Popup.ShouldBeNull();
            SubscriberCount(replacementPopup, nameof(UIContextMenuComponent.Dismissed))
                .ShouldBe(0);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void TooltipTargetReplacementCannotAttachAfterPrechangeDestroy()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode ownerNode = new(root) { Name = "Owner" };
            ownerNode.SetTransform<UIBoundableTransform>();
            UITooltipComponent tooltip = ownerNode.AddComponent<UITooltipComponent>()!;
            SceneNode targetNode = new(root) { Name = "New target" };
            targetNode.SetTransform<UIBoundableTransform>();
            UIButtonComponent replacement = targetNode.AddComponent<UIButtonComponent>()!;
            tooltip.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UITooltipComponent.Target))
                    tooltip.Destroy(true);
            };

            tooltip.Target = replacement;

            tooltip.IsDestroyed.ShouldBeTrue();
            tooltip.Target.ShouldBeNull();
            SubscriberCount(replacement, nameof(UIInteractableComponent.MouseOverlapEnter),
                typeof(UIInteractableComponent)).ShouldBe(0);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    private static int SubscriberCount(object owner, string eventName, Type? declaredOn = null)
    {
        FieldInfo? field = (declaredOn ?? owner.GetType()).GetField(eventName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
            throw new InvalidOperationException($"Event storage was not found: {eventName}.");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static SceneNode CreateCanvas(out UITestCanvasInputProbe input)
    {
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(240, 120));
        SceneNode root = new("Control canvas", canvas) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        input = root.AddComponent<UITestCanvasInputProbe>()!;
        root.IsActiveSelf = true;
        return root;
    }

    private static UIContextMenuComponent CreateMenu(SceneNode root)
    {
        SceneNode node = new(root) { Name = "Popup" };
        node.SetTransform<UIBoundableTransform>();
        return node.AddComponent<UIContextMenuComponent>()!;
    }
}
