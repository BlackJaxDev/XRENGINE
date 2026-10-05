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

        mainRead.BindForReading();
        mainWrite.BindForWriting();
        mainBind.Bind();

        try
        {
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

                    workerRead.BindForReading();
                    workerWrite.BindForWriting();
                    workerBind.Bind();

                    try
                    {
                        XRFrameBuffer.BoundForReading.ShouldBeSameAs(workerBind);
                        XRFrameBuffer.BoundForWriting.ShouldBeSameAs(workerBind);
                        XRFrameBuffer.CurrentlyBound.ShouldBeSameAs(workerBind);
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
        }
        finally
        {
            mainBind.Unbind();
            XRFrameBuffer.BoundForReading.ShouldBeSameAs(mainRead);
            XRFrameBuffer.BoundForWriting.ShouldBeSameAs(mainWrite);
            mainWrite.UnbindFromWriting();
            mainRead.UnbindFromReading();
        }
    }
}
