using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedShadingOutput
{
    private readonly WebGpuAdvancedTexturePair[] _globals = new WebGpuAdvancedTexturePair[WebGpuAdvancedShadingCohort.SlotCount];
    private readonly WebGpuAdvancedTexturePair[] _working = new WebGpuAdvancedTexturePair[WebGpuAdvancedShadingCohort.SlotCount];
    private readonly Dictionary<(ulong Epoch, AdvancedGpuHandle Handle), WebGpuAdvancedSampler> _samplers = [];
    private int _globalCount;

    private void PrepareCohorts(in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedVisibilityFrame visibility,
        WebGpuAdvancedShadingFrame frame)
    {
        AdvancedGpuScenePublicationSnapshot snapshot = visibility.Scene!.Snapshot;
        bool rebuild = !CanReusePlan(snapshot, frame, in request);
        if (rebuild)
        {
            // An exception must not leave a partially replaced plan reusable against
            // the previously accepted publication on a later frame.
            frame.DatabaseEpoch = 0;
            _globalCount = 0;
            uint view = request.NativeViewIndex;
            if (request.EnableLightProbesAndIbl)
                foreach (ref readonly AdvancedProbeRecord probe in snapshot.GlobalResources.Probes.PhysicalRecords)
                {
                    if ((probe.Flags & 3u) != 3u || !ViewMatches(probe.ViewMaskLo, probe.ViewMaskHi, view)) continue;
                    AddGlobal(snapshot, probe.Irradiance);
                    AddGlobal(snapshot, probe.PrefilteredRadiance);
                }
            foreach (ref readonly AdvancedShadowRecord shadow in snapshot.GlobalResources.Shadows.PhysicalRecords)
                if (((uint)shadow.Flags & 1u) != 0 && ViewMatches(shadow.ViewMaskLo, shadow.ViewMaskHi, view))
                    AddGlobal(snapshot, shadow.Texture);
            foreach (ref readonly AdvancedDecalRecord decal in snapshot.GlobalResources.Decals.PhysicalRecords)
            {
                if ((decal.Flags & AdvancedDecalRecord.EnabledFlag) == 0 || !ViewMatches(decal.ViewMaskLo, decal.ViewMaskHi, view)) continue;
                AddGlobal(snapshot, decal.MaskTexture);
                if (!snapshot.Materials.TryGet(decal.Material, out AdvancedMaterialRecord material))
                    throw Invalid("DecalMaterialMissing", "an enabled decal lacks its exact published material");
                AddMaterial(snapshot, in material, _globals, ref _globalCount);
            }
            ReadOnlySpan<AdvancedMaterialRecord> materials = snapshot.Materials.PhysicalRecords;
            if (frame.MaterialRows.Length < Math.Max(materials.Length, 1)) Array.Resize(ref frame.MaterialRows, Math.Max(materials.Length, 1));
            frame.MaterialRows.AsSpan().Fill(uint.MaxValue);
            frame.CohortCount = 0;
            for (int dense = 0; dense < materials.Length; dense++)
            {
                if (snapshot.Materials.PhysicalOccupancy[dense] == 0) continue;
                ref readonly AdvancedMaterialRecord material = ref materials[dense];
                if (material.CoverageMode is not (EAdvancedMaterialCoverageMode.Opaque or EAdvancedMaterialCoverageMode.Masked)) continue;
                AdvancedGpuHandle handle = new(material.ShadingKernelId, material.ShadingKernelGeneration);
                if (!snapshot.Kernels.TryGetDenseIndex(handle, out uint kernelDense) ||
                    !snapshot.Kernels.TryGet(handle, out AdvancedShadingKernelRecord kernel) || !IsCanonicalKernel(in material, in kernel))
                    throw Invalid("KernelUnsupported", "the material requires a kernel other than its exact canonical native companion");
                _globals.AsSpan(0, _globalCount).CopyTo(_working);
                int count = _globalCount;
                AddMaterial(snapshot, in material, _working, ref count);
                int selected = -1;
                for (int index = 0; index < frame.CohortCount; index++)
                    if (frame.Cohorts[index]!.Matches(kernelDense, _working.AsSpan(0, count))) { selected = index; break; }
                if (selected < 0)
                {
                    if (frame.CohortCount >= WebGpuAdvancedShadingFrame.MaximumCohorts - 1)
                        throw Invalid("CohortCapacity", "the complete publication exceeds 127 native cohorts plus the diagnostic cohort");
                    selected = frame.CohortCount++;
                    WebGpuAdvancedShadingCohort cohort = frame.Cohorts[selected] ??= new(_renderer);
                    PrepareCohort(snapshot, cohort, kernelDense, _working.AsSpan(0, count));
                }
                frame.MaterialRows[dense] = (uint)selected;
            }
            WebGpuAdvancedShadingCohort diagnostic = frame.Cohorts[frame.CohortCount] ??= new(_renderer);
            PrepareCohort(snapshot, diagnostic, uint.MaxValue, []);
            frame.CohortCount++;
            for (int index = frame.CohortCount; index < frame.Cohorts.Length; index++)
            {
                WebGpuAdvancedShadingCohort? obsolete = frame.Cohorts[index];
                if (obsolete is null) continue;
                obsolete.Dispose();
                frame.Cohorts[index] = null;
            }
            frame.Materials.EnsureCapacity(checked(Math.Max(materials.Length, 1) * 4));
            frame.Materials.UploadPreparation(MemoryMarshal.AsBytes(frame.MaterialRows.AsSpan(0, Math.Max(materials.Length, 1))));
            frame.DatabaseEpoch = snapshot.DatabaseEpoch; frame.MaterialGenerations = snapshot.MaterialPayloads.Generations;
            frame.ResourceGenerations = snapshot.ResourceGenerations; frame.ViewIndex = request.NativeViewIndex; frame.IblEnabled = request.EnableLightProbesAndIbl;
        }
        else
            for (int index = 0; index < frame.CohortCount; index++)
            {
                WebGpuAdvancedShadingCohort cohort = frame.Cohorts[index]!;
                PrepareCohort(snapshot, cohort, cohort.Kernel, cohort.Pairs.AsSpan(0, cohort.PairCount), uploadMap: false);
            }
        frame.Width = request.Target.Width;
        frame.Height = request.Target.Height;
        frame.TileCapacity = checked(((frame.Width + 15u) / 16u) * ((frame.Height + 15u) / 16u));
        long tileBytes = (long)frame.TileCapacity * frame.CohortCount * 16;
        if (tileBytes > _renderer.MaximumAdvancedStorageBytes)
            throw Invalid("TileCapacity", "complete per-cohort physical tile capacity exceeds the selected storage binding limit");
        frame.Tiles.EnsureCapacity(checked((int)tileBytes));
        frame.Counts.EnsureCapacity(frame.CohortCount * 16);
        frame.Arguments.EnsureCapacity(frame.CohortCount * 16);
        Span<uint> zero = stackalloc uint[WebGpuAdvancedShadingFrame.MaximumCohorts * 4];
        zero.Clear();
        frame.Counts.UploadPreparation(MemoryMarshal.AsBytes(zero[..(frame.CohortCount * 4)]));
    }

    private static bool CanReusePlan(AdvancedGpuScenePublicationSnapshot snapshot, WebGpuAdvancedShadingFrame frame,
        in AdvancedVisibilityStageBackendRequest request)
    {
        if (frame.CohortCount == 0 || frame.DatabaseEpoch != snapshot.DatabaseEpoch ||
            frame.MaterialGenerations != snapshot.MaterialPayloads.Generations || frame.ResourceGenerations != snapshot.ResourceGenerations ||
            frame.ViewIndex != request.NativeViewIndex || frame.IblEnabled != request.EnableLightProbesAndIbl) return false;
        for (int index = 0; index < frame.CohortCount; index++)
        {
            WebGpuAdvancedShadingCohort cohort = frame.Cohorts[index]!;
            for (int pairIndex = 0; pairIndex < cohort.PairCount; pairIndex++)
            {
                WebGpuAdvancedTexturePair pair = cohort.Pairs[pairIndex];
                if (!snapshot.ResourcePayloads.TryGetTextureSource(pair.Texture, out XRTexture source, out ulong generation) ||
                    !ReferenceEquals(source, pair.Source) || generation != pair.ContentGeneration) return false;
            }
        }
        return true;
    }

    private void AddGlobal(AdvancedGpuScenePublicationSnapshot snapshot, AdvancedTextureReference reference)
    {
        if (!reference.Handle.IsValid) return;
        if (!snapshot.Textures.TryGet(reference.Handle, out AdvancedTextureRecord record))
            throw Invalid("TextureGenerationMissing", "a valid global texture handle is absent from the retained publication");
        AddPair(snapshot, reference.Handle, record.DefaultSampler, _globals, ref _globalCount);
    }

    private static void AddMaterial(AdvancedGpuScenePublicationSnapshot snapshot, in AdvancedMaterialRecord material,
        WebGpuAdvancedTexturePair[] pairs, ref int count)
    {
        bool mirror = material.MaterialLayoutHash == WebGpuAdvancedShadingParameters.MirrorHash;
        uint words = mirror ? MaterialBindingLayouts.ProjectiveMirror.RowWordCount : MaterialBindingLayouts.OpaqueDeferred.RowWordCount;
        if ((!mirror && !WebGpuAdvancedStandardMaterialContract.IsStandard(in material)) ||
            material.ConstantWordCount != words || material.ConstantWordOffset > snapshot.MaterialPayloads.ConstantWords.Length ||
            material.ConstantWordCount > snapshot.MaterialPayloads.ConstantWords.Length - material.ConstantWordOffset ||
            material.TextureReferenceCount != (mirror ? 3u : 4u))
            throw Invalid("MaterialLayoutUnsupported", "the material payload is not the complete canonical native schema");
        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings = snapshot.MaterialPayloads.TextureBindings;
        if (material.TextureReferenceOffset > bindings.Length || material.TextureReferenceCount > bindings.Length - material.TextureReferenceOffset)
            throw Invalid("MaterialTextureRange", "the published material texture range exceeds the frozen binding arena");
        for (uint index = 0; index < material.TextureReferenceCount; index++)
        {
            AdvancedMaterialTextureBinding binding = bindings[(int)(material.TextureReferenceOffset + index)];
            if (binding.Texture.Handle.IsValid) AddPair(snapshot, binding.Texture.Handle, binding.Sampler.Handle, pairs, ref count);
        }
    }

    private static void AddPair(AdvancedGpuScenePublicationSnapshot snapshot, AdvancedGpuHandle texture, AdvancedGpuHandle sampler,
        WebGpuAdvancedTexturePair[] pairs, ref int count)
    {
        for (int index = 0; index < count; index++) if (pairs[index].Texture == texture && pairs[index].Sampler == sampler) return;
        if (!snapshot.Textures.TryGet(texture, out AdvancedTextureRecord record) ||
            !snapshot.Samplers.TryGet(sampler, out _) ||
            !snapshot.ResourcePayloads.TryGetTextureSource(texture, out XRTexture source, out ulong generation))
            throw Invalid("TexturePairMissing", "a valid texture/sampler pair lacks its exact retained records and source generation");
        int dimensionCount = 0;
        for (int index = 0; index < count; index++) if (pairs[index].Dimension == record.Dimension) dimensionCount++;
        int capacity = record.Dimension == EAdvancedTextureDimension.Texture2D ? 10 :
            record.Dimension is EAdvancedTextureDimension.Cube or EAdvancedTextureDimension.Texture2DArray ? 1 : 0;
        if (dimensionCount >= capacity || count == pairs.Length)
            throw Invalid("TextureBankCapacity", "native closure exceeds the explicit bank of ten 2D pairs, one cube pair, and one 2D-array pair");
        pairs[count++] = new(texture, sampler, record.Dimension, source, generation);
    }

    private void PrepareCohort(AdvancedGpuScenePublicationSnapshot snapshot, WebGpuAdvancedShadingCohort cohort,
        uint kernel, ReadOnlySpan<WebGpuAdvancedTexturePair> pairs, bool uploadMap = true)
    {
        cohort.Kernel = kernel; cohort.PairCount = pairs.Length;
        pairs.CopyTo(cohort.Pairs);
        cohort.BindingWords.AsSpan().Clear();
        Span<bool> occupied = stackalloc bool[WebGpuAdvancedShadingCohort.SlotCount];
        occupied.Clear();
        int next2D = 0;
        for (int index = 0; index < pairs.Length; index++)
        {
            WebGpuAdvancedTexturePair pair = pairs[index];
            if (!snapshot.Textures.TryGet(pair.Texture, out AdvancedTextureRecord record) ||
                !snapshot.Samplers.TryGet(pair.Sampler, out AdvancedSamplerRecord samplerRecord) ||
                !AdvancedGpuResourceSourceEncoder.TryEncode(pair.Source, EAdvancedResourceFallback.Zero,
                    out AdvancedGpuResourceBindingSource current, out _, out _) || current.SourceContentGeneration != pair.ContentGeneration ||
                current.TextureRecord.Dimension != record.Dimension || current.TextureRecord.Width != record.Width ||
                current.TextureRecord.Height != record.Height || current.TextureRecord.DepthOrLayers != record.DepthOrLayers ||
                current.TextureRecord.MipCount != record.MipCount || current.TextureRecord.FormatClass != record.FormatClass ||
                current.TextureRecord.Flags != record.Flags)
                throw Invalid("TextureGenerationChanged", "the source no longer matches its frozen texture metadata and content generation");
            WebGpuTextureResource resource = WebGpuTextureResource.Resolve(_renderer, pair.Source);
            if (resource.Width != record.Width || resource.Height != record.Height || resource.Layers != record.DepthOrLayers || resource.Mips != record.MipCount || resource.Samples != 1 || WebGpuTextureFormat.IsDepth(resource.Format) || WebGpuTextureFormat.IsInteger(resource.Format) ||
                resource.Format is "r32float" or "rg32float" or "rgba32float" &&
                    _renderer.DeviceCapabilities?.Features.Contains("float32-filterable") != true)
                throw Invalid("TextureSampleType", "native bank sampling requires an exact filterable non-depth floating-point texture");
            var key = (snapshot.DatabaseEpoch, pair.Sampler);
            int slot = pair.Dimension == EAdvancedTextureDimension.Texture2D ? next2D++ : pair.Dimension == EAdvancedTextureDimension.Cube ? 10 : 11;
            if (_samplers.TryGetValue(key, out WebGpuAdvancedSampler? sampler) && !sampler.Matches(in samplerRecord))
            {
                // Older slots can still own the previous record behind the same
                // logical handle. Their cohort references retire it independently.
                _samplers.Remove(key); sampler = null;
            }
            if (sampler is null)
            {
                cohort.SetSampler(slot, null);
                int capacity = (int)AdvancedFrameSlotContract.DefaultSlotCount * WebGpuAdvancedShadingFrame.MaximumCohorts * WebGpuAdvancedShadingCohort.SlotCount;
                if (_samplers.Count >= capacity) throw Invalid("SamplerCapacity", "the output exceeds its completion-slot texture/sampler bank capacity");
                sampler = new(_renderer, in samplerRecord, key, _onSamplerUnused);
                try { sampler.Generate(); }
                catch { sampler.Dispose(); throw; }
                _samplers.Add(key, sampler);
            }
            sampler.Generate();
            cohort.SetSampler(slot, sampler);
            cohort.SetView(slot, in resource); occupied[slot] = true;
            Span<uint> words = cohort.BindingWords.AsSpan(index * 8, 8);
            words[0] = pair.Texture.Index; words[1] = pair.Texture.Generation;
            words[2] = pair.Sampler.Index; words[3] = pair.Sampler.Generation;
            words[4] = (uint)pair.Dimension; words[5] = (uint)slot;
        }
        for (int slot = 0; slot < occupied.Length; slot++) if (!occupied[slot]) cohort.ReleaseView(slot);
        cohort.Bindings.EnsureCapacity(WebGpuAdvancedShadingCohort.SlotCount * 32);
        if (uploadMap) cohort.Bindings.UploadPreparation(MemoryMarshal.AsBytes(cohort.BindingWords.AsSpan()));
    }

    private void ReleaseSampler(WebGpuAdvancedSampler sampler)
    {
        if (_samplers.TryGetValue(sampler.Key, out WebGpuAdvancedSampler? current) && ReferenceEquals(current, sampler))
            _samplers.Remove(sampler.Key);
    }

    private static bool IsCanonicalKernel(in AdvancedMaterialRecord material, in AdvancedShadingKernelRecord kernel)
    {
        bool mirror = material.MaterialLayoutHash == WebGpuAdvancedShadingParameters.MirrorHash;
        if (!mirror && !WebGpuAdvancedStandardMaterialContract.IsStandard(in material)) return false;
        uint coverage = (uint)material.CoverageMode, state = (uint)material.RenderStateClass;
        ulong identity = unchecked((material.MaterialLayoutHash ^ (((ulong)coverage << 32) | state)) * 1099511628211ul);
        EAdvancedMaterialRequiredAttributeMask attributes = mirror ? EAdvancedMaterialRequiredAttributeMask.Position :
            EAdvancedMaterialRequiredAttributeMask.Position | EAdvancedMaterialRequiredAttributeMask.Normal |
            EAdvancedMaterialRequiredAttributeMask.Tangent | EAdvancedMaterialRequiredAttributeMask.TexCoord0 |
            EAdvancedMaterialRequiredAttributeMask.TexCoord1 | EAdvancedMaterialRequiredAttributeMask.Color0 |
            EAdvancedMaterialRequiredAttributeMask.AnalyticalDerivatives;
        EAdvancedMaterialEligibilityFlags eligibility = EAdvancedMaterialEligibilityFlags.NativeOpaque | EAdvancedMaterialEligibilityFlags.Unlit;
        if (!mirror) eligibility |= EAdvancedMaterialEligibilityFlags.NativeMasked | EAdvancedMaterialEligibilityFlags.LateTransparent | EAdvancedMaterialEligibilityFlags.LateRefractive;
        EAdvancedMaterialFeatureFlags features = EAdvancedMaterialFeatureFlags.DoubleSided;
        if (!mirror) features |= EAdvancedMaterialFeatureFlags.BaseColorTexture | EAdvancedMaterialFeatureFlags.NormalTexture |
            EAdvancedMaterialFeatureFlags.MetallicRoughnessTexture | EAdvancedMaterialFeatureFlags.Emissive |
            EAdvancedMaterialFeatureFlags.ReceivesShadows | EAdvancedMaterialFeatureFlags.CastsShadows |
            EAdvancedMaterialFeatureFlags.VertexDeformation | EAdvancedMaterialFeatureFlags.Animated;
        return kernel.RequiredAttributeMask == attributes && kernel.SupportedEligibility == eligibility &&
            kernel.SupportedFeatures == features && kernel.Flags == 0 &&
            coverage < 32 && state < 32 && kernel.MaterialLayoutHash == material.MaterialLayoutHash &&
            kernel.ShaderIdentityHash == identity && kernel.SupportedCoverageMask == 1u << (int)coverage &&
            kernel.RenderStateClassMask == 1u << (int)state &&
            (material.FeatureFlags & ~kernel.SupportedFeatures) == 0 &&
            (material.EligibilityFlags & ~kernel.SupportedEligibility) == 0;
    }
    private static bool ViewMatches(uint low, uint high, uint view)
        => (low | high) == 0 || (view < 32 ? (low & (1u << (int)view)) != 0 : view < 64 && (high & (1u << (int)(view - 32))) != 0);
    private static NotSupportedException Invalid(string code, string reason) => new($"WebGPU.Advanced.{code}: {reason}.");
}
