using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIWorldOutputAttemptContractTests
{
    [Test]
    public void MissingOrFailedWorldReceiptVetoesTheCurrentDispatch()
    {
        XRWindow window = NewWindow();
        ulong first = Invoke<ulong>(window, "BeginOpenGlRenderDispatch");
        XRViewport viewport = (XRViewport)RuntimeHelpers.GetUninitializedObject(
            typeof(XRViewport));
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
        XRViewport viewport = (XRViewport)RuntimeHelpers.GetUninitializedObject(
            typeof(XRViewport));
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

    private static XRWindow NewWindow()
    {
        XRWindow window = (XRWindow)RuntimeHelpers.GetUninitializedObject(
            typeof(XRWindow));
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
