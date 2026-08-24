using XREngine.Rendering;
using XREngine.Rendering.Profiling;

namespace XREngine.RenderBench;

/// <summary>Non-MCP command-line adapter over the same frame-granular profile executor.</summary>
public sealed class RenderBenchRunner(
    RenderBenchOptions options,
    RenderBenchProcessState state,
    bool networkListenerStopped,
    Func<bool> shutdownRequested)
{
    public string Run()
    {
        if (!networkListenerStopped)
            throw new InvalidOperationException("The MCP listener must be stopped before RenderBench execution.");
        if (shutdownRequested())
            throw new OperationCanceledException("RenderBench shutdown was requested.");

        RenderProfileRecipe recipe = options.RecipeFile is not null
            ? RenderProfileRecipe.Parse(File.ReadAllText(options.RecipeFile))
            : new RenderProfileRecipe
        {
            Name = options.Recipe,
            Component = options.ExecutionMode == RenderExecutionMode.Component ? "SyntheticClearSubmission" : "PresentationlessFrame",
            ExecutionMode = options.ExecutionMode,
            Backend = RuntimeGraphicsApiKind.Vulkan,
            Fixture = options.Fixture,
            Width = options.Width,
            Height = options.Height,
            FrameSlots = options.FrameSlots,
            ColorFormat = options.ColorFormat.ToString(),
            DepthFormat = options.DepthFormat.ToString(),
            SampleCount = options.Samples,
            WarmupFrames = options.WarmupFrames,
            StabilityFrames = options.StabilityFrames,
            CaptureFrames = options.CaptureFrames,
            TimeoutSeconds = int.MaxValue,
            Scene = new RenderProfileSceneConfiguration
            {
                AnimationIdentity = options.FrozenWorld ? "frozen" : "fixed-step-sine",
                FixedTimeStepSeconds = options.FixedStepSeconds,
                RandomSeed = options.RandomSeed,
            },
        };
        RenderBenchProfileExecutor executor = new(options, state, recipe);
        RenderProfilePreparation preparation = executor.PrepareAsync(recipe, CancellationToken.None).GetAwaiter().GetResult();
        executor.StabilizeAsync(recipe, CancellationToken.None).GetAwaiter().GetResult();
        executor.WarmCaptureThread(recipe);
        int firstFrame = checked((int)executor.NextFrameId);
        for (int index = 0; index < recipe.TotalCaptureFrames; index++)
        {
            if (shutdownRequested())
                throw new OperationCanceledException("RenderBench shutdown was requested.");
            executor.ExecuteMeasuredFrame(recipe, firstFrame + index);
        }
        RenderProfileResult result = executor.DrainAsync(recipe, preparation, CancellationToken.None).GetAwaiter().GetResult();
        return result.Artifacts["result"];
    }
}
