using System;
using System.IO;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

// Shader / program lifecycle for GpuBvhTree.
//
// Each BVH stage (Morton coding, sort, build, and refit)
// has its own compute shader and matching XRRenderProgram. This partial owns
// the shader / program fields plus the create / ensure-linked / destroy
// helpers. Programs are constructed lazily on the first build and reused for
// the lifetime of the GpuBvhTree.
public sealed partial class GpuBvhTree
{
    // Shaders. Owned; freed in DisposeProgramsCore.
    private XRShader? _buildShader;
    private XRShader? _refitShader;
    private XRShader? _mortonShader;
    private XRShader? _smallSortShader;
    private XRShader? _radixHistogramShader;
    private XRShader? _radixPrefixShader;
    private XRShader? _radixScatterShader;
    private XRShader? _qualityShader;

    // Programs. One per shader; same lifetime.
    private XRRenderProgram? _buildProgram;
    private XRRenderProgram? _refitProgram;
    private XRRenderProgram? _mortonProgram;
    private XRRenderProgram? _smallSortProgram;
    private XRRenderProgram? _radixHistogramProgram;
    private XRRenderProgram? _radixPrefixProgram;
    private XRRenderProgram? _radixScatterProgram;
    private XRRenderProgram? _qualityProgram;

    /// <summary>
    /// Ensures the compute programs required by the requested build path have
    /// linked. Returns <c>true</c> only when every program needed to actually
    /// run a build at this primitive count is linked and dispatch-ready.
    /// </summary>
    public bool EnsureProgramsReady(uint primitiveCount)
    {
        EnsurePrograms();

        // Morton/build/refit are always required.
        bool ready = EnsureProgramReady(_mortonProgram) &&
            EnsureProgramReady(_buildProgram) &&
            EnsureProgramReady(_refitProgram);
        ready &= EnsureProgramReady(_qualityProgram);

        if (primitiveCount > 1u)
        {
            // Tiny trees use one shared-memory bitonic workgroup; larger trees
            // use the stable four-pass radix pipeline.
            ready &= primitiveCount <= 1024u
                ? EnsureProgramReady(_smallSortProgram)
                : EnsureProgramReady(_radixHistogramProgram) &&
                    EnsureProgramReady(_radixPrefixProgram) &&
                    EnsureProgramReady(_radixScatterProgram);
        }

        return ready;
    }

    /// <summary>
    /// Lazily constructs every BVH program. Cheap when programs already exist
    /// because of the <c>??=</c> operator.
    /// </summary>
    private void EnsurePrograms()
    {
        _buildProgram ??= CreateProgram(ref _buildShader, "Scene3D/RenderPipeline/bvh_build.comp");
        _refitProgram ??= CreateProgram(ref _refitShader, "Scene3D/RenderPipeline/bvh_refit.comp");
        _mortonProgram ??= CreateProgram(ref _mortonShader, "Scene3D/RenderPipeline/OctreeGeneration/morton_codes.comp");
        _smallSortProgram ??= CreateProgram(ref _smallSortShader, "Scene3D/RenderPipeline/OctreeGeneration/sort_morton.comp");
        _radixHistogramProgram ??= CreateProgram(ref _radixHistogramShader, "Scene3D/RenderPipeline/OctreeGeneration/radix_morton_histogram.comp");
        _radixPrefixProgram ??= CreateProgram(ref _radixPrefixShader, "Scene3D/RenderPipeline/OctreeGeneration/radix_morton_prefix.comp");
        _radixScatterProgram ??= CreateProgram(ref _radixScatterShader, "Scene3D/RenderPipeline/OctreeGeneration/radix_morton_scatter.comp");
        _qualityProgram ??= CreateProgram(ref _qualityShader, "Scene3D/RenderPipeline/bvh_quality_diagnostics.comp");
    }

    /// <summary>
    /// Destroys every shader and program so the next <see cref="EnsurePrograms"/>
    /// call rebuilds them. Currently unreferenced; retained because the
    /// <see cref="BuildMode"/> / <see cref="MaxLeafPrimitives"/> setters used to
    /// invalidate cached programs when those values were specialization
    /// constants. They are now plain uniforms, so a reset is no longer required.
    /// </summary>
    private void ResetPrograms()
    {
        _buildProgram?.Destroy();
        _refitProgram?.Destroy();
        _mortonProgram?.Destroy();
        _smallSortProgram?.Destroy();
        _radixHistogramProgram?.Destroy();
        _radixPrefixProgram?.Destroy();
        _radixScatterProgram?.Destroy();
        _qualityProgram?.Destroy();

        _buildShader?.Destroy();
        _refitShader?.Destroy();
        _mortonShader?.Destroy();
        _smallSortShader?.Destroy();
        _radixHistogramShader?.Destroy();
        _radixPrefixShader?.Destroy();
        _radixScatterShader?.Destroy();
        _qualityShader?.Destroy();

        _buildProgram = null;
        _refitProgram = null;
        _mortonProgram = null;
        _smallSortProgram = null;
        _radixHistogramProgram = null;
        _radixPrefixProgram = null;
        _radixScatterProgram = null;
        _qualityProgram = null;

        _buildShader = null;
        _refitShader = null;
        _mortonShader = null;
        _smallSortShader = null;
        _radixHistogramShader = null;
        _radixPrefixShader = null;
        _radixScatterShader = null;
        _qualityShader = null;
    }

    private static XRRenderProgram CreateProgram(ref XRShader? shader, string path)
    {
        shader ??= ShaderHelper.LoadEngineShader(path, EShaderType.Compute);
        return new XRRenderProgram(true, false, shader)
        {
            Name = $"GpuBvh.{Path.GetFileNameWithoutExtension(path)}"
        };
    }

    /// <summary>
    /// Polls a program's link state without blocking. Returns <c>true</c> only
    /// when the program is fully linked and safe to dispatch. Triggers
    /// <see cref="XRRenderProgram.Link"/> on the first call after compilation
    /// completes.
    /// </summary>
    private static bool EnsureProgramReady(XRRenderProgram? program)
    {
        if (program is null)
            return false;

        if (program.IsLinked)
            return true;

        program.Link();

        return program.IsLinked;
    }

    private void DisposeProgramsCore()
    {
        _buildShader?.Destroy();
        _refitShader?.Destroy();
        _mortonShader?.Destroy();
        _smallSortShader?.Destroy();
        _radixHistogramShader?.Destroy();
        _radixPrefixShader?.Destroy();
        _radixScatterShader?.Destroy();
        _qualityShader?.Destroy();

        _buildProgram?.Destroy();
        _refitProgram?.Destroy();
        _mortonProgram?.Destroy();
        _smallSortProgram?.Destroy();
        _radixHistogramProgram?.Destroy();
        _radixPrefixProgram?.Destroy();
        _radixScatterProgram?.Destroy();
        _qualityProgram?.Destroy();
    }
}
