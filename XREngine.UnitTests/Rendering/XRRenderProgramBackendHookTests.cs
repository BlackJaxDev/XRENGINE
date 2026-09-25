using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Backend wrappers subscribe to program hooks for one renderer generation. Unsubscription must
/// take effect immediately so a retired wrapper is not retained by a program that is never used
/// or linked again.
/// </summary>
[TestFixture]
public sealed class XRRenderProgramBackendHookTests
{
    [Test]
    public void UseRequested_RaisesSubscribedHandler()
    {
        XRRenderProgram program = new();
        XRRenderProgram? observed = null;
        program.UseRequested += p => observed = p;

        program.Use();

        observed.ShouldBeSameAs(program);
    }

    [Test]
    public void UseRequested_UnsubscribedHandler_IsNotRaised()
    {
        XRRenderProgram program = new();
        int calls = 0;
        Action<XRRenderProgram> handler = _ => calls++;
        program.UseRequested += handler;
        program.UseRequested -= handler;

        program.Use();

        calls.ShouldBe(0);
    }

    [Test]
    public void LinkRequested_UnsubscribedHandler_IsNotRaised()
    {
        XRRenderProgram program = new();
        int calls = 0;
        Action<XRRenderProgram> handler = _ => calls++;
        program.LinkRequested += handler;
        program.LinkRequested -= handler;

        program.Link();

        calls.ShouldBe(0);
        program.LinkReady.ShouldBeTrue();
    }
}
