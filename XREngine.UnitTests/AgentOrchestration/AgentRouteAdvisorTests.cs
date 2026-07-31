using NUnit.Framework;
using Shouldly;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class AgentRouteAdvisorTests
{
    [TestCase("Inventory all markdown files", AgentModelCatalog.Luna)]
    [TestCase("Implement an ordinary editor dialog", AgentModelCatalog.Terra)]
    [TestCase("Diagnose a subtle Vulkan GPU race", AgentModelCatalog.Sol)]
    public void RecommendsRepositoryTiersWithoutLaunching(string objective, string expectedModel)
    {
        AgentRouteRecommendation recommendation = AgentRouteAdvisor.Recommend(objective);

        recommendation.RecommendedModel.ShouldBe(expectedModel);
        recommendation.RequiresExplicitCallerAuthorization.ShouldBeTrue();
    }

    [Test]
    public void CatalogRejectsAliasesAndUnknownModels()
    {
        AgentModelCatalog.IsApproved("gpt-5.6").ShouldBeFalse();
        AgentModelCatalog.IsApproved("gpt-5.6-sol").ShouldBeTrue();
        AgentModelCatalog.Models.Count.ShouldBe(3);
    }
}
