using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

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
        public partial class VkMaterial(VulkanRenderer api, XRMaterial data) : VkObject<XRMaterial>(api, data)
        {
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
            protected override uint CreateObjectInternal()
                => CacheObject(this);

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
                Data.Textures.PostModified += OnTexturesModified;
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
                Data.Textures.PostModified -= OnTexturesModified;
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
                if (program is null || !program.Link() || Renderer.DescriptorFrameSlotFrameCount <= 0)
                    return false;

                if (!TryEnsureState(program, out ProgramDescriptorState? state) || state is null)
                    return false;

                int resolvedFrame = Math.Clamp(frameIndex, 0, state.FrameCount - 1);

                if (!UpdateUniformBuffers(state, resolvedFrame))
                    return false;

                ulong resourceFingerprint = ComputeResourceFingerprint(program);
                if (state.ResourceFingerprint != resourceFingerprint)
                    state.Dirty = true;

                if ((state.Dirty || _materialDirty) && !UpdateDescriptorSets(state))
                    return false;

                state.ResourceFingerprint = resourceFingerprint;
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
                => OnTexturesModified();

            private void OnTexturesModified()
            {
                lock (_stateSync)
                {
                    foreach (ProgramDescriptorState state in _programStates.Values)
                        state.Dirty = true;
                    _materialDirty = true;
                }
            }

            internal void ReleaseDescriptorReferencesForPhysicalResourceDestruction()
            {
                lock (_stateSync)
                {
                    DestroyAllProgramStates(retireDescriptorPools: true);
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
        /// Uniform buffers are rebuilt from current parameter values on each bind, so value-only
        /// changes must not rewrite descriptor sets while a command buffer is recording.
        /// </summary>
        private void OnParameterValueChanged(ShaderVar _)
        {
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
                        WarnNoMaterialBindings(program);
                        state = null;
                        return false;
                    }

                    int frameCount = Renderer.DescriptorFrameSlotFrameCount;
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

                int frameCount = Renderer.DescriptorFrameSlotFrameCount;
                if (frameCount <= 0)
                    return false;

                int setCount = program.DescriptorSetLayouts.Count;
                IReadOnlyList<DescriptorBindingInfo> bindings = program.DescriptorBindings;

                if (!CanHandleProgramBindings(program, bindings))
                    return false;

                bool hasMaterialBindings = HasMaterialParameterOrSamplerBindings(bindings);
                if (!hasMaterialBindings)
                {
                    WarnNoMaterialBindings(program);
                    return false;
                }

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

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();
                }

                DescriptorSetLayout[] layoutArray = [.. program.DescriptorSetLayouts];
                uint[] variableDescriptorCounts = program.DescriptorSetsRequireVariableDescriptorCount
                    ? VulkanBindlessMaterialDescriptors.BuildVariableDescriptorCounts(bindings, layoutArray.Length)
                    : [];
                DescriptorSet[][] descriptorSets = new DescriptorSet[frameCount][];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    DescriptorSet[] frameSets = new DescriptorSet[setCount];

                    fixed (DescriptorSetLayout* layoutPtr = layoutArray)
                    fixed (DescriptorSet* setPtr = frameSets)
                    fixed (uint* variableDescriptorCountPtr = variableDescriptorCounts)
                    {
                        DescriptorSetVariableDescriptorCountAllocateInfo variableDescriptorCountInfo = new()
                        {
                            SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
                            DescriptorSetCount = (uint)layoutArray.Length,
                            PDescriptorCounts = variableDescriptorCountPtr,
                        };

                        DescriptorSetAllocateInfo allocInfo = new()
                        {
                            SType = StructureType.DescriptorSetAllocateInfo,
                            PNext = program.DescriptorSetsRequireVariableDescriptorCount ? &variableDescriptorCountInfo : null,
                            DescriptorPool = descriptorPool,
                            DescriptorSetCount = (uint)setCount,
                            PSetLayouts = layoutPtr,
                        };

                        if (Api!.AllocateDescriptorSets(Device, ref allocInfo, setPtr) != Result.Success)
                        {
                            Api.DestroyDescriptorPool(Device, descriptorPool, null);
                            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                            WarnOnce("Failed to allocate Vulkan descriptor sets for material.");
                            return false;
                        }
                    }

                    descriptorSets[frame] = frameSets;
                }

                if (!TryCreateUniformResources(program, bindings, frameCount, out Dictionary<(uint set, uint binding), UniformBindingResource> uniformResources))
                {
                    Api!.DestroyDescriptorPool(Device, descriptorPool, null);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                    return false;
                }

                state = new ProgramDescriptorState
                {
                    Program = program,
                    Bindings = bindings,
                    DescriptorSets = descriptorSets,
                    UniformBindings = uniformResources,
                    HasMaterialParameterOrSamplerBindings = hasMaterialBindings,
                    FrameCount = frameCount,
                    SetCount = setCount,
                    SchemaFingerprint = ComputeSchemaFingerprint(bindings, frameCount, setCount),
                    DescriptorPool = descriptorPool,
                    Dirty = true,
                };

                return true;
            }

            private static bool HasMaterialParameterOrSamplerBindings(IReadOnlyList<DescriptorBindingInfo> bindings)
            {
                foreach (DescriptorBindingInfo binding in bindings)
                {
                    switch (binding.DescriptorType)
                    {
                        case DescriptorType.UniformBuffer:
                        case DescriptorType.CombinedImageSampler:
                        case DescriptorType.Sampler:
                        case DescriptorType.SampledImage:
                        case DescriptorType.StorageImage:
                        case DescriptorType.InputAttachment:
                        case DescriptorType.UniformTexelBuffer:
                        case DescriptorType.StorageTexelBuffer:
                            return true;
                    }
                }

                return false;
            }

            private void WarnNoMaterialBindings(VkRenderProgram program)
            {
                string programName = program.Data?.Name ?? "<unnamed>";
                string materialName = Data.Name ?? "<unnamed>";
                WarnOnce($"Material '{materialName}' program '{programName}' has no Vulkan parameter or sampler bindings after descriptor resolution.");
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
                    uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);

                    switch (binding.DescriptorType)
                    {
                        case DescriptorType.CombinedImageSampler:
                        case DescriptorType.Sampler:
                        case DescriptorType.SampledImage:
                        case DescriptorType.StorageImage:
                        case DescriptorType.InputAttachment:
                        case DescriptorType.UniformTexelBuffer:
                        case DescriptorType.StorageTexelBuffer:
                            if (descriptorCount > 32 && !VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding))
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
            private void DestroyAllProgramStates(bool retireDescriptorPools = false)
            {
                foreach (ProgramDescriptorState state in _programStates.Values)
                    DestroyProgramState(state, retireDescriptorPools);
                _programStates.Clear();
            }

            /// <summary>
            /// Releases all Vulkan resources owned by <paramref name="state"/>:
            /// uniform buffers, device memory, and the descriptor pool (which implicitly frees its sets).
            /// </summary>
            private void DestroyProgramState(ProgramDescriptorState state, bool retireDescriptorPool = false)
            {
                DestroyUniformResources(state.UniformBindings);

                if (state.DescriptorPool.Handle != 0)
                {
                    if (retireDescriptorPool)
                    {
                        Renderer.RetireDescriptorPool(state.DescriptorPool);
                    }
                    else
                    {
                        Api!.DestroyDescriptorPool(Device, state.DescriptorPool, null);
                        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                    }
                }
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
                    uint count = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding) * (uint)frameCount;
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

            private ulong ComputeResourceFingerprint(VkRenderProgram program)
            {
                HashCode hash = new();
                hash.Add(Renderer.ResourceAllocatorIdentity);
                hash.Add(Data.Textures.Count);
                for (int i = 0; i < Data.Textures.Count; i++)
                    AddTextureDescriptorResourceFingerprint(ref hash, Data.Textures[i]);

                program.AddSamplerResourceFingerprint(ref hash);

                return unchecked((ulong)hash.ToHashCode());
            }

            private void AddTextureDescriptorResourceFingerprint(ref HashCode hash, XRTexture? texture)
            {
                hash.Add(texture?.GetHashCode() ?? 0);
                if (texture is null)
                {
                    hash.Add(0UL);
                    return;
                }

                bool allowSynchronousTextureUpload = Renderer.AllowSynchronousResourceUploads;
                object? apiObject = Renderer.GetOrCreateAPIRenderObject(texture, generateNow: allowSynchronousTextureUpload);
                if (apiObject is IVkImageDescriptorSource imageSource)
                {
                    hash.Add(imageSource.IsDescriptorReady);
                    hash.Add(imageSource.DescriptorGeneration);
                    hash.Add(imageSource.DescriptorImage.Handle);
                    hash.Add(imageSource.DescriptorView.Handle);
                    hash.Add(imageSource.DescriptorSampler.Handle);
                    hash.Add(imageSource.DescriptorViewType);
                    hash.Add(imageSource.DescriptorFormat);
                    hash.Add(imageSource.DescriptorAspect);
                    hash.Add(imageSource.DescriptorUsage);
                }
                else
                {
                    hash.Add(0UL);
                }

                if (apiObject is IVkTexelBufferDescriptorSource texelSource)
                {
                    hash.Add(texelSource.DescriptorBufferView.Handle);
                    hash.Add(texelSource.DescriptorBufferFormat);
                }
                else
                {
                    hash.Add(0UL);
                }
            }

            /// <summary>
            /// Re-writes descriptor sets for every frame in <paramref name="state"/>.
            /// Called when the material or program state is marked dirty.
            /// </summary>
            /// <returns><c>true</c> if all frames were successfully updated.</returns>
            private bool UpdateDescriptorSets(ProgramDescriptorState state)
            {
                for (int frame = 0; frame < state.FrameCount; frame++)
                    if (!UpdateFrameDescriptorSet(state, frame))
                        return false;

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

                    uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);

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
                                if (!TryResolveTextureInfo(state.Program, binding, binding.DescriptorType, i, out DescriptorImageInfo info))
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
                                if (!TryResolveTexelBufferInfo(state.Program, binding, i, out BufferView texelView))
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
                    {
                        if (!TryUpdateDescriptorSetsWithTemplates(state, frameIndex, writeArray))
                            Api!.UpdateDescriptorSets(Device, (uint)writeArray.Length, writePtr, 0, null);
                    }
                }

                return true;
            }

            private bool TryUpdateDescriptorSetsWithTemplates(ProgramDescriptorState state, int frameIndex, WriteDescriptorSet[] writeArray)
            {
                if (RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template)
                    return false;

                if (state.Program.DescriptorSetLayouts.Count < state.DescriptorSets[frameIndex].Length)
                    return false;

                DescriptorSet[] frameSets = state.DescriptorSets[frameIndex];
                for (int setIndex = 0; setIndex < frameSets.Length; setIndex++)
                {
                    List<WriteDescriptorSet> setWrites = [];
                    for (int i = 0; i < writeArray.Length; i++)
                    {
                        if (writeArray[i].DstSet.Handle == frameSets[setIndex].Handle)
                            setWrites.Add(writeArray[i]);
                    }

                    if (setWrites.Count == 0)
                        continue;

                    if (!Renderer.TryUpdateDescriptorSetWithTemplate(
                        frameSets[setIndex],
                        state.Program.DescriptorSetLayouts[setIndex],
                        PipelineBindPoint.Graphics,
                        state.Program.PipelineLayout,
                        (uint)setIndex,
                        CollectionsMarshal.AsSpan(setWrites)))
                    {
                        return false;
                    }
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
                VkRenderProgram program,
                IReadOnlyList<DescriptorBindingInfo> bindings,
                int frameCount,
                out Dictionary<(uint set, uint binding), UniformBindingResource> resources)
            {
                resources = new Dictionary<(uint set, uint binding), UniformBindingResource>();

                foreach (DescriptorBindingInfo binding in bindings)
                {
                    if (binding.DescriptorType != DescriptorType.UniformBuffer)
                        continue;

                    ShaderVar? parameter = null;
                    AutoUniformBlockInfo? reflectedBlock = null;
                    uint bufferSize;

                    if (program.TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
                    {
                        reflectedBlock = block;
                        bufferSize = block.Size;
                        if (bufferSize == 0)
                        {
                            WarnOnce($"Material uniform block '{binding.Name}' has zero reflected size.");
                            DestroyUniformResources(resources);
                            return false;
                        }
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(binding.Name) || !_parameterLookup.TryGetValue(binding.Name, out parameter))
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

                        bufferSize = valueSize;
                    }

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
                        ReflectedBlock = reflectedBlock,
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

                    Silk.NET.Vulkan.Buffer buffer = resource.Buffers[frameIndex];
                    void* mapped;
                    if (!Renderer.TryMapBufferMemory(buffer, memory, 0, resource.Size, out mapped))
                        return false;

                    try
                    {
                        Span<byte> data = new(mapped, (int)resource.Size);
                        data.Clear();

                        bool wrote = resource.ReflectedBlock is { } reflectedBlock
                            ? TryWriteReflectedUniformBlock(data, reflectedBlock)
                            : resource.Parameter is { } parameter && TryWriteShaderVar(data, parameter);

                        if (!wrote)
                        {
                            WarnOnce($"Failed to serialize material uniform binding '{resource.Name}' using reflected layout.");
                            return false;
                        }
                    }
                    finally
                    {
                        Renderer.UnmapBufferMemory(buffer, memory);
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
            private bool TryResolveTextureInfo(VkRenderProgram program, DescriptorBindingInfo binding, DescriptorType descriptorType, int arrayIndex, out DescriptorImageInfo imageInfo)
            {
                imageInfo = default;
                if (!TryResolveBoundTexture(program, binding, arrayIndex, out XRTexture? texture) || texture is null)
                {
                    imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
                    if (imageInfo.ImageView.Handle != 0)
                    {
                        RecordDescriptorFallback(binding);
                        return true;
                    }

                    WarnOnce($"No texture available for material descriptor binding '{binding.Name}'.");
                    RecordDescriptorFailure(binding, "missing material texture and placeholder unavailable");
                    return false;
                }

                if (TryCreateTextureDescriptor(binding, texture, descriptorType, out imageInfo))
                    return true;

                RecordDescriptorFailure(binding, "material texture descriptor creation failed");
                return false;
            }

            /// <summary>
            /// Resolves the texture bound to <paramref name="binding"/> at <paramref name="arrayIndex"/>
            /// and obtains a <see cref="BufferView"/> for a texel buffer descriptor write.
            /// </summary>
            /// <returns><c>true</c> if a valid buffer view was obtained.</returns>
            private bool TryResolveTexelBufferInfo(VkRenderProgram program, DescriptorBindingInfo binding, int arrayIndex, out BufferView texelView)
            {
                texelView = default;
                if (!TryResolveBoundTexture(program, binding, arrayIndex, out XRTexture? texture) || texture is null)
                {
                    WarnOnce($"No texture available for material texel descriptor binding '{binding.Name}'.");
                    RecordDescriptorFailure(binding, "missing material texel texture");
                    return false;
                }

                if (TryCreateTexelBufferDescriptor(texture, out texelView))
                    return true;

                RecordDescriptorFailure(binding, "material texel descriptor creation failed");
                return false;
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
            private bool TryResolveBoundTexture(VkRenderProgram program, DescriptorBindingInfo binding, int arrayIndex, out XRTexture? texture)
            {
                MaterialTextureBindingResolution textureBinding = MaterialTextureBindingResolver.Resolve(
                    Data,
                    binding.Name,
                    (int)binding.Binding,
                    arrayIndex,
                    VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding),
                    samplerName =>
                    {
                        if (program.TryGetSamplerTexture(samplerName, out XRTexture? namedTexture))
                            return namedTexture;

                        return null;
                    });

                texture = textureBinding.Texture;
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
            private bool TryCreateTextureDescriptor(DescriptorBindingInfo binding, XRTexture texture, DescriptorType descriptorType, out DescriptorImageInfo imageInfo)
            {
                imageInfo = default;

                bool includeSampler = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler;

                bool allowSynchronousTextureUpload = Renderer.AllowSynchronousResourceUploads;
                if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: allowSynchronousTextureUpload) is not IVkImageDescriptorSource source)
                {
                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' does not have a Vulkan image wrapper.");
                    return false;
                }

                if (!source.TryEnsureDescriptorReadyForUse($"material descriptor '{binding.Name}'", allowSynchronousTextureUpload))
                {
                    imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
                    if (imageInfo.ImageView.Handle != 0)
                    {
                        WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' is not ready for Vulkan descriptor use on binding '{binding.Name}'. Using placeholder.");
                        RecordDescriptorFallback(binding);
                        return true;
                    }

                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' is not ready for Vulkan descriptor use on binding '{binding.Name}'.");
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
                    bool stencilOnly = RequiresStencilOnlyDescriptor(binding);
                    ImageView aspectView = stencilOnly
                        ? source.GetStencilOnlyDescriptorView()
                        : source.GetDepthOnlyDescriptorView();
                    string aspectLabel = stencilOnly ? "stencil-only" : "depth-only";
                    if (aspectView.Handle != 0)
                    {
                        if (!Renderer.IsLiveImageView(aspectView))
                        {
                            imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
                            if (imageInfo.ImageView.Handle != 0)
                            {
                                WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' references a retired Vulkan {aspectLabel} image view for binding '{binding.Name}'. Using placeholder.");
                                RecordDescriptorFallback(binding);
                                return true;
                            }

                            WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' references a retired Vulkan {aspectLabel} image view for binding '{binding.Name}'.");
                            return false;
                        }

                        if (!TryResolveDescriptorSampler(binding, includeSampler, source, out Sampler sampler))
                            return false;

                        imageInfo = new DescriptorImageInfo
                        {
                            ImageLayout = Renderer.ResolveDescriptorImageLayout(source, descriptorType),
                            ImageView = aspectView,
                            Sampler = sampler,
                        };
                        return true;
                    }

                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' uses a combined depth-stencil format and no {aspectLabel} view is available.");
                    return false;
                }

                ImageView descriptorView = ResolveDescriptorView(binding, source);
                if (descriptorView.Handle == 0)
                {
                    imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
                    if (imageInfo.ImageView.Handle != 0)
                    {
                        WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' cannot provide expected view type '{binding.ExpectedImageViewType}' for binding '{binding.Name}'. Using placeholder.");
                        RecordDescriptorFallback(binding);
                        return true;
                    }

                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' cannot provide expected view type '{binding.ExpectedImageViewType}' for binding '{binding.Name}'.");
                    return false;
                }

                if (!Renderer.IsLiveImageView(descriptorView))
                {
                    imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
                    if (imageInfo.ImageView.Handle != 0)
                    {
                        WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' references a retired Vulkan image view for binding '{binding.Name}'. Using placeholder.");
                        RecordDescriptorFallback(binding);
                        return true;
                    }

                    WarnOnce($"Material texture '{texture.Name ?? "<unnamed>"}' references a retired Vulkan image view for binding '{binding.Name}'.");
                    return false;
                }

                if (!TryResolveDescriptorSampler(binding, includeSampler, source, out Sampler descriptorSampler))
                    return false;

                imageInfo = new DescriptorImageInfo
                {
                    ImageLayout = Renderer.ResolveDescriptorImageLayout(source, descriptorType),
                    ImageView = descriptorView,
                    Sampler = descriptorSampler,
                };
                return imageInfo.ImageView.Handle != 0;
            }

            private bool TryResolveDescriptorSampler(DescriptorBindingInfo binding, bool includeSampler, IVkImageDescriptorSource source, out Sampler sampler)
            {
                sampler = default;
                if (!includeSampler)
                    return true;

                sampler = source.DescriptorSampler;
                if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
                    return true;

                if (sampler.Handle != 0)
                {
                    WarnOnce($"Material texture for binding '{binding.Name}' references a retired Vulkan sampler. Using placeholder sampler.");
                    RecordDescriptorFallback(binding);
                }

                sampler = Renderer.GetPlaceholderSampler();
                if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
                {
                    WarnOnce($"Material texture for binding '{binding.Name}' has no Vulkan sampler. Using placeholder sampler.");
                    RecordDescriptorFallback(binding);
                    return true;
                }

                WarnOnce($"Material texture for binding '{binding.Name}' has no Vulkan sampler and placeholder sampler is unavailable.");
                return false;
            }

            private static ImageView ResolveDescriptorView(DescriptorBindingInfo binding, IVkImageDescriptorSource source)
            {
                if (binding.ExpectedImageViewType is not { } expectedViewType)
                    return source.DescriptorView;

                return source.GetDescriptorView(expectedViewType);
            }

            private static bool RequiresStencilOnlyDescriptor(DescriptorBindingInfo binding)
                => binding.Name?.Contains("Stencil", StringComparison.OrdinalIgnoreCase) == true;

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

            private bool TryWriteReflectedUniformBlock(Span<byte> destination, AutoUniformBlockInfo block)
            {
                foreach (AutoUniformMember member in block.Members)
                {
                    if (!TryWriteReflectedUniformMember(destination, member))
                        return false;
                }

                return true;
            }

            private bool TryWriteReflectedUniformMember(Span<byte> destination, AutoUniformMember member)
            {
                if (member.Offset >= (uint)destination.Length)
                    return false;

                if (_parameterLookup.TryGetValue(member.Name, out ShaderVar? parameter))
                {
                    if (!TryCreateUniformValue(parameter, out ProgramUniformValue value))
                        return false;

                    return TryWriteUniformValue(destination, member, value);
                }

                if (TryResolveEngineUniformValue(member.Name, out ProgramUniformValue engineValue))
                    return TryWriteUniformValue(destination, member, engineValue);

                if (member.DefaultValue is AutoUniformDefaultValue defaultValue)
                {
                    ProgramUniformValue value = new(defaultValue.Type, defaultValue.Value, false);
                    return TryWriteUniformValue(destination, member, value);
                }

                if (member.DefaultArrayValues is { Count: > 0 } defaults)
                {
                    object[] values = new object[defaults.Count];
                    for (int i = 0; i < defaults.Count; i++)
                        values[i] = defaults[i].Value;

                    ProgramUniformValue value = new(member.EngineType ?? EShaderVarType._float, values, true);
                    return TryWriteUniformValue(destination, member, value);
                }

                if (member.StructMembers is { Count: > 0 } structMembers)
                {
                    foreach (AutoUniformMember child in structMembers)
                    {
                        AutoUniformMember shiftedChild = child with { Offset = member.Offset + child.Offset };
                        if (!TryWriteReflectedUniformMember(destination, shiftedChild))
                            return false;
                    }

                    return true;
                }

                WarnOnce($"Material reflected uniform '{member.Name}' could not be resolved.");
                return false;
            }

            private static bool TryCreateUniformValue(ShaderVar parameter, out ProgramUniformValue value)
            {
                if (parameter is ShaderArrayBase)
                {
                    object uniformableArray = parameter.GenericValue;
                    if (uniformableArray.GetType().GetProperty(nameof(IUniformableArray<ShaderVar>.Values))?.GetValue(uniformableArray) is not Array shaderValues)
                    {
                        value = default;
                        return false;
                    }

                    object[] values = new object[shaderValues.Length];
                    for (int i = 0; i < shaderValues.Length; i++)
                    {
                        if (shaderValues.GetValue(i) is not ShaderVar element)
                        {
                            value = default;
                            return false;
                        }

                        values[i] = element.GenericValue;
                    }

                    value = new ProgramUniformValue(parameter.TypeName, values, true);
                    return true;
                }

                value = new ProgramUniformValue(parameter.TypeName, parameter.GenericValue, false);
                return true;
            }

            private bool TryResolveEngineUniformValue(string name, out ProgramUniformValue value)
            {
                value = default;

                if (!Enum.TryParse(NormalizeEngineUniformName(name), ignoreCase: false, out EEngineUniform uniform))
                    return false;

                XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
                XRCamera? rightCamera = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
                bool stereo = RuntimeEngine.Rendering.State.IsStereoPass;
                var area = RuntimeEngine.Rendering.State.RenderArea;

                switch (uniform)
                {
                    case EEngineUniform.UpdateDelta:
                        value = new ProgramUniformValue(EShaderVarType._float, RuntimeEngine.Time.Timer.Update.Delta, false);
                        return true;
                    case EEngineUniform.ViewMatrix:
                    case EEngineUniform.LeftEyeViewMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.PrevViewMatrix:
                    case EEngineUniform.PrevLeftEyeViewMatrix:
                        value = new ProgramUniformValue(
                            EShaderVarType._mat4,
                            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalViewData) && temporalViewData.HistoryReady
                                ? temporalViewData.PrevViewMatrix
                                : camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity,
                            false);
                        return true;
                    case EEngineUniform.RightEyeViewMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.Transform.InverseRenderMatrix ?? camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.PrevRightEyeViewMatrix:
                        value = new ProgramUniformValue(
                            EShaderVarType._mat4,
                            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalRightViewData) && temporalRightViewData.HistoryReady
                                ? temporalRightViewData.RightEyePrevViewMatrix
                                : rightCamera?.Transform.InverseRenderMatrix ?? camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity,
                            false);
                        return true;
                    case EEngineUniform.InverseViewMatrix:
                    case EEngineUniform.LeftEyeInverseViewMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.RenderMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.RightEyeInverseViewMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.Transform.RenderMatrix ?? camera?.Transform.RenderMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.InverseProjMatrix:
                    case EEngineUniform.LeftEyeInverseProjMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, camera?.InverseProjectionMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.RightEyeInverseProjMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.InverseProjectionMatrix ?? camera?.InverseProjectionMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.ViewProjectionMatrix:
                    case EEngineUniform.LeftEyeViewProjectionMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, camera?.ViewProjectionMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.RightEyeViewProjectionMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.ViewProjectionMatrix ?? camera?.ViewProjectionMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.ProjMatrix:
                    case EEngineUniform.LeftEyeProjMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, camera?.ProjectionMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.PrevProjMatrix:
                    case EEngineUniform.PrevLeftEyeProjMatrix:
                        value = new ProgramUniformValue(
                            EShaderVarType._mat4,
                            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalProjectionData) && temporalProjectionData.HistoryReady
                                ? temporalProjectionData.PrevProjection
                                : camera?.ProjectionMatrix ?? Matrix4x4.Identity,
                            false);
                        return true;
                    case EEngineUniform.RightEyeProjMatrix:
                        value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.ProjectionMatrix ?? camera?.ProjectionMatrix ?? Matrix4x4.Identity, false);
                        return true;
                    case EEngineUniform.PrevRightEyeProjMatrix:
                        value = new ProgramUniformValue(
                            EShaderVarType._mat4,
                            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalRightProjectionData) && temporalRightProjectionData.HistoryReady
                                ? temporalRightProjectionData.RightEyePrevProjection
                                : rightCamera?.ProjectionMatrix ?? camera?.ProjectionMatrix ?? Matrix4x4.Identity,
                            false);
                        return true;
                    case EEngineUniform.CameraPosition:
                        value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderTranslation ?? Vector3.Zero, false);
                        return true;
                    case EEngineUniform.CameraForward:
                        value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderForward ?? Vector3.UnitZ, false);
                        return true;
                    case EEngineUniform.CameraUp:
                        value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderUp ?? Vector3.UnitY, false);
                        return true;
                    case EEngineUniform.CameraRight:
                        value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderRight ?? Vector3.UnitX, false);
                        return true;
                    case EEngineUniform.CameraNearZ:
                        value = new ProgramUniformValue(EShaderVarType._float, camera?.NearZ ?? 0f, false);
                        return true;
                    case EEngineUniform.CameraFarZ:
                        value = new ProgramUniformValue(EShaderVarType._float, camera?.FarZ ?? 0f, false);
                        return true;
                    case EEngineUniform.ScreenWidth:
                        value = new ProgramUniformValue(EShaderVarType._float, (float)area.Width, false);
                        return true;
                    case EEngineUniform.ScreenHeight:
                        value = new ProgramUniformValue(EShaderVarType._float, (float)area.Height, false);
                        return true;
                    case EEngineUniform.ScreenOrigin:
                        value = new ProgramUniformValue(EShaderVarType._vec2, Vector2.Zero, false);
                        return true;
                    case EEngineUniform.DepthMode:
                        value = new ProgramUniformValue(EShaderVarType._int, (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal), false);
                        return true;
                    case EEngineUniform.ClipSpaceYDirection:
                        value = new ProgramUniformValue(EShaderVarType._int, (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection, false);
                        return true;
                    case EEngineUniform.ClipDepthRange:
                        value = new ProgramUniformValue(EShaderVarType._int, (int)RuntimeEngine.Rendering.EffectiveClipDepthRange, false);
                        return true;
                    case EEngineUniform.FramebufferTextureYDirection:
                        value = new ProgramUniformValue(EShaderVarType._int, (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan), false);
                        return true;
                    case EEngineUniform.VRMode:
                        value = new ProgramUniformValue(EShaderVarType._int, stereo ? 1 : 0, false);
                        return true;
                    default:
                        return false;
                }
            }

            private static bool TryWriteUniformValue(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
            {
                if (member.IsArray)
                    return TryWriteUniformArray(destination, member, value);

                return TryWriteSingleUniform(destination, member.Offset, value.Type, value.Value);
            }

            private static bool TryWriteUniformArray(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
            {
                if (!value.IsArray || member.ArrayLength == 0 || member.ArrayStride == 0 || value.Value is not Array array)
                    return false;

                int count = Math.Min(array.Length, (int)member.ArrayLength);
                for (int i = 0; i < count; i++)
                {
                    object? element = array.GetValue(i);
                    if (element is null)
                        continue;

                    uint offset = member.Offset + (uint)i * member.ArrayStride;
                    if (!TryWriteSingleUniform(destination, offset, value.Type, element))
                        return false;
                }

                return true;
            }

            private static bool TryWriteSingleUniform(Span<byte> destination, uint offset, EShaderVarType type, object value)
            {
                if (offset >= (uint)destination.Length)
                    return false;

                ref byte start = ref destination[(int)offset];
                switch (type)
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
                    case EShaderVarType._double:
                        Unsafe.WriteUnaligned(ref start, Convert.ToDouble(value));
                        return true;
                    case EShaderVarType._vec2 when value is Vector2 v2:
                        Unsafe.WriteUnaligned(ref start, v2);
                        return true;
                    case EShaderVarType._vec3 when value is Vector3 v3:
                        Unsafe.WriteUnaligned(ref start, new Vector4(v3, 0f));
                        return true;
                    case EShaderVarType._vec3 when value is Vector4 v3From4:
                        Unsafe.WriteUnaligned(ref start, v3From4);
                        return true;
                    case EShaderVarType._vec3 when value is ColorF3 c3:
                        Unsafe.WriteUnaligned(ref start, new Vector4(c3.R, c3.G, c3.B, 0f));
                        return true;
                    case EShaderVarType._vec3 when value is ColorF4 c3From4:
                        Unsafe.WriteUnaligned(ref start, new Vector4(c3From4.R, c3From4.G, c3From4.B, 0f));
                        return true;
                    case EShaderVarType._vec4 when value is Vector4 v4:
                        Unsafe.WriteUnaligned(ref start, v4);
                        return true;
                    case EShaderVarType._vec4 when value is Vector3 v4From3:
                        Unsafe.WriteUnaligned(ref start, new Vector4(v4From3, 0f));
                        return true;
                    case EShaderVarType._vec4 when value is ColorF4 c4:
                        Unsafe.WriteUnaligned(ref start, new Vector4(c4.R, c4.G, c4.B, c4.A));
                        return true;
                    case EShaderVarType._vec4 when value is ColorF3 c4From3:
                        Unsafe.WriteUnaligned(ref start, new Vector4(c4From3.R, c4From3.G, c4From3.B, 0f));
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
                    case EShaderVarType._bvec2 when value is BoolVector2 bv2:
                        WriteBoolVector2(destination[(int)offset..], bv2);
                        return true;
                    case EShaderVarType._bvec3 when value is BoolVector3 bv3:
                        WriteBoolVector3(destination[(int)offset..], bv3);
                        return true;
                    case EShaderVarType._bvec4 when value is BoolVector4 bv4:
                        WriteBoolVector4(destination[(int)offset..], bv4);
                        return true;
                    case EShaderVarType._dvec2 when value is DVector2 dv2:
                        Unsafe.WriteUnaligned(ref start, dv2);
                        return true;
                    case EShaderVarType._dvec3 when value is DVector3 dv3:
                        Unsafe.WriteUnaligned(ref start, new DVector4(dv3.X, dv3.Y, dv3.Z, 0.0));
                        return true;
                    case EShaderVarType._dvec4 when value is DVector4 dv4:
                        Unsafe.WriteUnaligned(ref start, dv4);
                        return true;
                    case EShaderVarType._mat3 when value is Matrix4x4 mat3:
                        WriteMatrix3x3Std140(destination[(int)offset..], mat3);
                        return true;
                    case EShaderVarType._mat4 when value is Matrix4x4 mat:
                        Unsafe.WriteUnaligned(ref start, mat);
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>
            /// Returns the GPU-side byte size for a given <see cref="ShaderVar"/> type.
            /// vec3 types are 12 bytes with 16-byte alignment; arrays use the aligned stride.
            /// Returns <c>0</c> for unsupported types.
            /// </summary>
            private static uint GetShaderVarSize(ShaderVar parameter)
            {
                if (parameter is ShaderArrayBase array)
                {
                    uint stride = GetShaderVarArrayStride(parameter.TypeName);
                    return stride == 0 ? 0 : stride * (uint)Math.Max(array.Length, 0);
                }

                return GetShaderVarElementSize(parameter.TypeName);
            }

            private static uint GetShaderVarElementSize(EShaderVarType type)
                => type switch
                {
                    EShaderVarType._float or EShaderVarType._int or EShaderVarType._uint or EShaderVarType._bool => 4,
                    EShaderVarType._double => 8,
                    EShaderVarType._vec2 or EShaderVarType._ivec2 or EShaderVarType._uvec2 or EShaderVarType._bvec2 => 8,
                    EShaderVarType._vec3 or EShaderVarType._ivec3 or EShaderVarType._uvec3 or EShaderVarType._bvec3 => 12,
                    EShaderVarType._vec4 or EShaderVarType._ivec4 or EShaderVarType._uvec4 or EShaderVarType._bvec4 => 16,
                    EShaderVarType._dvec2 => 16,
                    EShaderVarType._dvec3 => 24,
                    EShaderVarType._dvec4 => 32,
                    EShaderVarType._mat3 => 48,
                    EShaderVarType._mat4 => 64,
                    _ => 0,
                };

            private static uint GetShaderVarArrayStride(EShaderVarType type)
            {
                uint elementSize = GetShaderVarElementSize(type);
                return elementSize == 0 ? 0 : Align(elementSize, 16u);
            }

            private static uint Align(uint value, uint alignment)
                => alignment == 0 ? value : (value + alignment - 1u) / alignment * alignment;

            /// <summary>
            /// Serializes the current value of <paramref name="parameter"/> into <paramref name="destination"/>.
            /// The destination span must have been pre-cleared and be at least as large as
            /// <see cref="GetShaderVarSize"/> reports. vec3 types write only xyz; any std140
            /// padding lane remains available to a following scalar.
            /// </summary>
            /// <param name="destination">Target byte span (typically a mapped Vulkan buffer region).</param>
            /// <param name="parameter">The shader variable whose value is to be written.</param>
            /// <returns><c>true</c> if the value was successfully written.</returns>
            private static bool TryWriteShaderVar(Span<byte> destination, ShaderVar parameter)
            {
                uint requiredSize = GetShaderVarSize(parameter);
                if (requiredSize == 0 || destination.Length < requiredSize)
                    return false;

                if (parameter is ShaderArrayBase array)
                    return TryWriteShaderArray(destination, array);

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
                    case EShaderVarType._double:
                        Unsafe.WriteUnaligned(ref start, Convert.ToDouble(value));
                        return true;
                    case EShaderVarType._vec2 when value is Vector2 v2:
                        Unsafe.WriteUnaligned(ref start, v2);
                        return true;
                    case EShaderVarType._vec3 when value is Vector3 v3:
                        Unsafe.WriteUnaligned(ref start, v3);
                        return true;
                    case EShaderVarType._vec3 when value is Vector4 v3From4:
                        Unsafe.WriteUnaligned(ref start, new Vector3(v3From4.X, v3From4.Y, v3From4.Z));
                        return true;
                    case EShaderVarType._vec3 when value is ColorF3 c3:
                        Unsafe.WriteUnaligned(ref start, new Vector3(c3.R, c3.G, c3.B));
                        return true;
                    case EShaderVarType._vec3 when value is ColorF4 c3From4:
                        Unsafe.WriteUnaligned(ref start, new Vector3(c3From4.R, c3From4.G, c3From4.B));
                        return true;
                    case EShaderVarType._vec4 when value is Vector4 v4:
                        Unsafe.WriteUnaligned(ref start, v4);
                        return true;
                    case EShaderVarType._vec4 when value is Vector3 v4From3:
                        Unsafe.WriteUnaligned(ref start, new Vector4(v4From3, 0f));
                        return true;
                    case EShaderVarType._vec4 when value is ColorF4 c4:
                        Unsafe.WriteUnaligned(ref start, new Vector4(c4.R, c4.G, c4.B, c4.A));
                        return true;
                    case EShaderVarType._vec4 when value is ColorF3 c4From3:
                        Unsafe.WriteUnaligned(ref start, new Vector4(c4From3.R, c4From3.G, c4From3.B, 0f));
                        return true;
                    case EShaderVarType._ivec2 when value is IVector2 iv2:
                        Unsafe.WriteUnaligned(ref start, iv2);
                        return true;
                    case EShaderVarType._ivec3 when value is IVector3 iv3:
                        Unsafe.WriteUnaligned(ref start, iv3);
                        return true;
                    case EShaderVarType._ivec4 when value is IVector4 iv4:
                        Unsafe.WriteUnaligned(ref start, iv4);
                        return true;
                    case EShaderVarType._uvec2 when value is UVector2 uv2:
                        Unsafe.WriteUnaligned(ref start, uv2);
                        return true;
                    case EShaderVarType._uvec3 when value is UVector3 uv3:
                        Unsafe.WriteUnaligned(ref start, uv3);
                        return true;
                    case EShaderVarType._uvec4 when value is UVector4 uv4:
                        Unsafe.WriteUnaligned(ref start, uv4);
                        return true;
                    case EShaderVarType._bvec2 when value is BoolVector2 bv2:
                        WriteBoolVector2(destination, bv2);
                        return true;
                    case EShaderVarType._bvec3 when value is BoolVector3 bv3:
                        WriteBoolVector3(destination, bv3);
                        return true;
                    case EShaderVarType._bvec4 when value is BoolVector4 bv4:
                        WriteBoolVector4(destination, bv4);
                        return true;
                    case EShaderVarType._dvec2 when value is DVector2 dv2:
                        Unsafe.WriteUnaligned(ref start, dv2);
                        return true;
                    case EShaderVarType._dvec3 when value is DVector3 dv3:
                        Unsafe.WriteUnaligned(ref start, dv3);
                        return true;
                    case EShaderVarType._dvec4 when value is DVector4 dv4:
                        Unsafe.WriteUnaligned(ref start, dv4);
                        return true;
                    case EShaderVarType._mat3 when value is Matrix4x4 mat3:
                        WriteMatrix3x3Std140(destination, mat3);
                        return true;
                    case EShaderVarType._mat4 when value is Matrix4x4 mat:
                        Unsafe.WriteUnaligned(ref start, mat);
                        return true;
                    default:
                        return false;
                }
            }

            private static bool TryWriteShaderArray(Span<byte> destination, ShaderArrayBase array)
            {
                uint stride = GetShaderVarArrayStride(array.TypeName);
                if (stride == 0)
                    return false;

                object value = array.GenericValue;
                if (value.GetType().GetProperty(nameof(IUniformableArray<ShaderVar>.Values))?.GetValue(value) is not Array values)
                    return array.Length == 0;

                int count = Math.Min(array.Length, values.Length);
                for (int i = 0; i < count; i++)
                {
                    if (values.GetValue(i) is not ShaderVar element)
                        continue;

                    int offset = checked((int)(stride * (uint)i));
                    if (offset >= destination.Length)
                        return false;

                    Span<byte> elementDestination = destination[offset..Math.Min(destination.Length, offset + (int)stride)];
                    if (!TryWriteShaderVar(elementDestination, element))
                        return false;
                }

                return true;
            }

            private static void WriteBoolVector2(Span<byte> destination, BoolVector2 value)
            {
                ref byte start = ref destination[0];
                Unsafe.WriteUnaligned(ref start, value.X ? 1 : 0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 4), value.Y ? 1 : 0);
            }

            private static void WriteBoolVector3(Span<byte> destination, BoolVector3 value)
            {
                ref byte start = ref destination[0];
                Unsafe.WriteUnaligned(ref start, value.X ? 1 : 0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 4), value.Y ? 1 : 0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 8), value.Z ? 1 : 0);
            }

            private static void WriteBoolVector4(Span<byte> destination, BoolVector4 value)
            {
                ref byte start = ref destination[0];
                Unsafe.WriteUnaligned(ref start, value.X ? 1 : 0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 4), value.Y ? 1 : 0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 8), value.Z ? 1 : 0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 12), value.W ? 1 : 0);
            }

            private static void WriteMatrix3x3Std140(Span<byte> destination, Matrix4x4 value)
            {
                ref byte start = ref destination[0];
                Unsafe.WriteUnaligned(ref start, new Vector4(value.M11, value.M12, value.M13, 0f));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 16), new Vector4(value.M21, value.M22, value.M23, 0f));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 32), new Vector4(value.M31, value.M32, value.M33, 0f));
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

            private static string GetDescriptorBindingClass(DescriptorType descriptorType)
                => descriptorType switch
                {
                    DescriptorType.StorageImage => "storage-image",
                    DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => "uniform-buffer",
                    DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => "storage-buffer",
                    DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer => "texel-buffer",
                    _ => "sampled-image",
                };

            private void RecordDescriptorFallback(DescriptorBindingInfo binding, int count = 1)
                => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorFallback(
                    Data.Name,
                    GetDescriptorBindingClass(binding.DescriptorType),
                    binding.Name,
                    binding.Set,
                    binding.Binding,
                    count);

            private void RecordDescriptorFailure(DescriptorBindingInfo binding, string reason)
                => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                    Data.Name,
                    GetDescriptorBindingClass(binding.DescriptorType),
                    binding.Name,
                    binding.Set,
                    binding.Binding,
                    skippedDraw: true,
                    skippedDispatch: false,
                    reason);

            #endregion
        }
    }
}
