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
        XRWorld world = new("SplatWorld", new XRScene("SplatScene", node));
        string intermediateDirectory = Path.Combine(Path.GetTempPath(), "xre-browser-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            using BrowserCapabilityReport report = new("/game/SplatWorld.world", intermediateDirectory, CancellationToken.None);
            BrowserWorldCapabilityAudit.Inspect(world, resolver: null, CancellationToken.None, report: report);

            BrowserCapabilityDiagnostic diagnostic = report.Diagnostics
                .Where(static candidate => candidate.Component == typeof(GaussianSplatComponent).FullName)
                .ShouldHaveSingleItem();
            diagnostic.Severity.ShouldBe("required");
            diagnostic.Code.ShouldBe("BrowserCook.ShaderUnsupported");
            diagnostic.NodePath.ShouldContain("Splats");
            diagnostic.Reason.ShouldContain(nameof(GaussianSplatComponent));
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
