namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// Plans pass metadata with the backend-neutral compiler when no renderer publishes a plan. The result has the order,
/// batches, synchronization edges, and logical lifetimes, but no barriers, submissions, or memory aliasing.
/// </summary>
public static class RenderGraphOfflinePlanner
{
    /// <summary>The note that every offline snapshot carries.</summary>
    public const string OfflineNote = "Offline plan: the neutral compiler ordered and batched the passes. It has no physical barriers, submissions, or aliasing.";

    /// <summary>Plans <paramref name="passMetadata"/>. Returns an empty snapshot for null or empty metadata.</summary>
    /// <param name="passMetadata">The pass metadata to plan.</param>
    /// <param name="revision">A revision that identifies the metadata, such as its revision stamp.</param>
    public static RenderGraphPlanSnapshot Plan(IReadOnlyCollection<RenderPassMetadata>? passMetadata, ulong revision = 0)
    {
        RenderGraphCompilation compilation;
        string? failure = null;
        try
        {
            compilation = RenderGraphCompiler.Compile(passMetadata);
        }
        catch (InvalidOperationException ex)
        {
            // A dependency cycle or a duplicate pass index has no plan. Report it instead of a partial order.
            compilation = RenderGraphCompilation.Empty;
            failure = string.Concat("The compiler failed: ", ex.Message);
        }

        string[] notes = failure is null ? [OfflineNote] : [OfflineNote, failure];
        return RenderGraphPlanSnapshotFactory.FromCompilation(EPlanSnapshotOrigin.OfflineCompile, revision, passMetadata, compilation, notes: notes);
    }
}
