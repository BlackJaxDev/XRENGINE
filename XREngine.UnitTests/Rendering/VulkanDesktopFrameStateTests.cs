using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDesktopFrameStateTests
{
    [Test]
    public void ActivityState_PublishesImmutableFrameAndSlotUntilMatchingExit()
    {
        DesktopFrameActivityState state = new();

        state.TryEnter(17, 0, out long publicationToken).ShouldBeTrue();
        state.TryEnter(18, 1, out _).ShouldBeFalse();

        DesktopFrameActivitySnapshot active = state.Capture();
        active.IsActive.ShouldBeTrue();
        active.FrameNumber.ShouldBe(17UL);
        active.FrameSlot.ShouldBe(0);

        state.TryExit(publicationToken + 1).ShouldBeFalse();
        state.Capture().ShouldBe(active);
        state.TryExit(publicationToken).ShouldBeTrue();
        state.Capture().ShouldBe(new DesktopFrameActivitySnapshot(false, 0, -1));
    }

    [Test]
    public void ActivityState_PublishesOneCoherentWinnerUnderContention()
    {
        const int contenderCount = 16;
        DesktopFrameActivityState state = new();
        using ManualResetEventSlim start = new(initialState: false);
        Task[] contenders = new Task[contenderCount];
        int successCount = 0;
        long winningFrameNumber = 0;
        int winningFrameSlot = -1;
        long winningPublicationToken = 0;

        for (int i = 0; i < contenders.Length; i++)
        {
            int contenderIndex = i;
            contenders[i] = Task.Run(() =>
            {
                start.Wait();
                ulong frameNumber = unchecked((ulong)contenderIndex + 1UL);
                int frameSlot = contenderIndex % 2;
                if (!state.TryEnter(frameNumber, frameSlot, out long publicationToken))
                    return;

                Volatile.Write(ref winningFrameNumber, unchecked((long)frameNumber));
                Volatile.Write(ref winningFrameSlot, frameSlot);
                Volatile.Write(ref winningPublicationToken, publicationToken);
                Interlocked.Increment(ref successCount);
            });
        }

        start.Set();
        Task.WaitAll(contenders);

        successCount.ShouldBe(1);
        DesktopFrameActivitySnapshot active = state.Capture();
        active.IsActive.ShouldBeTrue();
        active.FrameNumber.ShouldBe(unchecked((ulong)Volatile.Read(ref winningFrameNumber)));
        active.FrameSlot.ShouldBe(Volatile.Read(ref winningFrameSlot));
        state.TryExit(Volatile.Read(ref winningPublicationToken)).ShouldBeTrue();
    }
}
