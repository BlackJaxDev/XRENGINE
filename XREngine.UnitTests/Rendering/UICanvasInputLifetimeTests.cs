using System.Reflection;
using System.Runtime.ExceptionServices;
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
    public void DirectDestroy_UnsubscribesActivationHostAfterHostReplacement()
    {
        using UITestShaderScope shader = new();
        (IRuntimeRenderingHostServices firstHost, SchedulingProbe first) = SchedulingProbe.Create();
        using IDisposable firstScope = RuntimeRenderingHostServices.Install(firstHost);
        SceneNode root = CreateRoot(out UICanvasInputComponent input);
        first.TrackedOwner = input;
        int delivered = 0;
        input.LeftClickDown += _ => delivered++;
        input.RightClick += _ => delivered++;
        input.EscapePressed += () => delivered++;
        input.BackPressed += _ => delivered++;
        try
        {
            root.IsActiveSelf = true;
            Assert.That(first.ActiveCollect, Is.EqualTo(1));
            Assert.That(first.ActiveSwap, Is.EqualTo(1));
            (IRuntimeRenderingHostServices secondHost, SchedulingProbe second) = SchedulingProbe.Create();
            second.TrackedOwner = input;
            using IDisposable secondScope = RuntimeRenderingHostServices.Install(secondHost);
            input.Destroy(true);
            typeof(UICanvasInputComponent).GetMethod("OnEscapeStateChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, [true]);
            Assert.Multiple(() =>
            {
                Assert.That(first.ActiveCollect, Is.Zero);
                Assert.That(first.ActiveSwap, Is.Zero);
                Assert.That(second.ActiveCollect, Is.Zero);
                Assert.That(second.ActiveSwap, Is.Zero);
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
    public void ThrowingCleanup_StillReleasesSwapAndFocusAndRejectsReentrantActivation()
    {
        using UITestShaderScope shader = new();
        (IRuntimeRenderingHostServices host, SchedulingProbe scheduling) = SchedulingProbe.Create();
        using IDisposable hostScope = RuntimeRenderingHostServices.Install(host);
        SceneNode root = CreateRoot(out UICanvasInputComponent input);
        scheduling.TrackedOwner = input;
        UIButtonComponent button = root.NewChild("Focus target").AddComponent<UIButtonComponent>()!;
        try
        {
            root.IsActiveSelf = true;
            input.FocusedComponent = button;
            Assert.That(button.IsFocused, Is.True);
            scheduling.AfterCollectUnsubscribe = () =>
            {
                input.FocusedComponent = button;
                input.IsActive = true;
                throw new InvalidOperationException("Collect cleanup failed.");
            };
            Assert.That(() => input.Destroy(true), Throws.Exception);
            Assert.Multiple(() =>
            {
                Assert.That(input.FocusedComponent, Is.Null);
                Assert.That(button.IsFocused, Is.False);
                Assert.That(scheduling.ActiveCollect, Is.Zero);
                Assert.That(scheduling.ActiveSwap, Is.Zero);
            });
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DeactivateThenReactivate_InstallsOneCurrentHostPair()
    {
        using UITestShaderScope shader = new();
        (IRuntimeRenderingHostServices host, SchedulingProbe scheduling) = SchedulingProbe.Create();
        using IDisposable hostScope = RuntimeRenderingHostServices.Install(host);
        SceneNode root = CreateRoot(out UICanvasInputComponent input);
        scheduling.TrackedOwner = input;
        try
        {
            root.IsActiveSelf = true;
            root.IsActiveSelf = false;
            Assert.That(scheduling.ActiveCollect, Is.Zero);
            Assert.That(scheduling.ActiveSwap, Is.Zero);
            root.IsActiveSelf = true;
            Assert.That(scheduling.ActiveCollect, Is.EqualTo(1));
            Assert.That(scheduling.ActiveSwap, Is.EqualTo(1));
            input.Destroy(true);
            Assert.That(scheduling.ActiveCollect, Is.Zero);
            Assert.That(scheduling.ActiveSwap, Is.Zero);
        }
        finally { root.Destroy(true); }
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

    public class SchedulingProbe : DispatchProxy
    {
        private IRuntimeRenderingHostServices _fallback = null!;
        public int ActiveCollect { get; private set; }
        public int ActiveSwap { get; private set; }
        public Action? AfterCollectUnsubscribe { get; set; }
        public UICanvasInputComponent? TrackedOwner { get; set; }

        public static (IRuntimeRenderingHostServices Host, SchedulingProbe Probe) Create()
        {
            IRuntimeRenderingHostServices host = Create<IRuntimeRenderingHostServices, SchedulingProbe>();
            SchedulingProbe probe = (SchedulingProbe)(object)host;
            probe._fallback = RuntimeRenderingHostServices.Current;
            return (host, probe);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            bool tracked = args is [{ } callback, ..] &&
                callback is Action action && ReferenceEquals(action.Target, TrackedOwner);
            switch (targetMethod.Name)
            {
                case nameof(IRuntimeRenderSchedulingServices.SubscribeViewportCollectVisible):
                    if (tracked) ActiveCollect++;
                    return null;
                case nameof(IRuntimeRenderSchedulingServices.UnsubscribeViewportCollectVisible):
                    if (tracked)
                    {
                        ActiveCollect--;
                        AfterCollectUnsubscribe?.Invoke();
                    }
                    return null;
                case nameof(IRuntimeRenderSchedulingServices.SubscribeViewportSwapBuffers):
                    if (tracked) ActiveSwap++;
                    return null;
                case nameof(IRuntimeRenderSchedulingServices.UnsubscribeViewportSwapBuffers):
                    if (tracked) ActiveSwap--;
                    return null;
            }
            try { return targetMethod.Invoke(_fallback, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
