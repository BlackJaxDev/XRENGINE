using XREngine.Rendering.Profiling;

namespace XREngine.RenderBench;

/// <summary>Exact recipe plus resolved catalog defaults used by one run.</summary>
public sealed record RenderBenchEffectiveConfiguration(
    int SchemaVersion,
    RenderProfileRecipe Recipe,
    RenderBenchFixtureManifest Fixture);
