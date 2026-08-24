using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Profiling;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderProfileSessionManagerTests
{
    [Test]
    public void RecipeParser_AcceptsJsoncAndRejectsUnknownFields()
    {
        RenderProfileRecipe recipe = RenderProfileRecipe.Parse("""
            {
              // A deterministic component fixture.
              "name": "secondary-recording",
              "component": "SecondaryRecording",
              "fixture": "secondary-recording",
              "execution_mode": "component",
            }
            """);

        recipe.ExecutionMode.ShouldBe(RenderExecutionMode.Component);
        Should.Throw<System.Text.Json.JsonException>(() => RenderProfileRecipe.Parse("""
            { "name":"bad", "component":"x", "fixture":"x", "unexpected":true }
            """));
    }

    [Test]
    public async Task SessionManager_RunsOnlyAfterArmAndPublishesAfterDrain()
    {
        RenderProfileSessionManager manager = new();
        TestExecutor executor = new();
        RenderProfileRecipe recipe = new()
        {
            Name = "secondary-recording",
            Component = "SecondaryRecording",
            Fixture = "secondary-recording",
            CaptureFrames = 3,
        };

        string sessionId = manager.Create(recipe, executor);
        (await manager.WaitReadyAsync(sessionId, TimeSpan.FromSeconds(2))).State.ShouldBe(RenderProfileState.Created);
        executor.MeasuredFrames.ShouldBe(0);

        manager.Arm(sessionId);
        _ = manager.Start(sessionId);
        await WaitForCompletionAsync(manager, sessionId);

        RenderProfileResult result = manager.GetResult(sessionId);
        result.CapturedFrames.ShouldBe(3);
        result.WorkloadIdentity.ShouldBe("test-workload");
        executor.MeasuredFrames.ShouldBe(3);
        executor.Drained.ShouldBeTrue();
    }

    [Test]
    public void PresentationlessTarget_RequiresHeadlessCapability()
    {
        PresentationlessRenderTarget target = new(1920, 1080);
        target.ExecutionMode.ShouldBe(RenderExecutionMode.Presentationless);
        target.RequiredBackendCapabilities.ShouldBe(RendererBackendCapabilities.PresentationlessRendering);
        new RendererBackendCreateContext(target).Window.ShouldBeNull();
    }

    [Test]
    public void RendererTargets_KeepExecutionModesAndOutputOwnershipDistinct()
    {
        RenderTargetOutputProperties output = new(1920, 1080, Layers: 2, FrameSlotCount: 3);

        new ComponentRenderTarget("SecondaryRecording", output).ExecutionMode.ShouldBe(RenderExecutionMode.Component);
        new HeadlessWsiRenderTarget(output).RequiredBackendCapabilities.ShouldBe(RendererBackendCapabilities.HeadlessWsiPresentation);
        new OpenXrRenderTarget(output).RequiredBackendCapabilities.ShouldBe(RendererBackendCapabilities.OpenXrPresentation);
        new PresentationlessRenderTarget(1920, 1080).OutputProperties.ShouldNotBeNull();
    }

    private static async Task WaitForCompletionAsync(RenderProfileSessionManager manager, string sessionId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (manager.GetStatus(sessionId).State == RenderProfileState.Completed)
                return;
            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for render-profile session completion.");
    }

    private sealed class TestExecutor : IRenderProfileExecutor
    {
        public int MeasuredFrames { get; private set; }
        public bool Drained { get; private set; }

        public Task<RenderProfilePreparation> PrepareAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken)
            => Task.FromResult(new RenderProfilePreparation("test-adapter", "test-driver", "test-workload", []));

        public Task StabilizeAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void ExecuteMeasuredFrame(RenderProfileRecipe recipe, int frameIndex)
            => MeasuredFrames++;

        public Task<RenderProfileResult> DrainAsync(RenderProfileRecipe recipe, RenderProfilePreparation preparation, CancellationToken cancellationToken)
        {
            Drained = true;
            return Task.FromResult(new RenderProfileResult
            {
                SessionId = string.Empty,
                RecipeName = string.Empty,
                ExecutionMode = recipe.ExecutionMode,
                WorkloadIdentity = preparation.WorkloadIdentity,
            });
        }

        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
