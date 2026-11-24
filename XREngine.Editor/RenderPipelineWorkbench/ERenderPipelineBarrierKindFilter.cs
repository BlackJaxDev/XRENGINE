namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>The barrier kinds that the Barriers tab of the Execution view shows.</summary>
[Flags]
public enum ERenderPipelineBarrierKindFilter
{
    /// <summary>No kind.</summary>
    None = 0,

    /// <summary>Image barriers.</summary>
    Image = 1 << 0,

    /// <summary>Buffer barriers.</summary>
    Buffer = 1 << 1,

    /// <summary>Swapchain barriers.</summary>
    Swapchain = 1 << 2,

    /// <summary>Every kind.</summary>
    All = Image | Buffer | Swapchain,
}
