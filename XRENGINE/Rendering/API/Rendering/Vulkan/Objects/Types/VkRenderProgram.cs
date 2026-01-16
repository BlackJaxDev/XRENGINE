using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const EProgramStageMask GraphicsStageMask =
        EProgramStageMask.VertexShaderBit |
        EProgramStageMask.TessControlShaderBit |
        EProgramStageMask.TessEvaluationShaderBit |
        EProgramStageMask.GeometryShaderBit |
        EProgramStageMask.FragmentShaderBit |
        EProgramStageMask.MeshShaderBit |
        EProgramStageMask.TaskShaderBit;

    public class VkRenderProgram(VulkanRenderer renderer, XRRenderProgram data) : VkObject<XRRenderProgram>(renderer, data)
    {
        private readonly Dictionary<XRShader, VkShader> _shaderCache = new();
        private readonly Dictionary<EProgramStageMask, VkShader> _stageLookup = new();
        private DescriptorSetLayout[] _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private readonly List<DescriptorBindingInfo> _programDescriptorBindings = new();
        private readonly Dictionary<string, AutoUniformBlockInfo> _autoUniformBlocks = new(StringComparer.Ordinal);

        public override VkObjectType Type => VkObjectType.Program;
        public override bool IsGenerated => true;
        public bool IsLinked { get; private set; }
        public PipelineLayout PipelineLayout => _pipelineLayout;
        public IReadOnlyList<DescriptorSetLayout> DescriptorSetLayouts => _descriptorSetLayouts;
        public IReadOnlyList<DescriptorBindingInfo> DescriptorBindings => _programDescriptorBindings;
        public IReadOnlyDictionary<string, AutoUniformBlockInfo> AutoUniformBlocks => _autoUniformBlocks;

        protected override uint CreateObjectInternal() => CacheObject(this);

        protected override void DeleteObjectInternal()
        {
            DestroyLayouts();
            RemoveCachedObject(BindingId);
        }

        protected override void LinkData()
        {
            Data.LinkRequested += OnLinkRequested;
            Data.UseRequested += OnUseRequested;
            Data.Shaders.PostAnythingAdded += ShaderAdded;
            Data.Shaders.PostAnythingRemoved += ShaderRemoved;

            foreach (XRShader shader in Data.Shaders)
                ShaderAdded(shader);
        }

        protected override void UnlinkData()
        {
            Data.LinkRequested -= OnLinkRequested;
            Data.UseRequested -= OnUseRequested;
            Data.Shaders.PostAnythingAdded -= ShaderAdded;
            Data.Shaders.PostAnythingRemoved -= ShaderRemoved;

            foreach (XRShader shader in Data.Shaders)
                ShaderRemoved(shader);

            DestroyLayouts();
        }

        private void ShaderAdded(XRShader shader)
        {
            if (_shaderCache.ContainsKey(shader))
                return;

            if (Renderer.GetOrCreateAPIRenderObject(shader) is not VkShader vkShader)
                return;

            _shaderCache.Add(shader, vkShader);
            IsLinked = false;
        }

        private void ShaderRemoved(XRShader shader)
        {
            if (_shaderCache.Remove(shader, out VkShader? vkShader) && vkShader is not null)
                vkShader.Destroy();

            IsLinked = false;
        }

        private void OnLinkRequested(XRRenderProgram program)
        {
            if (Engine.InvokeOnMainThread(() => OnLinkRequested(program)))
                return;

            if (!Link())
                Debug.LogWarning($"Failed to link Vulkan program '{Data.Name ?? "UnnamedProgram"}'.");
        }

        private void OnUseRequested(XRRenderProgram program)
        {
            if (Engine.InvokeOnMainThread(() => OnUseRequested(program)))
                return;

            if (!IsLinked)
                Link();
        }

        public bool Link()
        {
            if (IsLinked)
                return true;

            if (!Data.LinkReady)
                return false;

            if (_shaderCache.Count == 0)
            {
                Debug.LogWarning($"Cannot link Vulkan program '{Data.Name ?? "UnnamedProgram"}' because it contains no shaders.");
                return false;
            }

            foreach (VkShader shader in _shaderCache.Values)
                shader.Generate();

            BuildStageLookup();
            BuildDescriptorLayouts();

            IsLinked = true;
            return true;
        }

        private void BuildStageLookup()
        {
            _stageLookup.Clear();
            foreach (VkShader shader in _shaderCache.Values)
            {
                EProgramStageMask mask = ToProgramStageMask(shader.StageFlags);
                if (mask == EProgramStageMask.None)
                    continue;

                _stageLookup[mask] = shader;
            }
        }

        private void BuildDescriptorLayouts()
        {
            DestroyLayouts();

            IEnumerable<DescriptorBindingInfo> shaderBindings = EnumerateShaderDescriptorBindings();
            string programName = Data.Name ?? "UnnamedProgram";
            var result = BuildDescriptorLayoutsShared(Renderer, Device, shaderBindings, programName);

            _descriptorSetLayouts = result.Layouts;
            _programDescriptorBindings.Clear();
            _programDescriptorBindings.AddRange(result.Bindings);
            _autoUniformBlocks.Clear();
            foreach (VkShader shader in _shaderCache.Values)
            {
                if (shader.AutoUniformBlock is { } block)
                    _autoUniformBlocks[block.InstanceName] = block;
            }

            CreatePipelineLayout(_descriptorSetLayouts);
        }

        public bool TryGetAutoUniformBlock(string name, out AutoUniformBlockInfo block)
            => _autoUniformBlocks.TryGetValue(name, out block);

        private void CreatePipelineLayout(IReadOnlyList<DescriptorSetLayout> layouts)
        {
            if (_pipelineLayout.Handle != 0)
            {
                Api!.DestroyPipelineLayout(Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (layouts.Count == 0)
            {
                PipelineLayoutCreateInfo info = new() { SType = StructureType.PipelineLayoutCreateInfo };
                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
                return;
            }

            DescriptorSetLayout[] layoutArray = layouts.ToArray();
            fixed (DescriptorSetLayout* layoutPtr = layoutArray)
            {
                PipelineLayoutCreateInfo info = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)layoutArray.Length,
                    PSetLayouts = layoutPtr,
                };

                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
            }
        }

        private void DestroyLayouts()
        {
            if (_descriptorSetLayouts.Length > 0)
            {
                foreach (DescriptorSetLayout layout in _descriptorSetLayouts)
                    Api!.DestroyDescriptorSetLayout(Device, layout, null);

                _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
            }

            if (_pipelineLayout.Handle != 0)
            {
                Api!.DestroyPipelineLayout(Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            _programDescriptorBindings.Clear();
            IsLinked = false;
        }

        public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages()
            => GetShaderStages(EProgramStageMask.AllShaderBits);

        public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages(EProgramStageMask mask)
        {
            foreach (EProgramStageMask flag in EnumerateStages(mask))
            {
                if (_stageLookup.TryGetValue(flag, out VkShader? shader))
                    yield return shader.ShaderStageCreateInfo;
            }
        }

        private IEnumerable<DescriptorBindingInfo> EnumerateShaderDescriptorBindings()
        {
            foreach (VkShader shader in _shaderCache.Values)
            {
                foreach (DescriptorBindingInfo binding in shader.DescriptorBindings)
                    yield return binding;
            }
        }

        public Pipeline CreateGraphicsPipeline(ref GraphicsPipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
        {
            if (!Link())
                throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");

            PipelineShaderStageCreateInfo[] stages = GetShaderStages(GraphicsStageMask).ToArray();
            if (stages.Length == 0)
                throw new InvalidOperationException("Graphics pipeline creation requires at least one graphics shader stage.");

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
            if (!Link())
                throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");

            PipelineShaderStageCreateInfo computeStage = GetShaderStages(EProgramStageMask.ComputeShaderBit).SingleOrDefault();
            if (computeStage.Module.Handle == 0)
                throw new InvalidOperationException("Compute pipeline creation requires a compute shader stage.");

            pipelineInfo.Stage = computeStage;
            pipelineInfo.Layout = _pipelineLayout;

            Result result = Api!.CreateComputePipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

            return pipeline;
        }

        private static EProgramStageMask ToProgramStageMask(ShaderStageFlags stage)
            => stage switch
            {
                ShaderStageFlags.VertexBit => EProgramStageMask.VertexShaderBit,
                ShaderStageFlags.TessellationControlBit => EProgramStageMask.TessControlShaderBit,
                ShaderStageFlags.TessellationEvaluationBit => EProgramStageMask.TessEvaluationShaderBit,
                ShaderStageFlags.GeometryBit => EProgramStageMask.GeometryShaderBit,
                ShaderStageFlags.FragmentBit => EProgramStageMask.FragmentShaderBit,
                ShaderStageFlags.ComputeBit => EProgramStageMask.ComputeShaderBit,
                ShaderStageFlags.MeshBitNV => EProgramStageMask.MeshShaderBit,
                ShaderStageFlags.TaskBitNV => EProgramStageMask.TaskShaderBit,
                _ => EProgramStageMask.None
            };

    }

        private static DescriptorLayoutBuildResult BuildDescriptorLayoutsShared(VulkanRenderer renderer, Device device, IEnumerable<DescriptorBindingInfo> bindings, string programName)
        {
            Dictionary<(uint set, uint binding), DescriptorSetLayoutBindingBuilder> builders = new();
            foreach (DescriptorBindingInfo binding in bindings)
            {
                var key = (binding.Set, binding.Binding);
                if (!builders.TryGetValue(key, out DescriptorSetLayoutBindingBuilder? builder))
                {
                    builder = new DescriptorSetLayoutBindingBuilder(binding);
                    builders.Add(key, builder);
                }
                else
                {
                    builder.Merge(binding);
                }
            }

            if (builders.Count == 0)
                return new DescriptorLayoutBuildResult(Array.Empty<DescriptorSetLayout>(), new List<DescriptorBindingInfo>());

            List<DescriptorSetLayout> layouts = new();
            foreach (IGrouping<uint, DescriptorSetLayoutBindingBuilder> group in builders.Values.GroupBy(b => b.Set).OrderBy(g => g.Key))
            {
                DescriptorSetLayoutBinding[] vkBindings = group
                    .OrderBy(b => b.Binding)
                    .Select(b => b.ToBinding())
                    .ToArray();

                fixed (DescriptorSetLayoutBinding* bindingsPtr = vkBindings)
                {
                    DescriptorSetLayoutCreateInfo layoutInfo = new()
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)vkBindings.Length,
                        PBindings = bindingsPtr,
                    };

                    if (renderer.Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, out DescriptorSetLayout layout) != Result.Success)
                        throw new InvalidOperationException($"Failed to create descriptor set layout for program '{programName}'.");

                    layouts.Add(layout);
                }
            }

            List<DescriptorBindingInfo> mergedBindings = builders.Values
                .OrderBy(b => b.Set)
                .ThenBy(b => b.Binding)
                .Select(b => b.ToDescriptorBindingInfo())
                .ToList();

            return new DescriptorLayoutBuildResult(layouts.ToArray(), mergedBindings);
        }

        private sealed class DescriptorSetLayoutBindingBuilder
        {
            public uint Set { get; }
            public uint Binding { get; }
            public DescriptorType DescriptorType { get; }
            public uint Count { get; }
            public ShaderStageFlags StageFlags { get; private set; }

            public DescriptorSetLayoutBindingBuilder(DescriptorBindingInfo info)
            {
                Set = info.Set;
                Binding = info.Binding;
                DescriptorType = info.DescriptorType;
                Count = Math.Max(info.Count, 1u);
                StageFlags = info.StageFlags;
            }

            public void Merge(DescriptorBindingInfo info)
            {
                if (info.DescriptorType != DescriptorType || Math.Max(info.Count, 1u) != Count)
                    throw new InvalidOperationException($"Conflicting descriptor definitions detected for set {Set}, binding {Binding}.");

                StageFlags |= info.StageFlags;
            }

            public DescriptorSetLayoutBinding ToBinding()
                => new()
                {
                    Binding = Binding,
                    DescriptorType = DescriptorType,
                    DescriptorCount = Count,
                    StageFlags = StageFlags,
                };

            public DescriptorBindingInfo ToDescriptorBindingInfo()
                => new(Set, Binding, DescriptorType, StageFlags, Count, string.Empty);
        }

        private readonly record struct DescriptorLayoutBuildResult(DescriptorSetLayout[] Layouts, List<DescriptorBindingInfo> Bindings);

        private static readonly EProgramStageMask[] StageOrder =
        {
            EProgramStageMask.TaskShaderBit,
            EProgramStageMask.MeshShaderBit,
            EProgramStageMask.VertexShaderBit,
            EProgramStageMask.TessControlShaderBit,
            EProgramStageMask.TessEvaluationShaderBit,
            EProgramStageMask.GeometryShaderBit,
            EProgramStageMask.FragmentShaderBit,
            EProgramStageMask.ComputeShaderBit,
        };

        private static IEnumerable<EProgramStageMask> EnumerateStages(EProgramStageMask mask)
        {
            foreach (EProgramStageMask stage in StageOrder)
            {
                if (mask.HasFlag(stage))
                    yield return stage;
            }
        }

    }
