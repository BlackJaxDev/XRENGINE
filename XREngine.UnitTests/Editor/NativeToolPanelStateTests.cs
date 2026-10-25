using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using Shouldly;
using XREngine.Editor;
using XREngine.Editor.UI.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Rendering;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeToolPanelStateTests
{
    [Test]
    public void ReparentedRowsRejectOwnedVirtualBoundBeforeWrite()
    {
        using UITestShaderScope shaders = new();
        TestWorldContext world = new();
        SceneNode root = CreateCanvasRoot("Tools");
        SceneNode foreign = new("Foreign", new UIBoundableTransform());
        root.SetWorldContext(world);
        foreign.SetWorldContext(world);
        SceneNode slot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockLayout layout = new();
        try
        {
            NativeToolPanelStateBinding binding = NativeToolPanelState.Bind(console,
                slot, layout, 1, () => { }, () => { })!;
            UIListTransform list = (UIListTransform)typeof(NativeVirtualTextPanel)
                .GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(console)!;
            list.UpperVirtualBound = -1;
            NativeDockPanelStateReceipt source = binding.CaptureReceipt();
            list.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UIListTransform.UpperVirtualBound))
                    list.SceneNode!.Transform.SetParent(foreign.Transform, false,
                        EParentAssignmentMode.Immediate);
            };
            NativeDockPanelStateValues requested = source.Values with { ScrollOffset = 120 };
            Should.Throw<InvalidOperationException>(() => binding.Restore(requested));
            list.UpperVirtualBound.ShouldBe(-1);
            list.Parent.ShouldBeSameAs(foreign.Transform);
            source.IsCurrent.ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void ReparentedRowsRejectOwnedScrollWriteAndInvalidateSourceReceipt()
    {
        using UITestShaderScope shaders = new();
        TestWorldContext world = new();
        SceneNode root = CreateCanvasRoot("Tools");
        SceneNode foreign = new("Foreign", new UIBoundableTransform());
        root.SetWorldContext(world);
        foreign.SetWorldContext(world);
        SceneNode slot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockLayout layout = new();
        try
        {
            NativeToolPanelStateBinding binding = NativeToolPanelState.Bind(console,
                slot, layout, 1, () => { }, () => { })!;
            NativeDockPanelStateReceipt source = binding.CaptureReceipt();
            UIListTransform list = (UIListTransform)typeof(NativeVirtualTextPanel)
                .GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(console)!;
            float previous = list.ContentScrollOffset;
            list.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UIListTransform.ContentScrollOffset) &&
                    change is XRPropertyChangingEventArgs<float> scroll &&
                    scroll.NewValue > previous)
                    list.SceneNode!.Transform.SetParent(foreign.Transform, false,
                        EParentAssignmentMode.Immediate);
            };
            NativeDockPanelStateValues requested = source.Values with { ScrollOffset = 120 };
            Should.Throw<InvalidOperationException>(() => binding.Restore(requested));
            list.ContentScrollOffset.ShouldBe(previous);
            list.Parent.ShouldBeSameAs(foreign.Transform);
            source.IsCurrent.ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void InitialBindingAdmitsOnlyItsCapturedDestroyedPredecessor()
    {
        SceneNode host = new("Host");
        SceneNode oldSlot = host.NewChild("Old slot");
        SceneNode replacement = host.NewChild("Replacement slot");
        SceneNode callbackSlot = host.NewChild("Callback slot");
        Dictionary<string, SceneNode> slots = new() { ["console"] = oldSlot };
        try
        {
            oldSlot.Destroy(true);
            ObjectConstructionIdentity oldIdentity = ObjectConstructionIdentity.Capture(oldSlot);
            NativeDockTreeHost.IsExpectedInitialSlotMapping(slots, "console",
                replacement, oldSlot, oldIdentity).ShouldBeTrue();

            slots["console"] = callbackSlot;
            NativeDockTreeHost.IsExpectedInitialSlotMapping(slots, "console",
                replacement, oldSlot, oldIdentity).ShouldBeFalse();

            slots["console"] = replacement;
            NativeDockTreeHost.IsExpectedInitialSlotMapping(slots, "console",
                replacement, oldSlot, oldIdentity).ShouldBeTrue();
        }
        finally { host.Destroy(true); }
    }

    [Test]
    public void DestinationRegistrationReplacementStopsFirstFilterWriteAndAllowsFreshRestore()
    {
        using UITestShaderScope shaders = new();
        SceneNode host = CreateCanvasRoot("Host");
        SceneNode slot = host.NewChildWithTransform<UIBoundableTransform>(out _, "Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockWindowRegistration registration = host.AddComponent<NativeDockWindowRegistration>()!;
        object context = new();
        string key = Guid.NewGuid().ToString("N");
        registration.Configure(key, _ => true, context);
        NativeDockLayout layout = new() { ConsoleFilter = "old" };
        try
        {
            NativeToolPanelStateBinding binding = NativeToolPanelState.Bind(console,
                slot, layout, 1, () => { }, () => { })!;
            ObjectConstructionIdentity identity = ObjectConstructionIdentity.Capture(registration);
            ulong revision = registration.RegistrationRevision;
            bool replaced = false;
            console.PropertyChanging += (_, change) =>
            {
                if (replaced || change.PropertyName != nameof(ConsolePanel.Filter)) return;
                replaced = true;
                registration.Configure(key, _ => true, context);
            };
            NativeDockPanelStateValues requested = NativeDockPanelStateValues
                .Capture(layout, NativeDockPanel.Console) with { ConsoleFilter = "incoming" };
            Should.Throw<InvalidOperationException>(() => binding.Restore(requested,
                () => identity.IsCurrent && registration.RegistrationRevision == revision));
            console.Filter.ShouldBe("old");

            ulong freshRevision = registration.RegistrationRevision;
            NativeDockPanelStateReceipt applied = binding.Restore(requested,
                () => identity.IsCurrent && registration.RegistrationRevision == freshRevision);
            applied.IsCurrent.ShouldBeTrue();
            console.Filter.ShouldBe("incoming");
        }
        finally { host.Destroy(true); }
    }

    [Test]
    public void OwnedScrollRestoreRejectsVetoBeforeViewportClamp()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateCanvasRoot("Tools");
        root.SetWorldContext(new TestWorldContext());
        SceneNode slot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockLayout layout = new();
        try
        {
            NativeToolPanelStateBinding binding = NativeToolPanelState.Bind(console,
                slot, layout, 1, () => { }, () => { })!;
            UIListTransform list = (UIListTransform)typeof(NativeVirtualTextPanel)
                .GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(console)!;
            list.ShouldNotBeNull();
            list.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(UIListTransform.ContentScrollOffset) &&
                    change is XRPropertyChangingEventArgs<float> scroll &&
                    scroll.NewValue > 0)
                    scroll.AllowChange = false;
            };
            NativeDockPanelStateValues requested = NativeDockPanelStateValues
                .Capture(layout, NativeDockPanel.Console) with { ScrollOffset = 120 };
            Should.Throw<InvalidOperationException>(() => binding.Restore(requested));
            list.ContentScrollOffset.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ActiveConsoleAndProfilerAcceptInitialRestoreWithWidgetFeedback()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateCanvasRoot("Tools");
        root.SetWorldContext(new TestWorldContext());
        SceneNode consoleSlot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Console slot");
        SceneNode profilerSlot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Profiler slot");
        consoleSlot.NewChild<ConsolePanel>(out var console);
        profilerSlot.NewChild<ProfilerPanel>(out var profiler);
        NativeDockLayout layout = new()
        {
            ConsoleCategory = ELogCategory.Rendering,
            ProfilerView = (int)ENativeProfilerView.FrameOutputs,
        };
        try
        {
            console.IsActiveInHierarchy.ShouldBeTrue();
            profiler.IsActiveInHierarchy.ShouldBeTrue();
            NativeToolPanelState.Bind(console, consoleSlot, layout, 1,
                () => { }, () => { }).ShouldNotBeNull();
            NativeToolPanelState.Bind(profiler, profilerSlot, layout, 2,
                () => { }, () => { }).ShouldNotBeNull();
            console.Category.ShouldBe(ELogCategory.Rendering);
            profiler.View.ShouldBe(ENativeProfilerView.FrameOutputs);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ActiveConsoleForeignCategoryEditPersistsBeforeStaleFilterRestoreStops()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateCanvasRoot("Tools");
        root.SetWorldContext(new TestWorldContext());
        SceneNode slot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockLayout layout = new() { ConsoleFilter = "old" };
        try
        {
            NativeToolPanelStateBinding binding = NativeToolPanelState.Bind(console,
                slot, layout, 1, () => { }, () => { })!;
            console.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(ConsolePanel.Filter))
                    console.Category = ELogCategory.Rendering;
            };
            NativeDockPanelStateValues requested = NativeDockPanelStateValues
                .Capture(layout, NativeDockPanel.Console) with { ConsoleFilter = "incoming" };
            Should.Throw<InvalidOperationException>(() => binding.Restore(requested));
            console.Filter.ShouldBe("old");
            console.Category.ShouldBe(ELogCategory.Rendering);
            layout.ConsoleCategory.ShouldBe(ELogCategory.Rendering);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void FirstDestinationSetterRejectsSourceAwayAndBackBeforeWriting()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = CreateCanvasRoot("Tools");
        root.SetWorldContext(new TestWorldContext());
        SceneNode sourceSlot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Source slot");
        SceneNode destinationSlot = root.NewChildWithTransform<UIBoundableTransform>(out _, "Destination slot");
        sourceSlot.NewChild<ConsolePanel>(out var source);
        destinationSlot.NewChild<ConsolePanel>(out var destination);
        NativeDockLayout sourceLayout = new() { ConsoleFilter = "original" };
        NativeDockLayout destinationLayout = new() { ConsoleFilter = "destination" };
        try
        {
            NativeToolPanelStateBinding sourceBinding = NativeToolPanelState.Bind(source,
                sourceSlot, sourceLayout, 1, () => { }, () => { })!;
            NativeToolPanelStateBinding destinationBinding = NativeToolPanelState.Bind(destination,
                destinationSlot, destinationLayout, 1, () => { }, () => { })!;
            NativeDockPanelStateReceipt sourceReceipt = sourceBinding.CaptureReceipt();
            destination.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName != nameof(ConsolePanel.Filter)) return;
                source.Filter = "newer";
                source.Filter = "original";
            };
            NativeDockPanelStateValues requested = sourceReceipt.Values with
                { ConsoleFilter = "incoming" };
            Should.Throw<InvalidOperationException>(() =>
                destinationBinding.Restore(requested, () => sourceReceipt.IsCurrent));
            destination.Filter.ShouldBe("destination");
            source.Filter.ShouldBe("original");
            sourceReceipt.IsCurrent.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RetainedConsoleBindingRestoresLiveFieldsAndKeepsUnrelatedLayout()
    {
        using UITestShaderScope shaders = new();
        SceneNode slot = CreateCanvasRoot("Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockLayout layout = new()
        {
            ConsoleFilter = "destination",
            AssetSearch = "unrelated",
        };
        try
        {
            NativeToolPanelStateBinding binding = NativeToolPanelState.Bind(console,
                slot, layout, 1, () => { }, () => { })!;
            NativeDockPanelStateValues requested = NativeDockPanelStateValues
                .Capture(layout, NativeDockPanel.Console) with
            {
                ConsoleFilter = "source",
                ConsoleCategory = ELogCategory.Rendering,
                ConsoleAutoScroll = false,
                ScrollOffset = 36,
            };
            NativeDockPanelStateReceipt applied = binding.Restore(requested);
            applied.IsCurrent.ShouldBeTrue();
            console.Filter.ShouldBe("source");
            console.Category.ShouldBe(ELogCategory.Rendering);
            console.AutoScroll.ShouldBeFalse();
            console.ScrollOffset.ShouldBe(36);
            applied.Values.WriteTo(layout);
            layout.ConsoleFilter.ShouldBe("source");
            layout.AssetSearch.ShouldBe("unrelated");
        }
        finally { slot.Destroy(true); }
    }

    [Test]
    public void ConsoleCallbackEditStopsRestoreBeforeItsFilterWrite()
    {
        using UITestShaderScope shaders = new();
        SceneNode slot = CreateCanvasRoot("Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockLayout layout = new() { ConsoleFilter = "old" };
        try
        {
            NativeToolPanelStateBinding binding = NativeToolPanelState.Bind(console,
                slot, layout, 1, () => { }, () => { })!;
            console.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(ConsolePanel.Filter))
                    console.Category = ELogCategory.Rendering;
            };
            NativeDockPanelStateValues requested = NativeDockPanelStateValues
                .Capture(layout, NativeDockPanel.Console) with
                { ConsoleFilter = "incoming", ConsoleCategory = null };
            Should.Throw<InvalidOperationException>(() => binding.Restore(requested));
            console.Filter.ShouldBe("old");
            console.Category.ShouldBe(ELogCategory.Rendering);
            layout.ConsoleFilter.ShouldBe("old");
            layout.ConsoleCategory.ShouldBe(ELogCategory.Rendering);
        }
        finally { slot.Destroy(true); }
    }

    [Test]
    public void ConsoleRestoresAllFieldsAndQueuesScrollWithoutAnImmediateSave()
    {
        using UITestShaderScope shaders = new();
        SceneNode slot = CreateCanvasRoot("Console slot");
        slot.NewChild<ConsolePanel>(out var console);
        NativeDockLayout layout = new()
        {
            ConsoleFilter = "before",
            ConsoleCategory = ELogCategory.Rendering,
            ConsoleAutoScroll = false,
        };
        layout.PanelScrollOffsets[1] = 24;
        int saves = 0, requests = 0;
        bool owned = true;
        try
        {
            NativeToolPanelState.Bind(console, slot, layout, 1, () => saves++,
                () => requests++, () => owned);
            console.Filter.ShouldBe("before");
            console.Category.ShouldBe(ELogCategory.Rendering);
            console.AutoScroll.ShouldBeFalse();
            console.ScrollOffset.ShouldBe(24);
            saves.ShouldBe(0);

            console.Filter = "after";
            console.Category = null;
            console.AutoScroll = true;
            layout.ConsoleFilter.ShouldBe("after");
            layout.ConsoleCategory.ShouldBeNull();
            layout.ConsoleAutoScroll.ShouldBeTrue();
            saves.ShouldBe(3);

            Action<float> scroll = (Action<float>)typeof(NativeVirtualTextPanel)
                .GetField("ScrollOffsetChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(console)!;
            console.ScrollOffset = 72;
            scroll(72);
            layout.PanelScrollOffsets[1].ShouldBe(72);
            requests.ShouldBe(1);
            saves.ShouldBe(3);

            scroll(144);
            layout.PanelScrollOffsets[1].ShouldBe(72);

            owned = false;
            console.Filter = "unowned";
            scroll(144);
            layout.ConsoleFilter.ShouldBe("after");
            layout.PanelScrollOffsets[1].ShouldBe(72);
            requests.ShouldBe(1);
            saves.ShouldBe(3);
        }
        finally { slot.Destroy(true); }
    }

    [Test]
    public void ProfilerRestoresAndSavesViewButDetachedComponentCannotSave()
    {
        using UITestShaderScope shaders = new();
        SceneNode slot = CreateCanvasRoot("Profiler slot");
        SceneNode panelNode = slot.NewChild<ProfilerPanel>(out var profiler);
        NativeDockLayout layout = new() { ProfilerView = (int)ENativeProfilerView.FrameOutputs };
        int saves = 0;
        try
        {
            NativeToolPanelState.Bind(profiler, slot, layout, 2, () => saves++, () => { });
            profiler.View.ShouldBe(ENativeProfilerView.FrameOutputs);
            saves.ShouldBe(0);
            profiler.View = ENativeProfilerView.CpuRoots;
            layout.ProfilerView.ShouldBe((int)ENativeProfilerView.CpuRoots);
            saves.ShouldBe(1);
            panelNode.Transform.SetParent(null, false,
                XREngine.Scene.Transforms.EParentAssignmentMode.Immediate);
            profiler.View = ENativeProfilerView.FrameOutputs;
            layout.ProfilerView.ShouldBe((int)ENativeProfilerView.CpuRoots);
            saves.ShouldBe(1);
        }
        finally
        {
            panelNode.Destroy(true);
            slot.Destroy(true);
        }
    }

    private static SceneNode CreateCanvasRoot(string name)
    {
        SceneNode root = new(name, new UICanvasTransform());
        root.AddComponent<UICanvasComponent>();
        return root;
    }
}
