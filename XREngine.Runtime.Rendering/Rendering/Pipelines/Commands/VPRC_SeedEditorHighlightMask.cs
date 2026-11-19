using System;
using XREngine.Rendering.RenderGraph;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Requires the editor mask and picking identity writes before a frame can use them.
/// A pending shader cannot leave a previous frame's editor identity visible.
/// </summary>
public sealed class VPRC_SeedEditorHighlightMask : VPRC_DispatchCompute
{
    private const string SeedPassName = "Advanced.EditorHighlightSeed";
    private readonly Func<bool> _authorSeed;

    public VPRC_SeedEditorHighlightMask()
        => _authorSeed = AuthorSeed;

    protected override void Execute()
    {
        try
        {
            ExecuteSeed();
        }
        catch (Exception exception)
        {
            throw new RequiredRenderCommandException("The required editor highlight mask seed failed.", exception);
        }
    }

    private void ExecuteSeed()
    {
        if (!ActivePipelineInstance.TryGetActiveRenderPassIndex(SeedPassName, out int passIndex))
            throw new InvalidOperationException("The editor highlight seed has no render-graph pass.");

        using IDisposable passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex);
        AbstractRenderer renderer = AbstractRenderer.Current
            ?? throw new InvalidOperationException("The editor highlight seed requires an active renderer.");
        bool accepted = renderer.TryExecuteRequiredGpuProducerBatch(
            _authorSeed, out XRGpuFence? fence, out Exception? failure);
        bool fenceAdopted = false;
        try
        {
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (!accepted)
                throw new RenderResourcePreparationPendingException(
                    "The editor highlight mask producer is not ready.");
            if (fence is null)
                throw new InvalidOperationException("The editor highlight mask producer returned no completion fence.");
            if (!ActivePipelineInstance.TryAdoptEditorPickingSeedFence(fence))
                throw new InvalidOperationException(
                    "The editor highlight seed has no matching frozen package candidate.");
            fenceAdopted = true;
        }
        finally
        {
            // The candidate keeps the same rental until the ordered marker
            // reports native queue acceptance or an aborted chain releases it.
            if (!fenceAdopted)
                fence?.Dispose();
        }
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        RenderPassBuilder builder = context.GetOrCreateSyntheticPass(SeedPassName, ERenderGraphPassStage.Compute);
        builder.UseEngineDescriptors()
            .SampleStorageTexture(MakeTextureResource(AdvancedVisibilityResourceNames.Metadata))
            .SampleStorageTexture(MakeTextureResource(AdvancedVisibilityResourceNames.Identity))
            .WriteTexture(MakeTextureResource(AdvancedRenderPipeline.EditorHighlightMaskTextureName))
            .WriteTexture(MakeTextureResource(AdvancedRenderPipeline.EditorPickingIdentityTextureName))
            .DependsOn(context.GetOrCreateSyntheticPass(
                "Advanced.NativeOpaqueShading", ERenderGraphPassStage.Compute).PassIndex);
    }

    private bool AuthorSeed()
    {
        AbstractRenderer renderer = AbstractRenderer.Current
            ?? throw new InvalidOperationException("The editor highlight seed has no active renderer.");
        if (!renderer.SupportsEditorPickingIdentity(out string? supportFailure))
            throw new NotSupportedException(
                supportFailure ?? "The renderer cannot write the editor identity attachment.");
        renderer.ResetComputeProgramBindings(ComputeProgram);
        try
        {
            _ = renderer.GetOrCreateAPIRenderObject(ComputeProgram, generateNow: true)
                ?? throw new NotSupportedException("The editor highlight seed has no backend program wrapper.");
            ActivePipelineInstance.RenderState.ApplyScopedProgramBindings(ComputeProgram);
            if (Textures is { } textures)
            {
                for (int index = 0; index < textures.Count; ++index)
                {
                    ComputeTextureBinding binding = textures[index];
                    ComputeProgram.BindImageTexture(binding.Unit, binding.TextureFactory(), binding.Level,
                        binding.Layer.HasValue, binding.Layer.GetValueOrDefault(), binding.Access, binding.Format);
                }
            }
            ERendererComputeEnqueueStatus status = renderer.TryDispatchCompute(ComputeProgram, X(), Y(), Z());
            if (status == ERendererComputeEnqueueStatus.ProgramPending)
            {
                XRRenderProgram.ShaderProgramBackendStatus backend = ComputeProgram.ShaderMetadata.Backend;
                if (backend.Stage is EShaderProgramBackendStage.Failed or
                    EShaderProgramBackendStage.BinaryUploadFailed or EShaderProgramBackendStage.Abandoned)
                    throw new NotSupportedException(
                        $"The editor highlight seed failed to compile: {backend.FailureReason ?? backend.Detail ?? backend.Stage.ToString()}");
                return false;
            }
            if (status != ERendererComputeEnqueueStatus.Enqueued)
                throw new NotSupportedException($"The editor highlight seed could not enqueue: {status}.");
            ERendererComputeEnqueueStatus barrierStatus = renderer.TryMemoryBarrier(
                EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.Framebuffer | EMemoryBarrierMask.TextureFetch);
            if (barrierStatus != ERendererComputeEnqueueStatus.Enqueued)
                throw new NotSupportedException($"The editor highlight seed barrier could not enqueue: {barrierStatus}.");
            return true;
        }
        finally
        {
            renderer.ResetComputeProgramBindings(ComputeProgram);
        }
    }
}
