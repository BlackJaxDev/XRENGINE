using System.Reflection;
using System.Linq;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public class UIToggleLifetimeTests
{
    [Test]
    public void DirectDestroy_DetachesExactBindingAndTickWhileCanvasRemainsActive()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateCanvasRoot(out TestWorldContext world);
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        UIValueState<UIToggleComponent.ECurrentState> source = new(UIToggleComponent.ECurrentState.False);
        toggle.Binding = source;
        try
        {
            root.IsActiveSelf = true;
            Assert.That(HasBindingListener(source), Is.True);
            toggle.Destroy(true);
            source.SetValue(UIToggleComponent.ECurrentState.True);
            Assert.Multiple(() =>
            {
                Assert.That(HasBindingListener(source), Is.False);
                Assert.That(toggle.Binding, Is.Null);
                Assert.That(toggle.CurrentState, Is.EqualTo(UIToggleComponent.ECurrentState.False));
                Assert.That(GetTickRegistered(toggle), Is.False);
                Assert.That(GetWorldTickCount(world, toggle), Is.Zero);
                Assert.That(root.IsDestroyed, Is.False);
            });
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BindingChange_ReentrantReplacementKeepsOnlyLatestSubscription()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateCanvasRoot(out _);
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        UIValueState<UIToggleComponent.ECurrentState> initial = new(UIToggleComponent.ECurrentState.False);
        UIValueState<UIToggleComponent.ECurrentState> attempted = new(UIToggleComponent.ECurrentState.True);
        UIValueState<UIToggleComponent.ECurrentState> replacement = new(UIToggleComponent.ECurrentState.Intermediate);
        toggle.Binding = initial;
        try
        {
            root.IsActiveSelf = true;
            Action<UIToggleComponent.ECurrentState> staleHandler = GetBindingHandler(initial)!;
            bool replaced = false;
            toggle.PropertyChanging += (_, args) =>
            {
                if (replaced || args.PropertyName != nameof(UIToggleComponent.Binding))
                    return;
                replaced = true;
                toggle.Binding = replacement;
            };
            toggle.Binding = attempted;
            staleHandler(UIToggleComponent.ECurrentState.False);
            Assert.Multiple(() =>
            {
                Assert.That(toggle.Binding, Is.SameAs(replacement));
                Assert.That(HasBindingListener(initial), Is.False);
                Assert.That(HasBindingListener(attempted), Is.False);
                Assert.That(HasBindingListener(replacement), Is.True);
                Assert.That(toggle.CurrentState, Is.EqualTo(UIToggleComponent.ECurrentState.Intermediate));
            });
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ThrowingBindingNotificationAfterDestroy_DoesNotRestoreSubscription()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateCanvasRoot(out _);
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        UIValueState<UIToggleComponent.ECurrentState> initial = new(UIToggleComponent.ECurrentState.False);
        UIValueState<UIToggleComponent.ECurrentState> next = new(UIToggleComponent.ECurrentState.True);
        toggle.Binding = initial;
        try
        {
            root.IsActiveSelf = true;
            toggle.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(UIToggleComponent.Binding))
                    return;
                toggle.Destroy(true);
                throw new InvalidOperationException("Property notification failed after destruction.");
            };
            Assert.Throws<InvalidOperationException>(() => toggle.Binding = next);
            Assert.Multiple(() =>
            {
                Assert.That(toggle.Binding, Is.Null);
                Assert.That(HasBindingListener(initial), Is.False);
                Assert.That(HasBindingListener(next), Is.False);
                Assert.That(GetTickRegistered(toggle), Is.False);
            });
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BindingAdd_DeactivatesOwnerWithoutLeavingLateSubscription()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateCanvasRoot(out TestWorldContext world);
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        ReentrantToggleValueBinding binding = new(UIToggleComponent.ECurrentState.True);
        bool deactivated = false;
        binding.OnAdd = () =>
        {
            if (deactivated)
                return;
            deactivated = true;
            root.IsActiveSelf = false;
        };
        try
        {
            root.IsActiveSelf = true;
            toggle.Binding = binding;
            Assert.Multiple(() =>
            {
                Assert.That(deactivated, Is.True);
                Assert.That(binding.SubscriberCount, Is.Zero);
                Assert.That(toggle.Binding, Is.SameAs(binding));
                Assert.That(GetTickRegistered(toggle), Is.False);
                Assert.That(GetWorldTickCount(world, toggle), Is.Zero);
            });
            root.IsActiveSelf = true;
            Assert.That(binding.SubscriberCount, Is.EqualTo(1));
            Assert.That(GetWorldTickCount(world, toggle), Is.EqualTo(1));
            toggle.Destroy(true);
            Assert.That(binding.SubscriberCount, Is.Zero);
            Assert.That(GetWorldTickCount(world, toggle), Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BindingRemove_DeactivatesOwnerButPreservesReplacementForReactivation()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateCanvasRoot(out TestWorldContext world);
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        ReentrantToggleValueBinding original = new(UIToggleComponent.ECurrentState.False);
        ReentrantToggleValueBinding replacement = new(UIToggleComponent.ECurrentState.True);
        toggle.Binding = original;
        try
        {
            root.IsActiveSelf = true;
            original.OnRemove = () => root.IsActiveSelf = false;
            toggle.Binding = replacement;
            Assert.Multiple(() =>
            {
                Assert.That(toggle.Binding, Is.SameAs(replacement));
                Assert.That(original.SubscriberCount, Is.Zero);
                Assert.That(replacement.SubscriberCount, Is.Zero);
                Assert.That(GetTickRegistered(toggle), Is.False);
                Assert.That(GetWorldTickCount(world, toggle), Is.Zero);
            });
            root.IsActiveSelf = true;
            Assert.That(replacement.SubscriberCount, Is.EqualTo(1));
            Assert.That(GetWorldTickCount(world, toggle), Is.EqualTo(1));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void StateCallbackDestroy_PreventsLaterCommitCallback()
    {
        using UITestShaderScope shader = new();
        SceneNode root = CreateCanvasRoot(out _);
        UIToggleComponent toggle = root.NewChild("Toggle").AddComponent<UIToggleComponent>()!;
        int commits = 0;
        toggle.ValueCommitted += _ => commits++;
        toggle.OnStateChanged += _ => toggle.Destroy(true);
        try
        {
            root.IsActiveSelf = true;
            Assert.That(toggle.Toggle(), Is.True);
            Assert.That(commits, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateCanvasRoot(out TestWorldContext world)
    {
        SceneNode root = new("Toggle canvas", new UICanvasTransform()) { IsActiveSelf = false };
        world = new TestWorldContext();
        root.SetWorldContext(world);
        root.AddComponent<UICanvasComponent>();
        return root;
    }

    private static bool HasBindingListener(UIValueState<UIToggleComponent.ECurrentState> source)
        => GetBindingHandler(source) is not null;

    private static Action<UIToggleComponent.ECurrentState>? GetBindingHandler(
        UIValueState<UIToggleComponent.ECurrentState> source)
        => (typeof(UIValueState<UIToggleComponent.ECurrentState>)
            .GetField("ValueChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source) as Action<UIToggleComponent.ECurrentState>);

    private static bool GetTickRegistered(UIToggleComponent toggle)
        => (bool)typeof(UIToggleComponent).GetField("_tickRegistered",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(toggle)!;

    private static int GetWorldTickCount(TestWorldContext world, UIToggleComponent toggle)
    {
        int count = 0;
        var ticks = (System.Collections.IEnumerable)typeof(TestWorldContext)
            .GetField("_ticks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(world)!;
        foreach (object entry in ticks)
            if (entry.GetType().GetField("Item2")!.GetValue(entry) is Delegate tick &&
                ReferenceEquals(tick.Target, toggle))
                count++;
        return count;
    }
}
