using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UICanvasInputLifetimeTests
{
    [Test]
    public void DirectDestroy_UnregistersPointerTickAndInputEvents()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateRoot(out UICanvasInputComponent input);
        int delivered = 0;
        input.LeftClickDown += _ => delivered++;
        input.RightClick += _ => delivered++;
        input.EscapePressed += () => delivered++;
        input.BackPressed += _ => delivered++;
        try
        {
            root.IsActiveSelf = true;
            Assert.That(PointerTickCount(input), Is.EqualTo(1));
            input.Destroy(true);
            typeof(UICanvasInputComponent).GetMethod("OnEscapeStateChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, [true]);
            Assert.Multiple(() =>
            {
                Assert.That(PointerTickCount(input), Is.Zero);
                Assert.That(delivered, Is.Zero);
                Assert.That(EventOwner(input, "LeftClickDown"), Is.Null);
                Assert.That(EventOwner(input, "RightClick"), Is.Null);
                Assert.That(EventOwner(input, "EscapePressed"), Is.Null);
                Assert.That(EventOwner(input, "BackPressed"), Is.Null);
            });
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ThrowingCleanup_StillReleasesPointerTickAndFocusAndRejectsReentrantActivation()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateRoot(out UICanvasInputComponent input);
        UIButtonComponent button = root.NewChild("Focus target").AddComponent<UIButtonComponent>()!;
        try
        {
            root.IsActiveSelf = true;
            input.FocusedComponent = button;
            Assert.That(button.IsFocused, Is.True);
            bool thrown = false;
            button.PropertyChanged += (_, args) =>
            {
                if (thrown || args.PropertyName != nameof(UIButtonComponent.IsFocused) || button.IsFocused)
                    return;
                thrown = true;
                input.FocusedComponent = button;
                input.IsActive = true;
                throw new InvalidOperationException("Focus cleanup failed.");
            };
            Assert.That(() => input.Destroy(true), Throws.Exception);
            Assert.Multiple(() =>
            {
                Assert.That(thrown, Is.True);
                Assert.That(input.FocusedComponent, Is.Null);
                Assert.That(button.IsFocused, Is.False);
                Assert.That(PointerTickCount(input), Is.Zero);
            });
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DeactivateThenReactivate_RegistersOnePointerTick()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateRoot(out UICanvasInputComponent input);
        try
        {
            root.IsActiveSelf = true;
            root.IsActiveSelf = false;
            Assert.That(PointerTickCount(input), Is.Zero);
            root.IsActiveSelf = true;
            Assert.That(PointerTickCount(input), Is.EqualTo(1));
            input.Destroy(true);
            Assert.That(PointerTickCount(input), Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    /// <summary>Counts the pointer hit-test ticks that the component holds in its tick registrations.</summary>
    private static int PointerTickCount(UICanvasInputComponent input)
    {
        FieldInfo? field = null;
        for (Type? type = input.GetType(); type is not null && field is null; type = type.BaseType)
            field = type.GetField("_tickCache", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        int count = 0;
        foreach (object entry in (System.Collections.IEnumerable)field!.GetValue(input)!)
        {
            Delegate tick = (Delegate)entry.GetType().GetField("Item3")!.GetValue(entry)!;
            if (tick.Method.Name == "TickPointerInput" && ReferenceEquals(tick.Target, input))
                count++;
        }
        return count;
    }

    [Test]
    public void TeardownCallback_CannotCreateNewPendingVirtualFocus()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateRoot(out UICanvasInputComponent input);
        SceneNode listNode = new(root, "Virtual rows");
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        list.ItemSize = 20;
        UIVirtualTreeProvider tree = new(() => new UIBoundableTransform(),
            (_, _, _, _) => { }, (_, _, _) => { });
        tree.Insert(1, 0, 0, "Item");
        list.VirtualProvider = tree;
        UIButtonComponent button = root.NewChild("Focused button").AddComponent<UIButtonComponent>()!;
        try
        {
            root.IsActiveSelf = true;
            input.FocusedComponent = button;
            bool callbackRan = false;
            bool requestAccepted = true;
            button.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(UIButtonComponent.IsFocused) || button.IsFocused)
                    return;
                callbackRan = true;
                requestAccepted = input.RequestVirtualItemFocus(list, 1);
            };
            input.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(callbackRan, Is.True);
                Assert.That(requestAccepted, Is.False);
                Assert.That(input.FocusedComponent, Is.Null);
                Assert.That(typeof(UICanvasInputComponent).GetField("_pendingVirtualFocusItemId",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input), Is.EqualTo(0UL));
            });
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateRoot(out UICanvasInputComponent input)
    {
        SceneNode root = new("Input lifetime", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        input = root.AddComponent<UICanvasInputComponent>()!;
        return root;
    }

    private static object? EventOwner(UICanvasInputComponent input, string name)
        => typeof(UICanvasInputComponent).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input);
}
