using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIBackdropAttemptContractTests
{
    [Test]
    public void KnownIdleRetiresOnlyMatchingNativeOwnerSlots()
    {
        AbstractRenderer firstOwner = (AbstractRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(OpenGLRenderer));
        AbstractRenderer secondOwner = (AbstractRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(OpenGLRenderer));
        using UIRenderSurfacePool pool = new(EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
        pool.TryReserve(16, 16, out UIRenderSurface? first, out string? firstFailure)
            .ShouldBeTrue(firstFailure);
        pool.TryReserve(24, 24, out UIRenderSurface? second, out string? secondFailure)
            .ShouldBeTrue(secondFailure);
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first.OwnerRenderer = firstOwner;
        first.OwnerBackendGeneration = 11;
        second.OwnerRenderer = secondOwner;
        second.OwnerBackendGeneration = 22;
        first.Release();
        second.Release();

        pool.RetireOwner(firstOwner, 11);
        pool.SlotCount.ShouldBe(1);
        second.OwnerRetired.ShouldBeFalse();
        pool.RetireOwner(secondOwner, 11);
        pool.SlotCount.ShouldBe(1);
        pool.RetireOwner(secondOwner, 22);
        pool.SlotCount.ShouldBe(0);
    }

    [Test]
    public void MissingOrFailedRequiredConsumerVetoesOnlyItsCurrentDispatch()
    {
        XRWindow window = NewUninitializedWindow();
        ulong first = Invoke<ulong>(window, "BeginOpenGlRenderDispatch");
        window.RequireOpenGlBackdrop(31);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", first).ShouldBeFalse();
        window.CompleteOpenGlBackdrop(31, succeeded: false);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", first).ShouldBeFalse();
        typeof(XRWindow).GetField("_activeOpenGlDispatchToken",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, 0UL);

        ulong second = Invoke<ulong>(window, "BeginOpenGlRenderDispatch");
        second.ShouldNotBe(first);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", first).ShouldBeFalse();
        window.RequireOpenGlBackdrop(32);
        window.CompleteOpenGlBackdrop(32, succeeded: true);
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", second).ShouldBeTrue();
    }

    [Test]
    public void FailureBeforeReceiptRegistrationVetoesTheDispatch()
    {
        XRWindow window = NewUninitializedWindow();
        ulong token = Invoke<ulong>(window, "BeginOpenGlRenderDispatch");
        window.FailOpenGlBackdropAttempt();
        Invoke<bool>(window, "CanPresentOpenGlRenderDispatch", token).ShouldBeFalse();
    }

    private static XRWindow NewUninitializedWindow()
    {
        XRWindow window = (XRWindow)RuntimeHelpers.GetUninitializedObject(typeof(XRWindow));
        typeof(XRWindow).GetField("_requiredOpenGlBackdropReceipts",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, new HashSet<ulong>());
        return window;
    }

    private static T Invoke<T>(XRWindow window, string method, params object?[] args)
        => (T)typeof(XRWindow).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;

}
