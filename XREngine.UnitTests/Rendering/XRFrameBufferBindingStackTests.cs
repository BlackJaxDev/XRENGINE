using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class XRFrameBufferBindingStackTests
{
    [Test]
    public void BoundFrameBuffers_AreThreadLocal()
    {
        XRFrameBuffer mainRead = new();
        XRFrameBuffer mainWrite = new();
        XRFrameBuffer mainBind = new();
        XRFrameBuffer workerRead = new();
        XRFrameBuffer workerWrite = new();
        XRFrameBuffer workerBind = new();

        try
        {
            mainRead.BindForReading();
            mainWrite.BindForWriting();
            mainBind.Bind();

            XRFrameBuffer.BoundForReading.ShouldBeSameAs(mainBind);
            XRFrameBuffer.BoundForWriting.ShouldBeSameAs(mainBind);
            XRFrameBuffer.CurrentlyBound.ShouldBeSameAs(mainBind);

            Exception? workerException = null;
            Thread workerThread = new(() =>
            {
                try
                {
                    XRFrameBuffer.BoundForReading.ShouldBeNull();
                    XRFrameBuffer.BoundForWriting.ShouldBeNull();
                    XRFrameBuffer.CurrentlyBound.ShouldBeNull();

                    try
                    {
                        workerRead.BindForReading();
                        workerWrite.BindForWriting();
                        workerBind.Bind();

                        XRFrameBuffer.BoundForReading.ShouldBeSameAs(workerBind);
                        XRFrameBuffer.BoundForWriting.ShouldBeSameAs(workerBind);
                        XRFrameBuffer.CurrentlyBound.ShouldBeSameAs(workerBind);

                        workerBind.Unbind();
                        XRFrameBuffer.BoundForReading.ShouldBeSameAs(workerRead);
                        XRFrameBuffer.BoundForWriting.ShouldBeSameAs(workerWrite);
                        XRFrameBuffer.CurrentlyBound.ShouldBeNull();
                    }
                    finally
                    {
                        workerBind.Unbind();
                        XRFrameBuffer.BoundForReading.ShouldBeSameAs(workerRead);
                        XRFrameBuffer.BoundForWriting.ShouldBeSameAs(workerWrite);
                        workerWrite.UnbindFromWriting();
                        workerRead.UnbindFromReading();
                    }

                    XRFrameBuffer.BoundForReading.ShouldBeNull();
                    XRFrameBuffer.BoundForWriting.ShouldBeNull();
                    XRFrameBuffer.CurrentlyBound.ShouldBeNull();
                }
                catch (Exception ex)
                {
                    workerException = ex;
                }
            });

            workerThread.Start();
            workerThread.Join();
            if (workerException is not null)
                throw workerException;

            XRFrameBuffer.BoundForReading.ShouldBeSameAs(mainBind);
            XRFrameBuffer.BoundForWriting.ShouldBeSameAs(mainBind);
            XRFrameBuffer.CurrentlyBound.ShouldBeSameAs(mainBind);

            mainBind.Unbind();
            XRFrameBuffer.BoundForReading.ShouldBeSameAs(mainRead);
            XRFrameBuffer.BoundForWriting.ShouldBeSameAs(mainWrite);
            XRFrameBuffer.CurrentlyBound.ShouldBeNull();
        }
        finally
        {
            mainBind.Unbind();
            XRFrameBuffer.BoundForReading.ShouldBeSameAs(mainRead);
            XRFrameBuffer.BoundForWriting.ShouldBeSameAs(mainWrite);
            mainWrite.UnbindFromWriting();
            mainRead.UnbindFromReading();
            workerBind.Destroy(now: true);
            workerWrite.Destroy(now: true);
            workerRead.Destroy(now: true);
            mainBind.Destroy(now: true);
            mainWrite.Destroy(now: true);
            mainRead.Destroy(now: true);
        }

        XRFrameBuffer.BoundForReading.ShouldBeNull();
        XRFrameBuffer.BoundForWriting.ShouldBeNull();
        XRFrameBuffer.CurrentlyBound.ShouldBeNull();
    }
}
