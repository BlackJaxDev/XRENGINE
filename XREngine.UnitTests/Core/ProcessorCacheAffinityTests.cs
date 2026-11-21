using NUnit.Framework;
using Shouldly;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class ProcessorCacheAffinityTests
{
    private const uint Mb = 1024u * 1024u;

    [Test]
    public void Select_UnequalL3Caches_UsesTheLargestCacheCores()
    {
        ProcessorCacheGroup[] caches =
        [
            new(0, 0x0000FFFFUL, 96 * Mb),
            new(0, 0xFFFF0000UL, 32 * Mb),
        ];

        ProcessorCacheAffinitySelection selection = ProcessorCacheAffinity.Select(caches);

        selection.Applies.ShouldBeTrue();
        selection.Group.ShouldBe((ushort)0);
        selection.Mask.ShouldBe(0x0000FFFFUL);
        selection.LogicalProcessorCount.ShouldBe(16);
    }

    [Test]
    public void Select_EqualL3Caches_DoesNotRestrict()
    {
        ProcessorCacheGroup[] caches =
        [
            new(0, 0x0000FFFFUL, 32 * Mb),
            new(0, 0xFFFF0000UL, 32 * Mb),
        ];

        ProcessorCacheAffinity.Select(caches).Applies.ShouldBeFalse();
    }

    [Test]
    public void Select_OneL3Cache_DoesNotRestrict()
        => ProcessorCacheAffinity.Select([new ProcessorCacheGroup(0, 0xFFUL, 36 * Mb)]).Applies.ShouldBeFalse();

    [Test]
    public void Select_SeveralLargestCaches_CombinesTheirCores()
    {
        ProcessorCacheGroup[] caches =
        [
            new(0, 0x000000FFUL, 64 * Mb),
            new(0, 0x0000FF00UL, 64 * Mb),
            new(0, 0x00FF0000UL, 32 * Mb),
        ];

        ProcessorCacheAffinitySelection selection = ProcessorCacheAffinity.Select(caches);

        selection.Applies.ShouldBeTrue();
        selection.Mask.ShouldBe(0x0000FFFFUL);
    }

    [Test]
    public void Select_LargestCachesInTwoProcessorGroups_DoesNotRestrict()
    {
        ProcessorCacheGroup[] caches =
        [
            new(0, 0x000000FFUL, 64 * Mb),
            new(1, 0x000000FFUL, 64 * Mb),
            new(0, 0x0000FF00UL, 32 * Mb),
        ];

        ProcessorCacheAffinity.Select(caches).Applies.ShouldBeFalse();
    }

    [Test]
    public void Select_LargestCacheWithOneProcessor_DoesNotRestrict()
    {
        ProcessorCacheGroup[] caches =
        [
            new(0, 0x1UL, 64 * Mb),
            new(0, 0xFEUL, 32 * Mb),
        ];

        ProcessorCacheAffinity.Select(caches).Applies.ShouldBeFalse();
    }
}
