using NUnit.Framework;
using Shouldly;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
[NonParallelizable]
public class BrokerConfigurationTests
{
    private const string ConcurrencyVariable = "XRE_LOCAL_AGENT_BROKER_MAX_CONCURRENCY";
    private const string RetainedRunsVariable = "XRE_LOCAL_AGENT_BROKER_MAX_RUNS";
    private string _temporaryRoot = string.Empty;
    private string? _previousConcurrency;
    private string? _previousRetainedRuns;

    [SetUp]
    public void SetUp()
    {
        _previousConcurrency = Environment.GetEnvironmentVariable(ConcurrencyVariable);
        _previousRetainedRuns = Environment.GetEnvironmentVariable(RetainedRunsVariable);
        Environment.SetEnvironmentVariable(ConcurrencyVariable, null);
        Environment.SetEnvironmentVariable(RetainedRunsVariable, null);
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"xrengine-broker-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        File.WriteAllText(Path.Combine(_temporaryRoot, "AGENTS.md"), "# Test");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(ConcurrencyVariable, _previousConcurrency);
        Environment.SetEnvironmentVariable(RetainedRunsVariable, _previousRetainedRuns);
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    [TestCase(null, 4, 32)]
    [TestCase("1", 1, 32)]
    [TestCase("8", 8, 32)]
    [TestCase("20", 20, 32)]
    [TestCase("32", 32, 32)]
    [TestCase("33", 33, 33)]
    [TestCase("100", 100, 100)]
    public void ConcurrencyBoundsKeepDefaultAndIncreaseAdmissionCapacity(
        string? concurrency, int expectedConcurrency, int expectedCapacity)
    {
        Environment.SetEnvironmentVariable(ConcurrencyVariable, concurrency);
        BrokerConfiguration configuration = Parse();
        configuration.MaximumConcurrentRuns.ShouldBe(expectedConcurrency);
        configuration.MaximumRetainedRuns.ShouldBe(expectedCapacity);
    }

    [TestCase("0")]
    [TestCase("101")]
    [TestCase("not-an-integer")]
    public void InvalidConcurrencyIsRejectedBeforeLaunch(string concurrency)
    {
        Environment.SetEnvironmentVariable(ConcurrencyVariable, concurrency);
        Should.Throw<ArgumentException>(Parse).Message.ShouldContain("between 1 and 100");
    }

    [TestCase("4", "4")]
    [TestCase("20", "24")]
    [TestCase("100", "256")]
    public void ExplicitSufficientCapacityIsPreserved(string concurrency, string capacity)
    {
        Environment.SetEnvironmentVariable(ConcurrencyVariable, concurrency);
        Environment.SetEnvironmentVariable(RetainedRunsVariable, capacity);
        Parse().MaximumRetainedRuns.ShouldBe(int.Parse(capacity));
    }

    [TestCase("100", "32", "must be at least")]
    [TestCase("4", "3", "between 4 and 256")]
    [TestCase("100", "257", "between 4 and 256")]
    public void InvalidAdmissionCapacityIsRejectedBeforeLaunch(
        string concurrency, string capacity, string diagnostic)
    {
        Environment.SetEnvironmentVariable(ConcurrencyVariable, concurrency);
        Environment.SetEnvironmentVariable(RetainedRunsVariable, capacity);
        Should.Throw<ArgumentException>(Parse).Message.ShouldContain(diagnostic);
    }

    private BrokerConfiguration Parse()
        => BrokerConfiguration.Parse(["--repo-root", _temporaryRoot]);
}
