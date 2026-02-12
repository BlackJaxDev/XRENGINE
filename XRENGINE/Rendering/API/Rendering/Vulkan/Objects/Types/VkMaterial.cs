using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkMaterial(VulkanRenderer api, XRMaterial data) : VkObject<XRMaterial>(api, data)
        {
            private readonly object _stateSync = new();
            private readonly Dictionary<uint, ProgramDescriptorState> _programStates = new();
            private readonly Dictionary<string, ShaderVar> _parameterLookup = new(StringComparer.Ordinal);
            private readonly HashSet<string> _warnedMessages = new(StringComparer.Ordinal);
            private bool _materialDirty = true;

            private sealed class ProgramDescriptorState
            {
                public required VkRenderProgram Program { get; init; }
                public required IReadOnlyList<DescriptorBindingInfo> Bindings { get; init; }
                public required DescriptorSet[][] DescriptorSets { get; init; }
                public required Dictionary<(uint set, uint binding), UniformBindingResource> UniformBindings { get; init; }
                public required int FrameCount { get; init; }
                public required int SetCount { get; init; }
                public required ulong SchemaFingerprint { get; init; }
                public DescriptorPool DescriptorPool;
                public bool Dirty = true;
            }

            private sealed class UniformBindingResource
            {
                public required string Name { get; init; }
                public required ShaderVar Parameter { get; init; }
                public required uint Size { get; init; }
                public required Silk.NET.Vulkan.Buffer[] Buffers { get; init; }
                public required DeviceMemory[] Memories { get; init; }
            }

            public override VkObjectType Type => VkObjectType.Material;
            public override bool IsGenerated => IsActive;
            protected override uint CreateObjectInternal() => CacheObject(this);

            protected override void DeleteObjectInternal()
            {
                lock (_stateSync)
                    DestroyAllProgramStates();
                RemoveCachedObject(BindingId);
            }

            protected override void LinkData()
            {
                Data.Textures.PostAnythingAdded += OnTextureChanged;
                Data.Textures.PostAnythingRemoved += OnTextureChanged;
                Data.PropertyChanged += OnMaterialPropertyChanged;
                RebuildParameterLookup();
                _materialDirty = true;
            }

            protected override void UnlinkData()
            {
                Data.Textures.PostAnythingAdded -= OnTextureChanged;
                Data.Textures.PostAnythingRemoved -= OnTextureChanged;
                Data.PropertyChanged -= OnMaterialPropertyChanged;
                UnsubscribeParameterEvents();

                lock (_stateSync)
                    DestroyAllProgramStates();
            }

            public bool TryBindDescriptorSets(CommandBuffer commandBuffer, VkRenderProgram program, int frameIndex, uint firstSet = 0)
            {
                if (program is null || !program.Link() || Renderer.swapChainImages is null || Renderer.swapChainImages.Length == 0)
                    return false;

                if (!TryEnsureState(program, out ProgramDescriptorState? state))
                    return false;

                int resolvedFrame = Math.Clamp(frameIndex, 0, state.FrameCount - 1);

                if (!UpdateUniformBuffers(state, resolvedFrame))
                    return false;

                if ((state.Dirty || _materialDirty) && !UpdateDescriptorSets(state))
                    return false;

                state.Dirty = false;
                _materialDirty = false;

                DescriptorSet[] sets = state.DescriptorSets[resolvedFrame];
                if (sets.Length == 0)
                    return true;

                fixed (DescriptorSet* setPtr = sets)
                {
                    Api!.CmdBindDescriptorSets(
                        commandBuffer,
                        PipelineBindPoint.Graphics,
                        program.PipelineLayout,
                        firstSet,
                        (uint)sets.Length,
                        setPtr,
                        0,
                        null);
                }

                return true;
            }

            private void OnTextureChanged(XRTexture? _)
            {
                lock (_stateSync)
                {
                    foreach (ProgramDescriptorState state in _programStates.Values)
                        state.Dirty = true;
                    _materialDirty = true;
                }
            }

            private void OnMaterialPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(XRMaterial.Parameters):
                        RebuildParameterLookup();
                        lock (_stateSync)
                        {
                            DestroyAllProgramStates();
                            _materialDirty = true;
                        }
                        break;
                    case nameof(XRMaterial.Textures):
                        OnTextureChanged(null);
                        break;
                }
            }

            private void RebuildParameterLookup()
            {
                lock (_stateSync)
                {
                    UnsubscribeParameterEvents();
                    _parameterLookup.Clear();

                    foreach (ShaderVar parameter in Data.Parameters)
                    {
                        if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name))
                            continue;

                        _parameterLookup[parameter.Name] = parameter;
                        parameter.ValueChanged += OnParameterValueChanged;
                    }
                }
            }

            private void UnsubscribeParameterEvents()
            {
                foreach (ShaderVar parameter in _parameterLookup.Values)
                    parameter.ValueChanged -= OnParameterValueChanged;
            }

            private void OnParameterValueChanged(ShaderVar _)
            {
                lock (_stateSync)
                    _materialDirty = true;
            }

            private bool TryEnsureState(VkRenderProgram program, out ProgramDescriptorState? state)
            {
                lock (_stateSync)
                {
                    if (program.DescriptorSetLayouts is null || program.DescriptorSetLayouts.Count == 0 || program.DescriptorBindings.Count == 0)
                    {
                        state = null;
                        return false;
                    }

                    int frameCount = Renderer.swapChainImages!.Length;
                    int setCount = program.DescriptorSetLayouts.Count;
                    ulong fingerprint = ComputeSchemaFingerprint(program.DescriptorBindings, frameCount, setCount);
                    uint key = program.BindingId;

                    if (_programStates.TryGetValue(key, out ProgramDescriptorState? existing) &&
                        existing.SchemaFingerprint == fingerprint &&
                        existing.FrameCount == frameCount &&
                        existing.SetCount == setCount)
                    {
                        state = existing;
                        return true;
                    }

                    if (_programStates.TryGetValue(key, out ProgramDescriptorState? stale))
                    {
                        DestroyProgramState(stale);
                        _programStates.Remove(key);
                    }

                    if (!TryCreateProgramState(program, out ProgramDescriptorState? created))
                    {
                        state = null;
                        return false;
                    }

                    _programStates[key] = created;
                    state = created;
                    return true;
                }
            }

            private bool TryCreateProgramState(VkRenderProgram program, out ProgramDescriptorState? state)
            {
                state = null;

                int frameCount = Renderer.swapChainImages?.Length ?? 0;
                if (frameCount <= 0)
                    return false;

                int setCount = program.DescriptorSetLayouts.Count;
                IReadOnlyList<DescriptorBindingInfo> bindings = program.DescriptorBindings;

                if (!CanHandleProgramBindings(program, bindings))
                    return false;

                DescriptorPoolSize[] poolSizes = BuildDescriptorPoolSizes(bindings, frameCount);
                if (poolSizes.Length == 0)
                    return false;

                DescriptorPool descriptorPool;
                fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
                {
                    DescriptorPoolCreateInfo poolInfo = new()
                    {
                        SType = StructureType.DescriptorPoolCreateInfo,
                        Flags = program.DescriptorSetsRequireUpdateAfterBind
                            ? DescriptorPoolCreateFlags.UpdateAfterBindBit
                            : 0,
                        PoolSizeCount = (uint)poolSizes.Length,
                        PPoolSizes = poolSizesPtr,
                        MaxSets = (uint)(setCount * frameCount),
                    };

                    if (Api!.CreateDescriptorPool(Device, ref poolInfo, null, out descriptorPool) != Result.Success)
                    {
                        WarnOnce("Failed to create Vulkan descriptor pool for material.");
                        return false;
                    }
                }

                DescriptorSetLayout[] layoutArray = [.. program.DescriptorSetLayouts];
                DescriptorSet[][] descriptorSets = new DescriptorSet[frameCount][];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    DescriptorSet[] frameSets = new DescriptorSet[setCount];

                    fixed (DescriptorSetLayout* layoutPtr = layoutArray)
                    fixed (DescriptorSet* setPtr = frameSets)
                    {
                        DescriptorSetAllocateInfo allocInfo = new()
                        {
                            SType = StructureType.DescriptorSetAllocateInfo,
                            DescriptorPool = descriptorPool,
                            DescriptorSetCount = (uint)setCount,
                            PSetLayouts = layoutPtr,
                        };

                        if (Api!.AllocateDescriptorSets(Device, ref allocInfo, setPtr) != Result.Success)
                        {
                            Api.DestroyDescriptorPool(Device, descriptorPool, null);
                            WarnOnce("Failed to allocate Vulkan descriptor sets for material.");
                            return false;
                        }
                    }

                    descriptorSets[frame] = frameSets;
                }

                if (!TryCreateUniformResources(bindings, frameCount, out Dictionary<(uint set, uint binding), UniformBindingResource> uniformResources))
                {
                    Api!.DestroyDescriptorPool(Device, descriptorPool, null);
                    return false;
                }

                state = new ProgramDescriptorState
                {
                    Program = program,
                    Bindings = bindings,
                    DescriptorSets = descriptorSets,
                    UniformBindings = uniformResources,
                    FrameCount = frameCount,
                    SetCount = setCount,
                    SchemaFingerprint = ComputeSchemaFingerprint(bindings, frameCount, setCount),
                    DescriptorPool = descriptorPool,
                    Dirty = true,
                };

                return true;
            }

            private bool CanHandleProgramBindings(VkRenderProgram program, IReadOnlyList<DescriptorBindingInfo> bindings)
            {
                foreach (DescriptorBindingInfo binding in bindings)
                {
                    uint descriptorCount = Math.Max(binding.Count, 1u);

                    switch (binding.DescriptorType)
                    {
                        case DescriptorType.CombinedImageSampler:
                        case DescriptorType.SampledImage:
                        case DescriptorType.StorageImage:
                        case DescriptorType.UniformTexelBuffer:
                        case DescriptorType.StorageTexelBuffer:
                            if (descriptorCount > 32)
                                return false;
                            break;

                        case DescriptorType.UniformBuffer:
                        {
                            if (descriptorCount != 1 || string.IsNullOrWhiteSpace(binding.Name))
                                return false;

                            string normalized = NormalizeEngineUniformName(binding.Name);
                            if (Enum.TryParse(normalized, out EEngineUniform _))
                                return false;

                            if (program.TryGetAutoUniformBlock(binding.Name, out _))
                                return false;

                            if (!_parameterLookup.TryGetValue(binding.Name, out ShaderVar? parameter))
                                return false;

                            if (GetShaderVarSize(parameter) == 0)
                                return false;
                            break;
                        }

                        default:
                            return false;
                    }
                }

                return true;
            }

            private static DescriptorPoolSize[] BuildDescriptorPoolSizes(IReadOnlyList<DescriptorBindingInfo> bindings, int frameCount)
            {
                Dictionary<DescriptorType, uint> counts = new();
                foreach (DescriptorBindingInfo binding in bindings)
                {
                    uint count = Math.Max(binding.Count, 1u) * (uint)frameCount;
                    if (counts.TryGetValue(binding.DescriptorType, out uint existing))
                        counts[binding.DescriptorType] = existing + count;
                    else
                        counts[binding.DescriptorType] = count;
                }

                DescriptorPoolSize[] sizes = new DescriptorPoolSize[counts.Count];
                int i = 0;
                foreach ((DescriptorType type, uint count) in counts)
                    sizes[i++] = new DescriptorPoolSize { Type = type, DescriptorCount = count };
                return sizes;
            }

            private static ulong ComputeSchemaFingerprint(IReadOnlyList<DescriptorBindingInfo> bindings, int frameCount, int setCount)
            {
                HashCode hash = new();
                hash.Add(frameCount);
                hash.Add(setCount);

                foreach (DescriptorBindingInfo binding in bindings.OrderBy(b => b.Set).ThenBy(b => b.Binding))
                {
                    hash.Add(binding.Set);
                    hash.Add(binding.Binding);
                    hash.Add((int)binding.DescriptorType);
                    hash.Add(binding.Count);
                    hash.Add((int)binding.StageFlags);
                    hash.Add(binding.Name);
                }

                return unchecked((ulong)hash.ToHashCode());
            }

            private bool TryCreateUniformResources(
                IReadOnlyList<DescriptorBindingInfo> bindings,
                int frameCount,
                out Dictionary<(uint set, uint binding), UniformBindingResource> resources)
            {
                resources = new Dictionary<(uint set, uint binding), UniformBindingResource>();

                foreach (DescriptorBindingInfo binding in bindings)
                {
                    if (binding.DescriptorType != DescriptorType.UniformBuffer)
                        continue;

                    if (string.IsNullOrWhiteSpace(binding.Name) || !_parameterLookup.TryGetValue(binding.Name, out ShaderVar? parameter))
                    {
                        WarnOnce($"Material uniform binding '{binding.Name}' could not be resolved.");
                        DestroyUniformResources(resources);
                        return false;
                    }

                    uint valueSize = GetShaderVarSize(parameter);
                    if (valueSize == 0)
                    {
                        WarnOnce($"Unsupported material uniform type '{parameter.TypeName}' for binding '{binding.Name}'.");
                        DestroyUniformResources(resources);
                        return false;
                    }

                    uint bufferSize = valueSize;

                    Silk.NET.Vulkan.Buffer[] buffers = new Silk.NET.Vulkan.Buffer[frameCount];
                    DeviceMemory[] memories = new DeviceMemory[frameCount];

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        (buffers[frame], memories[frame]) = Renderer.CreateBuffer(
                            bufferSize,
                            BufferUsageFlags.UniformBufferBit,
                            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                            null);
                    }

                    resources[(binding.Set, binding.Binding)] = new UniformBindingResource
                    {
                        Name = binding.Name,
                        Parameter = parameter,
                        Size = bufferSize,
                        Buffers = buffers,
                        Memories = memories
                    };
                }

                return true;
            }

            private bool UpdateUniformBuffers(ProgramDescriptorState state, int frameIndex)
            {
                foreach (UniformBindingResource resource in state.UniformBindings.Values)
                {
                    if (frameIndex < 0 || frameIndex >= resource.Buffers.Length)
                        return false;

                    DeviceMemory memory = resource.Memories[frameIndex];
                    if (memory.Handle == 0)
                        return false;

                    void* mapped;
                    if (Api!.MapMemory(Device, memory, 0, resource.Size, 0, &mapped) != Result.Success)
                        return false;

                    try
                    {
                        Span<byte> data = new(mapped, (int)resource.Size);
                        data.Clear();
                        if (!TryWriteShaderVar(data, resource.Parameter))
                            return false;
                    }
                    finally
                    {
                        Api.UnmapMemory(Device, memory);
                    }
                }

                return true;
            }

            private bool UpdateDescriptorSets(ProgramDescriptorState state)
            {
                for (int frame = 0; frame < state.FrameCount; frame++)
                {
                    if (!UpdateFrameDescriptorSet(state, frame))
                        return false;
                }

                return true;
            }

            private bool UpdateFrameDescriptorSet(ProgramDescriptorState state, int frameIndex)
            {
                List<WriteDescriptorSet> writes = new();
                List<DescriptorBufferInfo> bufferInfos = new();
                List<DescriptorImageInfo> imageInfos = new();
                List<BufferView> texelBufferViews = new();
                List<(int writeIndex, int bufferIndex)> bufferMap = new();
                List<(int writeIndex, int imageIndex)> imageMap = new();
                List<(int writeIndex, int texelIndex)> texelMap = new();

                foreach (DescriptorBindingInfo binding in state.Bindings)
                {
                    if (binding.Set >= state.DescriptorSets[frameIndex].Length)
                        return false;

                    uint descriptorCount = Math.Max(binding.Count, 1u);

                    switch (binding.DescriptorType)
                    {
                        case DescriptorType.UniformBuffer:
                        {
                            if (!state.UniformBindings.TryGetValue((binding.Set, binding.Binding), out UniformBindingResource? resource))
                                return false;

                            bufferMap.Add((writes.Count, bufferInfos.Count));
                            bufferInfos.Add(new DescriptorBufferInfo
                            {
                                Buffer = resource.Buffers[frameIndex],
                                Offset = 0,
                                Range = resource.Size,
                            });

                            writes.Add(new WriteDescriptorSet
                            {
                                SType = StructureType.WriteDescriptorSet,
                                DstSet = state.DescriptorSets[frameIndex][binding.Set],
                                DstBinding = binding.Binding,
                                DescriptorCount = 1,
                                DescriptorType = DescriptorType.UniformBuffer,
                            });
                            break;
                        }

                        case DescriptorType.CombinedImageSampler:
                        case DescriptorType.SampledImage:
                        case DescriptorType.StorageImage:
                        {
                            int imageStart = imageInfos.Count;
                            for (int i = 0; i < descriptorCount; i++)
                            {
                                if (!TryResolveTextureInfo(binding, binding.DescriptorType, i, out DescriptorImageInfo info))
                                    return false;
                                imageInfos.Add(info);
                            }

                            imageMap.Add((writes.Count, imageStart));
                            writes.Add(new WriteDescriptorSet
                            {
                                SType = StructureType.WriteDescriptorSet,
                                DstSet = state.DescriptorSets[frameIndex][binding.Set],
                                DstBinding = binding.Binding,
                                DescriptorCount = descriptorCount,
                                DescriptorType = binding.DescriptorType,
                            });
                            break;
                        }

                        case DescriptorType.UniformTexelBuffer:
                        case DescriptorType.StorageTexelBuffer:
                        {
                            int texelStart = texelBufferViews.Count;
                            for (int i = 0; i < descriptorCount; i++)
                            {
                                if (!TryResolveTexelBufferInfo(binding, i, out BufferView texelView))
                                    return false;
                                texelBufferViews.Add(texelView);
                            }

                            texelMap.Add((writes.Count, texelStart));
                            writes.Add(new WriteDescriptorSet
                            {
                                SType = StructureType.WriteDescriptorSet,
                                DstSet = state.DescriptorSets[frameIndex][binding.Set],
                                DstBinding = binding.Binding,
                                DescriptorCount = descriptorCount,
                                DescriptorType = binding.DescriptorType,
                            });
                            break;
                        }
                    }
                }

                WriteDescriptorSet[] writeArray = writes.Count == 0 ? Array.Empty<WriteDescriptorSet>() : [.. writes];
                DescriptorBufferInfo[] bufferArray = bufferInfos.Count == 0 ? Array.Empty<DescriptorBufferInfo>() : [.. bufferInfos];
                DescriptorImageInfo[] imageArray = imageInfos.Count == 0 ? Array.Empty<DescriptorImageInfo>() : [.. imageInfos];
                BufferView[] texelArray = texelBufferViews.Count == 0 ? Array.Empty<BufferView>() : [.. texelBufferViews];

                fixed (WriteDescriptorSet* writePtr = writeArray)
                fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
                fixed (DescriptorImageInfo* imagePtr = imageArray)
                fixed (BufferView* texelPtr = texelArray)
                {
                    foreach ((int writeIndex, int bufferIndex) in bufferMap)
                        writePtr[writeIndex].PBufferInfo = bufferPtr + bufferIndex;

                    foreach ((int writeIndex, int imageIndex) in imageMap)
                        writePtr[writeIndex].PImageInfo = imagePtr + imageIndex;

                    foreach ((int writeIndex, int texelIndex) in texelMap)
                        writePtr[writeIndex].PTexelBufferView = texelPtr + texelIndex;

                    if (writeArray.Length > 0)
                        Api!.UpdateDescriptorSets(Device, (uint)writeArray.Length, writePtr, 0, null);
                }

                return true;
            }

            private bool TryResolveTextureInfo(DescriptorBindingInfo binding, DescriptorType descriptorType, int arrayIndex, out DescriptorImageInfo imageInfo)
            {
                imageInfo = default;
                if (!TryResolveBoundTexture(binding, arrayIndex, out XRTexture? texture) || texture is null)
                {
                    WarnOnce($"No texture available for material descriptor binding '{binding.Name}'.");
                    return false;
                }

                return TryCreateTextureDescriptor(texture, descriptorType, out imageInfo);
            }

            private bool TryResolveTexelBufferInfo(DescriptorBindingInfo binding, int arrayIndex, out BufferView texelView)
            {
                texelView = default;
                if (!TryResolveBoundTexture(binding, arrayIndex, out XRTexture? texture) || texture is null)
                {
                    WarnOnce($"No texture available for material texel descriptor binding '{binding.Name}'.");
                    return false;
                }

                return TryCreateTexelBufferDescriptor(texture, out texelView);
            }

            private bool TryResolveBoundTexture(DescriptorBindingInfo binding, int arrayIndex, out XRTexture? texture)
            {
                texture = null;
                if (Data.Textures.Count <= 0)
                    return false;

                int index = (int)binding.Binding + arrayIndex;
                if (index >= 0 && index < Data.Textures.Count)
                    texture = Data.Textures[index];

                texture ??= Data.Textures.FirstOrDefault(t => t is not null);
                return texture is not null;
            }

            private bool TryCreateTextureDescriptor(XRTexture texture, DescriptorType descriptorType, out DescriptorImageInfo imageInfo)
            {
                imageInfo = default;

                bool includeSampler = descriptorType == DescriptorType.CombinedImageSampler;
                ImageLayout layout = descriptorType == DescriptorType.StorageImage
                    ? ImageLayout.General
                    : ImageLayout.ShaderReadOnlyOptimal;

                if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
                {
                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' does not have a Vulkan image wrapper.");
                    return false;
                }

                imageInfo = new DescriptorImageInfo
                {
                    ImageLayout = layout,
                    ImageView = source.DescriptorView,
                    Sampler = includeSampler ? source.DescriptorSampler : default,
                };
                return imageInfo.ImageView.Handle != 0;
            }

            private bool TryCreateTexelBufferDescriptor(XRTexture texture, out BufferView texelView)
            {
                texelView = default;

                if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
                {
                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' does not have a Vulkan texel-buffer wrapper.");
                    return false;
                }

                texelView = source.DescriptorBufferView;
                return texelView.Handle != 0;
            }

            private void DestroyAllProgramStates()
            {
                foreach (ProgramDescriptorState state in _programStates.Values)
                    DestroyProgramState(state);
                _programStates.Clear();
            }

            private void DestroyProgramState(ProgramDescriptorState state)
            {
                DestroyUniformResources(state.UniformBindings);

                if (state.DescriptorPool.Handle != 0)
                    Api!.DestroyDescriptorPool(Device, state.DescriptorPool, null);
            }

            private void DestroyUniformResources(Dictionary<(uint set, uint binding), UniformBindingResource> resources)
            {
                foreach (UniformBindingResource resource in resources.Values)
                {
                    for (int i = 0; i < resource.Buffers.Length; i++)
                    {
                        if (resource.Buffers[i].Handle != 0)
                            Api!.DestroyBuffer(Device, resource.Buffers[i], null);
                        if (resource.Memories[i].Handle != 0)
                            Api!.FreeMemory(Device, resource.Memories[i], null);
                    }
                }
            }

            private static string NormalizeEngineUniformName(string name)
            {
                const string suffix = "_VTX";
                return name.EndsWith(suffix, StringComparison.Ordinal)
                    ? name[..^suffix.Length]
                    : name;
            }

            private static uint GetShaderVarSize(ShaderVar parameter)
                => parameter.TypeName switch
                {
                    EShaderVarType._float or EShaderVarType._int or EShaderVarType._uint or EShaderVarType._bool => 4,
                    EShaderVarType._vec2 or EShaderVarType._ivec2 or EShaderVarType._uvec2 => 8,
                    EShaderVarType._vec3 or EShaderVarType._vec4 or EShaderVarType._ivec3 or EShaderVarType._ivec4 or EShaderVarType._uvec3 or EShaderVarType._uvec4 => 16,
                    EShaderVarType._mat4 => 64,
                    _ => 0,
                };

            private static bool TryWriteShaderVar(Span<byte> destination, ShaderVar parameter)
            {
                uint requiredSize = GetShaderVarSize(parameter);
                if (requiredSize == 0 || destination.Length < requiredSize)
                    return false;

                ref byte start = ref destination[0];
                object value = parameter.GenericValue;

                switch (parameter.TypeName)
                {
                    case EShaderVarType._float:
                        Unsafe.WriteUnaligned(ref start, Convert.ToSingle(value));
                        return true;
                    case EShaderVarType._int:
                        Unsafe.WriteUnaligned(ref start, Convert.ToInt32(value));
                        return true;
                    case EShaderVarType._uint:
                        Unsafe.WriteUnaligned(ref start, Convert.ToUInt32(value));
                        return true;
                    case EShaderVarType._bool:
                        Unsafe.WriteUnaligned(ref start, Convert.ToBoolean(value) ? 1 : 0);
                        return true;
                    case EShaderVarType._vec2 when value is Vector2 v2:
                        Unsafe.WriteUnaligned(ref start, v2);
                        return true;
                    case EShaderVarType._vec3 when value is Vector3 v3:
                        Unsafe.WriteUnaligned(ref start, new Vector4(v3, 0f));
                        return true;
                    case EShaderVarType._vec4 when value is Vector4 v4:
                        Unsafe.WriteUnaligned(ref start, v4);
                        return true;
                    case EShaderVarType._ivec2 when value is IVector2 iv2:
                        Unsafe.WriteUnaligned(ref start, iv2);
                        return true;
                    case EShaderVarType._ivec3 when value is IVector3 iv3:
                        Unsafe.WriteUnaligned(ref start, new IVector4(iv3.X, iv3.Y, iv3.Z, 0));
                        return true;
                    case EShaderVarType._ivec4 when value is IVector4 iv4:
                        Unsafe.WriteUnaligned(ref start, iv4);
                        return true;
                    case EShaderVarType._uvec2 when value is UVector2 uv2:
                        Unsafe.WriteUnaligned(ref start, uv2);
                        return true;
                    case EShaderVarType._uvec3 when value is UVector3 uv3:
                        Unsafe.WriteUnaligned(ref start, new UVector4(uv3.X, uv3.Y, uv3.Z, 0));
                        return true;
                    case EShaderVarType._uvec4 when value is UVector4 uv4:
                        Unsafe.WriteUnaligned(ref start, uv4);
                        return true;
                    case EShaderVarType._mat4 when value is Matrix4x4 mat:
                        Unsafe.WriteUnaligned(ref start, mat);
                        return true;
                    default:
                        return false;
                }
            }

            private void WarnOnce(string message)
            {
                if (_warnedMessages.Add(message))
                    Debug.VulkanWarning(message);
            }
        }
    }
}
