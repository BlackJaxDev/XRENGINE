using System.Reflection;
using NUnit.Framework;
using Silk.NET.Windowing;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop.Windowing;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenGlWindowDispatchTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void Dispatch_UsesVerdictBeforeSwapAndRestoresPolicy(bool permit)
    {
        var (backend, probe) = Create();
        probe.PermitSwap = permit;
        backend.DispatchRender();
        Assert.That(probe.Calls, Is.EqualTo(permit
            ? new[] { "begin", "render", "verdict", "flush", "swap", "complete" }
            : new[] { "begin", "render", "verdict", "complete", "flush" }));
        Assert.That(probe.AutomaticSwapDuringRender, Is.False);
        Assert.That(probe.AutomaticSwapAtCompletion, Is.True);
        Assert.That(probe.CompletedToken, Is.EqualTo(17UL));
        Assert.That(probe.Presented, Is.EqualTo(permit));
    }

    [Test]
    public void Dispatch_RespectsManualSwapPolicy()
    {
        var (backend, probe) = Create();
        probe.AutomaticSwap = false;
        backend.DispatchRender();
        Assert.That(probe.Calls, Is.EqualTo(new[] { "begin", "render", "complete", "flush" }));
        Assert.That(probe.AutomaticSwap, Is.False);
        Assert.That(probe.Presented, Is.False);
    }

    [Test]
    public void Dispatch_RenderFailureFlushesAndKeepsPriorPresentation()
    {
        var (backend, probe) = Create();
        var failure = new InvalidOperationException("render failure");
        probe.Render = () => throw failure;
        Assert.That(Assert.Throws<InvalidOperationException>(backend.DispatchRender), Is.SameAs(failure));
        Assert.That(probe.Calls, Is.EqualTo(new[] { "begin", "render", "complete", "flush" }));
        Assert.That(probe.AutomaticSwap, Is.True);
        Assert.That(probe.Presented, Is.False);
        probe.Render = null;
        probe.Calls.Clear();
        backend.DispatchRender();
        Assert.That(probe.Presented, Is.True);
    }

    [Test]
    public void Dispatch_SwapFailureDoesNotClaimPresentation()
    {
        var (backend, probe) = Create();
        var failure = new InvalidOperationException("swap failure");
        probe.SwapFailure = failure;
        Assert.That(Assert.Throws<InvalidOperationException>(backend.DispatchRender), Is.SameAs(failure));
        Assert.That(probe.AutomaticSwap, Is.True);
        Assert.That(probe.CompletedToken, Is.EqualTo(17UL));
        Assert.That(probe.Presented, Is.False);
    }

    [Test]
    public void Dispatch_RejectsNestedRenderWithoutSwapping()
    {
        var (backend, probe) = Create();
        probe.Render = backend.DispatchRender;
        Assert.Throws<InvalidOperationException>(backend.DispatchRender);
        Assert.That(probe.Calls, Is.EqualTo(new[] { "begin", "render", "complete", "flush" }));
        Assert.That(probe.AutomaticSwap, Is.True);
        Assert.That(probe.Presented, Is.False);
    }

    private static (IRuntimeWindowBackend Backend, OpenGlDispatchTestProbe Probe) Create()
    {
        IWindow window = DispatchProxy.Create<IWindow, OpenGlDispatchTestProbe>();
        var probe = (OpenGlDispatchTestProbe)window;
        Type type = typeof(DesktopSilkWindowBackendFactory).Assembly.GetType(
            "XREngine.Runtime.Platform.Desktop.Windowing.DesktopSilkWindowBackend", true)!;
        var backend = (IRuntimeWindowBackend)Activator.CreateInstance(type, window, default(RuntimeWindowCreateOptions))!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        type.GetField("_glContext", flags)!.SetValue(backend, probe);
        type.GetField("_sink", flags)!.SetValue(backend, probe);
        FieldInfo flush = type.GetField("_glFlush", flags)!;
        flush.SetValue(backend, Delegate.CreateDelegate(flush.FieldType, probe, nameof(probe.Flush)));
        return (backend, probe);
    }
}
