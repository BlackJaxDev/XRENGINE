using System.IO;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// GPU timing scopes for the fullscreen passes of the post chain (temporal upscale, FXAA,
/// post process, final post process, bloom, ambient occlusion and the vendor output blit), for
/// the headset eyes and for the mono desktop view, and for the DLSS evaluate. These passes are
/// ordinary mesh draws or vendor operations, so the native stage scopes do not time them.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    private const string StereoFragmentShaderSuffix = "Stereo";
    private const string FullscreenQuadRendererPrefix = "FullscreenQuad:";
    private static readonly string[] DlssEvaluateGpuProfilerPath = ["Vendor", "DlssEvaluate"];

    /// <summary>
    /// Profiler path of each material, or null when the material is not a fullscreen pass. A
    /// weak table keeps the cache from holding materials that the scene released.
    /// </summary>
    private readonly ConditionalWeakTable<XRMaterial, StrongBox<string[]?>> _fullscreenPassGpuProfilerPaths = new();

    /// <summary>
    /// Starts a GPU timing scope for a fullscreen pass draw. A draw whose material has a stereo
    /// fragment shader (a file name that ends with "Stereo", such as
    /// TemporalSuperResolutionStereo.fs) gets the path "Stereo > Fullscreen > shader". A mono
    /// draw of an <see cref="XRQuadFrameBuffer"/> (mesh renderer name "FullscreenQuad:...") gets
    /// "Fullscreen > shader". Other draws, and frames without GPU timing, get an empty scope.
    /// The path of each material is built once, so the frame records no allocation after the
    /// first use.
    /// </summary>
    private VulkanGpuProfilerScope TryBeginFullscreenPassGpuProfilerScope(CommandBuffer commandBuffer, in PendingMeshDraw draw)
    {
        if (!FrameTelemetry._vulkanGpuProfilerRecordingActive || !RuntimeEngine.IsRenderThread)
            return default;

        XRMaterial? material = draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
        if (material is null)
            return default;

        if (!_fullscreenPassGpuProfilerPaths.TryGetValue(material, out StrongBox<string[]?>? entry))
        {
            entry = new StrongBox<string[]?>(BuildFullscreenPassGpuProfilerPath(material, draw.Renderer.MeshRenderer.Name));
            _fullscreenPassGpuProfilerPaths.AddOrUpdate(material, entry);
        }

        return entry.Value is { } path
            ? TryBeginVulkanGpuProfilerScope(commandBuffer, path)
            : default;
    }

    /// <summary>
    /// Starts the GPU timing scope "Vendor > DlssEvaluate" around the recorded DLSS evaluate.
    /// Frames without GPU timing get an empty scope.
    /// </summary>
    private VulkanGpuProfilerScope TryBeginDlssEvaluateGpuProfilerScope(CommandBuffer commandBuffer)
        => FrameTelemetry._vulkanGpuProfilerRecordingActive && RuntimeEngine.IsRenderThread
            ? TryBeginVulkanGpuProfilerScope(commandBuffer, DlssEvaluateGpuProfilerPath)
            : default;

    /// <summary>
    /// Returns ["Stereo", "Fullscreen", shader] for a material with a stereo fragment shader,
    /// ["Fullscreen", shader] for a material drawn by a fullscreen quad renderer, or null for
    /// other materials. The shader name is the file name of the first fragment shader, or the
    /// material name when the shader has no file.
    /// </summary>
    private static string[]? BuildFullscreenPassGpuProfilerPath(XRMaterial material, string? meshRendererName)
    {
        IReadOnlyList<XRShader> shaders = material.FragmentShaders;
        string? firstName = null;
        for (int i = 0; i < shaders.Count; i++)
        {
            XRShader shader = shaders[i];
            string? source = shader.Source?.FilePath ?? shader.FilePath ?? shader.Name;
            if (string.IsNullOrEmpty(source))
                continue;

            string name = Path.GetFileNameWithoutExtension(source);
            if (name.EndsWith(StereoFragmentShaderSuffix, StringComparison.Ordinal))
                return ["Stereo", "Fullscreen", name];
            firstName ??= name;
        }

        if (meshRendererName is null || !meshRendererName.StartsWith(FullscreenQuadRendererPrefix, StringComparison.Ordinal))
            return null;

        return ["Fullscreen", firstName ?? material.Name ?? "Unnamed"];
    }
}
