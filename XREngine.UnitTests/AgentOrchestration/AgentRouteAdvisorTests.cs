using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class AgentRouteAdvisorTests
{
    [TestCase("Inventory all markdown files", AgentModelCatalog.Luna6)]
    [TestCase("Implement an ordinary editor dialog", AgentModelCatalog.Sol6)]
    [TestCase("Diagnose a subtle Vulkan GPU race", AgentModelCatalog.Astra6)]
    public void RecommendsRepositoryTiersWithoutLaunching(string objective, string expectedModel)
    {
        AgentRouteRecommendation recommendation = AgentRouteAdvisor.Recommend(objective);

        recommendation.RecommendedModel.ShouldBe(expectedModel);
        recommendation.ModelFamily.ShouldBe(AgentModelCatalog.Gpt6Family);
        recommendation.DeprecatedModel.ShouldBeFalse();
        recommendation.RequiresExplicitCallerAuthorization.ShouldBeTrue();
    }

    [Test]
    public void ExplicitGpt56RouteIsMarkedDeprecated()
    {
        AgentRouteRecommendation recommendation = AgentRouteAdvisor.Recommend(
            "Implement an ordinary editor dialog",
            modelFamily: AgentModelCatalog.Gpt56Family);

        recommendation.RecommendedModel.ShouldBe(AgentModelCatalog.Terra);
        recommendation.DeprecatedModel.ShouldBeTrue();
    }

    [Test]
    public void CatalogSupportsElevenExactModelsAndModelSpecificEffortRules()
    {
        AgentModelCatalog.Models.Count.ShouldBe(11);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Luna);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Terra);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Sol);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Astra6);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Luna6);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Sol6);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Sol61);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.ClaudeFable51);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.ClaudeOpus55);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.ClaudeSonnet55);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.ClaudeHaiku55);
        AgentModelCatalog.IsApproved(AgentModelCatalog.Sol61).ShouldBeTrue();
        AgentModelCatalog.SupportsResponseControls(AgentModelCatalog.Sol61).ShouldBeTrue();
        AgentModelCatalog.IsApproved("GPT-6.1-SOL").ShouldBeFalse();
        AgentModelCatalog.IsApproved("gpt-6.1-sol-2026-10-01").ShouldBeFalse();
        AgentModelCatalog.IsApproved("gpt-6").ShouldBeFalse();
        AgentModelCatalog.IsDeprecated(AgentModelCatalog.Sol).ShouldBeTrue();
        AgentModelCatalog.IsDeprecated(AgentModelCatalog.Sol6).ShouldBeFalse();
        AgentModelCatalog.IsDeprecated(AgentModelCatalog.Sol61).ShouldBeFalse();
        AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Astra6, "none").ShouldBeFalse();
        AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Astra6, "max").ShouldBeTrue();
        AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Luna6, "none").ShouldBeTrue();
        AgentModelCatalog.IsApproved("CLAUDE-OPUS-5-5").ShouldBeFalse();
        AgentModelCatalog.IsDeprecated(AgentModelCatalog.ClaudeFable51).ShouldBeFalse();
    }

    [TestCase("low")]
    [TestCase("medium")]
    [TestCase("high")]
    [TestCase("xhigh")]
    [TestCase("max")]
    public void Gpt61SolAcceptsSupportedReasoningEfforts(string effort)
        => AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Sol61, effort).ShouldBeTrue();

    [TestCase("none")]
    [TestCase("minimal")]
    public void Gpt61SolRejectsUnsupportedReasoningEfforts(string effort)
        => AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Sol61, effort).ShouldBeFalse();

    [TestCase(AgentModelCatalog.ClaudeFable51, "low")]
    [TestCase(AgentModelCatalog.ClaudeOpus55, "medium")]
    [TestCase(AgentModelCatalog.ClaudeSonnet55, "xhigh")]
    [TestCase(AgentModelCatalog.ClaudeHaiku55, "max")]
    public void ClaudeModelsAcceptSupportedReasoningEfforts(string model, string effort)
        => AgentModelCatalog.SupportsReasoningEffort(model, effort).ShouldBeTrue();

    [TestCase(AgentModelCatalog.ClaudeFable51, "none")]
    [TestCase(AgentModelCatalog.ClaudeOpus55, "minimal")]
    [TestCase(AgentModelCatalog.ClaudeSonnet55, "none")]
    [TestCase(AgentModelCatalog.ClaudeHaiku55, "minimal")]
    public void ClaudeModelsRejectUnsupportedReasoningEfforts(string model, string effort)
        => AgentModelCatalog.SupportsReasoningEffort(model, effort).ShouldBeFalse();

    [Test]
    public void StartSchemaAdvertisesEveryApprovedExactModel()
    {
        McpToolSpec startTool = BrokerMcpToolCatalog.Tools.Single(tool => tool.Name == "start_agent_run");
        string[] advertisedModels = startTool.InputSchema["properties"]!["requested_model"]!["enum"]!
            .AsArray().Select(model => model!.GetValue<string>()).ToArray();

        advertisedModels.ShouldBe(AgentModelCatalog.Models);
    }

    [Test]
    public void Gpt61SolCannotSelectHierarchicalSwarm()
    {
        var request = new AgentRunRequest
        {
            Objective = "Propose one source change.",
            RequestedModel = AgentModelCatalog.Sol61,
            ReasoningEffort = "max",
            Swarm = new AgentSwarmOptions { AllowedPaths = ["Source.cs"] },
        };

        Should.Throw<ArgumentException>(() => SwarmRequestValidator.Validate(request))
            .Message.ShouldContain("gpt-6-luna");
    }
}
