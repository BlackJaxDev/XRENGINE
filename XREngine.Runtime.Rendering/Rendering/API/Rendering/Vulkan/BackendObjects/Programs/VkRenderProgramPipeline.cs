using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public class VkRenderProgramPipeline(VulkanRenderer renderer, XRRenderProgramPipeline data) : VkObject<XRRenderProgramPipeline>(renderer, data)
    {
        private readonly Dictionary<EProgramStageMask, VkRenderProgram> _stagePrograms = new();
        private DescriptorSetLayout[] _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private readonly List<DescriptorBindingInfo> _descriptorBindings = new();
        private bool _layoutsDirty = true;

        public override VkObjectType Type => VkObjectType.ProgramPipeline;
        public override bool IsGenerated => IsActive;
        public PipelineLayout PipelineLayout => _pipelineLayout;

        protected override uint CreateObjectInternal() => CacheObject(this);

        protected override void DeleteObjectInternal()
        {
            DestroyLayouts();
            RemoveCachedObject(BindingId);
        }

        protected override void LinkData()
        {
            // XRRenderProgramPipeline currently does not raise events when programs are assigned.
            // The renderer explicitly calls Set/Clear to configure this pipeline.
        }

        protected override void UnlinkData()
        {
            _stagePrograms.Clear();
            DestroyLayouts();
        }

        public void Bind()
        {
            // Nothing to do yet. Vulkan pipelines are bound directly when recording commands.
        }

        public void Clear(EProgramStageMask mask)
        {
            bool removed = false;
            foreach (EProgramStageMask stage in EnumerateStages(mask))
            {
                _stagePrograms.Remove(stage);
                removed = true;
            }

            if (removed)
                MarkLayoutsDirty();
        }

        public void Set(EProgramStageMask mask, VkRenderProgram program)
        {
            if (!program.Link())
                throw new InvalidOperationException($"Program '{program.Data.Name ?? "UnnamedProgram"}' is not linkable.");

            foreach (EProgramStageMask stage in EnumerateStages(mask))
                _stagePrograms[stage] = program;

            MarkLayoutsDirty();
        }

        public IEnumerable<PipelineShaderStageCreateInfo> EnumerateShaderStages()
            => EnumerateShaderStages(EProgramStageMask.AllShaderBits);

        private IEnumerable<PipelineShaderStageCreateInfo> EnumerateShaderStages(EProgramStageMask mask)
        {
            foreach (EProgramStageMask stage in EnumerateStages(mask))
            {
                if (_stagePrograms.TryGetValue(stage, out VkRenderProgram? program))
                {
                    foreach (PipelineShaderStageCreateInfo stageInfo in program.GetShaderStages(stage))
                        yield return stageInfo;
                }
            }
        }

        public Pipeline CreateGraphicsPipeline(ref GraphicsPipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
        {
            EnsureLayouts();

            if (pipelineCache.Handle == 0)
                pipelineCache = Renderer.ActivePipelineCache;

            PipelineShaderStageCreateInfo[] stages = EnumerateShaderStages(GraphicsStageMask).ToArray();
            if (stages.Length == 0)
                throw new InvalidOperationException("Graphics pipeline creation requires configured shader stages.");

            fixed (PipelineShaderStageCreateInfo* stagesPtr = stages)
            {
                pipelineInfo.StageCount = (uint)stages.Length;
                pipelineInfo.PStages = stagesPtr;
                pipelineInfo.Layout = _pipelineLayout;

                Result result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
                if (result != Result.Success)
                    throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");

                return pipeline;
            }
        }

        public Pipeline CreateComputePipeline(ref ComputePipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
        {
            EnsureLayouts();

            if (pipelineCache.Handle == 0)
                pipelineCache = Renderer.ActivePipelineCache;

            PipelineShaderStageCreateInfo computeStage = EnumerateShaderStages(EProgramStageMask.ComputeShaderBit).SingleOrDefault();
            if (computeStage.Module.Handle == 0)
                throw new InvalidOperationException("Compute pipeline creation requires a compute shader stage.");

            pipelineInfo.Stage = computeStage;
            pipelineInfo.Layout = _pipelineLayout;

            Result result = Api!.CreateComputePipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

            return pipeline;
        }

        private void EnsureLayouts()
        {
            if (Renderer.IsDeviceLost)
                return;

            if (!_layoutsDirty)
                return;

            DestroyLayouts();

            if (_stagePrograms.Count == 0)
            {
                CreatePipelineLayout(Array.Empty<DescriptorSetLayout>());
                _layoutsDirty = false;
                return;
            }

            IEnumerable<DescriptorBindingInfo> bindingInfos = _stagePrograms.Values.SelectMany(p => p.DescriptorBindings);
            string pipelineName = Data.Name ?? "UnnamedPipeline";
            var result = BuildDescriptorLayoutsShared(Renderer, Device, bindingInfos, pipelineName);

            _descriptorSetLayouts = result.Layouts;
            _descriptorBindings.Clear();
            _descriptorBindings.AddRange(result.Bindings);

            CreatePipelineLayout(_descriptorSetLayouts);
            _layoutsDirty = false;
        }

        private void DestroyLayouts()
        {
            if (_descriptorSetLayouts.Length > 0)
            {
                foreach (DescriptorSetLayout layout in _descriptorSetLayouts)
                    Renderer.ReleaseCachedDescriptorSetLayout(layout);

                _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
            }

            if (_pipelineLayout.Handle != 0)
                DestroyPipelineLayout("VkRenderProgramPipeline.DestroyLayouts");

            _descriptorBindings.Clear();
            _layoutsDirty = true;
        }

        private void CreatePipelineLayout(DescriptorSetLayout[] layouts)
        {
            if (Renderer.IsDeviceLost)
                return;

            DestroyPipelineLayout("VkRenderProgramPipeline.CreatePipelineLayout");

            if (layouts.Length == 0)
            {
                PushConstantRange pushRange = CreateCommonPushConstantRange();
                PipelineLayoutCreateInfo info = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushRange
                };
                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException("Failed to create pipeline layout for pipeline object.");
                Renderer.TrackLivePipelineLayout(_pipelineLayout, "VkRenderProgramPipeline.PipelineLayout");
                return;
            }

            DescriptorSetLayout[] layoutArray = [.. layouts];
            fixed (DescriptorSetLayout* layoutPtr = layoutArray)
            {
                PushConstantRange pushRange = CreateCommonPushConstantRange();
                PipelineLayoutCreateInfo info = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)layoutArray.Length,
                    PSetLayouts = layoutPtr,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushRange
                };

                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException("Failed to create pipeline layout for pipeline object.");
                Renderer.TrackLivePipelineLayout(_pipelineLayout, "VkRenderProgramPipeline.PipelineLayout");
            }
        }

        private void DestroyPipelineLayout(string owner)
        {
            if (_pipelineLayout.Handle == 0)
                return;

            PipelineLayout pipelineLayout = _pipelineLayout;
            _pipelineLayout = default;

            if (Renderer.TryBeginDestroyPipelineLayout(pipelineLayout, owner))
                Api!.DestroyPipelineLayout(Device, pipelineLayout, null);
        }

        private static PushConstantRange CreateCommonPushConstantRange()
            => new()
            {
                StageFlags = CommonPushConstantStageFlags,
                Offset = 0,
                Size = CommonPushConstantSize
            };

        private void MarkLayoutsDirty()
            => _layoutsDirty = true;

    }
}
