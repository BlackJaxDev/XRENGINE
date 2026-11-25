using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIWorldOutputAttemptContractTests
{
    [Test]
    public void MissingOrFailedWorldReceiptVetoesTheCurrentDispatch()
    {
        XRWindow window = NewWindow();
        ulong first = Invoke<ulong>(window, "BeginOpenGlRenderDispatch");
        XRViewport viewport = IdentityOnly<XRViewport>();
        RenderCommandCollection commands = new();
        SetField(window, "_expectedOpenGlWorldViewport", viewport);
        SetField(window, "_expectedOpenGlWorldCommands", commands);
        SetField(window, "_expectedOpenGlWorldPackageGeneration", 7L);
        window.RequireOpenGlWorldUi(viewport, commands, 7, 41, 61);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", first)
            .ShouldBeFalse();
        window.CompleteOpenGlWorldUi(41, succeeded: false);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", first)
            .ShouldBeFalse();

        SetField(window, "_activeOpenGlDispatchToken", 0UL);
        ulong second = Invoke<ulong>(window, "BeginOpenGlRenderDispatch");
        second.ShouldNotBe(first);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", first)
            .ShouldBeFalse();
        window.FailOpenGlWorldUiAttempt();
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", second)
            .ShouldBeFalse();
    }

    [Test]
    public void PinnedImageVetoesSkippedRenderAndViewportListChange()
    {
        XRWindow window = NewWindow();
        ulong token = Invoke<ulong>(window, "BeginOpenGlRenderDispatch");
        XRViewport viewport = IdentityOnly<XRViewport>();
        RenderCommandCollection commands = new();
        SetField(window, "_expectedOpenGlWorldViewport", viewport);
        SetField(window, "_expectedOpenGlWorldCommands", commands);
        SetField(window, "_expectedOpenGlWorldPackageGeneration", 8L);
        EventList<XRViewport> viewports = new();
        viewports.Add(viewport);
        SetField(window, "_viewports", viewports);

        viewport.EnableRetainedWorldUiColor.ShouldBeFalse();
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", token)
            .ShouldBeFalse();
        viewports.Clear();
        window.RequireOpenGlWorldUi(viewport, commands, 8, 43, 63);
        window.CompleteOpenGlWorldUi(43, succeeded: true);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", token)
            .ShouldBeFalse();
    }

    [Test]
    public void VulkanWorldInputRequiresTheExactStagedPublicationTuple()
    {
        XRWindow window = NewWindow();
        XRViewport viewport = IdentityOnly<XRViewport>();
        UICanvasComponent canvas = IdentityOnly<UICanvasComponent>();
        UIPublication publication = IdentityOnly<UIPublication>();
        UIPublication replacement = IdentityOnly<UIPublication>();
        UICanvasComponent otherCanvas = IdentityOnly<UICanvasComponent>();
        List<(XRViewport Viewport, UICanvasComponent Canvas,
            UIPublication Publication, ulong ReceiptId)> staged =
            [(viewport, canvas, publication, 71UL)];
        SetField(window, "_windowUiFrame", staged);
        SetField(window, "_windowUiFrameId", 19UL);

        window.HasStagedVulkanWorldUiPublication(
            19UL, viewport, canvas, publication, 71UL).ShouldBeTrue();
        window.HasStagedVulkanWorldUiPublication(
            20UL, viewport, canvas, publication, 71UL).ShouldBeFalse();
        window.HasStagedVulkanWorldUiPublication(
            19UL, viewport, otherCanvas, publication, 71UL).ShouldBeFalse();
        window.HasStagedVulkanWorldUiPublication(
            19UL, viewport, canvas, replacement, 71UL).ShouldBeFalse();
        window.HasStagedVulkanWorldUiPublication(
            19UL, viewport, canvas, publication, 72UL).ShouldBeFalse();

        staged.Add((viewport, canvas, replacement, 72UL));
        window.HasStagedVulkanWorldUiPublication(
            19UL, viewport, canvas, publication, 71UL).ShouldBeFalse();
    }

    [Test]
    public void VulkanWorldInputOrdersFramesWithinTheExactRenderer()
    {
        object first = new();
        object replacement = new();

        XRWindow.IsRequiredVulkanWorldUiOutputCurrent(
            first, first, first, 9UL, 9UL, 42UL, 41UL).ShouldBeFalse();
        XRWindow.IsRequiredVulkanWorldUiOutputCurrent(
            first, first, first, 9UL, 9UL, 42UL, 43UL).ShouldBeTrue();
        XRWindow.IsRequiredVulkanWorldUiOutputCurrent(
            replacement, first, first, 9UL, 10UL, 42UL, 44UL).ShouldBeFalse();
        XRWindow.IsRequiredVulkanWorldUiOutputCurrent(
            replacement, replacement, first, 9UL, 1UL, 42UL, 1UL).ShouldBeTrue();
    }

    // These doubles prove identity only. They have no constructor-owned resources
    // and must not enter the real finalizer or destruction queues.
    private static T IdentityOnly<T>() where T : class
    {
        T value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        GC.SuppressFinalize(value);
        return value;
    }

    private static XRWindow NewWindow()
    {
        XRWindow window = IdentityOnly<XRWindow>();
        SetField(window, "_requiredOpenGlBackdropReceipts", new HashSet<ulong>());
        SetField(window, "_requiredOpenGlWorldReceipts", new HashSet<ulong>());
        SetField(window, "_openGlWorldNativeTokens", new Dictionary<ulong, ulong>());
        return window;
    }

    private static void SetField(XRWindow window, string field, object value)
        => typeof(XRWindow).GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static T Invoke<T>(XRWindow window, string method, params object?[] args)
        => (T)typeof(XRWindow).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
}
