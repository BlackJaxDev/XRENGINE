using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIWorldStencilMaskTests
{
    [Test]
    public void OnlyTheAttachmentBitsMustBeWritable()
    {
        UIWorldStencilMask.CoversAttachment(0xff, 8).ShouldBeTrue();
        UIWorldStencilMask.CoversAttachment(unchecked((int)0xffffffff), 8).ShouldBeTrue();
        UIWorldStencilMask.CoversAttachment(0xfe, 8).ShouldBeFalse();
        UIWorldStencilMask.CoversAttachment(-1, 0).ShouldBeFalse();
        UIWorldStencilMask.CoversAttachment(-1, -1).ShouldBeFalse();
        UIWorldStencilMask.CoversAttachment(-1, 33).ShouldBeFalse();
    }
}
