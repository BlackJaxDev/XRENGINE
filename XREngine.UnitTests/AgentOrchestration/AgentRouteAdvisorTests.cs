using NUnit.Framework;
using Shouldly;
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
    public void CatalogSupportsSixExactModelsAndModelSpecificEffortRules()
    {
        AgentModelCatalog.Models.Count.ShouldBe(6);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Luna);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Terra);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Sol);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Astra6);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Luna6);
        AgentModelCatalog.Models.ShouldContain(AgentModelCatalog.Sol6);
        AgentModelCatalog.IsApproved("gpt-6").ShouldBeFalse();
        AgentModelCatalog.IsDeprecated(AgentModelCatalog.Sol).ShouldBeTrue();
        AgentModelCatalog.IsDeprecated(AgentModelCatalog.Sol6).ShouldBeFalse();
        AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Astra6, "none").ShouldBeFalse();
        AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Astra6, "max").ShouldBeTrue();
        AgentModelCatalog.SupportsReasoningEffort(AgentModelCatalog.Luna6, "none").ShouldBeTrue();
    }
}
