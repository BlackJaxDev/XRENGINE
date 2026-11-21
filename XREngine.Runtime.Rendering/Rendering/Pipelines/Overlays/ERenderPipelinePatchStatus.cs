namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>The result of one patch when the overlay chain applies.</summary>
public enum ERenderPipelinePatchStatus
{
    /// <summary>The patch applied.</summary>
    Applied,

    /// <summary>The patch applied through an anchor alias. The base renamed the anchor.</summary>
    AnchorMoved,

    /// <summary>The target does not exist in the base. The overlay does not apply.</summary>
    AnchorMissing,

    /// <summary>The patch found its target but failed, for example on a bad property value. The overlay does not apply.</summary>
    Failed,

    /// <summary>The patch did not run, because an earlier patch of the same overlay chain failed.</summary>
    NotApplied,
}
