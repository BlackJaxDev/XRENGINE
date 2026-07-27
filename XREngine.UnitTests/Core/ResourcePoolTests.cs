using System.Threading;
using NUnit.Framework;
using XREngine.Core;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class ResourcePoolTests
{
    [Test]
    public void CapacityShrinkAndRelease_KeepExactRetainedObjectLimit()
    {
        int generatedCount = 0;
        int destroyedCount = 0;
        ResourcePool<TestPoolable> pool = new(
            initialCount: 2,
            generator: () => new TestPoolable(
                Interlocked.Increment(ref generatedCount),
                () => Interlocked.Increment(ref destroyedCount)),
            capacity: 2);

        TestPoolable first = pool.Take();
        TestPoolable second = pool.Take();
        pool.Release(first);
        pool.Release(second);

        pool.Capacity = 1;

        Assert.That(destroyedCount, Is.EqualTo(1));

        TestPoolable retained = pool.Take();
        TestPoolable generated = pool.Take();
        Assert.That(generatedCount, Is.EqualTo(3));
        Assert.That(retained.ResetCount, Is.EqualTo(2));
        Assert.That(generated.ResetCount, Is.EqualTo(1));

        pool.Release(retained);
        pool.Release(generated);

        Assert.That(destroyedCount, Is.EqualTo(2));
    }

    private sealed class TestPoolable(int id, Action onDestroyed) : IPoolable
    {
        private readonly Action _onDestroyed = onDestroyed;

        public int Id { get; } = id;
        public int ResetCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public void OnPoolableReset()
            => ++ResetCount;

        public void OnPoolableReleased()
            => ++ReleaseCount;

        public void OnPoolableDestroyed()
            => _onDestroyed();
    }
}
