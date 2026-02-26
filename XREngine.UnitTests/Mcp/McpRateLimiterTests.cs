using NUnit.Framework;
using Shouldly;
using System;
using XREngine.Editor.Mcp;

namespace XREngine.UnitTests.Mcp;

[TestFixture]
public class McpRateLimiterTests
{
    [Test]
    public void TryAcquire_BlocksAfterQuotaWithinWindow()
    {
        var limiter = new McpRateLimiter();
        var now = DateTimeOffset.UtcNow;

        limiter.TryAcquire("client-a", 2, TimeSpan.FromSeconds(60), now, out _).ShouldBeTrue();
        limiter.TryAcquire("client-a", 2, TimeSpan.FromSeconds(60), now.AddSeconds(1), out _).ShouldBeTrue();

        bool third = limiter.TryAcquire("client-a", 2, TimeSpan.FromSeconds(60), now.AddSeconds(2), out var retryAfter);
        third.ShouldBeFalse();
        retryAfter.TotalSeconds.ShouldBeGreaterThan(0);
    }

    [Test]
    public void TryAcquire_ResetsAfterWindowExpires()
    {
        var limiter = new McpRateLimiter();
        var now = DateTimeOffset.UtcNow;

        limiter.TryAcquire("client-b", 1, TimeSpan.FromSeconds(5), now, out _).ShouldBeTrue();
        limiter.TryAcquire("client-b", 1, TimeSpan.FromSeconds(5), now.AddSeconds(1), out _).ShouldBeFalse();

        limiter.TryAcquire("client-b", 1, TimeSpan.FromSeconds(5), now.AddSeconds(6), out _).ShouldBeTrue();
    }
}
