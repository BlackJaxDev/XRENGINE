using XREngine.Rendering.Compute;

namespace XREngine.Rendering;

public partial class XRMeshRenderer
{
    private ulong _skinningPrepassDemandFrame;
    private ulong _skinningPrepassShadowDemandFrame;
    private ulong _skinningPrepassSkippedFrame;

    /// <summary>
    /// True when a traditional view or capture draw read this renderer's skinning prepass output
    /// within <see cref="SkinningPrepassDemand.DemandWindowFrames"/> frames before
    /// <paramref name="frameId"/>, or a traditional shadow draw read it within
    /// <see cref="SkinningPrepassDemand.ShadowDemandWindowFrames"/> frames.
    /// </summary>
    internal bool IsSkinningPrepassDemanded(ulong frameId)
        => IsWithin(_skinningPrepassDemandFrame, frameId, SkinningPrepassDemand.DemandWindowFrames) ||
           IsWithin(_skinningPrepassShadowDemandFrame, frameId, SkinningPrepassDemand.ShadowDemandWindowFrames);

    /// <summary>
    /// Compares render frame ids in both directions: shadow views render before the owner view
    /// and can carry the next frame's id.
    /// </summary>
    private static bool IsWithin(ulong demandFrame, ulong frameId, ulong window)
        => demandFrame != 0UL &&
           (frameId >= demandFrame ? frameId - demandFrame : demandFrame - frameId) <= window;

    /// <summary>Records that the owner view skipped this renderer's prepass in <paramref name="frameId"/>.</summary>
    internal void MarkSkinningPrepassSkipped(ulong frameId)
        => SetField(ref _skinningPrepassSkippedFrame, frameId, publishNotifications: false);

    /// <summary>Records that the owner view ran this renderer's prepass in this frame.</summary>
    internal void MarkSkinningPrepassRun()
        => SetField(ref _skinningPrepassSkippedFrame, 0UL, publishNotifications: false);

    /// <summary>
    /// Registers a traditional draw of this renderer as a reader of the skinning prepass output.
    /// A renderer that deforms from <see cref="DeformMeshRenderer"/> reads that renderer's output
    /// too, so the source is registered as well. Every CPU draw passes through here
    /// (<see cref="BaseVersion.Render"/> and <see cref="BaseVersion.RenderIndexedIndirect"/>).
    /// </summary>
    internal void NoteSkinningPrepassOutputRead()
    {
        if (Mesh is { } mesh && (mesh.HasSkinning || mesh.HasBlendshapes))
            NoteSkinningPrepassOutputReadCore();
        if (DeformMeshRenderer is { } source && !ReferenceEquals(source, this))
            source.NoteSkinningPrepassOutputReadCore();
    }

    private void NoteSkinningPrepassOutputReadCore()
    {
        bool shadowConsumer = RuntimeEngine.Rendering.State.RenderingPipelineState?.ShadowPass == true;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (shadowConsumer)
            SetField(ref _skinningPrepassShadowDemandFrame, frameId, publishNotifications: false);
        else
            SetField(ref _skinningPrepassDemandFrame, frameId, publishNotifications: false);
        // Stale because of the skip: the latest owner decision for this renderer was a skip in
        // this frame or the one before it. When the owner view no longer sees the renderer, it
        // neither runs nor skips it; that older output is not counted here.
        if (IsWithin(_skinningPrepassSkippedFrame, frameId, 1UL))
            SkinningPrepassDemand.RecordStaleRead(this, shadowConsumer);
    }
}
