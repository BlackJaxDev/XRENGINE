using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeDockCloseApprovalGateTests
{
    [Test]
    public void VetoedShutdownThenApprovedManualCloseKeepsManualDecision()
    {
        object window = new();
        NativeDockCloseApprovalGate gate = new(window);
        WindowCloseApproval vetoed = new(1, ApplicationShutdownIntent: true);
        WindowCloseApproval manual = new(2, ApplicationShutdownIntent: false);

        gate.TryConsume(window, vetoed).ShouldBeFalse();
        gate.Register(window, manual).ShouldBeTrue();
        gate.TryConsume(window, vetoed).ShouldBeFalse();
        gate.TryConsume(window, manual).ShouldBeTrue();
    }

    [Test]
    public void LaterApprovedRequestInvalidatesEarlierQueuedClose()
    {
        object window = new();
        NativeDockCloseApprovalGate gate = new(window);
        WindowCloseApproval first = new(5, ApplicationShutdownIntent: true);
        WindowCloseApproval second = new(6, ApplicationShutdownIntent: false);

        gate.Register(window, first).ShouldBeTrue();
        gate.Register(window, second).ShouldBeTrue();
        gate.TryConsume(window, first).ShouldBeFalse();
        gate.TryConsume(window, second).ShouldBeTrue();
        gate.TryConsume(window, second).ShouldBeFalse();
    }

    [Test]
    public void ReplacedWindowCannotConsumeOldWindowApproval()
    {
        object firstWindow = new();
        object replacement = new();
        NativeDockCloseApprovalGate gate = new(firstWindow);
        WindowCloseApproval approved = new(9, ApplicationShutdownIntent: true);

        gate.Register(firstWindow, approved).ShouldBeTrue();
        gate.TryConsume(replacement, approved).ShouldBeFalse();
        gate.TryConsume(firstWindow, approved).ShouldBeTrue();
        gate.Register(replacement, new WindowCloseApproval(10, true)).ShouldBeFalse();
    }
}
