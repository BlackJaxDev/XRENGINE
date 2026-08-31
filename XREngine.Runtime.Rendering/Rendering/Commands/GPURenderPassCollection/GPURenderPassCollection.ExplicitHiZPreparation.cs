using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        /// <summary>
        /// Ensures the bounded coarse Hi-Z capture is present in the committed explicit-frame
        /// resource generation before visibility collection freezes its package signature.
        /// This only allocates and binds the external texture; it does not build Hi-Z or issue GPU work.
        /// </summary>
        public bool TryPrepareExplicitHiZCoarseTiles(XRRenderPipelineInstance pipeline)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            if (ResolveActiveOcclusionMode() != EOcclusionCullingMode.GpuHiZ ||
                !IsBoundedCoarseHiZEnabled())
            {
                return true;
            }

            if (!ReferenceEquals(_ownerPipeline, pipeline) ||
                pipeline.Pipeline is not DefaultRenderPipeline ||
                pipeline.ActiveGeneration is null)
            {
                return false;
            }

            if (!pipeline.TryGetTexture(DefaultRenderPipeline.DepthViewTextureName, out XRTexture? depthTexture) ||
                depthTexture is null ||
                !TryResolveHiZDepthSource(depthTexture, out _, out uint sourceWidth, out uint sourceHeight))
            {
                return false;
            }

            uint tileWidth = (sourceWidth + HiZCoarseTileSize - 1u) / HiZCoarseTileSize;
            uint tileHeight = (sourceHeight + HiZCoarseTileSize - 1u) / HiZCoarseTileSize;
            if (tileWidth == 0u || tileHeight == 0u ||
                tileWidth > MaxHiZCoarseTilesPerAxis || tileHeight > MaxHiZCoarseTilesPerAxis)
            {
                return false;
            }

            EnsureHiZCoarseTiles(tileWidth, tileHeight);
            if (_hiZCoarseTiles is null)
                return false;

            // EnsureHiZCoarseTiles binds on allocation. Rebind after an active-generation
            // switch so a matching retained texture is present in this exact registry too.
            pipeline.BindImportedTexture(_hiZCoarseTiles);
            return true;
        }
    }
}
