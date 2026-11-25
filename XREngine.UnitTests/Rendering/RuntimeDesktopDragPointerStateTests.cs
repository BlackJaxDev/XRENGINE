using System.Numerics;
using NUnit.Framework;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop.Windowing;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeDesktopDragPointerStateTests
{
    private static readonly WindowSurfaceSnapshot Surface = new(4, 800, 600, 1600, 1200,
        2, 2, false, false, 20);
    private static readonly Vector2 ClientOrigin = new(100, 200);
    private static readonly Vector2 TargetOrigin = new(900, 300);
    private static readonly Vector2 TargetSize = new(500, 400);
    private static readonly nint Source = 101;
    private static readonly nint Target = 202;

    [Test]
    public void ReleaseRetainsExactMessagePointTargetAndGeometry()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong press = state.BeginPress(21, new(110, 220), Source, Surface, ClientOrigin);
        state.ConfirmCapture(press, Source, 22, Surface, ClientOrigin);

        state.Release(23, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);
        RuntimeDesktopDragPointerSnapshot snapshot = state.Snapshot;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.BackendLifetime, Is.EqualTo(77));
            Assert.That(snapshot.SourceWindowHandle, Is.EqualTo(Source));
            Assert.That(snapshot.PressSequence, Is.EqualTo(press));
            Assert.That(snapshot.LeftButtonDown, Is.False);
            Assert.That(snapshot.Surface, Is.EqualTo(Surface));
            Assert.That(snapshot.ClientScreenPosition, Is.EqualTo(ClientOrigin));
            Assert.That(snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.Released));
            Assert.That(snapshot.Terminal.ScreenPosition, Is.EqualTo(new Vector2(945, 350)));
            Assert.That(snapshot.Terminal.TargetWindowHandle, Is.EqualTo(Target));
            Assert.That(snapshot.Terminal.TargetClientScreenPosition, Is.EqualTo(TargetOrigin));
            Assert.That(snapshot.Terminal.TargetClientSize, Is.EqualTo(TargetSize));
            Assert.That(snapshot.Terminal.TimestampTicks, Is.EqualTo(23));
        });
    }

    [Test]
    public void CaptureLossBeforeReleaseKeepsCancellationTerminal()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong press = state.BeginPress(21, default, Source, Surface, ClientOrigin);
        state.ConfirmCapture(press, Source, 22, Surface, ClientOrigin);
        state.Cancel(23, 0, Surface, ClientOrigin);
        state.Release(24, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);

        Assert.That(state.Snapshot.Terminal.PressSequence, Is.EqualTo(press));
        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.Cancelled));
        Assert.That(state.Snapshot.Terminal.TimestampTicks, Is.EqualTo(23));
    }

    [Test]
    public void PollingButtonUpCancelsAndNeverCreatesARelease()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong press = state.BeginPress(21, default, Source, Surface, ClientOrigin);
        state.ConfirmCapture(press, Source, 22, Surface, ClientOrigin);
        state.Observe(23, new(940, 345), Target, Source, false, Surface, ClientOrigin);
        state.Release(24, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);

        Assert.That(state.Snapshot.Terminal.PressSequence, Is.EqualTo(press));
        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.Cancelled));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReleaseWithoutSourceCaptureOrValidTargetCancels(bool validTarget)
    {
        DesktopDragPointerState state = new(77, Source);
        ulong press = state.BeginPress(21, default, Source, Surface, ClientOrigin);
        state.ConfirmCapture(press, validTarget ? Source : 0, 22, Surface, ClientOrigin);
        state.Release(23, new(945, 350), Target, Source, true,
            TargetOrigin, validTarget ? default : TargetSize, Surface, ClientOrigin);

        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.Cancelled));
    }

    [Test]
    public void NewPressKeepsOldTerminalUntilItsOwnCompletion()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong first = state.BeginPress(21, default, Source, Surface, ClientOrigin);
        state.ConfirmCapture(first, Source, 22, Surface, ClientOrigin);
        state.Release(23, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);
        ulong second = state.BeginPress(24, default, Source, Surface, ClientOrigin);

        Assert.That(second, Is.GreaterThan(first));
        Assert.That(state.Snapshot.Terminal.PressSequence, Is.EqualTo(first));
        state.ConfirmCapture(second, Source, 25, Surface, ClientOrigin);
        state.Release(26, new(950, 355), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);
        Assert.That(state.Snapshot.Terminal.PressSequence, Is.EqualTo(second));
    }

    [Test]
    public void CaptureChangeAfterReleaseDoesNotReplaceIt()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong press = state.BeginPress(21, default, Source, Surface, ClientOrigin);
        state.ConfirmCapture(press, Source, 22, Surface, ClientOrigin);
        state.Release(23, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);
        state.Cancel(24, 0, Surface, ClientOrigin);

        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.Released));
        Assert.That(state.Snapshot.Terminal.TimestampTicks, Is.EqualTo(23));
    }

    [Test]
    public void SourceInvalidationCancelsACompletedReceipt()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong press = state.BeginPress(21, default, Source, Surface, ClientOrigin);
        state.ConfirmCapture(press, Source, 22, Surface, ClientOrigin);
        state.Release(23, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);
        state.Invalidate(24);

        Assert.That(state.Snapshot.SourceWindowHandle, Is.EqualTo((nint)0));
        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.Cancelled));
    }

    [Test]
    public void InvalidatedSourceCannotAdmitAZeroCapturePress()
    {
        DesktopDragPointerState state = new(77, Source);
        state.Invalidate(21);
        ulong press = state.BeginPress(22, default, 0, Surface, ClientOrigin);
        state.ConfirmCapture(press, 0, 23, Surface, ClientOrigin);
        state.Release(24, default, Target, 0, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin);

        Assert.That(press, Is.Zero);
        Assert.That(state.Snapshot.LeftButtonDown, Is.False);
        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.None));
    }

    [TestCase(false, 0x01)]
    [TestCase(true, 0x02)]
    public void LogicalLeftPollingUsesTheConfiguredPhysicalButton(bool swapped, int expectedKey)
    {
        Assert.That(DesktopDragPointerState.LogicalLeftVirtualKey(swapped), Is.EqualTo(expectedKey));
    }

    [Test]
    public void NestedNewPressRejectsOldReleaseAndHover()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong oldPress = state.BeginPress(21, default, 0, Surface, ClientOrigin);
        state.ConfirmCapture(oldPress, Source, 22, Surface, ClientOrigin);
        DesktopDragPointerObservation old = state.CurrentObservation;
        ulong newPress = state.BeginPress(23, new(10, 20), 0, Surface, ClientOrigin);
        state.ConfirmCapture(newPress, Source, 24, Surface, ClientOrigin);

        state.Observe(25, new(945, 350), Target, Source, true,
            Surface, ClientOrigin, old);
        state.Release(26, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin, old);

        Assert.That(state.Snapshot.PressSequence, Is.EqualTo(newPress));
        Assert.That(state.Snapshot.LeftButtonDown, Is.True);
        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.None));
        Assert.That(state.Snapshot.ScreenPosition, Is.EqualTo(new Vector2(10, 20)));
    }

    [Test]
    public void NestedCancellationRejectsOldReleaseAndCancellation()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong press = state.BeginPress(21, default, 0, Surface, ClientOrigin);
        state.ConfirmCapture(press, Source, 22, Surface, ClientOrigin);
        DesktopDragPointerObservation old = state.CurrentObservation;
        state.Cancel(23, 0, Surface, ClientOrigin);

        state.Release(24, new(945, 350), Target, Source, true,
            TargetOrigin, TargetSize, Surface, ClientOrigin, old);
        state.Cancel(25, Source, Surface, ClientOrigin, old);

        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.Cancelled));
        Assert.That(state.Snapshot.Terminal.TimestampTicks, Is.EqualTo(23));
        Assert.That(state.Snapshot.TimestampTicks, Is.EqualTo(23));
    }

    [Test]
    public void RestoredSourceDoesNotReuseAnOldPressSequence()
    {
        DesktopDragPointerState state = new(77, Source);
        ulong oldPress = state.BeginPress(21, default, Source, Surface, ClientOrigin);
        state.Invalidate(22);
        state.Restore(Source, Surface, ClientOrigin);

        Assert.That(state.Snapshot.LeftButtonDown, Is.False);
        Assert.That(state.Snapshot.Terminal.Kind, Is.EqualTo(RuntimeDesktopDragTerminalKind.None));
        Assert.That(state.BeginPress(23, default, Source, Surface, ClientOrigin),
            Is.GreaterThan(oldPress));
    }

    [Test]
    public void SignedMessageCoordinatesStaySigned()
    {
        long packed = ((long)unchecked((ushort)-42) << 16) | unchecked((ushort)-27);
        Vector2 point = DesktopDragPointerState.DecodeSignedClientPoint((nint)packed);
        Assert.That(point, Is.EqualTo(new Vector2(-27, -42)));
    }
}
