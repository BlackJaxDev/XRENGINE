using System;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Shadows;

namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static void RecordRendererStateCounter(ERendererProfilerCounter counter, long count = 1)
                    => RendererState.RecordCounter(counter, count);

                public static void RecordMemoryBarrier(EMemoryBarrierMask mask)
                    => RendererState.RecordMemoryBarrier(mask);

                public static void RecordSceneAssetVisible(
                    string? sourceAssetIdentity,
                    string? cookedVariantIdentity,
                    string? meshName,
                    string? materialName,
                    int materialSlots,
                    int textureCount,
                    long triangleCount,
                    bool skinned,
                    string? representation)
                    => SceneAssets.RecordVisibleRenderer(
                        sourceAssetIdentity,
                        cookedVariantIdentity,
                        meshName,
                        materialName,
                        materialSlots,
                        textureCount,
                        triangleCount,
                        skinned,
                        representation);

                public static void RecordTextureUpload(long bytes, TimeSpan elapsed)
                    => SceneAssets.RecordTextureUpload(bytes, elapsed);

                public static void RecordSkinningUpload(
                    long boneMatrixBytes,
                    long blendshapeWeightBytes,
                    int skinningDispatches = 0,
                    int blendshapeDispatches = 0,
                    long coreInfluenceBytes = 0,
                    long spillHeaderBytes = 0,
                    long spillEntryBytes = 0,
                    long skinPaletteBytes = 0,
                    int skippedSkinningDispatches = 0,
                    int reusedSkinnedOutputBuffers = 0,
                    int liveSkinningShaderPermutations = 0,
                    long blendshapeActiveListUploadBytes = 0,
                    long blendshapeDeltaBytes = 0,
                    int blendshapeAuthoredShapeCount = 0,
                    int blendshapeActiveShapeCount = 0,
                    int blendshapeAffectedVertexCount = 0,
                    int skippedBlendshapeDispatches = 0,
                    int compactedActiveBlendshapeCount = 0,
                    int liveBlendshapeShaderPermutations = 0)
                    => SceneAssets.RecordSkinningUpload(
                        boneMatrixBytes,
                        blendshapeWeightBytes,
                        skinningDispatches,
                        blendshapeDispatches,
                        coreInfluenceBytes,
                        spillHeaderBytes,
                        spillEntryBytes,
                        skinPaletteBytes,
                        skippedSkinningDispatches,
                        reusedSkinnedOutputBuffers,
                        liveSkinningShaderPermutations,
                        blendshapeActiveListUploadBytes,
                        blendshapeDeltaBytes,
                        blendshapeAuthoredShapeCount,
                        blendshapeActiveShapeCount,
                        blendshapeAffectedVertexCount,
                        skippedBlendshapeDispatches,
                        compactedActiveBlendshapeCount,
                        liveBlendshapeShaderPermutations);

                public static void RecordShaderVariant(
                    bool requested = false,
                    bool warming = false,
                    bool linked = false,
                    bool failed = false,
                    bool loadedFromDiskCache = false,
                    bool generatedThisRun = false)
                    => SceneAssets.RecordShaderVariant(
                        requested,
                        warming,
                        linked,
                        failed,
                        loadedFromDiskCache,
                        generatedThisRun);

                public static void RecordGpuDrivenBucketWork(
                    int activeBuckets = 0,
                    int emptyBucketSkips = 0,
                    int fullBucketScans = 0,
                    int materialScatterDispatches = 0)
                    => GpuDriven.RecordBucketWork(
                        activeBuckets,
                        emptyBucketSkips,
                        fullBucketScans,
                        materialScatterDispatches);

                public static void RecordGpuDrivenStageTiming(
                    TimeSpan indirectGeneration,
                    TimeSpan gpuCull,
                    TimeSpan sortCompact)
                    => GpuDriven.RecordGpuDrivenStageTiming(indirectGeneration, gpuCull, sortCompact);

                public static void RecordGpuDrivenDelayedDiagnosticReadback(long bytes)
                    => GpuDriven.RecordDelayedDiagnosticReadback(bytes);

                public static void RecordGpuDrivenHiZMode(string? mode)
                    => GpuDriven.UpdateHiZMode(mode);

                public static void RecordGpuDrivenHiZPhase(bool twoPhase, long phaseOneDraws, long phaseTwoDraws)
                    => GpuDriven.RecordHiZPhase(twoPhase, phaseOneDraws, phaseTwoDraws);

                public static void RecordShadowAtlasSolveDiagnostics(ShadowAtlasSolveDiagnostics diagnostics)
                    => ShadowAtlas.RecordSolveDiagnostics(diagnostics);
            }
        }
    }
}
