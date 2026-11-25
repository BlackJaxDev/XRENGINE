namespace XREngine.Rendering.UI;

/// <summary>Records the exact resource owners seen after an ordinary native frame.</summary>
internal sealed record UIWorldOutputReadiness(
    object Window, object Pipeline, object Context, object Generation,
    long ContextGeneration, long BackendGeneration,
    int ResourceGeneration, ulong CommandGeneration)
{
    internal bool Matches(object window, object pipeline, object? context,
        object? generation, long contextGeneration, long backendGeneration,
        int resourceGeneration, ulong commandGeneration, bool hasPendingGeneration)
        => !hasPendingGeneration && context is not null && generation is not null &&
            ReferenceEquals(Window, window) &&
            ReferenceEquals(Pipeline, pipeline) &&
            ReferenceEquals(Context, context) &&
            ReferenceEquals(Generation, generation) &&
            ContextGeneration == contextGeneration &&
            BackendGeneration == backendGeneration &&
            ResourceGeneration == resourceGeneration &&
            CommandGeneration == commandGeneration;
}
