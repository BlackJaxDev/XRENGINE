using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class WindowCloseApprovalTests
{
    [Test]
    public void FailedShutdownRequestCannotMarkLaterManualApproval()
    {
        XRWindow window = NewWindowWithBackend(out CloseBackendProxy backend);
        List<WindowCloseApproval> approvals = [];
        window.CloseApproved += (_, approval) => approvals.Add(approval);
        backend.OnClose = () => RecordSimulatedApproval(window, 1);
        backend.ThrowAfterClose = true;

        InvokePerformClose(window, applicationShutdownIntent: true);
        Assert.That(approvals, Is.Empty);

        backend.ThrowAfterClose = false;
        InvokePerformClose(window, applicationShutdownIntent: false);
        Assert.That(approvals, Has.Count.EqualTo(1));
        Assert.That(approvals[0], Is.EqualTo(new WindowCloseApproval(1, false)));
    }

    [Test]
    public void VetoedShutdownRequestCannotMarkLaterManualApproval()
    {
        XRWindow window = NewWindowWithBackend(out CloseBackendProxy backend);
        List<WindowCloseApproval> approvals = [];
        window.CloseApproved += (_, approval) => approvals.Add(approval);
        backend.OnClose = static () => { };

        InvokePerformClose(window, applicationShutdownIntent: true);
        backend.OnClose = () => RecordSimulatedApproval(window, 2);
        InvokePerformClose(window, applicationShutdownIntent: false);

        Assert.That(approvals, Is.EqualTo(new[] { new WindowCloseApproval(2, false) }));
    }

    [Test]
    public void NestedManualRequestCannotReplaceActiveShutdownDecision()
    {
        XRWindow window = NewWindowWithBackend(out CloseBackendProxy backend);
        List<WindowCloseApproval> approvals = [];
        int nativeCalls = 0;
        window.CloseApproved += (_, approval) => approvals.Add(approval);
        backend.OnClose = () =>
        {
            nativeCalls++;
            InvokePerformClose(window, applicationShutdownIntent: false);
            RecordSimulatedApproval(window, 3);
        };

        InvokePerformClose(window, applicationShutdownIntent: true);

        Assert.That(nativeCalls, Is.EqualTo(1));
        Assert.That(approvals, Is.EqualTo(new[] { new WindowCloseApproval(3, true) }));
    }

    [Test]
    public void DeferredCloseUsesLatestManualOrShutdownIntent()
    {
        XRWindow window = NewWindowWithBackend(out CloseBackendProxy backend);
        List<WindowCloseApproval> approvals = [];
        ulong sequence = 0;
        window.CloseApproved += (_, approval) => approvals.Add(approval);
        backend.OnClose = () => RecordSimulatedApproval(window, ++sequence);

        InvokeQueueDeferredClose(window, applicationShutdownIntent: false);
        InvokeQueueDeferredClose(window, applicationShutdownIntent: true);
        InvokeProcessDeferredClose(window);
        InvokeQueueDeferredClose(window, applicationShutdownIntent: true);
        InvokeQueueDeferredClose(window, applicationShutdownIntent: false);
        InvokeProcessDeferredClose(window);

        Assert.That(approvals, Is.EqualTo(new[]
        {
            new WindowCloseApproval(1, true),
            new WindowCloseApproval(2, false),
        }));
    }

    [Test]
    public void ApprovedCloseDropsNestedDeferredRequest()
    {
        XRWindow window = NewWindowWithBackend(out CloseBackendProxy backend);
        List<WindowCloseApproval> approvals = [];
        int nativeCalls = 0;
        window.CloseApproved += (_, approval) => approvals.Add(approval);
        backend.OnClose = () =>
        {
            nativeCalls++;
            if (nativeCalls == 1)
            {
                InvokeQueueDeferredClose(window, applicationShutdownIntent: false);
                InvokeProcessDeferredClose(window);
            }
            RecordSimulatedApproval(window, (ulong)nativeCalls);
        };

        InvokePerformClose(window, applicationShutdownIntent: true);
        Assert.That(nativeCalls, Is.EqualTo(1));
        InvokeProcessDeferredClose(window);
        Assert.That(nativeCalls, Is.EqualTo(1));
        Assert.That(approvals, Is.EqualTo(new[] { new WindowCloseApproval(1, true) }));
    }

    [Test]
    public void RejectedCloseKeepsNestedDeferredManualRequest()
    {
        XRWindow window = NewWindowWithBackend(out CloseBackendProxy backend);
        List<WindowCloseApproval> approvals = [];
        int nativeCalls = 0;
        window.CloseApproved += (_, approval) => approvals.Add(approval);
        backend.OnClose = () =>
        {
            nativeCalls++;
            if (nativeCalls == 1)
            {
                InvokeQueueDeferredClose(window, applicationShutdownIntent: false);
                InvokeProcessDeferredClose(window);
                return;
            }
            RecordSimulatedApproval(window, 2);
        };

        InvokePerformClose(window, applicationShutdownIntent: true);
        Assert.That(approvals, Is.Empty);
        InvokeProcessDeferredClose(window);
        Assert.That(approvals, Is.EqualTo(new[] { new WindowCloseApproval(2, false) }));
    }

    [Test]
    public void ApprovedCloseRejectsLaterQueuedNativeRequest()
    {
        XRWindow window = NewWindowWithBackend(out CloseBackendProxy backend);
        int nativeCalls = 0;
        backend.OnClose = () => nativeCalls++;
        typeof(XRWindow).GetField("_approvedNativeCloseInProgress",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);

        InvokePerformClose(window, applicationShutdownIntent: false);

        Assert.That(nativeCalls, Is.Zero);
    }

    [Test]
    public void ThrowingObserverCannotBlockLaterApprovedCloseObservers()
    {
        XRWindow window = (XRWindow)RuntimeHelpers.GetUninitializedObject(typeof(XRWindow));
        WindowCloseApproval approval = new(17, ApplicationShutdownIntent: true);
        int laterCalls = 0;
        window.CloseApproved += (_, _) => throw new InvalidOperationException("observer failed");
        window.CloseApproved += (_, received) =>
        {
            Assert.That(received, Is.EqualTo(approval));
            laterCalls++;
        };

        Assert.DoesNotThrow(() => typeof(XRWindow).GetMethod("PublishCloseApproval",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [approval]));
        Assert.That(laterCalls, Is.EqualTo(1));
    }

    private static XRWindow NewWindowWithBackend(out CloseBackendProxy backend)
    {
        XRWindow window = (XRWindow)RuntimeHelpers.GetUninitializedObject(typeof(XRWindow));
        IRuntimeWindowBackend proxy = DispatchProxy.Create<IRuntimeWindowBackend, CloseBackendProxy>();
        backend = (CloseBackendProxy)proxy;
        typeof(XRWindow).GetField("_desktopBackend",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, proxy);
        return window;
    }

    private static void RecordSimulatedApproval(XRWindow window, ulong sequence)
    {
        int intent = (int)typeof(XRWindow).GetField("_nativeCloseApplicationShutdownIntent",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        typeof(XRWindow).GetMethod("RecordApprovedNativeClose",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
            [new WindowCloseApproval(sequence, intent != 0)]);
    }

    private static void InvokePerformClose(XRWindow window, bool applicationShutdownIntent)
        => typeof(XRWindow).GetMethod("PerformCloseRequest",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
            [applicationShutdownIntent]);

    private static void InvokeQueueDeferredClose(XRWindow window, bool applicationShutdownIntent)
        => typeof(XRWindow).GetMethod("QueueDeferredCloseRequest",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
            [applicationShutdownIntent]);

    private static void InvokeProcessDeferredClose(XRWindow window)
        => typeof(XRWindow).GetMethod("ProcessDeferredCloseRequest",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

}
