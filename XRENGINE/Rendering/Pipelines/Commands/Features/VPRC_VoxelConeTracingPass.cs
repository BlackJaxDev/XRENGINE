using System;
using System.Collections.Generic;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Placeholder voxel cone tracing pass. Ensures required resources exist and
    /// provides a hook for future voxelization and cone tracing integration.
    /// </summary>
    public class VPRC_VoxelConeTracingPass : ViewportRenderCommand
    {
        /// <summary>
        /// Name of the 3D texture that stores the voxelized scene volume.
        /// </summary>
        public string VolumeTextureName { get; set; } = DefaultRenderPipeline.VoxelConeTracingVolumeTextureName;

        /// <summary>
        /// Render passes that should contribute to the voxel volume.
        /// </summary>
        public IReadOnlyList<int>? RenderPasses { get; set; }
            = [(int)EDefaultRenderPass.OpaqueDeferred, (int)EDefaultRenderPass.OpaqueForward];

        private bool _gpuDispatch = true;
        public bool GpuDispatch
        {
            get => _gpuDispatch;
            set => SetField(ref _gpuDispatch, value);
        }

        /// <summary>
        /// If true, the voxel volume is reset every frame before voxelization.
        /// </summary>
        public bool ClearVolumeEachFrame { get; set; } = false;

        public void SetOptions(string volumeTextureName, IReadOnlyList<int> renderPasses, bool gpuDispatch, bool clearVolumeEachFrame)
        {
            VolumeTextureName = volumeTextureName;
            RenderPasses = renderPasses;
            GpuDispatch = gpuDispatch;
            ClearVolumeEachFrame = clearVolumeEachFrame;
        }

        protected override void Execute()
        {
            if (ActivePipelineInstance.Pipeline is not DefaultRenderPipeline defaultPipeline || !defaultPipeline.UsesVoxelConeTracing)
                return;

            if (RenderPasses is null || RenderPasses.Count == 0)
                return;

            if (!ActivePipelineInstance.TryGetTexture(VolumeTextureName, out var texture) || texture is not XRTexture3D voxelTexture)
                return;

            if (ActivePipelineInstance.RenderState.SceneCamera is null || ActivePipelineInstance.RenderState.RenderingScene is null)
                return;

            if (ClearVolumeEachFrame)
                voxelTexture.Clear(ColorF4.Transparent);

            XRMaterial voxelizationMaterial = defaultPipeline.GetVoxelConeTracingVoxelizationMaterial();

            void OnSettingUniforms(XRMaterialBase material, XRRenderProgram program)
            {
                program.Uniform("texture3D", 0);
                program.BindImageTexture(0u, voxelTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA8);
            }

            voxelizationMaterial.SettingUniforms += OnSettingUniforms;

            try
            {
                using var overrideTicket = ActivePipelineInstance.RenderState.PushOverrideMaterial(voxelizationMaterial);
                
                foreach (int renderPass in RenderPasses)
                {
                    if (_gpuDispatch)
                        ActivePipelineInstance.MeshRenderCommands.RenderGPU(renderPass);
                    else
                        ActivePipelineInstance.MeshRenderCommands.RenderCPU(renderPass);
                }
            }
            finally
            {
                voxelizationMaterial.SettingUniforms -= OnSettingUniforms;
            }

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);

            if (voxelTexture.AutoGenerateMipmaps)
            {
                voxelTexture.GenerateMipmapsGPU();
                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.TextureFetch);
            }
        }
    }
}
