namespace XREngine.Scene.Importers;

/// <summary>
/// Options controlling attachment of user-supplied Unity <c>.controller</c> assets from a VPM
/// repository, local archive, package folder, or Unity project onto an imported avatar.
/// </summary>
public sealed class VrcftTemplateAttachOptions
{
    /// <summary>
    /// User-supplied source for the template package: a VPM repository URL, a local <c>.zip</c>
    /// archive, a local VPM package folder, or a Unity project root containing <c>Packages/</c>.
    /// Required unless <see cref="ExistingProjectContext"/> already contains the target package.
    /// </summary>
    public string? PackageSource { get; init; }

    /// <summary>
    /// Optional VPM package identifier. Leave <see langword="null"/> when the repository, archive,
    /// or folder provides a single package.
    /// </summary>
    public string? PackageId { get; init; }

    /// <summary>
    /// Optional exact VPM package version to select when <see cref="PackageSource"/> is a VPM
    /// repository URL. Leave <see langword="null"/> to select the greatest stable version.
    /// </summary>
    public string? PackageVersion { get; init; }

    /// <summary>
    /// Ordered list of package-relative (or absolute) <c>.controller</c> file paths to compile
    /// and merge into the avatar's single <c>AnimStateMachineComponent</c>.
    /// </summary>
    public IReadOnlyList<string> ControllerPaths { get; init; } = [];

    /// <summary>
    /// Optional explicit face-mesh node path relative to the avatar root (for example, <c>Face</c>
    /// or <c>Meshes/Head</c>). Leave <see langword="null"/> to resolve the face mesh automatically.
    /// </summary>
    public string? FaceMeshPathOverride { get; init; }

    /// <summary>
    /// Source mesh node path authored inside the template <c>.anim</c> clips to rewrite to the
    /// resolved face mesh path. Defaults to <c>Body</c>.
    /// </summary>
    public string SourceFaceMeshPath { get; init; } = "Body";

    /// <summary>
    /// Cache directory used for VPM downloads and staged standalone package projects.
    /// Defaults to <c>Engine.Assets.GameCachePath</c> when omitted.
    /// </summary>
    public string? CacheRoot { get; init; }

    /// <summary>
    /// Optional existing import context for the avatar's staged Unity project. When its project
    /// root already contains the requested package, attachment reuses it directly.
    /// </summary>
    public SourceProjectImportContext? ExistingProjectContext { get; init; }

    /// <summary>Optional callback that resolves relative local package paths.</summary>
    public Func<string, string>? ResolveLocalPath { get; init; }

    /// <summary>Optional progress callback reported during VPM download and staging.</summary>
    public Action<float, string>? Progress { get; init; }

    /// <summary>
    /// When <see langword="true"/>, sets local avatar defaults (<c>IsLocal = true</c> and, when
    /// declared by the controller, <c>FT/DirectBlend = 1.0</c>) after merging parameters.
    /// </summary>
    public bool ApplyLocalDefaults { get; init; } = true;

    /// <summary>
    /// Sets <c>AnimStateMachine.AllowMissingImportedBindings</c> so optional dummy paths and
    /// absent blendshapes bind as logged no-ops.
    /// </summary>
    public bool AllowMissingImportedBindings { get; init; } = true;
}
