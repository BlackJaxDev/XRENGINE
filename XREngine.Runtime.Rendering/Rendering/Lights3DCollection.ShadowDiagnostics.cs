using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Rendering;
using XREngine.Rendering.Shadows;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        private const int DirectionalCascadeSnapshotCapacity = 8;

        /// <summary>
        /// Adds one <see cref="DirectionalCascadeShadowSnapshot"/> for each published cascade of
        /// <paramref name="light"/> in <paramref name="source"/>. It reads the same published records
        /// and atlas allocations as the Advanced shadow capture, so the snapshot shows what the
        /// shading samples. Call it on the render thread from a diagnostic tool; it allocates.
        /// Returns the number of snapshots added.
        /// </summary>
        public int CaptureDirectionalCascadeShadowSnapshots(
            DirectionalLightComponent light,
            ShadowRequestSource source,
            List<DirectionalCascadeShadowSnapshot> output)
        {
            ArgumentNullException.ThrowIfNull(light);
            ArgumentNullException.ThrowIfNull(output);
            Span<DirectionalShadowGpuRecord> records = stackalloc DirectionalShadowGpuRecord[DirectionalCascadeSnapshotCapacity];
            light.CopyPublishedDirectionalShadowRecords(source, true, records, out int count);
            int added = 0;
            for (int cascade = 0; cascade < count; cascade++)
            {
                ref readonly DirectionalShadowGpuRecord published = ref records[cascade];
                bool hasAllocation = TryGetDirectionalCascadeShadowAtlasAllocation(
                    light, source, cascade, out ShadowAtlasAllocation allocation, out _);
                XRTexture2DArray? texture = null;
                if (hasAllocation &&
                    ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Directional, light.ShadowMapEncoding, allocation.PageIndex, out XRTexture2DArray pageTexture))
                    texture = pageTexture;
                bool sampleable = published.AtlasPacked0.X != 0;
                output.Add(new DirectionalCascadeShadowSnapshot(
                    source,
                    cascade,
                    count,
                    published.RenderedWorldToLight,
                    published.CurrentWorldToLight,
                    published.RenderedSplitBlendBias,
                    published.AtlasUvScaleBias != Vector4.Zero ? published.AtlasUvScaleBias : allocation.UvScaleBias,
                    texture,
                    hasAllocation ? allocation.PageIndex : -1,
                    light.ShadowMapEncoding,
                    hasAllocation && allocation.IsResident && allocation.LastRenderedFrame != 0u && sampleable,
                    sampleable,
                    hasAllocation && allocation.IsStaticCacheBacked,
                    hasAllocation ? allocation.ActiveFallback : ShadowFallbackMode.None,
                    hasAllocation ? allocation.LastRenderedFrame : 0u,
                    ShadowAtlas.CurrentFrameIdForDiagnostics));
                added++;
            }
            return added;
        }
    }
}
