using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class NativeDockDropTargetCleanupTests
{
    [Test]
    public void DisposalAttemptsEveryCapturedTargetAndPreservesReentrantNewRegistration()
    {
        List<IDisposable> registrations = [];
        NativePanelCallbackDropHandle later = new(() => { });
        NativePanelCallbackDropHandle first = new(() => throw new InvalidOperationException("first"));
        NativePanelCallbackDropHandle second = new(() => registrations.Add(later));
        NativePanelCallbackDropHandle third = new(() => throw new InvalidOperationException("third"));
        registrations.AddRange([first, second, third]);

        AggregateException? failure = Assert.Throws<AggregateException>(
            () => NativeDockTreeHost.DisposeDropRegistrations(registrations));
        Assert.That(failure!.InnerExceptions.Count, Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(first.WasDisposed, Is.True);
            Assert.That(second.WasDisposed, Is.True);
            Assert.That(third.WasDisposed, Is.True);
            Assert.That(registrations, Is.EqualTo(new IDisposable[] { later }));
        });
        NativeDockTreeHost.DisposeDropRegistrations(registrations);
        Assert.That(later.WasDisposed, Is.True);
        Assert.That(registrations, Is.Empty);
    }
}
