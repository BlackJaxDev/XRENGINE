namespace XREngine;

/// <summary>
/// Describes when an environment-backed runtime setting can become effective.
/// </summary>
public enum RuntimeEnvironmentApplyMode
{
    /// <summary>The next consumer read observes the value without rebuilding resources.</summary>
    Immediate,

    /// <summary>The value is used by the next operation or newly-created resource.</summary>
    NextOperation,

    /// <summary>The active renderer must be restarted before the value can take effect.</summary>
    RendererRestart,

    /// <summary>The active renderer and OpenXR session must be restarted before the value can take effect.</summary>
    OpenXrSessionRestart,

    /// <summary>The value is consumed during process bootstrap and requires an application restart.</summary>
    ProcessRestart,
}
