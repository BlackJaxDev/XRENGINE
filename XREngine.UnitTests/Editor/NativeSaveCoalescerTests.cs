using System.Diagnostics;
using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeSaveCoalescerTests
{
    private static readonly long Second = Stopwatch.Frequency;

    [Test]
    public void RepeatedRequestsSaveOnceAfterTheLastRequestSettles()
    {
        NativeSaveCoalescer saves = new(TimeSpan.FromSeconds(1));

        saves.Request(0);
        saves.Request(Second / 2);

        Assert.Multiple(() =>
        {
            Assert.That(saves.TryTakeSettled(Second), Is.False);
            Assert.That(saves.IsPending, Is.True);
            Assert.That(saves.TryTakeSettled(Second + Second / 2), Is.True);
            Assert.That(saves.IsPending, Is.False);
            Assert.That(saves.TryTakeSettled(10 * Second), Is.False);
        });
    }

    [Test]
    public void ImmediateTakeClearsTheRequestOnce()
    {
        NativeSaveCoalescer saves = new(TimeSpan.FromSeconds(1));

        Assert.That(saves.TryTakePending(), Is.False);
        saves.Request(0);

        Assert.Multiple(() =>
        {
            Assert.That(saves.TryTakePending(), Is.True);
            Assert.That(saves.TryTakePending(), Is.False);
            Assert.That(saves.TryTakeSettled(10 * Second), Is.False);
        });
    }
}
