using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// Canonical stage-native GPUScene storage layout. A stream is an aligned AoS
    /// record range, never a buffer per scalar field. All entries are published by
    /// <see cref="GPUScene"/> as one render-snapshot generation.
    /// </summary>
    public static class GPUSceneLayoutSchema
    {
        public enum Stream : byte
        {
            CullControl,
            CullBounds,
            Classification,
            MaterialState,
            Transform,
            PreviousTransform,
            Visibility,
            OptionalAabb,
        }

        public readonly record struct Descriptor(Stream Stream, uint Stride, uint Alignment, bool Optional);

        public static readonly IReadOnlyList<Descriptor> Streams =
        [
            new(Stream.CullControl, (uint)Marshal.SizeOf<DrawMetadata>(), 16u, false),
            new(Stream.CullBounds, (uint)Marshal.SizeOf<BoundsGpu>(), 16u, false),
            new(Stream.Classification, GPUViewBatchClassificationLayout.Stride, 16u, false),
            new(Stream.MaterialState, (uint)Marshal.SizeOf<MaterialStateGpu>(), 16u, false),
            new(Stream.Transform, (uint)Marshal.SizeOf<TransformGpu>(), 16u, false),
            new(Stream.PreviousTransform, (uint)Marshal.SizeOf<TransformGpu>(), 16u, false),
            new(Stream.Visibility, sizeof(uint), 4u, false),
            new(Stream.OptionalAabb, sizeof(float) * 8u, 16u, true),
        ];
    }

    /// <summary>Allocation-free per-publication stream traffic counters.</summary>
    public struct GPUSceneStreamTelemetry
    {
        public ulong Generation;
        /// <summary>
        /// Bytes routed through a temporary broad-command compatibility
        /// conversion. The canonical stage-native path leaves this at zero.
        /// </summary>
        public ulong CompatibilityConversionBytes;
        public GPUSceneStreamTraffic CullControl;
        public GPUSceneStreamTraffic CullBounds;
        public GPUSceneStreamTraffic Classification;
        public GPUSceneStreamTraffic MaterialState;
        public GPUSceneStreamTraffic Transform;
        public GPUSceneStreamTraffic PreviousTransform;
        public GPUSceneStreamTraffic Visibility;
        public GPUSceneStreamTraffic OptionalAabb;
    }

    /// <summary>Allocation-free cumulative traffic counters for one logical scene stream.</summary>
    public struct GPUSceneStreamTraffic
    {
        public ulong ElementsRead;
        public ulong ElementsWritten;
        public ulong BytesRead;
        public ulong BytesWritten;

        public void Record(uint elementsRead, uint elementsWritten, uint bytesRead, uint bytesWritten)
        {
            ElementsRead += elementsRead;
            ElementsWritten += elementsWritten;
            BytesRead += bytesRead;
            BytesWritten += bytesWritten;
        }
    }
}
