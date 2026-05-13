namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Tri-state outcome from <see cref="CpuRenderOcclusionCoordinator.ShouldRender(int, uint, out bool)"/>.
    /// </summary>
    public enum ECpuOcclusionDecision
    {
        /// <summary>Draw the mesh normally; query around the draw.</summary>
        Visible = 0,

        /// <summary>
        /// Mesh is occluded but is due for a periodic requery this frame. Caller should
        /// issue a depth-only AABB proxy draw around Begin/EndQuery instead of drawing
        /// the full mesh — refreshes the visibility result without contributing color.
        /// </summary>
        ProbeOnly = 1,

        /// <summary>
        /// Mesh is occluded and not scheduled for retest; emit no draw, no query.
        /// </summary>
        Skip = 2,
    }
}
