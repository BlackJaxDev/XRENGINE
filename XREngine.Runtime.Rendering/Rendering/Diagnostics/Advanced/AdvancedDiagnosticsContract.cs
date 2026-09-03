using System.Collections.Generic;

namespace XREngine.Rendering;

/// <summary>
/// Stable capture identifiers, RenderDoc annotation markers, and MCP-queryable diagnostic states.
/// </summary>
public static class AdvancedDiagnosticsContract
{
    public const string CaptureVisibilityIdentity = "Capture.Advanced.VisibilityIdentity";
    public const string CaptureOpaqueHdr = "Capture.Advanced.OpaqueHdr";
    public const string CaptureVelocity = "Capture.Advanced.Velocity";
    public const string CaptureSceneColorSnapshot = "Capture.Advanced.SceneColorSnapshot";
    public const string CapturePostOutput = "Capture.Advanced.PostOutput";

    public const string MarkerEarlyVisibility = "ARP: Early Visibility";
    public const string MarkerMaterialClassification = "ARP: Material Classification";
    public const string MarkerNativeOpaqueShading = "ARP: Native Opaque Shading";
    public const string MarkerLateTransparency = "ARP: Late Transparency & Special Passes";
    public const string MarkerPostProcessing = "ARP: Post Processing Chain";

    /// <summary>
    /// Generates a machine-readable MCP diagnostic status dictionary for the active pipeline configuration.
    /// </summary>
    public static Dictionary<string, object> BuildMcpDiagnosticReport(
        EAdvancedStereoMode stereoMode,
        uint viewCount,
        bool isFoveationEnabled,
        string activeGIMode,
        string? activeAOProvider)
    {
        return new Dictionary<string, object>
        {
            ["pipeline"] = "AdvancedRenderPipeline",
            ["stereoMode"] = stereoMode.ToString(),
            ["viewCount"] = viewCount,
            ["foveationEnabled"] = isFoveationEnabled,
            ["globalIllumination"] = activeGIMode,
            ["ambientOcclusion"] = activeAOProvider ?? "None"
        };
    }
}
