using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Optional host diagnostics and logging integration for runtime rendering.
/// </summary>
public interface IRuntimeRenderDiagnosticsServices
{
    /// <summary>
    /// Gets the texture diagnostics logging mode.
    /// </summary>
    TextureRuntimeLogMode TextureLogMode { get; }


    /// <summary>
    /// Pushes a pipeline context as current on the host and returns a scope that restores the previous context.
    /// </summary>
    IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline);

    /// <summary>
    /// Writes an informational message through the host output system.
    /// </summary>
    void LogOutput(string message);

    /// <summary>
    /// Writes a warning message through the host output system.
    /// </summary>
    void LogWarning(string message);

    /// <summary>
    /// Writes an exception through the host diagnostic system with optional contextual text.
    /// </summary>
    void LogException(Exception ex, string? context = null);

    /// <summary>
    /// Records a missing asset diagnostic for tooling, logs, or editor reports.
    /// </summary>
    void RecordMissingAsset(string assetPath, string category, string? context = null);
}
