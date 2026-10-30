using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Editor.Publishing;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class BrowserWorldCapabilityAuditTests
{
    [Test]
    public void Inspect_GaussianSplatComponent_ReportsShaderUnsupported()
    {
        SceneNode node = new("Splats");
        node.AddComponent<GaussianSplatComponent>().ShouldNotBeNull();

        AssertShaderUnsupported(node, typeof(GaussianSplatComponent), "Splats");
    }

    [Test]
    public void Inspect_GaussianCacheComponent_ReportsShaderUnsupported()
    {
        SceneNode node = new("AnimatedSplats");
        node.AddComponent<GaussianCacheComponent>().ShouldNotBeNull();

        AssertShaderUnsupported(node, typeof(GaussianCacheComponent), "AnimatedSplats");
    }

    private static void AssertShaderUnsupported(SceneNode node, Type componentType, string nodeName)
    {
        XRWorld world = new("SplatWorld", new XRScene("SplatScene", node));
        string intermediateDirectory = Path.Combine(Path.GetTempPath(), "xre-browser-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            using BrowserCapabilityReport report = new("/game/SplatWorld.world", intermediateDirectory, CancellationToken.None);
            BrowserWorldCapabilityAudit.Inspect(world, resolver: null, CancellationToken.None, report: report);

            BrowserCapabilityDiagnostic diagnostic = report.Diagnostics
                .Where(candidate => candidate.Component == componentType.FullName)
                .ShouldHaveSingleItem();
            diagnostic.Severity.ShouldBe("required");
            diagnostic.Code.ShouldBe("BrowserCook.ShaderUnsupported");
            diagnostic.NodePath.ShouldContain(nodeName);
            diagnostic.Reason.ShouldContain(componentType.Name);
            diagnostic.Reason.ShouldContain("WebGPU");
            report.HasRequiredFailures.ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(intermediateDirectory))
                Directory.Delete(intermediateDirectory, recursive: true);
        }
    }
}
