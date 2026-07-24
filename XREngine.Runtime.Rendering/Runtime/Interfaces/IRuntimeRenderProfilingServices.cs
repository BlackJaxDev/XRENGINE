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
/// Optional, allocation-free host profiling scopes used by rendering hot paths.
/// </summary>
public interface IRuntimeRenderProfilingServices
{

    /// <summary>
    /// Starts a host profiler scope. Returning <see langword="null"/> is the expected no-op fast path.
    /// </summary>
    IDisposable? StartProfileScope([CallerMemberName] string? scopeName = null);
}
