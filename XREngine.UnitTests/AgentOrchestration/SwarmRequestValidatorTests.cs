using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class SwarmRequestValidatorTests
{
    [TestCase("gpt-6-luna")]
    [TestCase("claude-haiku-5-5")]
    public void AcceptsSupportedModelAtMaxEffort(string model)
        => Should.NotThrow(() => SwarmRequestValidator.Validate(Request(model, "max")));

    [TestCase("claude-haiku-5-5", "medium")]
    [TestCase("gpt-6-luna", "high")]
    [TestCase("claude-sonnet-5-5", "max")]
    [TestCase("claude-opus-5-5", "max")]
    [TestCase("claude-fable-5-1", "max")]
    [TestCase("claude-haiku-latest", "max")]
    public async Task RejectsUnsupportedModelOrEffortBeforeProviderExecution(string model, string effort)
    {
        AgentRunRequest request = Request(model, effort);
        Should.Throw<ArgumentException>(() => SwarmRequestValidator.Validate(request));
        var client = new ScriptedAgentModelClient();
        using var slots = new SemaphoreSlim(1, 1);
        var runner = new AgentSwarmRunner(new AgentOrchestrator(client), slots);

        AgentSwarmRunResult result = await runner.RunAsync("invalid-swarm", request,
            [new AgentContextFileSnapshot { Path = "Source.cs", Content = "sample", Sha256 = "hash" }]);

        result.Aggregate.Status.ShouldBe(AgentRunStatus.Failed);
        result.Aggregate.RequestedModel.ShouldBe(model);
        result.Aggregate.Failure!.Category.ShouldBe(AgentFailureCategory.Validation);
        client.Requests.ShouldBeEmpty();
    }

    private static AgentRunRequest Request(string model, string effort)
        => new()
        {
            Objective = "Propose one source change.",
            RequestedModel = model,
            ReasoningEffort = effort,
            Swarm = new AgentSwarmOptions { AllowedPaths = ["Source.cs"] },
        };
}
