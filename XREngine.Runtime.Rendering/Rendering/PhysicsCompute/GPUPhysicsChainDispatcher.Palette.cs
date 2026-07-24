using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private static readonly PhysicsChainComputePass PartialPaletteSeedCompletionPass = new(
        PhysicsChainComputePassKind.BonePalettePublication,
        EMemoryBarrierMask.BufferUpdate | EMemoryBarrierMask.ShaderStorage);

    /// <summary>
    /// Seeds non-chain bones for partial renderer palettes directly on the GPU.
    /// Chain-owned mappings then overwrite their elements in the single global
    /// palette dispatch. Complete palettes need no seed copy.
    /// </summary>
    private bool SeedPartialGpuDrivenBonePalettes(IPhysicsChainComputeBackend backend)
    {
        if (_gpuDrivenSkinPaletteBuffer is null)
            return false;

        bool copiedAny = false;
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            uint elementCount = Math.Max(binding.BoneMatrixElementCount, 1u);
            if (!binding.DrivesCompleteBonePalette)
            {
                XRDataBuffer? source = binding.Renderer.SkinPaletteBuffer;
                uint copyElementCount = Math.Min(elementCount, source?.ElementCount ?? 0u);
                if (copyElementCount > 0u)
                {
                    if (!backend.EnsureGpuBufferReady(source!)
                        || !backend.EnsureGpuBufferReady(_gpuDrivenSkinPaletteBuffer))
                        return false;

                    nuint byteCount = checked((nuint)copyElementCount * (nuint)Unsafe.SizeOf<SkinPaletteMatrix>());
                    nint destinationOffset = checked((nint)((nuint)_gpuDrivenPaletteSliceBases[bindingIndex] * (nuint)Unsafe.SizeOf<SkinPaletteMatrix>()));
                    var copy = new PhysicsChainComputeBufferCopy(
                        source!,
                        0,
                        _gpuDrivenSkinPaletteBuffer,
                        destinationOffset,
                        byteCount);
                    if (!TryCopyBuffer(backend, copy, "partial-palette-seed"))
                        return false;

                    RecordGpuCopyBytes((long)byteCount, _currentDispatchGroupIsBatched);
                    copiedAny = true;
                }
            }
        }

        return !copiedAny || TryCompletePass(backend, PartialPaletteSeedCompletionPass);
    }
}
