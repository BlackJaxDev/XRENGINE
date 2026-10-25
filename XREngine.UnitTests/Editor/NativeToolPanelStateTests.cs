using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Editor.UI.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeToolPanelStateTests
{
    [Test]
    public void ConsoleRestoresAllFieldsAndQueuesScrollWithoutAnImmediateSave()
    {
        using UITestShaderScope shaders = new();
        SceneNode slot = new("Console slot", new UIBoundableTransform());
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
            scroll(72);
            layout.PanelScrollOffsets[1].ShouldBe(72);
            requests.ShouldBe(1);
            saves.ShouldBe(3);

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
        SceneNode slot = new("Profiler slot", new UIBoundableTransform());
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
}
