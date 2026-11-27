namespace XREngine.Scene.Importers;

/// <summary>
/// Options controlling Unity <c>.controller</c> compilation into a native <c>AnimStateMachine</c>.
/// </summary>
public sealed class UnityAnimatorCompileOptions
{
    /// <summary>
    /// Optional hierarchy path rewrites applied while importing referenced <c>.anim</c> clips
    /// (for example, remapping <c>Body</c> to the avatar's actual face mesh node path).
    /// </summary>
    public IReadOnlyDictionary<string, string>? NodePathRewrites { get; init; }

    /// <summary>
    /// Sets <c>AnimStateMachine.AllowMissingImportedBindings</c> on the compiled graph so
    /// dummy paths (such as <c>_Do_Nothing</c>) and absent blendshapes bind as logged no-ops.
    /// </summary>
    public bool AllowMissingImportedBindings { get; init; } = true;

    /// <summary>
    /// Records structured diagnostics for unsupported controller features (such as
    /// <c>WriteDefaults = Off</c>, sub-state machines, and unrecognized state behaviours).
    /// </summary>
    public bool EmitUnsupportedFeatureDiagnostics { get; init; } = true;

    /// <summary>
    /// Renames curve-driven internal float parameters in an incoming controller when merging into
    /// a non-empty <c>AnimStateMachine</c> that already defines or animates those parameter names.
    /// Shared input parameters that the incoming controller does not animate by curve remain shared.
    /// </summary>
    public bool IsolateCollidingAnimatedParameters { get; init; } = true;
}
