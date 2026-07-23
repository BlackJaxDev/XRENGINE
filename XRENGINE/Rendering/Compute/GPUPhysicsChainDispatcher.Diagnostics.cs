using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private const int GpuDrivenPaletteDiagnosticLogLimit = 8;
    private const int GpuDrivenPaletteDiagnosticEntryLimit = 16;
    private int _gpuDrivenPaletteDiagnosticLogCount;

    /// <summary>
    /// Reads the previous frame's Vulkan-authored palette only when the explicit
    /// skinning diagnostic flag is enabled. Running before the next transactional
    /// batch avoids observing a buffer while its replacement dispatch is pending.
    /// </summary>
    private void DebugReadbackGpuDrivenBonePaletteIfRequested(IPhysicsChainComputeBackend backend)
    {
        if (!RenderDiagnosticsFlags.SkinningPrepassDiag
            || _gpuDrivenPaletteDiagnosticLogCount >= GpuDrivenPaletteDiagnosticLogLimit
            || backend.Renderer is not VulkanRenderer renderer
            || _gpuDrivenSkinPaletteBuffer is null
            || _gpuDrivenBoneInvBindMatricesBuffer is null
            || _particlesBuffer is null
            || _transformMatricesBuffer is null
            || _gpuDrivenBoneMappings.Count == 0)
            return;

        int paletteEntryCount = Math.Min(
            checked((int)Math.Min(_gpuDrivenSkinPaletteBuffer.ElementCount, _gpuDrivenBoneInvBindMatricesBuffer.ElementCount)),
            GpuDrivenPaletteDiagnosticEntryLimit);
        int particleEntryCount = Math.Min(
            checked((int)Math.Min(_particlesBuffer.ElementCount, _transformMatricesBuffer.ElementCount)),
            GpuDrivenPaletteDiagnosticEntryLimit);
        if (paletteEntryCount <= 0 || particleEntryCount <= 0)
            return;

        Span<SkinPaletteMatrix> palettes = stackalloc SkinPaletteMatrix[paletteEntryCount];
        Span<Matrix4x4> inverseBinds = stackalloc Matrix4x4[paletteEntryCount];
        Span<GPUParticleData> particles = stackalloc GPUParticleData[particleEntryCount];
        Span<Matrix4x4> transforms = stackalloc Matrix4x4[particleEntryCount];

        bool paletteRead = renderer.TryReadBufferBytesForDiagnostics(
            _gpuDrivenSkinPaletteBuffer,
            0u,
            MemoryMarshal.AsBytes(palettes),
            out string paletteRoute);
        bool inverseBindRead = renderer.TryReadBufferBytesForDiagnostics(
            _gpuDrivenBoneInvBindMatricesBuffer,
            0u,
            MemoryMarshal.AsBytes(inverseBinds),
            out string inverseBindRoute);
        bool particleRead = renderer.TryReadBufferBytesForDiagnostics(
            _particlesBuffer,
            0u,
            MemoryMarshal.AsBytes(particles),
            out string particleRoute);
        bool transformRead = renderer.TryReadBufferBytesForDiagnostics(
            _transformMatricesBuffer,
            0u,
            MemoryMarshal.AsBytes(transforms),
            out string transformRoute);

        if (!paletteRead || !inverseBindRead || !particleRead || !transformRead)
        {
            Debug.PhysicsWarningEvery(
                "PhysicsChain.VulkanPaletteDiagnosticReadback",
                TimeSpan.FromSeconds(2),
                "[PhysicsChainPaletteGpu] Vulkan diagnostic readback unavailable. " +
                "palette={0} inverseBind={1} particles={2} transforms={3}.",
                paletteRoute,
                inverseBindRoute,
                particleRoute,
                transformRoute);
            return;
        }

        ++_gpuDrivenPaletteDiagnosticLogCount;
        var message = new StringBuilder(768);
        message.Append("[PhysicsChainPaletteGpu] frameSample#")
            .Append(_gpuDrivenPaletteDiagnosticLogCount)
            .Append(" mappings=")
            .Append(_gpuDrivenBoneMappings.Count)
            .Append(" paletteEntries=")
            .Append(paletteEntryCount)
            .Append(" particleEntries=")
            .Append(particleEntryCount);

        int mappingCount = Math.Min(_gpuDrivenBoneMappings.Count, GpuDrivenPaletteDiagnosticEntryLimit);
        for (int mappingIndex = 0; mappingIndex < mappingCount; ++mappingIndex)
        {
            GPUDrivenBoneMappingData mapping = _gpuDrivenBoneMappings[mappingIndex];
            if ((uint)mapping.BoneMatrixIndex >= (uint)palettes.Length
                || (uint)mapping.ParticleIndex >= (uint)particles.Length)
                continue;

            SkinPaletteMatrix palette = palettes[mapping.BoneMatrixIndex];
            Matrix4x4 inverseBind = inverseBinds[mapping.BoneMatrixIndex];
            GPUParticleData particle = particles[mapping.ParticleIndex];
            Matrix4x4 transform = transforms[mapping.ParticleIndex];
            Matrix4x4.Invert(inverseBind, out Matrix4x4 bindWorld);
            Vector3 bindPosition = bindWorld.Translation;
            Vector3 palettePosition = new(
                Vector4.Dot(palette.Row0, new Vector4(bindPosition, 1.0f)),
                Vector4.Dot(palette.Row1, new Vector4(bindPosition, 1.0f)),
                Vector4.Dot(palette.Row2, new Vector4(bindPosition, 1.0f)));
            float positionError = Vector3.Distance(palettePosition, particle.Position);
            message.Append(" | map")
                .Append(mappingIndex)
                .Append(" bone=")
                .Append(mapping.BoneMatrixIndex)
                .Append(" particle=")
                .Append(mapping.ParticleIndex)
                .Append(" paletteT=")
                .Append(FormatVector(palette.Row0.W, palette.Row1.W, palette.Row2.W))
                .Append(" invBindT=")
                .Append(FormatVector(inverseBind.M41, inverseBind.M42, inverseBind.M43))
                .Append(" bindP=")
                .Append(FormatVector(bindPosition.X, bindPosition.Y, bindPosition.Z))
                .Append(" paletteP=")
                .Append(FormatVector(palettePosition.X, palettePosition.Y, palettePosition.Z))
                .Append(" err=")
                .Append(positionError.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" particleP=")
                .Append(FormatVector(particle.Position.X, particle.Position.Y, particle.Position.Z))
                .Append(" transformT=")
                .Append(FormatVector(transform.M41, transform.M42, transform.M43));
        }

        Debug.PhysicsWarning(message.ToString());
    }

    private static string FormatVector(float x, float y, float z)
        => FormattableString.Invariant($"({x:F3},{y:F3},{z:F3})");
}
