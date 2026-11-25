using System.Reflection;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UISliderColorLifetimeTests
{
    [Test]
    public void DirectSliderDestroyDetachesBindingAndCancelsPreviewWithoutCommit()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateRoot();
        try
        {
            UIRangeSliderComponent slider = NewControl(root, "Slider")
                .AddComponent<UIRangeSliderComponent>()!;
            TrackedBinding<double> binding = new(0.2);
            slider.Binding = binding;
            root.IsActiveSelf = true;
            int commits = 0;
            slider.ValueCommitted += _ => commits++;
            slider.BeginDrag(0.8f).ShouldBeTrue();
            slider.Destroy(true);

            binding.SubscriberCount.ShouldBe(0);
            binding.Value.ShouldBe(0.2);
            slider.Binding.ShouldBeNull();
            slider.IsDragging.ShouldBeFalse();
            slider.Value.ShouldBe(0.2);
            commits.ShouldBe(0);
            binding.SetValue(0.9);
            slider.Value.ShouldBe(0.2);
            Should.Throw<ObjectDisposedException>(() => slider.Binding = binding);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SliderBindingCallbacksCannotResubscribeAfterReentrantDestroy()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateRoot();
        try
        {
            UIRangeSliderComponent slider = NewControl(root, "Slider")
                .AddComponent<UIRangeSliderComponent>()!;
            TrackedBinding<double> source = new(0.1);
            TrackedBinding<double> replacement = new(0.6);
            slider.Binding = source;
            root.IsActiveSelf = true;
            source.OnRemove = () => slider.Destroy(true);
            slider.Binding = replacement;

            source.SubscriberCount.ShouldBe(0);
            replacement.SubscriberCount.ShouldBe(0);
            slider.Binding.ShouldBeNull();
            source.SetValue(0.9);
            slider.Value.ShouldBe(0.1);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BindingAddDeactivationRejectsLateSliderAndColorEvents()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateRoot();
        try
        {
            UIRangeSliderComponent slider = NewControl(root, "Slider")
                .AddComponent<UIRangeSliderComponent>()!;
            UIColorSelectorComponent selector = NewControl(root, "Color")
                .AddComponent<UIColorSelectorComponent>()!;
            root.IsActiveSelf = true;
            TrackedBinding<double> sliderSource = new(0.4);
            Action<double>? staleSlider = null;
            sliderSource.OnAdd = () =>
            {
                staleSlider = sliderSource.Handlers;
                root.IsActiveSelf = false;
            };
            slider.Binding = sliderSource;
            sliderSource.SubscriberCount.ShouldBe(0);
            staleSlider.ShouldNotBeNull();
            double before = slider.Value;
            staleSlider?.Invoke(0.9);
            slider.Value.ShouldBe(before);

            sliderSource.OnAdd = null;
            root.IsActiveSelf = true;
            TrackedBinding<ColorF4> colorSource = new(new ColorF4(1, 0, 0, 1));
            Action<ColorF4>? staleColor = null;
            colorSource.OnAdd = () =>
            {
                staleColor = colorSource.Handlers;
                root.IsActiveSelf = false;
            };
            selector.Binding = colorSource;
            colorSource.SubscriberCount.ShouldBe(0);
            staleColor.ShouldNotBeNull();
            ColorF4 retained = selector.LinearColor;
            staleColor?.Invoke(new ColorF4(0, 1, 0, 1));
            selector.LinearColor.ShouldBe(retained);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SliderRollbackReactivationKeepsTheCurrentBindingSubscription()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateRoot();
        try
        {
            UIRangeSliderComponent slider = NewControl(root, "Slider")
                .AddComponent<UIRangeSliderComponent>()!;
            TrackedBinding<double> binding = new(0.2);
            slider.Binding = binding;
            root.IsActiveSelf = true;
            slider.BeginDrag(0.8f).ShouldBeTrue();
            int commits = 0;
            slider.ValueCommitted += _ => commits++;
            bool reactivated = false;
            slider.ValueChanged += change =>
            {
                if (reactivated || change.NewValue != 0.2)
                    return;
                reactivated = true;
                slider.IsActive = true;
            };

            slider.IsActive = false;

            reactivated.ShouldBeTrue();
            binding.SubscriberCount.ShouldBe(1);
            slider.Value.ShouldBe(0.2);
            commits.ShouldBe(0);
            binding.SetValue(0.6);
            slider.Value.ShouldBe(0.6);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ColorDismissReactivationKeepsExactPopupAndBindingOwners()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateRoot();
        try
        {
            UIColorSelectorComponent selector = NewControl(root, "Color")
                .AddComponent<UIColorSelectorComponent>()!;
            UIContextMenuComponent popup = NewControl(root, "Popup")
                .AddComponent<UIContextMenuComponent>()!;
            TrackedBinding<ColorF4> binding = new(new ColorF4(1, 0, 0, 1));
            selector.Binding = binding;
            selector.Popup = popup;
            root.IsActiveSelf = true;
            selector.BeginEdit().ShouldBeTrue();
            selector.PreviewHexSrgb("#00FF00FF").ShouldBeTrue();
            popup.Show(Vector2.Zero, new ContextMenuItem("Test", null));
            int commits = 0;
            selector.ColorCommitted += _ => commits++;
            bool reactivated = false;
            popup.Dismissed += () =>
            {
                if (reactivated)
                    return;
                reactivated = true;
                selector.IsActive = true;
            };

            selector.IsActive = false;

            reactivated.ShouldBeTrue();
            binding.SubscriberCount.ShouldBe(1);
            PopupListeners(popup).ShouldBe(2);
            selector.IsEditing.ShouldBeFalse();
            selector.LinearColor.ShouldBe(binding.Value);
            commits.ShouldBe(0);
            binding.SetValue(new ColorF4(0, 0, 1, 1));
            selector.LinearColor.ShouldBe(binding.Value);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ColorDestroyRollsBackPreviewAndReleasesPopupAndBinding()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateRoot();
        try
        {
            UIColorSelectorComponent selector = NewControl(root, "Color")
                .AddComponent<UIColorSelectorComponent>()!;
            UIContextMenuComponent popup = NewControl(root, "Popup")
                .AddComponent<UIContextMenuComponent>()!;
            TrackedBinding<ColorF4> binding = new(new ColorF4(1, 0, 0, 1));
            selector.Binding = binding;
            selector.Popup = popup;
            root.IsActiveSelf = true;
            int commits = 0;
            selector.ColorCommitted += _ => commits++;
            selector.BeginEdit().ShouldBeTrue();
            selector.PreviewHexSrgb("#00FF00FF").ShouldBeTrue();
            selector.Destroy(true);

            binding.SubscriberCount.ShouldBe(0);
            PopupListeners(popup).ShouldBe(0);
            selector.Binding.ShouldBeNull();
            selector.Popup.ShouldBeNull();
            selector.IsEditing.ShouldBeFalse();
            selector.LinearColor.ShouldBe(binding.Value);
            commits.ShouldBe(0);
            binding.SetValue(new ColorF4(0, 0, 1, 1));
            selector.LinearColor.ShouldBe(new ColorF4(1, 0, 0, 1));
            Should.Throw<ObjectDisposedException>(() => selector.Binding = binding);
            Should.Throw<ObjectDisposedException>(() => selector.Popup = popup);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ColorBindingReentryKeepsOnlyTheReplacementSource()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateRoot();
        try
        {
            UIColorSelectorComponent selector = NewControl(root, "Color")
                .AddComponent<UIColorSelectorComponent>()!;
            TrackedBinding<ColorF4> first = new(new ColorF4(1, 0, 0, 1));
            TrackedBinding<ColorF4> attempted = new(new ColorF4(0, 1, 0, 1));
            TrackedBinding<ColorF4> replacement = new(new ColorF4(0, 0, 1, 1));
            selector.Binding = first;
            root.IsActiveSelf = true;
            bool replaced = false;
            selector.PropertyChanging += (_, args) =>
            {
                if (replaced || args.PropertyName != nameof(UIColorSelectorComponent.Binding))
                    return;
                replaced = true;
                selector.Binding = replacement;
            };
            selector.Binding = attempted;

            selector.Binding.ShouldBeSameAs(replacement);
            selector.LinearColor.ShouldBe(replacement.Value);
            first.SubscriberCount.ShouldBe(0);
            attempted.SubscriberCount.ShouldBe(0);
            replacement.SubscriberCount.ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateRoot()
    {
        SceneNode root = new("Lifetime canvas", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        return root;
    }

    private static SceneNode NewControl(SceneNode root, string name)
    {
        SceneNode node = root.NewChild(name);
        node.SetTransform<UIBoundableTransform>();
        return node;
    }

    private static int PopupListeners(UIContextMenuComponent popup)
        => ((Delegate?)typeof(UIContextMenuComponent).GetField("Dismissed",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup))
            ?.GetInvocationList().Length ?? 0;

    private sealed class TrackedBinding<T>(T value) : IUIValueBinding<T>
    {
        private T _value = value;
        private Action<T>? _changed;
        public Action? OnAdd { get; set; }
        public Action? OnRemove { get; set; }
        public T Value => _value;
        public Action<T>? Handlers => _changed;
        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
        public event Action<T>? ValueChanged
        {
            add { _changed += value; OnAdd?.Invoke(); }
            remove { _changed -= value; OnRemove?.Invoke(); }
        }
        public void SetValue(T value)
        {
            _value = value;
            _changed?.Invoke(value);
        }
    }
}
