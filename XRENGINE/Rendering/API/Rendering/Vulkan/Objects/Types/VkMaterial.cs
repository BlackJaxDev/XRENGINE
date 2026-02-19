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
        /// <summary>
        /// Vulkan API wrapper for <see cref="XRMaterial"/>.
        /// Manages per-program descriptor state (pools, sets, uniform buffers, and texture bindings)
        /// so that each material can be bound to a command buffer with a single call to
        /// <see cref="TryBindDescriptorSets"/>.
        /// <para>
        /// Descriptor resources are lazily created on first use and automatically invalidated when
        /// the material's parameters, textures, or the program's binding layout change.
        /// A schema fingerprint is used to detect layout mismatches and trigger re-creation.
        /// </para>
        /// </summary>
        public class VkMaterial(VulkanRenderer api, XRMaterial data) : VkObject<XRMaterial>(api, data)
        {
            #region Nested Types

            /// <summary>
            /// Holds all Vulkan descriptor resources that have been allocated for a specific
            /// <see cref="VkRenderProgram"/>. A separate state is maintained per program because
            /// different programs may declare different descriptor set layouts.
            /// </summary>
            private sealed class ProgramDescriptorState
            {
                /// <summary>The render program this state was created for.</summary>
                public required VkRenderProgram Program { get; init; }

                /// <summary>Snapshot of the program's descriptor binding metadata at creation time.</summary>
                public required IReadOnlyList<DescriptorBindingInfo> Bindings { get; init; }

                /// <summary>
                /// Per-frame descriptor sets. Indexed as <c>[frameIndex][setIndex]</c>.
                /// One full copy per swap-chain image avoids write-after-read hazards.
                /// </summary>
                public required DescriptorSet[][] DescriptorSets { get; init; }

                /// <summary>
                /// Uniform buffer resources keyed by <c>(set, binding)</c>.
                /// Only material-owned uniform bindings appear here; engine-managed uniforms are excluded.
                /// </summary>
                public required Dictionary<(uint set, uint binding), UniformBindingResource> UniformBindings { get; init; }

                /// <summary>Number of swap-chain images (frames in flight) at the time of creation.</summary>
                public required int FrameCount { get; init; }

                /// <summary>Number of descriptor set layouts declared by the program.</summary>
                public required int SetCount { get; init; }

                /// <summary>
                /// Hash of the binding layout used to detect when the program's descriptor schema
                /// has changed, requiring the state to be rebuilt.
                /// </summary>
                public required ulong SchemaFingerprint { get; init; }

                /// <summary>The Vulkan descriptor pool from which all sets in this state were allocated.</summary>
                public DescriptorPool DescriptorPool;

                /// <summary>
                /// When <c>true</c>, the descriptor writes need to be re-issued
                /// (e.g. after a texture or parameter change).
                /// </summary>
                public bool Dirty = true;
            }

            /// <summary>
            /// Tracks a single material-owned uniform buffer binding, including per-frame
            /// Vulkan buffer handles and their backing device memory.
            /// </summary>
            private sealed class UniformBindingResource
            {
                /// <summary>The shader uniform name this resource is bound to.</summary>
                public required string Name { get; init; }

                /// <summary>The material <see cref="ShaderVar"/> whose value is uploaded each frame.</summary>
                public required ShaderVar Parameter { get; init; }

                /// <summary>Size in bytes of the uniform buffer (matches the shader var's GPU size).</summary>
                public required uint Size { get; init; }

                /// <summary>Per-frame Vulkan buffer handles.</summary>
                public required Silk.NET.Vulkan.Buffer[] Buffers { get; init; }

                /// <summary>Per-frame device memory backing the corresponding <see cref="Buffers"/> entries.</summary>
                public required DeviceMemory[] Memories { get; init; }
            }

            #endregion

            #region Fields

            /// <summary>Synchronizes access to <see cref="_programStates"/> and related mutable state.</summary>
            private readonly object _stateSync = new();

            /// <summary>
            /// Cached descriptor state per render program, keyed by <see cref="VkObject.BindingId"/>.
            /// Each entry owns a descriptor pool, descriptor sets, and uniform buffer resources.
            /// </summary>
            private readonly Dictionary<uint, ProgramDescriptorState> _programStates = new();

            /// <summary>
            /// Fast name-to-parameter lookup rebuilt whenever the material's parameter list changes.
            /// Uses ordinal comparison for shader uniform name matching.
            /// </summary>
            private readonly Dictionary<string, ShaderVar> _parameterLookup = new(StringComparer.Ordinal);

            /// <summary>
            /// Tracks warning messages that have already been emitted so each unique warning
            /// is only logged once, avoiding log spam during per-frame operations.
            /// </summary>
            private readonly HashSet<string> _warnedMessages = new(StringComparer.Ordinal);

            /// <summary>
            /// Set to <c>true</c> whenever any material parameter or texture changes,
            /// signaling that descriptor writes must be re-issued on the next bind.
            /// </summary>
            private bool _materialDirty = true;

            #endregion

            #region Properties

            /// <inheritdoc />
            public override VkObjectType Type => VkObjectType.Material;

            /// <inheritdoc />
            public override bool IsGenerated => IsActive;

            #endregion

            #region VkObject Lifecycle

            /// <inheritdoc />
            protected override uint CreateObjectInternal() => CacheObject(this);

            /// <inheritdoc />
            /// <remarks>Destroys all per-program descriptor states and removes this object from the cache.</remarks>
            protected override void DeleteObjectInternal()
            {
                lock (_stateSync)
                    DestroyAllProgramStates();
                RemoveCachedObject(BindingId);
            }

            /// <inheritdoc />
            /// <remarks>
            /// Subscribes to texture and property change events on the underlying <see cref="XRMaterial"/>
            /// and initialises the parameter lookup table.
            /// </remarks>
            protected override void LinkData()
            {
                Data.Textures.PostAnythingAdded += OnTextureChanged;
                Data.Textures.PostAnythingRemoved += OnTextureChanged;
                Data.PropertyChanged += OnMaterialPropertyChanged;
                RebuildParameterLookup();
                _materialDirty = true;
            }

            /// <inheritdoc />
            /// <remarks>
            /// Unsubscribes from all material events and releases every Vulkan resource
            /// owned by this wrapper (descriptor pools, buffers, device memory).
            /// </remarks>
            protected override void UnlinkData()
            {
                Data.Textures.PostAnythingAdded -= OnTextureChanged;
                Data.Textures.PostAnythingRemoved -= OnTextureChanged;
                Data.PropertyChanged -= OnMaterialPropertyChanged;
                UnsubscribeParameterEvents();

                lock (_stateSync)
                    DestroyAllProgramStates();
            }

            #endregion

            #region Public API

            /// <summary>
            /// Ensures that all descriptor resources for the given <paramref name="program"/> are
            /// up-to-date, uploads uniform data for the current frame, and binds the descriptor
            /// sets to <paramref name="commandBuffer"/>.
            /// </summary>
            /// <param name="commandBuffer">The Vulkan command buffer to record the bind into.</param>
            /// <param name="program">The linked render program whose descriptor layout is used.</param>
            /// <param name="frameIndex">Current swap-chain image / frame-in-flight index.</param>
            /// <param name="firstSet">First descriptor set index passed to <c>vkCmdBindDescriptorSets</c>.</param>
            /// <returns>
            /// <c>true</c> if descriptor sets were successfully bound (or no sets were needed);
            /// <c>false</c> if any required resource could not be created or updated.
            /// </returns>
            public bool TryBindDescriptorSets(CommandBuffer commandBuffer, VkRenderProgram program, int frameIndex, uint firstSet = 0)
            {
                if (program is null || !program.Link() || Renderer.swapChainImages is null || Renderer.swapChainImages.Length == 0)
                    return false;

                if (!TryEnsureState(program, out ProgramDescriptorState? state) || state is null)
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

                Renderer.BindDescriptorSetsTracked(
                    commandBuffer,
                    PipelineBindPoint.Graphics,
                    program.PipelineLayout,
                    firstSet,
                    sets);

                return true;
            }

            #endregion

            #region Event Handlers

            /// <summary>
            /// Called when a texture is added to or removed from <see cref="XRMaterial.Textures"/>.
            /// Marks every cached program state and the material itself as dirty so that
            /// descriptor writes are re-issued on the next bind.
            /// </summary>
            private void OnTextureChanged(XRTexture? _)
            {
                lock (_stateSync)
                {
                    foreach (ProgramDescriptorState state in _programStates.Values)
                        state.Dirty = true;
                    _materialDirty = true;
                }
            }

            /// <summary>
            /// Responds to property changes on the backing <see cref="XRMaterial"/>.
            /// A change to <see cref="XRMaterial.Parameters"/> triggers a full rebuild of the
            /// parameter lookup and destroys all cached program states.
            /// A change to <see cref="XRMaterial.Textures"/> is forwarded to <see cref="OnTextureChanged"/>.
            /// </summary>
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

            /// <summary>
            /// Called when the value of any subscribed <see cref="ShaderVar"/> changes.
            /// Sets <see cref="_materialDirty"/> so uniform buffers are re-uploaded on the next bind.
            /// </summary>
            private void OnParameterValueChanged(ShaderVar _)
            {
                lock (_stateSync)
                    _materialDirty = true;
            }

            #endregion

            #region Parameter Management

            /// <summary>
            /// Rebuilds <see cref="_parameterLookup"/> from the current <see cref="XRMaterial.Parameters"/> list.
            /// Previous event subscriptions are removed before re-subscribing to each parameter's
            /// <see cref="ShaderVar.ValueChanged"/> event.
            /// </summary>
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

            /// <summary>
            /// Detaches <see cref="OnParameterValueChanged"/> from every <see cref="ShaderVar"/>
            /// currently tracked in <see cref="_parameterLookup"/>.
            /// </summary>
            private void UnsubscribeParameterEvents()
            {
                foreach (ShaderVar parameter in _parameterLookup.Values)
                    parameter.ValueChanged -= OnParameterValueChanged;
            }

            #endregion

            #region Program State Management

            /// <summary>
            /// Returns an existing <see cref="ProgramDescriptorState"/> for <paramref name="program"/>,
            /// or creates one if none exists or the binding schema has changed since the last use.
            /// Stale states are destroyed before a new one is allocated.
            /// </summary>
            /// <param name="program">The linked render program to look up or create state for.</param>
            /// <param name="state">The valid state on success; <c>null</c> on failure.</param>
            /// <returns><c>true</c> if a usable state was obtained.</returns>
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

                    if (!TryCreateProgramState(program, out ProgramDescriptorState? created) || created is null)
                    {
                        state = null;
                        return false;
                    }

                    _programStates[key] = created;
                    state = created;
                    return true;
                }
            }

            /// <summary>
            /// Allocates a new <see cref="ProgramDescriptorState"/> for <paramref name="program"/>:
            /// creates a descriptor pool, allocates per-frame descriptor sets, and provisions
            /// uniform buffer resources for every material-owned uniform binding.
            /// </summary>
            /// <param name="program">The render program defining the required descriptor layout.</param>
            /// <param name="state">The newly created state on success; <c>null</c> on failure.</param>
            /// <returns><c>true</c> if all Vulkan resources were successfully allocated.</returns>
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

            /// <summary>
            /// Checks whether every binding in <paramref name="bindings"/> is a type that
            /// <see cref="VkMaterial"/> knows how to manage. Returns <c>false</c> if any binding
            /// is an engine-managed uniform, an unsupported descriptor type, exceeds the
            /// per-binding array limit, or has no matching material parameter.
            /// </summary>
            /// <param name="program">The program that owns the bindings (used for auto-uniform checks).</param>
            /// <param name="bindings">The program's descriptor binding metadata.</param>
            /// <returns><c>true</c> if all bindings can be serviced by the material.</returns>
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
                        case DescriptorType.InputAttachment:
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

                            if (program.TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out _))
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

            /// <summary>
            /// Destroys every cached <see cref="ProgramDescriptorState"/> and clears <see cref="_programStates"/>.
            /// Must be called while holding <see cref="_stateSync"/>.
            /// </summary>
            private void DestroyAllProgramStates()
            {
                foreach (ProgramDescriptorState state in _programStates.Values)
                    DestroyProgramState(state);
                _programStates.Clear();
            }

            /// <summary>
            /// Releases all Vulkan resources owned by <paramref name="state"/>:
            /// uniform buffers, device memory, and the descriptor pool (which implicitly frees its sets).
            /// </summary>
            private void DestroyProgramState(ProgramDescriptorState state)
            {
                DestroyUniformResources(state.UniformBindings);

                if (state.DescriptorPool.Handle != 0)
                    Api!.DestroyDescriptorPool(Device, state.DescriptorPool, null);
            }

            #endregion

            #region Descriptor Pool & Sets

            /// <summary>
            /// Aggregates bindings by <see cref="DescriptorType"/> and multiplies each count by
            /// <paramref name="frameCount"/> to produce the pool size array needed for
            /// <c>VkDescriptorPoolCreateInfo</c>.
            /// </summary>
            /// <param name="bindings">The program's descriptor binding metadata.</param>
            /// <param name="frameCount">Number of frames in flight (swap-chain images).</param>
            /// <returns>An array of <see cref="DescriptorPoolSize"/> entries suitable for pool creation.</returns>
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

            /// <summary>
            /// Computes a deterministic hash over the binding layout, frame count, and set count.
            /// The fingerprint is compared against a cached value to detect when a program's
            /// descriptor schema has changed and the state must be rebuilt.
            /// </summary>
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

            /// <summary>
            /// Re-writes descriptor sets for every frame in <paramref name="state"/>.
            /// Called when the material or program state is marked dirty.
            /// </summary>
            /// <returns><c>true</c> if all frames were successfully updated.</returns>
            private bool UpdateDescriptorSets(ProgramDescriptorState state)
            {
                for (int frame = 0; frame < state.FrameCount; frame++)
                {
                    if (!UpdateFrameDescriptorSet(state, frame))
                        return false;
                }

                return true;
            }

            /// <summary>
            /// Builds and submits <c>vkUpdateDescriptorSets</c> writes for a single frame.
            /// Collects buffer, image, and texel-buffer descriptor infos into contiguous arrays,
            /// then pins them so that the write structs can reference stable pointers.
            /// </summary>
            /// <param name="state">The program descriptor state containing the sets to update.</param>
            /// <param name="frameIndex">The frame-in-flight index to update.</param>
            /// <returns><c>true</c> if all descriptors were resolved and the update call succeeded.</returns>
            private bool UpdateFrameDescriptorSet(ProgramDescriptorState state, int frameIndex)
            {
                // Accumulate write operations and their associated descriptor infos.
                // Index maps record which write corresponds to which info entry so that
                // pointers can be patched after pinning the arrays.
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
                        case DescriptorType.InputAttachment:
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

                // Materialize lists into arrays so they can be pinned for the Vulkan call.
                WriteDescriptorSet[] writeArray = writes.Count == 0 ? Array.Empty<WriteDescriptorSet>() : [.. writes];
                DescriptorBufferInfo[] bufferArray = bufferInfos.Count == 0 ? Array.Empty<DescriptorBufferInfo>() : [.. bufferInfos];
                DescriptorImageInfo[] imageArray = imageInfos.Count == 0 ? Array.Empty<DescriptorImageInfo>() : [.. imageInfos];
                BufferView[] texelArray = texelBufferViews.Count == 0 ? Array.Empty<BufferView>() : [.. texelBufferViews];

                // Pin all arrays simultaneously and patch the native pointers into the write structs.
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

            #endregion

            #region Uniform Buffer Management

            /// <summary>
            /// Creates Vulkan buffers and device memory for every <see cref="DescriptorType.UniformBuffer"/>
            /// binding in <paramref name="bindings"/>. Each binding gets one buffer per frame in flight,
            /// backed by host-visible, host-coherent memory so values can be written without explicit flushes.
            /// </summary>
            /// <param name="bindings">The program's descriptor binding metadata.</param>
            /// <param name="frameCount">Number of frames in flight.</param>
            /// <param name="resources">On success, a dictionary of <c>(set, binding)</c> to resource.</param>
            /// <returns><c>true</c> if all buffers were allocated successfully; any partial work is cleaned up on failure.</returns>
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

            /// <summary>
            /// Maps each uniform buffer for <paramref name="frameIndex"/>, clears it, and writes
            /// the current <see cref="ShaderVar"/> value into the mapped memory.
            /// </summary>
            /// <param name="state">The program descriptor state whose uniform bindings are to be updated.</param>
            /// <param name="frameIndex">The frame-in-flight index selecting which buffer to write.</param>
            /// <returns><c>true</c> if every uniform buffer was successfully mapped and written.</returns>
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

            /// <summary>
            /// Destroys Vulkan buffers and frees device memory for every entry in <paramref name="resources"/>.
            /// Safe to call with partially-populated dictionaries (e.g. during error cleanup).
            /// </summary>
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

            #endregion

            #region Texture & Descriptor Resolution

            /// <summary>
            /// Resolves the texture bound to <paramref name="binding"/> at <paramref name="arrayIndex"/>
            /// and creates a <see cref="DescriptorImageInfo"/> suitable for an image descriptor write.
            /// </summary>
            /// <returns><c>true</c> if a valid image view was obtained.</returns>
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

            /// <summary>
            /// Resolves the texture bound to <paramref name="binding"/> at <paramref name="arrayIndex"/>
            /// and obtains a <see cref="BufferView"/> for a texel buffer descriptor write.
            /// </summary>
            /// <returns><c>true</c> if a valid buffer view was obtained.</returns>
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

            /// <summary>
            /// Looks up the <see cref="XRTexture"/> in the material's texture list that
            /// corresponds to the given <paramref name="binding"/> index plus <paramref name="arrayIndex"/>.
            /// Falls back to the first non-null texture if the computed index is out of range.
            /// </summary>
            /// <param name="binding">Descriptor binding whose <see cref="DescriptorBindingInfo.Binding"/> provides the base index.</param>
            /// <param name="arrayIndex">Offset within an array binding.</param>
            /// <param name="texture">The resolved texture, or <c>null</c> if none is available.</param>
            /// <returns><c>true</c> if a non-null texture was found.</returns>
            private bool TryResolveBoundTexture(DescriptorBindingInfo binding, int arrayIndex, out XRTexture? texture)
            {
                texture = null;
                if (Data.Textures.Count <= 0)
                    return false;

                // Use binding index + array offset to pick the texture slot.
                int index = (int)binding.Binding + arrayIndex;
                if (index < 0 || index >= Data.Textures.Count)
                    return false;

                texture = Data.Textures[index];
                return texture is not null;
            }

            /// <summary>
            /// Creates a <see cref="DescriptorImageInfo"/> for the given <paramref name="texture"/>,
            /// verifying that the texture's Vulkan image has the required usage flags and is not
            /// using a combined depth-stencil aspect (which is invalid for descriptors).
            /// </summary>
            /// <param name="texture">The engine texture to wrap.</param>
            /// <param name="descriptorType">The Vulkan descriptor type (combined image-sampler, sampled image, or storage image).</param>
            /// <param name="imageInfo">The populated descriptor info on success.</param>
            /// <returns><c>true</c> if a valid image view with a non-zero handle was obtained.</returns>
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

                bool requiresSampledUsage = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler or DescriptorType.InputAttachment;
                if (requiresSampledUsage && (source.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
                {
                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' is missing VK_IMAGE_USAGE_SAMPLED_BIT for descriptor type '{descriptorType}'.");
                    return false;
                }

                if (descriptorType == DescriptorType.StorageImage && (source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
                {
                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' is missing VK_IMAGE_USAGE_STORAGE_BIT for storage image binding.");
                    return false;
                }

                if (IsCombinedDepthStencilFormat(source.DescriptorFormat) &&
                    (source.DescriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
                {
                    // Use a depth-only view for combined depth-stencil descriptors.
                    ImageView depthOnlyView = source.GetDepthOnlyDescriptorView();
                    if (depthOnlyView.Handle != 0)
                    {
                        imageInfo = new DescriptorImageInfo
                        {
                            ImageLayout = layout,
                            ImageView = depthOnlyView,
                            Sampler = includeSampler ? source.DescriptorSampler : default,
                        };
                        return true;
                    }

                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' uses a combined depth-stencil format and no depth-only view is available.");
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

            /// <summary>
            /// Obtains a <see cref="BufferView"/> for the given <paramref name="texture"/> by
            /// querying its Vulkan API object as an <see cref="IVkTexelBufferDescriptorSource"/>.
            /// </summary>
            /// <param name="texture">The engine texture expected to expose a texel-buffer view.</param>
            /// <param name="texelView">The buffer view handle on success.</param>
            /// <returns><c>true</c> if a valid buffer view with a non-zero handle was obtained.</returns>
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

            /// <summary>
            /// Returns <c>true</c> if <paramref name="format"/> is a combined depth + stencil format.
            /// Such formats cannot be used directly in Vulkan descriptor image views without
            /// selecting a single aspect (depth or stencil).
            /// </summary>
            private static bool IsCombinedDepthStencilFormat(Format format)
                => format is Format.D24UnormS8Uint
                    or Format.D32SfloatS8Uint
                    or Format.D16UnormS8Uint;

            #endregion

            #region Shader Variable Utilities

            /// <summary>
            /// Strips the <c>_VTX</c> suffix from a uniform name so it can be matched against
            /// <see cref="EEngineUniform"/> values. Vertex-stage variants of engine uniforms
            /// use this suffix convention.
            /// </summary>
            private static string NormalizeEngineUniformName(string name)
            {
                const string suffix = "_VTX";
                return name.EndsWith(suffix, StringComparison.Ordinal)
                    ? name[..^suffix.Length]
                    : name;
            }

            /// <summary>
            /// Returns the GPU-side byte size for a given <see cref="ShaderVar"/> type.
            /// vec3 types are padded to 16 bytes (vec4 alignment) per std140/std430 rules.
            /// Returns <c>0</c> for unsupported types.
            /// </summary>
            private static uint GetShaderVarSize(ShaderVar parameter)
                => parameter.TypeName switch
                {
                    EShaderVarType._float or EShaderVarType._int or EShaderVarType._uint or EShaderVarType._bool => 4,   // 32-bit scalar
                    EShaderVarType._vec2 or EShaderVarType._ivec2 or EShaderVarType._uvec2 => 8,                          // 2 x 32-bit
                    EShaderVarType._vec3 or EShaderVarType._vec4 or EShaderVarType._ivec3 or EShaderVarType._ivec4 or EShaderVarType._uvec3 or EShaderVarType._uvec4 => 16,  // 4 x 32-bit (vec3 padded)
                    EShaderVarType._mat4 => 64,                                                                           // 4 x vec4 = 16 floats
                    _ => 0,
                };

            /// <summary>
            /// Serializes the current value of <paramref name="parameter"/> into <paramref name="destination"/>.
            /// The destination span must have been pre-cleared and be at least as large as
            /// <see cref="GetShaderVarSize"/> reports. vec3 types are written as vec4 (w = 0)
            /// to satisfy GPU alignment requirements.
            /// </summary>
            /// <param name="destination">Target byte span (typically a mapped Vulkan buffer region).</param>
            /// <param name="parameter">The shader variable whose value is to be written.</param>
            /// <returns><c>true</c> if the value was successfully written.</returns>
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

            #endregion

            #region Diagnostics

            /// <summary>
            /// Logs <paramref name="message"/> as a Vulkan warning, but only the first time
            /// a given message string is seen. Prevents log flooding during per-frame operations.
            /// </summary>
            private void WarnOnce(string message)
            {
                if (_warnedMessages.Add(message))
                    Debug.VulkanWarning(message);
            }

            #endregion
        }
    }
}
