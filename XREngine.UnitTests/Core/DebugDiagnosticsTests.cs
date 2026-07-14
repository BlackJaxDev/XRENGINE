using System.Reflection;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class DebugDiagnosticsTests
{
    [Test]
    public void ExpectedHttpListenerRequestQueueTeardown_IsNotTracedAsAnEngineException()
    {
        MethodInfo shouldTrace = typeof(XREngine.Debug).GetMethod(
            "ShouldTraceFirstChanceException",
            BindingFlags.NonPublic | BindingFlags.Static).ShouldNotBeNull();
        ObjectDisposedException expectedRuntimeException = new("System.Net.HttpRequestQueueV2Handle");

        bool result = (bool)shouldTrace.Invoke(null, [expectedRuntimeException]).ShouldNotBeNull();

        result.ShouldBeFalse();
    }
}
