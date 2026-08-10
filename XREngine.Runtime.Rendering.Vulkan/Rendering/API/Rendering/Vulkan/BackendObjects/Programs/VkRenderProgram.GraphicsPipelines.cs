using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Vectors;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    public Pipeline CreateGraphicsPipeline(ref GraphicsPipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
    {
        if (!Link())
            throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");

        if (pipelineCache.Handle == 0)
            pipelineCache = BackendContext.Pipelines.ActivePipelineCache;

        uint colorAttachmentCount = 0;
        if (pipelineInfo.PNext is not null)
        {
            var renderingInfo = (PipelineRenderingCreateInfo*)pipelineInfo.PNext;
            if (renderingInfo->SType == StructureType.PipelineRenderingCreateInfo)
                colorAttachmentCount = renderingInfo->ColorAttachmentCount;
        }
        else if (pipelineInfo.RenderPass.Handle != 0)
        {
            colorAttachmentCount = BackendContext.ProgramServices.GetRenderPassColorAttachmentCount(pipelineInfo.RenderPass);
        }

        PipelineShaderStageCreateInfo[] stages = GetShaderStages(VulkanProgramUtilities.GraphicsStageMask).ToArray();
        if (colorAttachmentCount == 0)
            stages = stages.Where(static s => s.Stage != ShaderStageFlags.FragmentBit).ToArray();

        if (stages.Length == 0)
            throw new InvalidOperationException("Graphics pipeline creation requires at least one graphics shader stage.");

        // ── DIAGNOSTIC: optionally log stages when creating pipeline for dynamic rendering ──
        bool tracePipeCreate = XREngine.Rendering.RenderDiagnosticsFlags.VkTracePipeCreate;
        if (tracePipeCreate)
        {
            var stageNames = string.Join(", ", stages.Select(s => s.Stage.ToString()));
            var stageModules = string.Join(", ", stages.Select(s => $"{s.Stage}=0x{s.Module.Handle:X}"));

            string colorFormats = "<none>";
            Format depthFormat = Format.Undefined;
            Format stencilFormat = Format.Undefined;

            if (pipelineInfo.PNext is not null)
            {
                var renderingInfo = (PipelineRenderingCreateInfo*)pipelineInfo.PNext;
                if (renderingInfo->SType == StructureType.PipelineRenderingCreateInfo)
                {
                    colorAttachmentCount = renderingInfo->ColorAttachmentCount;
                    depthFormat = renderingInfo->DepthAttachmentFormat;
                    stencilFormat = renderingInfo->StencilAttachmentFormat;

                    if (colorAttachmentCount > 0 && renderingInfo->PColorAttachmentFormats is not null)
                    {
                        var formats = new string[colorAttachmentCount];
                        for (int i = 0; i < colorAttachmentCount; i++)
                            formats[i] = renderingInfo->PColorAttachmentFormats[i].ToString();
                        colorFormats = string.Join(",", formats);
                    }
                }
            }

            Debug.RenderingWarning("[PipeCreate] prog={0} renderPass=0x{1:X} stages={2} stageFlags=[{3}] stageModules=[{4}] colors={5} colorFormats=[{6}] depth={7} stencil={8}",
                Data.Name ?? "?prog",
                pipelineInfo.RenderPass.Handle,
                stages.Length,
                stageNames,
                stageModules,
                colorAttachmentCount,
                colorFormats,
                depthFormat,
                stencilFormat);
            Debug.RenderingWarning("[PipeCreate] prog={0} stageLabels=[{1}]",
                Data.Name ?? "?prog",
                DescribeShaderStages());
        }
        // ── END DIAGNOSTIC ──

        fixed (PipelineShaderStageCreateInfo* stagesPtr = stages)
        {
            pipelineInfo.StageCount = (uint)stages.Length;
            pipelineInfo.PStages = stagesPtr;
            pipelineInfo.Layout = _pipelineLayout;

            Result result;
            DescriptorHeapProgramLayout? descriptorHeapLayout = _descriptorHeapLayout;
            if (BackendContext.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
            {
                void* originalPipelinePNext = pipelineInfo.PNext;
                PipelineCreateFlags2CreateInfoNative flags2 = new()
                {
                    SType = VulkanDescriptorHeapExt.PipelineCreateFlags2CreateInfoSType,
                    PNext = originalPipelinePNext,
                    Flags = unchecked((ulong)pipelineInfo.Flags) | VulkanDescriptorHeapExt.PipelineCreate2DescriptorHeapBit,
                };
                pipelineInfo.PNext = &flags2;

                if (descriptorHeapLayout is { Mappings.Length: > 0 })
                {
                    fixed (DescriptorSetAndBindingMappingEXTNative* mappingPtr = descriptorHeapLayout.Mappings)
                    {
                        void** originalStagePNext = stackalloc void*[stages.Length];
                        ShaderDescriptorSetAndBindingMappingInfoEXTNative* mappingInfos = stackalloc ShaderDescriptorSetAndBindingMappingInfoEXTNative[stages.Length];
                        for (int i = 0; i < stages.Length; i++)
                        {
                            originalStagePNext[i] = stagesPtr[i].PNext;
                            mappingInfos[i] = new ShaderDescriptorSetAndBindingMappingInfoEXTNative
                            {
                                SType = VulkanDescriptorHeapExt.ShaderDescriptorSetAndBindingMappingInfoSType,
                                PNext = originalStagePNext[i],
                                MappingCount = (uint)descriptorHeapLayout.Mappings.Length,
                                Mappings = mappingPtr,
                            };
                            stagesPtr[i].PNext = mappingInfos + i;
                        }

                        result = BackendContext.Pipelines.CreateGraphicsPipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline mappedHeapPipeline);
                        for (int i = 0; i < stages.Length; i++)
                            stagesPtr[i].PNext = originalStagePNext[i];

                        pipelineInfo.PNext = originalPipelinePNext;

                        if (result != Result.Success)
                        {
                            WriteShaderDiagnostics($"vkCreateGraphicsPipelines failed result={result}");
                            throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
                        }

                        BackendContext.ProgramServices.RegisterPipeline(mappedHeapPipeline, "VkRenderProgram.GraphicsMappedHeap");
                        BackendContext.ProgramServices.NotifyPipelineCreated("graphics");
                        return mappedHeapPipeline;
                    }
                }

                result = BackendContext.Pipelines.CreateGraphicsPipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline heapPipeline);
                pipelineInfo.PNext = originalPipelinePNext;

                if (result != Result.Success)
                {
                    WriteShaderDiagnostics($"vkCreateGraphicsPipelines failed result={result}");
                    throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
                }

                BackendContext.ProgramServices.RegisterPipeline(heapPipeline, "VkRenderProgram.GraphicsHeap");
                BackendContext.ProgramServices.NotifyPipelineCreated("graphics");
                return heapPipeline;
            }

            result = BackendContext.Pipelines.CreateGraphicsPipelinesSynchronized(pipelineCache, ref pipelineInfo, out Pipeline pipeline);
            if (result != Result.Success)
            {
                WriteShaderDiagnostics($"vkCreateGraphicsPipelines failed result={result}");
                throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
            }

            BackendContext.ProgramServices.RegisterPipeline(pipeline, "VkRenderProgram.Graphics");
            BackendContext.ProgramServices.NotifyPipelineCreated("graphics");
            return pipeline;
        }
    }

}
