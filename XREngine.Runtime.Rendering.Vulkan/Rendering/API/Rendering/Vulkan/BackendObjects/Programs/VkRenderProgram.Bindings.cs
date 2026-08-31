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
    internal BindingUpdateScope BeginBindingUpdate()
        => new(this);

    internal readonly ref struct BindingUpdateScope
    {
        private readonly VkRenderProgram _owner;
        private readonly BindingCaptureState _state;

        internal BindingUpdateScope(VkRenderProgram owner)
        {
            _owner = owner;
            _state = owner.PushBindingCapture();
        }

        public void Dispose()
            => _owner.PopBindingCapture(_state);
    }

    internal void ClearBindings()
    {
        if (TryGetActiveBindingCaptureState(out BindingCaptureState capture))
        {
            capture.Clear();
            return;
        }

        if (Monitor.IsEntered(_bindingLock))
        {
            ClearBindingsNoLock();
            return;
        }

        lock (_bindingLock)
            ClearBindingsNoLock();
    }

    private void ClearBindingsNoLock()
    {
        _appliedBindingSnapshot = null;
        _uniformValues.Clear();
        _samplersByUnit.Clear();
        _samplerNamesByUnit.Clear();
        _samplersByName.Clear();
        _imagesByUnit.Clear();
        _buffersByBinding.Clear();
        _readOnlyStorageBindings?.Dispose();
        _readOnlyStorageBindings = null;
        // Frame snapshots can already be owned by queued indirect draws. Binding
        // preparation clears the immediate program dictionaries between draws,
        // but it must not retire that frame-owned snapshot pool: doing so clears
        // immutable storage publications before descriptor resolution. The pool
        // is released when its render-frame identity advances or on teardown.
        _frameMaterialBindingSnapshots.Clear();
    }

    private void SetUniformValue(string name, EShaderVarType type, object value, bool isArray = false)
        => SetUniformValue(name, new ProgramUniformValue(type, value, isArray));

    private void SetUniformValue(string name, in ProgramUniformValue value)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!TryResolveBindingWriteState(out BindingCaptureState? capture))
            return;

        if (capture is not null)
        {
            capture.Uniforms[name] = value;
            capture.RecordUniform(name);
            return;
        }

        if (Monitor.IsEntered(_bindingLock))
        {
            DetachAppliedBindingSnapshotNoLock();
            _uniformValues[name] = value;
            return;
        }

        lock (_bindingLock)
        {
            DetachAppliedBindingSnapshotNoLock();
            _uniformValues[name] = value;
        }
    }

    internal bool TryGetUniformValue(string name, out ProgramUniformValue value)
    {
        if (TryGetActiveBindingCaptureState(out BindingCaptureState capture))
            return TryGetUniformValueNoLock(capture.Uniforms, name, out value);

        lock (_bindingLock)
            return TryGetUniformValueNoLock(
                _appliedBindingSnapshot?.Uniforms ?? _uniformValues,
                name,
                out value);
    }

    /// <summary>
    /// Reads an immutable draw snapshot directly when one is available, avoiding
    /// replay into shared mutable program dictionaries on command-buffer reuse.
    /// </summary>
    internal bool TryGetUniformValue(
        ComputeDispatchSnapshot? snapshot,
        string name,
        out ProgramUniformValue value)
    {
        if (snapshot is null)
            return TryGetUniformValue(name, out value);

        if (TryGetUniformValueNoLock(snapshot.Uniforms, name, out value))
            return true;

        return snapshot.MaterialUniformBindings is { } materialBindings &&
               TryGetUniformValueNoLock(materialBindings.Uniforms, name, out value);
    }

    private static bool TryGetUniformValueNoLock(
        Dictionary<string, ProgramUniformValue> uniforms,
        string name,
        out ProgramUniformValue value)
    {
            if (uniforms.TryGetValue(name, out value))
                return true;

            // Keep parity with vertex suffix-based engine uniforms.
            if (name.EndsWith("_VTX", StringComparison.Ordinal))
            {
                string stripped = VertexBaseUniformNames.GetOrAdd(
                    name,
                    static uniformName => uniformName[..^4]);
                if (uniforms.TryGetValue(stripped, out value))
                    return true;
            }
            else if (uniforms.TryGetValue(
                VertexSuffixedUniformNames.GetOrAdd(
                    name,
                    static uniformName => string.Concat(uniformName, "_VTX")),
                out value))
            {
                return true;
            }

        value = default;
        return false;
    }

    internal void ApplyBindingSnapshot(ComputeDispatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_bindingLock)
            _appliedBindingSnapshot = snapshot;
    }

    /// <summary>
    /// Replays one immutable binding layer into the active private capture.
    /// This is used for frame/view globals such as forward lighting, which are
    /// assembled once and shared by many material programs.
    /// </summary>
    internal void MergeBindingSnapshot(ComputeDispatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryGetActiveBindingCaptureState(out BindingCaptureState capture))
            throw new InvalidOperationException(
                "Immutable binding layers may only be merged into an active private capture.");

        capture.Uniforms.EnsureCapacity(capture.Uniforms.Count + snapshot.Uniforms.Count);
        foreach (KeyValuePair<string, ProgramUniformValue> pair in snapshot.Uniforms)
        {
            capture.Uniforms[pair.Key] = pair.Value;
            capture.RecordUniform(pair.Key);
        }

        capture.SamplersByUnit.EnsureCapacity(capture.SamplersByUnit.Count + snapshot.Samplers.Count);
        foreach (KeyValuePair<uint, XRTexture> pair in snapshot.Samplers)
            capture.SamplersByUnit[pair.Key] = pair.Value;

        capture.SamplerNamesByUnit.EnsureCapacity(
            capture.SamplerNamesByUnit.Count + snapshot.SamplerNamesByUnit.Count);
        foreach (KeyValuePair<uint, string> pair in snapshot.SamplerNamesByUnit)
            capture.SamplerNamesByUnit[pair.Key] = pair.Value;

        capture.SamplersByName.EnsureCapacity(
            capture.SamplersByName.Count + snapshot.SamplersByName.Count);
        foreach (KeyValuePair<string, XRTexture> pair in snapshot.SamplersByName)
            capture.SamplersByName[pair.Key] = pair.Value;

        capture.RequiredSamplerNames.EnsureCapacity(
            capture.RequiredSamplerNames.Count +
            snapshot.RequiredSamplerNames.Count);
        foreach (string name in snapshot.RequiredSamplerNames)
            capture.RequiredSamplerNames.Add(name);

        capture.ImagesByUnit.EnsureCapacity(capture.ImagesByUnit.Count + snapshot.Images.Count);
        foreach (KeyValuePair<uint, ProgramImageBinding> pair in snapshot.Images)
            capture.ImagesByUnit[pair.Key] = pair.Value;

        capture.BuffersByBinding.EnsureCapacity(
            capture.BuffersByBinding.Count + snapshot.Buffers.Count);
        foreach (KeyValuePair<uint, VulkanComputeBufferBinding> pair in snapshot.Buffers)
            capture.BuffersByBinding[pair.Key] = pair.Value.Data;
        if (snapshot.ReadOnlyStorageBindings is { } readOnlyBindings)
            ReplaceReadOnlyStorageBindings(
                ref capture.ReadOnlyStorageBindings,
                readOnlyBindings);
    }

    /// <summary>
    /// Materializes an applied immutable snapshot only when a later callback mutates
    /// program bindings. The steady reusable-draw path never pays this copy.
    /// </summary>
    private void DetachAppliedBindingSnapshotNoLock()
    {
        if (_appliedBindingSnapshot is not { } snapshot)
            return;

        _uniformValues.Clear();
        _samplersByUnit.Clear();
        _samplerNamesByUnit.Clear();
        _samplersByName.Clear();
        _imagesByUnit.Clear();
        _buffersByBinding.Clear();

        foreach (var pair in snapshot.Uniforms)
            _uniformValues[pair.Key] = pair.Value;

        foreach (var pair in snapshot.Samplers)
            _samplersByUnit[pair.Key] = pair.Value;

        foreach (var pair in snapshot.SamplerNamesByUnit)
            _samplerNamesByUnit[pair.Key] = pair.Value;

        foreach (var pair in snapshot.SamplersByName)
            _samplersByName[pair.Key] = pair.Value;

        foreach (var pair in snapshot.Images)
            _imagesByUnit[pair.Key] = pair.Value;

        foreach (var pair in snapshot.Buffers)
            _buffersByBinding[pair.Key] = pair.Value.Data;

        ReplaceReadOnlyStorageBindings(
            ref _readOnlyStorageBindings,
            snapshot.ReadOnlyStorageBindings);

        _appliedBindingSnapshot = null;
    }

    internal ComputeDispatchSnapshot CaptureComputeSnapshot()
    {
        if (TryGetActiveBindingCaptureState(out BindingCaptureState capture))
            return CaptureComputeSnapshot(capture);

        if (Monitor.IsEntered(_bindingLock))
            return CaptureComputeSnapshotNoLock();

        lock (_bindingLock)
            return CaptureComputeSnapshotNoLock();
    }

    /// <summary>
    /// Looks up a material binding snapshot captured earlier in the current
    /// render frame and exact pipeline binding scope.
    /// </summary>
    internal bool TryGetFrameMaterialBindingSnapshot(
        in MaterialBindingSnapshotCacheKey key,
        out ComputeDispatchSnapshot? snapshot)
    {
        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
        {
            snapshot = null;
            return false;
        }

        PrepareFrameMaterialBindingSnapshotCache(frameId);
        return _frameMaterialBindingSnapshots.TryGetValue(key, out snapshot);
    }

    /// <summary>
    /// Publishes a completed immutable material snapshot for the remainder of
    /// the current frame. A null entry is meaningful and avoids repeating an
    /// expensive binding callback that produced no descriptor resources.
    /// </summary>
    internal void CacheFrameMaterialBindingSnapshot(
        in MaterialBindingSnapshotCacheKey key,
        ComputeDispatchSnapshot? snapshot)
    {
        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
            return;

        PrepareFrameMaterialBindingSnapshotCache(frameId);
        snapshot?.EnableMaterialBindingFastPath();
        _frameMaterialBindingSnapshots[key] = snapshot;
    }

    /// <summary>
    /// Resolves a detached cross-frame artifact only when every owner generation
    /// still matches the slot's last publication.
    /// </summary>
    internal bool TryGetPersistentProgramBindingArtifact(
        in PersistentProgramBindingArtifactSlotKey slot,
        in PersistentProgramBindingArtifactGeneration generation,
        IRenderBindingPublisher[] materialPublishers,
        IRenderBindingPublisher[] meshPublishers,
        out ComputeDispatchSnapshot? artifact)
    {
        lock (_persistentProgramBindingArtifactSync)
        {
            if (_persistentProgramBindingArtifacts.TryGetValue(
                    slot,
                    out var entry) &&
                entry.Generation == generation &&
                entry.PublisherGenerations.Matches(
                    materialPublishers,
                    meshPublishers))
            {
                artifact = entry.Artifact;
                return true;
            }
        }

        artifact = null;
        return false;
    }

    /// <summary>
    /// Publishes one bounded owner slot. Revisions replace their prior artifact
    /// rather than retaining one cache entry per material edit.
    /// </summary>
    internal void CachePersistentProgramBindingArtifact(
        in PersistentProgramBindingArtifactSlotKey slot,
        in PersistentProgramBindingArtifactGeneration generation,
        IRenderBindingPublisher[] materialPublishers,
        IRenderBindingPublisher[] meshPublishers,
        ComputeDispatchSnapshot? artifact)
    {
        const int maximumPersistentArtifactSlots = 2048;
        lock (_persistentProgramBindingArtifactSync)
        {
            if (_persistentProgramBindingArtifacts.Count >=
                    maximumPersistentArtifactSlots &&
                !_persistentProgramBindingArtifacts.ContainsKey(slot))
            {
                _persistentProgramBindingArtifacts.Clear();
            }

            _persistentProgramBindingArtifacts[slot] =
                (
                    generation,
                    RenderBindingPublisherGenerationSnapshot.Capture(
                        materialPublishers,
                        meshPublishers),
                    artifact);
        }
    }

    internal bool TryGetAutoUniformMaterialWritePlan(
        string blockName,
        ulong publicationLayoutSignature,
        XRMaterial material,
        ulong runtimeUniformNameSignature,
        ulong runtimeUniformPublicationLayoutSignature,
        bool cacheDependsOnMaterialOrRuntime,
        out AutoUniformMaterialWritePlan? plan)
    {
        if (!cacheDependsOnMaterialOrRuntime)
            return _frequencyOwnedAutoUniformWritePlans.TryGetValue(
                blockName,
                out plan);

        plan = null;
        AutoUniformMaterialWritePlanCacheKey key = new(
            publicationLayoutSignature,
            material,
            runtimeUniformNameSignature,
            runtimeUniformPublicationLayoutSignature);
        return _autoUniformMaterialWritePlans.TryGetValue(
                blockName,
                out Dictionary<AutoUniformMaterialWritePlanCacheKey, AutoUniformMaterialWritePlan>? plans) &&
            plans.TryGetValue(key, out plan);
    }

    internal void CacheAutoUniformMaterialWritePlan(
        string blockName,
        ulong publicationLayoutSignature,
        XRMaterial material,
        ulong runtimeUniformNameSignature,
        ulong runtimeUniformPublicationLayoutSignature,
        bool cacheDependsOnMaterialOrRuntime,
        AutoUniformMaterialWritePlan plan)
    {
        if (!cacheDependsOnMaterialOrRuntime)
        {
            _frequencyOwnedAutoUniformWritePlans[blockName] = plan;
            return;
        }

        const int maximumCachedMaterialPlansPerBlock = 4096;
        AutoUniformMaterialWritePlanCacheKey key = new(
            publicationLayoutSignature,
            material,
            runtimeUniformNameSignature,
            runtimeUniformPublicationLayoutSignature);
        if (!_autoUniformMaterialWritePlans.TryGetValue(
                blockName,
                out Dictionary<AutoUniformMaterialWritePlanCacheKey, AutoUniformMaterialWritePlan>? plans))
        {
            plans = [];
            _autoUniformMaterialWritePlans.Add(blockName, plans);
        }
        else if (plans.Count >= maximumCachedMaterialPlansPerBlock)
        {
            plans.Clear();
        }

        plans[key] = plan;
    }

    /// <summary>
    /// Captures only numeric material values from the active private binding
    /// workspace. This bounded copy happens on a material revision, never as a
    /// consequence of advancing the render frame.
    /// </summary>
    internal MaterialUniformBindingPayload CaptureMaterialUniformBindingPayload()
    {
        if (TryGetActiveBindingCaptureState(out BindingCaptureState capture))
            return new MaterialUniformBindingPayload(
                new Dictionary<string, ProgramUniformValue>(capture.Uniforms, StringComparer.Ordinal));

        if (Monitor.IsEntered(_bindingLock))
            return CaptureMaterialUniformBindingPayloadNoLock();

        lock (_bindingLock)
            return CaptureMaterialUniformBindingPayloadNoLock();
    }

    private MaterialUniformBindingPayload CaptureMaterialUniformBindingPayloadNoLock()
    {
        DetachAppliedBindingSnapshotNoLock();
        return new MaterialUniformBindingPayload(
            new Dictionary<string, ProgramUniformValue>(_uniformValues, StringComparer.Ordinal));
    }

    private void PrepareFrameMaterialBindingSnapshotCache(ulong frameId)
    {
        if (_frameMaterialBindingSnapshotCacheFrame == frameId)
            return;

        // The material-cache epoch is independent from the snapshot-pool epoch.
        // A direct indirect capture can populate the pool before this cache is
        // first consulted in the same frame; retiring the pool here would clear
        // that queued draw's immutable storage publication. Pool retirement is
        // owned exclusively by its render-frame transition and program teardown.
        _frameMaterialBindingSnapshotCacheFrame = frameId;
        _frameMaterialBindingSnapshots.Clear();
    }

    private ComputeDispatchSnapshot CaptureComputeSnapshotNoLock()
    {
        DetachAppliedBindingSnapshotNoLock();
        return CaptureComputeSnapshot(
            _uniformValues,
            _samplersByUnit,
            _samplerNamesByUnit,
            _samplersByName,
            _imagesByUnit,
            _buffersByBinding,
            RentFrameBindingSnapshot());
    }

    private ComputeDispatchSnapshot CaptureComputeSnapshot(BindingCaptureState capture)
    {
        ComputeDispatchSnapshot snapshot =
            capture.RentFrameSnapshot() ?? new ComputeDispatchSnapshot();
        snapshot.ExchangeCapturedBindings(
            ref capture.Uniforms,
            ref capture.RuntimeUniformPublications,
            ref capture.MutableLegacyUniformNames,
            ref capture.RequiredSamplerNames,
            ref capture.SamplersByUnit,
            ref capture.SamplerNamesByUnit,
            ref capture.SamplersByName,
            ref capture.ImagesByUnit);
        CaptureComputeBufferBindings(capture.BuffersByBinding, snapshot);
        snapshot.SetReadOnlyStorageBindings(capture.ReadOnlyStorageBindings);
        snapshot.PublishBindingLayoutSignatures(
            BackendContext,
            WrapperLookup.Lookup,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline);
        return snapshot;
    }

    private ComputeDispatchSnapshot CaptureComputeSnapshot(
        Dictionary<string, ProgramUniformValue> uniforms,
        Dictionary<uint, XRTexture> samplersByUnit,
        Dictionary<uint, string> samplerNamesByUnit,
        Dictionary<string, XRTexture> samplersByName,
        Dictionary<uint, ProgramImageBinding> imagesByUnit,
        Dictionary<uint, XRDataBuffer> buffersByBinding,
        ComputeDispatchSnapshot? rentedSnapshot)
    {
        ComputeDispatchSnapshot snapshot = rentedSnapshot ?? new ComputeDispatchSnapshot();
        snapshot.Reset(
            uniforms,
            samplersByUnit,
            samplerNamesByUnit,
            samplersByName,
            imagesByUnit);
        CaptureComputeBufferBindings(buffersByBinding, snapshot);
        snapshot.SetReadOnlyStorageBindings(_readOnlyStorageBindings);
        snapshot.PublishBindingLayoutSignatures(
            BackendContext,
            WrapperLookup.Lookup,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline);

        return snapshot;
    }

    private void CaptureComputeBufferBindings(
        Dictionary<uint, XRDataBuffer> buffersByBinding,
        ComputeDispatchSnapshot snapshot)
    {
        snapshot.Buffers.EnsureCapacity(buffersByBinding.Count);
        bool allowSynchronousUpload = BackendContext.Resources.AllowSynchronousResourceUploads;
        foreach (KeyValuePair<uint, XRDataBuffer> pair in buffersByBinding)
        {
            XRDataBuffer buffer = pair.Value;
            if (WrapperLookup.GetOrCreate(buffer, generateNow: allowSynchronousUpload) is not VkDataBuffer vkBuffer ||
                !vkBuffer.TryCaptureComputeBufferSnapshot(allowSynchronousUpload, out VulkanComputeBufferBinding bufferBinding))
            {
                bufferBinding = new VulkanComputeBufferBinding(buffer, default, 0UL, 0);
            }

            snapshot.Buffers[pair.Key] = bufferBinding;
            if (!string.IsNullOrWhiteSpace(buffer.AttributeName))
                snapshot.BuffersByName.TryAdd(buffer.AttributeName, bufferBinding);
        }
    }

    /// <summary>
    /// Rents binding storage only while a render-pipeline frame context is active.
    /// The global render-frame ID covers desktop and OpenXR outputs, while captures
    /// made during initialization keep owning snapshots because their lifetime is
    /// not bounded by a published frame context.
    /// </summary>
    private ComputeDispatchSnapshot? RentFrameBindingSnapshot()
    {
        if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext is null)
            return null;

        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
            return null;

        if (_frameBindingSnapshotPoolFrame != frameId)
        {
            ReleaseFrameBindingSnapshots();
            _frameBindingSnapshotPoolFrame = frameId;
            _frameBindingSnapshotPoolCursor = 0;
        }

        int index = _frameBindingSnapshotPoolCursor++;
        if (index < _frameBindingSnapshotPool.Count)
            return _frameBindingSnapshotPool[index];

        ComputeDispatchSnapshot snapshot = new();
        _frameBindingSnapshotPool.Add(snapshot);
        return snapshot;
    }

    private void ReleaseFrameBindingSnapshots()
    {
        foreach (ComputeDispatchSnapshot snapshot in _frameBindingSnapshotPool)
            snapshot.ReleaseReadOnlyStorageBindings();
        _frameBindingSnapshotPoolFrame = 0;
        _frameBindingSnapshotPoolCursor = 0;
    }

    /// <summary>
    /// Performs an allocation-free preflight of immutable buffer bindings before a
    /// dispatch is reported as enqueued. Descriptor allocation still occurs while
    /// recording, but missing or usage-incompatible SSBOs are rejected here.
    /// </summary>
    internal bool ValidateComputeSnapshot(ComputeDispatchSnapshot snapshot, out string? failure)
    {
        for (int index = 0; index < _programDescriptorBindings.Count; index++)
        {
            DescriptorBindingInfo binding = _programDescriptorBindings[index];
            if (binding.DescriptorType is not (DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic))
                continue;

            bool found = snapshot.Buffers.TryGetValue(binding.Binding, out VulkanComputeBufferBinding buffer);
            if (!found && !string.IsNullOrWhiteSpace(binding.Name))
                found = snapshot.BuffersByName.TryGetValue(binding.Name, out buffer);

            if (!found)
            {
                failure = $"missing storage buffer at set {binding.Set}, binding {binding.Binding} ('{binding.Name}')";
                return false;
            }

            if (buffer.Buffer.Handle == 0 || buffer.Range == 0)
            {
                failure = $"storage buffer '{buffer.Data.AttributeName}' has no ready Vulkan handle or range";
                return false;
            }

            if (!VkDataBuffer.SupportsDescriptorType(binding.DescriptorType, buffer.UsageFlags))
            {
                failure = $"storage buffer '{buffer.Data.AttributeName}' was created with incompatible usage {buffer.UsageFlags}";
                return false;
            }
        }

        failure = null;
        return true;
    }

    internal bool HasBoundDescriptorResources()
    {
        if (TryGetActiveBindingCaptureState(out BindingCaptureState capture))
            return capture.SamplersByName.Count != 0 ||
                capture.BuffersByBinding.Count != 0;

        if (Monitor.IsEntered(_bindingLock))
            return HasBoundDescriptorResourcesNoLock();

        lock (_bindingLock)
            return HasBoundDescriptorResourcesNoLock();
    }

    private bool HasBoundDescriptorResourcesNoLock()
        => _appliedBindingSnapshot is { } snapshot
            ? snapshot.SamplersByName.Count != 0 || snapshot.Buffers.Count != 0
            : _samplersByName.Count != 0 || _buffersByBinding.Count != 0;

    private void Uniform(string name, Matrix4x4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._mat4, value));
    private void Uniform(string name, Quaternion value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec4, new Vector4(value.X, value.Y, value.Z, value.W)));
    private void Uniform(string name, Matrix4x4[] value) => SetUniformValue(name, EShaderVarType._mat4, CaptureUniformArray(value), true);
    private void Uniform(string name, Quaternion[] value)
        => SetUniformValue(name, EShaderVarType._vec4, CaptureQuaternionUniformArray(value), true);

    private void Uniform(string name, bool value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._bool, value));
    private void Uniform(string name, BoolVector2 value) => SetUniformValue(name, EShaderVarType._bvec2, value);
    private void Uniform(string name, BoolVector3 value) => SetUniformValue(name, EShaderVarType._bvec3, value);
    private void Uniform(string name, BoolVector4 value) => SetUniformValue(name, EShaderVarType._bvec4, value);
    private void Uniform(string name, bool[] value) => SetUniformValue(name, EShaderVarType._bool, CaptureUniformArray(value), true);
    private void Uniform(string name, BoolVector2[] value) => SetUniformValue(name, EShaderVarType._bvec2, CaptureUniformArray(value), true);
    private void Uniform(string name, BoolVector3[] value) => SetUniformValue(name, EShaderVarType._bvec3, CaptureUniformArray(value), true);
    private void Uniform(string name, BoolVector4[] value) => SetUniformValue(name, EShaderVarType._bvec4, CaptureUniformArray(value), true);

    private void Uniform(string name, float value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._float, value));
    private void Uniform(string name, Vector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec2, value));
    private void Uniform(string name, Vector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec3, value));
    private void Uniform(string name, Vector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec4, value));
    private void Uniform(string name, float[] value) => SetUniformValue(name, EShaderVarType._float, CaptureUniformArray(value), true);
    private void Uniform(string name, Span<float> value) => SetUniformValue(name, EShaderVarType._float, CaptureUniformArray((ReadOnlySpan<float>)value), true);
    private void Uniform(string name, Vector2[] value) => SetUniformValue(name, EShaderVarType._vec2, CaptureUniformArray(value), true);
    private void Uniform(string name, Vector3[] value) => SetUniformValue(name, EShaderVarType._vec3, CaptureUniformArray(value), true);
    private void Uniform(string name, Vector4[] value) => SetUniformValue(name, EShaderVarType._vec4, CaptureUniformArray(value), true);

    private void Uniform(string name, double value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._double, value));
    private void Uniform(string name, DVector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._dvec2, value));
    private void Uniform(string name, DVector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._dvec3, value));
    private void Uniform(string name, DVector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._dvec4, value));
    private void Uniform(string name, double[] value) => SetUniformValue(name, EShaderVarType._double, CaptureUniformArray(value), true);
    private void Uniform(string name, DVector2[] value) => SetUniformValue(name, EShaderVarType._dvec2, CaptureUniformArray(value), true);
    private void Uniform(string name, DVector3[] value) => SetUniformValue(name, EShaderVarType._dvec3, CaptureUniformArray(value), true);
    private void Uniform(string name, DVector4[] value) => SetUniformValue(name, EShaderVarType._dvec4, CaptureUniformArray(value), true);

    private void Uniform(string name, int value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._int, value));
    private void Uniform(string name, IVector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._ivec2, value));
    private void Uniform(string name, IVector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._ivec3, value));
    private void Uniform(string name, IVector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._ivec4, value));
    private void Uniform(string name, int[] value) => SetUniformValue(name, EShaderVarType._int, CaptureUniformArray(value), true);
    private void Uniform(string name, IVector2[] value) => SetUniformValue(name, EShaderVarType._ivec2, CaptureUniformArray(value), true);
    private void Uniform(string name, IVector3[] value) => SetUniformValue(name, EShaderVarType._ivec3, CaptureUniformArray(value), true);
    private void Uniform(string name, IVector4[] value) => SetUniformValue(name, EShaderVarType._ivec4, CaptureUniformArray(value), true);

    private void Uniform(string name, uint value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uint, value));
    private void Uniform(string name, UVector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uvec2, value));
    private void Uniform(string name, UVector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uvec3, value));
    private void Uniform(string name, UVector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uvec4, value));
    private void Uniform(string name, uint[] value) => SetUniformValue(name, EShaderVarType._uint, CaptureUniformArray(value), true);
    private void Uniform(string name, UVector2[] value) => SetUniformValue(name, EShaderVarType._uvec2, CaptureUniformArray(value), true);
    private void Uniform(string name, UVector3[] value) => SetUniformValue(name, EShaderVarType._uvec3, CaptureUniformArray(value), true);
    private void Uniform(string name, UVector4[] value) => SetUniformValue(name, EShaderVarType._uvec4, CaptureUniformArray(value), true);

    private void Sampler(string name, IRenderTextureResource texture, int textureUnit)
    {
        if (texture is not XRTexture xrTexture)
            return;

        if (!TryResolveBindingWriteState(out BindingCaptureState? capture))
            return;

        uint unit = textureUnit < 0 ? 0u : (uint)textureUnit;
        if (capture is not null)
        {
            capture.RejectTypedResourceWrite("sampler");
            capture.SetSampler(name, xrTexture, unit);
        }
        else if (Monitor.IsEntered(_bindingLock))
        {
            SetSamplerNoLock(name, xrTexture, unit);
        }
        else
        {
            lock (_bindingLock)
                SetSamplerNoLock(name, xrTexture, unit);
        }

    }

    private void SetSamplerNoLock(string name, XRTexture texture, uint unit)
    {
        DetachAppliedBindingSnapshotNoLock();
        _samplersByUnit[unit] = texture;
        if (!string.IsNullOrWhiteSpace(name))
        {
            _samplerNamesByUnit[unit] = name;
            _samplersByName[name] = texture;
        }
        else
        {
            _samplerNamesByUnit.Remove(unit);
        }
    }

    private void Sampler(int location, IRenderTextureResource texture, int textureUnit)
        => Sampler(location.ToString(), texture, textureUnit);

    private void BindImageTexture(uint unit, IRenderTextureResource texture, int level, bool layered, int layer, XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)
    {
        if (texture is not XRTexture xrTexture)
            return;

        if (!TryResolveBindingWriteState(out BindingCaptureState? capture))
            return;

        ProgramImageBinding binding = new(xrTexture, level, layered, layer, access, format);
        if (capture is not null)
        {
            capture.RejectTypedResourceWrite("storage image");
            capture.ImagesByUnit[unit] = binding;
        }
        else if (Monitor.IsEntered(_bindingLock))
        {
            DetachAppliedBindingSnapshotNoLock();
            _imagesByUnit[unit] = binding;
        }
        else
        {
            lock (_bindingLock)
            {
                DetachAppliedBindingSnapshotNoLock();
                _imagesByUnit[unit] = binding;
            }
        }

    }

    private void BindBuffer(uint index, XRDataBuffer buffer)
    {
        if (buffer is null)
            return;

        if (!TryResolveBindingWriteState(out BindingCaptureState? capture))
            return;

        if (capture is not null)
        {
            capture.RejectTypedResourceWrite("buffer");
            capture.BuffersByBinding[index] = buffer;
        }
        else if (Monitor.IsEntered(_bindingLock))
        {
            DetachAppliedBindingSnapshotNoLock();
            _buffersByBinding[index] = buffer;
        }
        else
        {
            lock (_bindingLock)
            {
                DetachAppliedBindingSnapshotNoLock();
                _buffersByBinding[index] = buffer;
            }
        }

    }

    private void BindReadOnlyStorage(ReadOnlyStorageBinding binding)
    {
        if (!TryResolveBindingWriteState(out BindingCaptureState? capture))
        {
            VulkanDescriptorResolutionDiagnostics.CaptureFirstDirectionalShadowPublication(
                this,
                binding,
                accepted: false,
                CurrentBindingCaptureWorkspace.Active?.Owner);
            return;
        }

        VulkanDescriptorResolutionDiagnostics.CaptureFirstDirectionalShadowPublication(
            this,
            binding,
            accepted: true,
            capture?.Owner);

        if (capture is not null)
        {
            capture.RejectTypedResourceWrite("read-only storage");
            ReplaceReadOnlyStorageBinding(ref capture.ReadOnlyStorageBindings, binding);
            capture.BuffersByBinding.Remove(binding.Binding);
            return;
        }

        if (Monitor.IsEntered(_bindingLock))
        {
            DetachAppliedBindingSnapshotNoLock();
            ReplaceReadOnlyStorageBinding(ref _readOnlyStorageBindings, binding);
            _buffersByBinding.Remove(binding.Binding);
            return;
        }

        lock (_bindingLock)
        {
            DetachAppliedBindingSnapshotNoLock();
            ReplaceReadOnlyStorageBinding(ref _readOnlyStorageBindings, binding);
            _buffersByBinding.Remove(binding.Binding);
        }
    }

    private static void ReplaceReadOnlyStorageBinding(
        ref ReadOnlyStorageBindingSet? bindings,
        ReadOnlyStorageBinding replacement)
    {
        if (bindings is { } current &&
            current.TryGet(replacement.Binding, out ReadOnlyStorageBinding existing) &&
            existing.Publication.IsSameToken(replacement.Publication) &&
            existing.Offset == replacement.Offset &&
            existing.Length == replacement.Length)
        {
            return;
        }

        ReadOnlyStorageBindingSet next = ReadOnlyStorageBindingSet.WithBinding(
            bindings,
            replacement);
        ReadOnlyStorageBindingSet? previous = bindings;
        bindings = next;
        previous?.Dispose();
    }

    private static void ReplaceReadOnlyStorageBindings(
        ref ReadOnlyStorageBindingSet? bindings,
        ReadOnlyStorageBindingSet? replacement)
    {
        ReadOnlyStorageBindingSet? next = replacement?.Retain();
        ReadOnlyStorageBindingSet? previous = bindings;
        bindings = next;
        previous?.Dispose();
    }

    internal void HandlePlannerDispatch(
        VulkanProgramPlannerPort planner,
        uint x,
        uint y,
        uint z,
        IEnumerable<(uint unit, IRenderTextureResource texture, int level, int? layer, XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)>? textures = null)
    {
        if (textures is not null)
        {
            foreach (var (unit, texture, level, layer, access, format) in textures)
                BindImageTexture(unit, texture, level, layer.HasValue, layer ?? 0, access, format);
        }

        int gx = x > int.MaxValue ? int.MaxValue : (int)x;
        int gy = y > int.MaxValue ? int.MaxValue : (int)y;
        int gz = z > int.MaxValue ? int.MaxValue : (int)z;
        planner.DispatchCompute(this, gx, gy, gz);
    }

}
