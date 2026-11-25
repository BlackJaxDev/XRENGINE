using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIRadioLifetimeTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void DirectDestroyRemovesButtonButKeepsLogicalSelectionForRecycledOption()
    {
        SceneNode root = new("Radio lifetime", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        using UIRadioGroupState group = new();
        UIRadioButtonComponent first = root.NewChild("First").AddComponent<UIRadioButtonComponent>()!;
        first.OptionId = 9;
        first.Group = group;
        root.IsActiveSelf = true;
        try
        {
            group.Select(9).ShouldBeTrue();
            first.Destroy(true);
            first.Group.ShouldBeNull();
            group.SelectedId.ShouldBe(9UL);
            group.Select(9).ShouldBeFalse();

            UIRadioButtonComponent replacement = root.NewChild("Replacement").AddComponent<UIRadioButtonComponent>()!;
            replacement.OptionId = 9;
            replacement.Group = group;
            replacement.IsSelected.ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisposeReleasesEveryButtonWhenBindingUnsubscribeThrows()
    {
        SceneNode root = new("Radio disposal", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIRadioLifetimeBinding binding = new(3);
        UIRadioGroupState group = new() { Binding = binding };
        UIRadioButtonComponent first = root.NewChild("First").AddComponent<UIRadioButtonComponent>()!;
        first.OptionId = 3;
        first.Group = group;
        UIRadioButtonComponent second = root.NewChild("Second").AddComponent<UIRadioButtonComponent>()!;
        second.OptionId = 4;
        second.Group = group;
        root.IsActiveSelf = true;
        try
        {
            binding.ThrowOnRemove = true;
            Should.Throw<InvalidOperationException>(group.Dispose);
            first.Group.ShouldBeNull();
            second.Group.ShouldBeNull();
            first.IsSelected.ShouldBeFalse();
            second.IsSelected.ShouldBeFalse();
            group.Binding.ShouldBeNull();
            group.Dispose();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReplacedBindingRejectsQueuedDeliveryFromOldSource()
    {
        UIRadioLifetimeBinding old = new(1);
        UIRadioLifetimeBinding replacement = new(2);
        using UIRadioGroupState group = new() { Binding = old };
        Action<ulong?> delayed = old.CaptureDelivery().ShouldNotBeNull();

        group.Binding = replacement;
        delayed(99);

        group.SelectedId.ShouldBe(2UL);
        replacement.SetValue(3);
        group.SelectedId.ShouldBe(3UL);
        Action<ulong?> disposedDelivery = replacement.CaptureDelivery().ShouldNotBeNull();
        group.Dispose();
        disposedDelivery(100);
        group.SelectedId.ShouldBe(3UL);
    }

    [Test]
    public void DisposeClearsInactiveButtonOwnership()
    {
        SceneNode root = new("Inactive radio", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIRadioGroupState group = new();
        UIRadioButtonComponent button = root.NewChild("Option").AddComponent<UIRadioButtonComponent>()!;
        button.OptionId = 1;
        button.Group = group;
        try
        {
            group.Dispose();
            button.Group.ShouldBeNull();
            root.IsActiveSelf = true;
            button.IsSelected.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DisposeContinuesAfterSelectedButtonCallbackThrows()
    {
        SceneNode root = new("Throwing radio", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIRadioGroupState group = new() { Binding = new UIValueState<ulong?>(3) };
        UIRadioButtonComponent first = root.NewChild("First").AddComponent<UIRadioButtonComponent>()!;
        first.OptionId = 3;
        first.Group = group;
        UIRadioButtonComponent second = root.NewChild("Second").AddComponent<UIRadioButtonComponent>()!;
        second.OptionId = 4;
        second.Group = group;
        root.IsActiveSelf = true;
        try
        {
            first.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UIRadioButtonComponent.IsSelected))
                    throw new InvalidOperationException("Injected selection callback failure.");
            };
            Should.Throw<InvalidOperationException>(group.Dispose);
            first.Group.ShouldBeNull();
            second.Group.ShouldBeNull();
            second.IsSelected.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReentrantGroupReplacementCannotAttachStaleGroup()
    {
        SceneNode root = new("Radio replacement", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        using UIRadioGroupState original = new() { Binding = new UIValueState<ulong?>(5) };
        using UIRadioGroupState stale = new();
        using UIRadioGroupState replacement = new();
        UIRadioButtonComponent button = root.NewChild("Option").AddComponent<UIRadioButtonComponent>()!;
        button.OptionId = 5;
        button.Group = original;
        root.IsActiveSelf = true;
        try
        {
            button.IsSelected.ShouldBeTrue();
            button.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UIRadioButtonComponent.IsSelected) && !button.IsSelected)
                    button.Group = replacement;
            };

            button.Group = stale;

            button.Group.ShouldBeSameAs(replacement);
            stale.Select(5).ShouldBeFalse();
            replacement.Select(5).ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PublicGroupAndOptionChangesPublishPropertyNotifications()
    {
        SceneNode root = new("Radio notifications", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        using UIRadioGroupState group = new();
        UIRadioButtonComponent button = root.NewChild("Option").AddComponent<UIRadioButtonComponent>()!;
        List<string> changed = [];
        button.PropertyChanged += (_, change) => changed.Add(change.PropertyName ?? string.Empty);
        try
        {
            button.Group = group;
            button.OptionId = 7;
            changed.ShouldContain(nameof(UIRadioButtonComponent.Group));
            changed.ShouldContain(nameof(UIRadioButtonComponent.OptionId));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PropertyChangingReplacementWinsOverOuterOptionWrite()
    {
        SceneNode root = new("Radio option reentry", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        using UIRadioGroupState group = new();
        UIRadioButtonComponent button = root.NewChild("Option").AddComponent<UIRadioButtonComponent>()!;
        button.Group = group;
        bool replaced = false;
        button.PropertyChanging += (_, change) =>
        {
            if (!replaced && change.PropertyName == nameof(UIRadioButtonComponent.OptionId))
            {
                replaced = true;
                button.OptionId = 7;
            }
        };
        try
        {
            button.OptionId = 5;
            button.OptionId.ShouldBe(7UL);
            root.IsActiveSelf = true;
            group.Select(7).ShouldBeTrue();
            group.Select(5).ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PropertyChangingDestroyCannotRestoreGroupRegistration()
    {
        SceneNode root = new("Radio terminal reentry", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        using UIRadioGroupState group = new();
        UIRadioButtonComponent button = root.NewChild("Option").AddComponent<UIRadioButtonComponent>()!;
        button.OptionId = 4;
        button.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(UIRadioButtonComponent.Group))
                button.Destroy(true);
        };
        try
        {
            button.Group = group;
            button.Group.ShouldBeNull();
            group.Select(4).ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PropertyChangedReplacementWinsOverOuterGroupWrite()
    {
        SceneNode root = new("Radio group reentry", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        using UIRadioGroupState stale = new();
        using UIRadioGroupState replacement = new();
        UIRadioButtonComponent button = root.NewChild("Option").AddComponent<UIRadioButtonComponent>()!;
        button.OptionId = 8;
        bool replaced = false;
        button.PropertyChanged += (_, change) =>
        {
            if (!replaced && change.PropertyName == nameof(UIRadioButtonComponent.Group))
            {
                replaced = true;
                button.Group = replacement;
            }
        };
        try
        {
            button.Group = stale;
            button.Group.ShouldBeSameAs(replacement);
            root.IsActiveSelf = true;
            stale.Select(8).ShouldBeFalse();
            replacement.Select(8).ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }
}
