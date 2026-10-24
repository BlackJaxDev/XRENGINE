using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerOutputPageTests
{
    [Test]
    public void AllOutputsRemainReachableWithoutUnboundedPageSize()
    {
        const int total = 257;
        int visited = 0;
        int pages = NativeProfilerOutputPage.Resolve(total, 0).PageCount;
        pages.ShouldBe(17);
        for (int requested = 0; requested < pages; requested++)
        {
            NativeProfilerOutputPage page = NativeProfilerOutputPage.Resolve(total, requested);
            page.Start.ShouldBe(visited);
            page.Count.ShouldBeLessThanOrEqualTo(NativeProfilerOutputPage.Size);
            visited += page.Count;
        }
        visited.ShouldBe(total);
        NativeProfilerOutputPage.Resolve(total, int.MaxValue).Index.ShouldBe(pages - 1);
        NativeProfilerOutputPage.Resolve(0, int.MaxValue).ShouldBe(new NativeProfilerOutputPage(0, 0, 0, 0, 0));
    }
}
