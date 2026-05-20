using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

// Shader / program lifecycle for GpuBvhTree.
//
// Each BVH stage (Morton coding, sort, build, refit, optional SAH refine)
// has its own compute shader and matching XRRenderProgram. This partial owns
// the shader / program fields plus the create / ensure-linked / destroy
// helpers. Programs are constructed lazily on the first build and reused for
// the lifetime of the GpuBvhTree.
public sealed partial class GpuBvhTree
{
    // Shaders. Owned; freed in DisposeProgramsCore.
    private XRShader? _buildShader;
    private XRShader? _refitShader;
    private XRShader? _refineShader;
    private XRShader? _mortonShader;
    private XRShader? _smallSortShader;
    private XRShader? _padShader;
    private XRShader? _tileSortShader;
    private XRShader? _mergeShader;
    private XRShader? _mergeLocalShader;

    // Programs. One per shader; same lifetime.
    private XRRenderProgram? _buildProgram;
    private XRRenderProgram? _refitProgram;
    private XRRenderProgram? _refineProgram;
    private XRRenderProgram? _mortonProgram;
    private XRRenderProgram? _smallSortProgram;
    private XRRenderProgram? _padProgram;
    private XRRenderProgram? _tileSortProgram;
    private XRRenderProgram? _mergeProgram;
    private XRRenderProgram? _mergeLocalProgram;

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

        if (primitiveCount > 1u)
        {
            // The tiled sort path (pad + tile + merge + merge_local) is only
            // used when primitiveCount > 1024; the small path uses the single
            // workgroup bitonic kernel.
            ready &= primitiveCount <= 1024u
                ? EnsureProgramReady(_smallSortProgram)
                : EnsureProgramReady(_padProgram) &&
                    EnsureProgramReady(_tileSortProgram) &&
                    EnsureProgramReady(_mergeProgram) &&
                    EnsureProgramReady(_mergeLocalProgram);
        }

        if (_buildMode == BvhBuildMode.MortonPlusSah)
            ready &= EnsureProgramReady(_refineProgram);

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
        _refineProgram ??= CreateProgram(ref _refineShader, "Scene3D/RenderPipeline/bvh_sah_refine.comp");
        _mortonProgram ??= CreateProgram(ref _mortonShader, "Scene3D/RenderPipeline/OctreeGeneration/morton_codes.comp");
        _smallSortProgram ??= CreateProgram(ref _smallSortShader, "Scene3D/RenderPipeline/OctreeGeneration/sort_morton.comp");
        _padProgram ??= CreateProgram(ref _padShader, "Scene3D/RenderPipeline/OctreeGeneration/pad_morton.comp");
        _tileSortProgram ??= CreateProgram(ref _tileSortShader, "Scene3D/RenderPipeline/OctreeGeneration/sort_morton_tiles.comp");
        _mergeProgram ??= CreateProgram(ref _mergeShader, "Scene3D/RenderPipeline/OctreeGeneration/merge_morton.comp");
        _mergeLocalProgram ??= CreateProgram(ref _mergeLocalShader, "Scene3D/RenderPipeline/OctreeGeneration/merge_morton_local.comp");
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
        _refineProgram?.Destroy();
        _mortonProgram?.Destroy();
        _smallSortProgram?.Destroy();
        _padProgram?.Destroy();
        _tileSortProgram?.Destroy();
        _mergeProgram?.Destroy();
        _mergeLocalProgram?.Destroy();

        _buildShader?.Destroy();
        _refitShader?.Destroy();
        _refineShader?.Destroy();
        _mortonShader?.Destroy();
        _smallSortShader?.Destroy();
        _padShader?.Destroy();
        _tileSortShader?.Destroy();
        _mergeShader?.Destroy();
        _mergeLocalShader?.Destroy();

        _buildProgram = null;
        _refitProgram = null;
        _refineProgram = null;
        _mortonProgram = null;
        _smallSortProgram = null;
        _padProgram = null;
        _tileSortProgram = null;
        _mergeProgram = null;
        _mergeLocalProgram = null;

        _buildShader = null;
        _refitShader = null;
        _refineShader = null;
        _mortonShader = null;
        _smallSortShader = null;
        _padShader = null;
        _tileSortShader = null;
        _mergeShader = null;
        _mergeLocalShader = null;
    }

    private static XRRenderProgram CreateProgram(ref XRShader? shader, string path)
    {
        shader ??= ShaderHelper.LoadEngineShader(path, EShaderType.Compute);
        return new XRRenderProgram(true, false, shader);
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

        if (!program.LinkReady)
            program.Link();

        return false;
    }

    private void DisposeProgramsCore()
    {
        _buildShader?.Destroy();
        _refitShader?.Destroy();
        _refineShader?.Destroy();
        _mortonShader?.Destroy();
        _smallSortShader?.Destroy();
        _padShader?.Destroy();
        _tileSortShader?.Destroy();
        _mergeShader?.Destroy();
        _mergeLocalShader?.Destroy();

        _buildProgram?.Destroy();
        _refitProgram?.Destroy();
        _refineProgram?.Destroy();
        _mortonProgram?.Destroy();
        _smallSortProgram?.Destroy();
        _padProgram?.Destroy();
        _tileSortProgram?.Destroy();
        _mergeProgram?.Destroy();
        _mergeLocalProgram?.Destroy();
    }
}
