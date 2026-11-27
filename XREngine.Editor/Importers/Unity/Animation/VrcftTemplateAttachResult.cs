using OscCore.OscQuery;
using XREngine.Components;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Result of attaching one or more Unity <c>.controller</c> assets from a staged package onto
/// an imported avatar.
/// </summary>
public sealed class VrcftTemplateAttachResult
{
    /// <summary>Gets the single <see cref="AnimStateMachineComponent"/> owned by the avatar root.</summary>
    public required AnimStateMachineComponent StateMachineComponent { get; init; }

    /// <summary>Gets the staged package directory that supplied the controller assets.</summary>
    public required string TemplatePackageRoot { get; init; }

    /// <summary>Gets the full file paths of the compiled controllers in merge order.</summary>
    public required IReadOnlyList<string> ControllerPaths { get; init; }

    /// <summary>Gets compilation metrics and diagnostics for each compiled controller.</summary>
    public required IReadOnlyList<UnityAnimatorCompileResult> CompileResults { get; init; }

    /// <summary>Gets the resolved face mesh details used for path remapping and coverage auditing.</summary>
    public required VrcftFaceMeshResolution FaceMeshResolution { get; init; }

    /// <summary>Gets the number of controller-referenced blendshapes present on the face mesh.</summary>
    public int MatchedBlendshapeCount { get; init; }

    /// <summary>Gets the number of controller-referenced blendshapes missing from the face mesh.</summary>
    public int UnmatchedBlendshapeCount => UnmatchedBlendshapes.Count;

    /// <summary>Gets the sorted list of controller-referenced blendshape names missing from the face mesh.</summary>
    public required IReadOnlyList<string> UnmatchedBlendshapes { get; init; }

    /// <summary>Gets all diagnostics produced during controller compilation and coverage auditing.</summary>
    public required IReadOnlyList<SourceImportDiagnostic> Diagnostics { get; init; }

    /// <summary>
    /// Gets OSCQuery wire-type overrides discovered from companion avatar expression-parameter
    /// assets (for example, parameters declared as float in a Direct Blend Tree but bool in OSC).
    /// </summary>
    public IReadOnlyDictionary<string, OscQueryValueKind> OscParameterTypeOverrides { get; init; }
        = new Dictionary<string, OscQueryValueKind>(StringComparer.Ordinal);
}
