using System.IO;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// GPU timing scopes for the stereo fullscreen passes of the headset eyes (temporal upscale,
/// FXAA, post process and final post process). These passes are ordinary mesh draws, so the
/// native stage scopes do not time them.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    private const string StereoFragmentShaderSuffix = "Stereo";

    /// <summary>
    /// Profiler path of each material, or null when the material has no stereo fragment
    /// shader. A weak table keeps the cache from holding materials that the scene released.
    /// </summary>
    private readonly ConditionalWeakTable<XRMaterial, StrongBox<string[]?>> _stereoPassGpuProfilerPaths = new();

    /// <summary>
    /// Starts a GPU timing scope for a draw whose material has a stereo fragment shader
    /// (a file name that ends with "Stereo"), such as TemporalSuperResolutionStereo.fs. The
    /// scope is named after that shader. Other draws, and frames without GPU timing, get an
    /// empty scope. The path of each material is built once, so the frame records no
    /// allocation after the first use.
    /// </summary>
    private VulkanGpuProfilerScope TryBeginStereoPassGpuProfilerScope(CommandBuffer commandBuffer, in PendingMeshDraw draw)
    {
        if (!FrameTelemetry._vulkanGpuProfilerRecordingActive || !RuntimeEngine.IsRenderThread)
            return default;

        XRMaterial? material = draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
        if (material is null)
            return default;

        if (!_stereoPassGpuProfilerPaths.TryGetValue(material, out StrongBox<string[]?>? entry))
        {
            entry = new StrongBox<string[]?>(BuildStereoPassGpuProfilerPath(material));
            _stereoPassGpuProfilerPaths.AddOrUpdate(material, entry);
        }

        return entry.Value is { } path
            ? TryBeginVulkanGpuProfilerScope(commandBuffer, path)
            : default;
    }

    /// <summary>
    /// Returns ["Stereo", "Fullscreen", shader name] for the first fragment shader of the
    /// material whose file name ends with "Stereo", or null when it has none.
    /// </summary>
    private static string[]? BuildStereoPassGpuProfilerPath(XRMaterial material)
    {
        IReadOnlyList<XRShader> shaders = material.FragmentShaders;
        for (int i = 0; i < shaders.Count; i++)
        {
            XRShader shader = shaders[i];
            string? source = shader.Source?.FilePath ?? shader.FilePath ?? shader.Name;
            if (string.IsNullOrEmpty(source))
                continue;

            string name = Path.GetFileNameWithoutExtension(source);
            if (name.EndsWith(StereoFragmentShaderSuffix, StringComparison.Ordinal))
                return ["Stereo", "Fullscreen", name];
        }
        return null;
    }
}
