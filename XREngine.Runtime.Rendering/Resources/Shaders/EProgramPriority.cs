namespace XREngine.Rendering
{
    /// <summary>
    /// Priority bucket for shader-program compile/link work.
    /// Lower numeric values are served first by the shared-context worker queue
    /// and any other priority-aware scheduler. Programs in the same bucket are FIFO.
    /// <para/>
    /// Default semantics:
    /// <list type="bullet">
    /// <item><see cref="Interactive"/> - editor/user-interaction overlays such as transform gizmos.</item>
    /// <item><see cref="Main"/> - main (lit/forward) pass for the active scene view.</item>
    /// <item><see cref="Forward"/> - secondary forward pass (transparent, overlay).</item>
    /// <item><see cref="DepthPrepass"/> - depth-only / G-buffer fill prepass.</item>
    /// <item><see cref="Shadow"/> - shadow map / cascaded / point-light shadow programs.</item>
    /// <item><see cref="VR"/> - active VR stereo variants (OVR_multiview, NV_stereo_view_rendering).</item>
    /// <item><see cref="Compute"/> - compute shaders (culling, simulation, post).</item>
    /// <item><see cref="Deferred"/> - cold background work that is not needed by the current run mode.</item>
    /// </list>
    /// </summary>
    public enum EProgramPriority : byte
    {
        Interactive = 0,
        Main = 1,
        Forward = 2,
        DepthPrepass = 3,
        Shadow = 4,
        VR = 5,
        Compute = 6,
        Deferred = 7,
    }
}
