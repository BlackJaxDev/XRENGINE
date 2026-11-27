namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Configures startup Unity animator template conversion, dynamic OSC parameter binding, and
/// OSCQuery discovery when <see cref="UnitTestingWorldSettings.FaceTracking"/> is enabled.
/// </summary>
public class UnitTestingFaceTrackingSettings
{
    /// <summary>
    /// Compiles and attaches user-configured Unity animator controllers from a staged VPM package
    /// or local project instead of the legacy hardcoded ARKit generator.
    /// </summary>
    public bool UseVrcftTemplates { get; set; } = true;

    /// <summary>
    /// User-supplied source for a face-tracking template package: a VPM repository URL, a local
    /// <c>.zip</c> archive, a local VPM package folder, or a Unity project root.
    /// </summary>
    public string? PackageSource { get; set; }

    /// <summary>
    /// Optional VPM package identifier. Leave null when the VPM repository or archive contains
    /// a single package.
    /// </summary>
    public string? PackageId { get; set; }

    /// <summary>
    /// Optional exact VPM package version. Leave null to select the greatest stable version.
    /// </summary>
    public string? PackageVersion { get; set; }

    /// <summary>
    /// Ordered list of package-relative (or absolute) <c>.controller</c> paths to compile and
    /// merge into the avatar's <c>AnimStateMachineComponent</c>.
    /// </summary>
    public List<string> ControllerPaths { get; set; } = [];

    /// <summary>
    /// Optional explicit face-mesh node path relative to the avatar root.
    /// Leave null to resolve automatically from avatar metadata or mesh blendshapes.
    /// </summary>
    public string? FaceMeshPathOverride { get; set; }

    /// <summary>
    /// Source mesh node path authored inside the imported <c>.anim</c> clips that should be
    /// rewritten to the avatar's resolved face mesh path. Defaults to <c>Body</c>.
    /// </summary>
    public string SourceFaceMeshPath { get; set; } = "Body";

    /// <summary>UDP port used to receive incoming OSC parameter updates.</summary>
    public int OscReceivePort { get; set; } = 9000;

    /// <summary>UDP port used for outbound OSC notifications such as <c>/avatar/change</c>.</summary>
    public int OscSendPort { get; set; } = 9001;

    /// <summary>Starts the loopback HTTP JSON and mDNS OSCQuery server for VRCFT v5+ discovery.</summary>
    public bool EnableOscQuery { get; set; } = true;

    /// <summary>Service name advertised over OSCQuery mDNS and <c>HOST_INFO</c>.</summary>
    public string OscQueryServiceName { get; set; } = "VRChat-Client-XRENGINE";

    /// <summary>Requested loopback HTTP port for OSCQuery. Set to <c>0</c> for an ephemeral port.</summary>
    public int OscQueryHttpPort { get; set; }
}
