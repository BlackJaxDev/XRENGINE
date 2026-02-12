using System;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// Minimal mesh/material/pass signature for backend parity comparisons.
    /// </summary>
    public readonly struct GpuCommandSignature(uint meshId, uint materialId, uint renderPass)
    {
        public uint MeshId { get; } = meshId;
        public uint MaterialId { get; } = materialId;
        public uint RenderPass { get; } = renderPass;
    }

    /// <summary>
    /// Compact snapshot used to compare OpenGL/Vulkan indirect output parity.
    /// </summary>
    public readonly struct GpuBackendParitySnapshot(string backendName, uint visibleCount, uint drawCount, GpuCommandSignature[] sampledSignatures)
    {
        public string BackendName { get; } = backendName;
        public uint VisibleCount { get; } = visibleCount;
        public uint DrawCount { get; } = drawCount;
        public GpuCommandSignature[] SampledSignatures { get; } = sampledSignatures;
    }

    public static class GpuBackendParity
    {
        public static GpuBackendParitySnapshot BuildSnapshot(
            string backendName,
            uint visibleCount,
            uint drawCount,
            ReadOnlySpan<GPUIndirectRenderCommand> commands,
            int maxSamples = 8)
        {
            if (maxSamples < 0)
                maxSamples = 0;

            uint available = (uint)commands.Length;
            uint targetSamples = Math.Min(visibleCount, available);
            if (targetSamples > (uint)maxSamples)
                targetSamples = (uint)maxSamples;

            var signatures = new GpuCommandSignature[targetSamples];
            for (uint i = 0; i < targetSamples; ++i)
            {
                GPUIndirectRenderCommand command = commands[(int)i];
                signatures[i] = new GpuCommandSignature(command.MeshID, command.MaterialID, command.RenderPass);
            }

            return new GpuBackendParitySnapshot(backendName, visibleCount, drawCount, signatures);
        }

        public static bool AreEquivalent(
            in GpuBackendParitySnapshot lhs,
            in GpuBackendParitySnapshot rhs,
            out string reason)
        {
            if (lhs.VisibleCount != rhs.VisibleCount)
            {
                reason = $"Visible count mismatch: {lhs.BackendName}={lhs.VisibleCount} vs {rhs.BackendName}={rhs.VisibleCount}";
                return false;
            }

            if (lhs.DrawCount != rhs.DrawCount)
            {
                reason = $"Draw count mismatch: {lhs.BackendName}={lhs.DrawCount} vs {rhs.BackendName}={rhs.DrawCount}";
                return false;
            }

            if (lhs.SampledSignatures.Length != rhs.SampledSignatures.Length)
            {
                reason = $"Sample count mismatch: {lhs.BackendName}={lhs.SampledSignatures.Length} vs {rhs.BackendName}={rhs.SampledSignatures.Length}";
                return false;
            }

            for (int i = 0; i < lhs.SampledSignatures.Length; ++i)
            {
                GpuCommandSignature a = lhs.SampledSignatures[i];
                GpuCommandSignature b = rhs.SampledSignatures[i];
                if (a.MeshId != b.MeshId || a.MaterialId != b.MaterialId || a.RenderPass != b.RenderPass)
                {
                    reason =
                        $"Signature mismatch at sample {i}: " +
                        $"{lhs.BackendName}=({a.MeshId},{a.MaterialId},{a.RenderPass}) vs " +
                        $"{rhs.BackendName}=({b.MeshId},{b.MaterialId},{b.RenderPass})";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
    }
}