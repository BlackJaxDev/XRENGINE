using System;
using System.Threading;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Demand rules and diagnostic counters of the skinning prepass skip.
/// </summary>
/// <remarks>
/// <para>
/// The skinning prepass (<see cref="SkinningPrepassDispatcher"/>) writes per-renderer skinned
/// outputs that only traditional draws read: CPU-pass draws of the renderer itself or of a
/// renderer that deforms from it (<see cref="XRMeshRenderer.DeformMeshRenderer"/>), shadow views
/// that draw traditionally, captures, and the live skinned-bounds readers. The Advanced
/// canonical path, its directional cascade lane and the Advanced visibility producers read the
/// aggregate deformation arena instead.
/// </para>
/// <para>
/// The owner view's <see cref="SkinningPrepassDispatcher.RunVisible"/> dispatches a renderer
/// when (1) one of its commands in the owner collection is not owned by the canonical GPU path,
/// so a CPU pass of this frame draws it; (2) a traditional view or capture draw read its output
/// in the current render frame or in the last <see cref="DemandWindowFrames"/> frames, or a
/// traditional shadow draw read it in the last <see cref="ShadowDemandWindowFrames"/> frames; or
/// (3) the live skinned-bounds readers are enabled. Otherwise it skips the renderer.
/// </para>
/// <para>
/// Order: the owner collection is complete before the owner view renders, so rule (1) covers
/// every traditional draw of the owner view in the same frame. Shadow views render before the
/// views and always read the previous frame's output; the desktop view and captures render
/// after the owner. Their demand comes from their draws (rule 2): when such a consumer starts
/// to draw a renderer after a skip, its first draws read the last dispatched output. Those reads
/// are counted here as stale reads; the draw itself is not changed.
/// </para>
/// </remarks>
public static class SkinningPrepassDemand
{
    /// <summary>Frames that one traditional view or capture read keeps the prepass running.</summary>
    public const ulong DemandWindowFrames = 3UL;

    /// <summary>
    /// Frames that one traditional shadow read keeps the prepass running. The shadow atlas
    /// renders a tile only when its schedule and budget allow, so a shadow consumer can read
    /// every few frames; a short window would lapse between its reads.
    /// </summary>
    public const ulong ShadowDemandWindowFrames = 120UL;

    private static long s_skippedDispatches;
    private static long s_staleViewReads;
    private static long s_staleShadowReads;

    /// <summary>Renderers that an owner view skipped, cumulative since launch.</summary>
    public static long SkippedDispatches => Interlocked.Read(ref s_skippedDispatches);

    /// <summary>Traditional view or capture draws that read output older than without the skip.</summary>
    public static long StaleViewReads => Interlocked.Read(ref s_staleViewReads);

    /// <summary>Traditional shadow draws that read output older than without the skip.</summary>
    public static long StaleShadowReads => Interlocked.Read(ref s_staleShadowReads);

    /// <summary>Returns the counters as one line, for MCP <c>invoke_method</c>.</summary>
    public static string Describe()
        => $"skippedDispatches={SkippedDispatches} staleViewReads={StaleViewReads} staleShadowReads={StaleShadowReads}";

    internal static void RecordSkip()
        => Interlocked.Increment(ref s_skippedDispatches);

    /// <summary>
    /// Counts a traditional draw that read the skinned output of <paramref name="renderer"/>
    /// after the owner view skipped its prepass, and writes a throttled warning that names the
    /// renderer and the consumer kind.
    /// </summary>
    internal static void RecordStaleRead(XRMeshRenderer renderer, bool shadowConsumer)
    {
        if (shadowConsumer)
            Interlocked.Increment(ref s_staleShadowReads);
        else
            Interlocked.Increment(ref s_staleViewReads);
        Debug.RenderingWarningEvery(
            "SkinningPrepassDemand.StaleRead",
            TimeSpan.FromSeconds(5),
            "[SkinningPrepass] {0} draw read the skinned output of '{1}' after the owner view skipped its prepass ({2}).",
            shadowConsumer ? "A shadow" : "A view or capture",
            renderer.Name ?? "<unnamed renderer>",
            Describe());
    }
}
