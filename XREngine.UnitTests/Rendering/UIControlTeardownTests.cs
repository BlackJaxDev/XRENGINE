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
    public void DisabledMenuItemCannotFocusOrActivateAndActionReopenKeepsNewMenu()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            int disabledActions = 0;
            menu.Show(new Vector2(8, 80), new ContextMenuItem("Disabled", () => disabledActions++, false));
            UIButtonComponent disabled = FirstMenuButton(menu);
            disabled.IsEnabled.ShouldBeFalse();
            disabled.IsTabStop.ShouldBeFalse();
            disabled.AccessibilityActivate().ShouldBeFalse();
            disabled.OnInteract();
            disabledActions.ShouldBe(0);
            menu.IsOpen.ShouldBeTrue();

            int actions = 0;
            menu.Show(new Vector2(8, 80), new ContextMenuItem("Reopen", () =>
            {
                actions++;
                menu.Show(new Vector2(18, 70), new ContextMenuItem("Latest", null));
            }));
            menu.UITransform.Visibility = EVisibility.Visible;
            UIButtonComponent reopen = FirstMenuButton(menu);
            reopen.CanInteract.ShouldBeTrue();
            reopen.OnInteract();

            actions.ShouldBe(1);
            menu.IsOpen.ShouldBeTrue();
            FirstMenuButton(menu).ShouldNotBeSameAs(reopen);
            reopen.OnInteract();
            actions.ShouldBe(1);

            menu.Show(new Vector2(8, 80), new ContextMenuItem("Destroy", () => menu.Destroy(true)));
            menu.UITransform.Visibility = EVisibility.Visible;
            FirstMenuButton(menu).OnInteract();
            menu.IsDestroyed.ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ComboPropertyCallbacksCannotEmitSupersededSelection()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode node = new(root) { Name = "Combo" };
            node.SetTransform<UIBoundableTransform>();
            UIComboBoxComponent combo = node.AddComponent<UIComboBoxComponent>()!;
            combo.SetOptions([new(1, "One"), new(2, "Two")]);
            List<UIValueChange<ulong?>> changes = [];
            combo.SelectionChanged += changes.Add;
            bool reentered = false;
            combo.PropertyChanged += (_, change) =>
            {
                if (reentered || change.PropertyName != nameof(UIComboBoxComponent.SelectedId))
                    return;
                reentered = true;
                combo.Select(2).ShouldBeTrue();
            };

            combo.Select(1).ShouldBeFalse();

            combo.SelectedId.ShouldBe(2UL);
            changes.Count.ShouldBe(1);
            changes[0].NewValue.ShouldBe(2UL);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ComboPrechangeReplacementAndDestroyRejectOuterSelection()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            SceneNode replacementNode = new(root) { Name = "Replacement combo" };
            replacementNode.SetTransform<UIBoundableTransform>();
            UIComboBoxComponent replacement = replacementNode.AddComponent<UIComboBoxComponent>()!;
            replacement.SetOptions([new(1, "One"), new(2, "Two")]);
            int replacementEvents = 0;
            replacement.SelectionChanged += _ => replacementEvents++;
            bool reentered = false;
            replacement.PropertyChanging += (_, change) =>
            {
                if (reentered || change.PropertyName != nameof(UIComboBoxComponent.SelectedId))
                    return;
                reentered = true;
                replacement.Select(2).ShouldBeTrue();
            };
            replacement.Select(1).ShouldBeFalse();
            replacement.SelectedId.ShouldBe(2UL);
            replacementEvents.ShouldBe(1);

            SceneNode destroyedNode = new(root) { Name = "Destroyed combo" };
            destroyedNode.SetTransform<UIBoundableTransform>();
            UIComboBoxComponent destroyed = destroyedNode.AddComponent<UIComboBoxComponent>()!;
            destroyed.SetOptions([new(1, "One")]);
            int staleEvents = 0;
            destroyed.SelectionChanged += _ => staleEvents++;
            destroyed.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UIComboBoxComponent.SelectedId))
                    destroyed.Destroy(true);
            };
            destroyed.Select(1).ShouldBeFalse();
            destroyed.IsDestroyed.ShouldBeTrue();
            staleEvents.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ComboSameIdReentryCancelsPendingSelection()
    {
        UIComboBoxComponent combo = new();
        combo.SetOptions([new(1, "One")]);
        int events = 0;
        combo.SelectionChanged += _ => events++;
        bool reentered = false;
        combo.PropertyChanging += (_, change) =>
        {
            if (reentered || change.PropertyName != nameof(UIComboBoxComponent.SelectedId))
                return;
            reentered = true;
            combo.Select(null).ShouldBeTrue();
        };

        combo.Select(1).ShouldBeFalse();

        combo.SelectedId.ShouldBeNull();
        events.ShouldBe(0);
    }

    [Test]
    public void ComboProviderReplacementDuringLookupCancelsOldSelection()
    {
        UIComboBoxComponent combo = new();
        ReentrantComboOptions original = new();
        ReentrantComboOptions replacement = new();
        combo.VirtualOptions = original;
        int events = 0;
        combo.SelectionChanged += _ => events++;
        original.OnLookup = () => combo.VirtualOptions = replacement;

        combo.Select(1).ShouldBeFalse();

        combo.VirtualOptions.ShouldBeSameAs(replacement);
        combo.SelectedId.ShouldBeNull();
        events.ShouldBe(0);
    }

    [Test]
    public void ComboOptionReplacementDuringPrechangeCannotCommitRemovedId()
    {
        UIComboBoxComponent combo = new();
        combo.SetOptions([new(1, "One")]);
        int events = 0;
        combo.SelectionChanged += _ => events++;
        combo.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UIComboBoxComponent.SelectedId))
                combo.SetOptions([new(2, "Two")]);
        };

        combo.Select(1).ShouldBeFalse();

        combo.SelectedId.ShouldBeNull();
        events.ShouldBe(0);
    }

    [Test]
    public void ComboBindingReplacementDuringWriteCancelsOldSelection()
    {
        UIComboBoxComponent combo = new();
        combo.SetOptions([new(1, "One"), new(2, "Two")]);
        UIValueState<ulong?> original = new(null);
        UIValueState<ulong?> replacement = new(2);
        combo.Binding = original;
        int events = 0;
        combo.SelectionChanged += _ => events++;
        original.ValueChanged += _ => combo.Binding = replacement;

        combo.Select(1).ShouldBeFalse();

        combo.Binding.ShouldBeSameAs(replacement);
        combo.SelectedId.ShouldBe(2UL);
        events.ShouldBe(1);
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

    private static UIButtonComponent FirstMenuButton(UIContextMenuComponent menu)
        => menu.SceneNode.FirstChild!.FirstChild!.FirstChild!
            .GetComponent<UIButtonComponent>()!;

    private sealed class ReentrantComboOptions : IUIComboOptionSource
    {
        public int Count => 1;
        public ulong Revision => 1;
        public Action? OnLookup { get; set; }
        public UIComboOption GetOption(int index) => index == 0 ? new(1, "One") :
            throw new ArgumentOutOfRangeException(nameof(index));
        public int IndexOfId(ulong id)
        {
            Action? callback = OnLookup;
            OnLookup = null;
            callback?.Invoke();
            return id == 1 ? 0 : -1;
        }
    }
}
