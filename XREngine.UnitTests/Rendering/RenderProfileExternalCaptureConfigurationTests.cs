using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Profiling;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderProfileExternalCaptureConfigurationTests
{
    [Test]
    public void RecipeParser_AcceptsExternalCaptureAndMarksItIntrusive()
    {
        RenderProfileRecipe recipe = RenderProfileRecipe.Parse("""
            {
              "name": "capture-diagnostic",
              "component": "HarnessSubmission",
              "fixture": "noop-control",
              "profile_mode": "diagnostics",
              "external_capture": {
                "tool_identity": "RenderDoc",
                "artifact_paths": ["renderdoc/capture.rdc"],
                "require_artifacts": true
              }
            }
            """);

        recipe.ExternalCapture.ToolIdentity.ShouldBe("RenderDoc");
        recipe.ExternalCapture.RequireArtifacts.ShouldBeTrue();
        recipe.IsIntrusive.ShouldBeTrue();
    }

    [Test]
    public void CleanRecipe_RejectsExternalCapture()
    {
        RenderProfileRecipe recipe = new()
        {
            Name = "clean-control",
            Component = "HarnessSubmission",
            Fixture = "noop-control",
            ProfileMode = RenderProfileMode.CleanProfile,
            LabelPolicy = RenderProfileLabelPolicy.Disabled,
            ExternalCapture = new()
            {
                ToolIdentity = "RenderDoc",
                ArtifactPaths = ["renderdoc/capture.rdc"],
            },
        };

        Should.Throw<ArgumentException>(recipe.Validate);
    }

    [Test]
    public void Configuration_RejectsMoreThanSixteenPaths()
    {
        RenderProfileExternalCaptureConfiguration configuration = new()
        {
            ToolIdentity = "RenderDoc",
            ArtifactPaths = Enumerable.Range(0, 17).Select(static index => $"renderdoc/{index}.rdc").ToArray(),
        };

        Should.Throw<ArgumentException>(configuration.Validate);
    }
}
