namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Groups render pipeline commands for tools such as the command palette and the outline.
    /// </summary>
    public enum ERenderCommandCategory
    {
        /// <summary>The command has no category. Tools report this as a problem.</summary>
        Uncategorized,
        /// <summary>Branches, loops, and other commands that run nested command containers.</summary>
        Flow,
        /// <summary>Commands that push, pop, or set render state.</summary>
        State,
        /// <summary>Commands that bind, clear, or change render targets.</summary>
        Targets,
        /// <summary>Commands that draw scene geometry.</summary>
        Scene,
        /// <summary>Commands that draw a fullscreen quad with a material.</summary>
        Fullscreen,
        /// <summary>Post-processing effects such as bloom, tonemapping, and color grading.</summary>
        PostProcess,
        /// <summary>Anti-aliasing and upscaling.</summary>
        AntiAliasing,
        /// <summary>Lighting, shadows, ambient occlusion, and global illumination.</summary>
        Lighting,
        /// <summary>Compute dispatch and ray tracing commands.</summary>
        Compute,
        /// <summary>Copy, blit, mipmap, and readback commands.</summary>
        Transfer,
        /// <summary>Fences, barriers, queries, and timers.</summary>
        Synchronization,
        /// <summary>Debug views and overlays.</summary>
        Debug,
        /// <summary>Commands that belong to the Advanced pipeline family.</summary>
        Advanced,
        /// <summary>Commands that run user code or other special behavior.</summary>
        Utility,
    }
}
