using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedShadingOutput
{
    private readonly WebGpuAdvancedTexturePair[] _globals = new WebGpuAdvancedTexturePair[WebGpuAdvancedShadingCohort.SlotCount];
    private readonly WebGpuAdvancedTexturePair[] _working = new WebGpuAdvancedTexturePair[WebGpuAdvancedShadingCohort.SlotCount];
    private readonly Dictionary<(ulong Epoch, AdvancedGpuHandle Handle, uint SamplingKey), WebGpuAdvancedSampler> _samplers = [];
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
            frame.NativeModifiersAbsent = false;
            _globalCount = 0;
            PrepareAuthoredDecals(in request, snapshot, frame);
            uint view = request.NativeViewIndex;
            bool needsNativeGlobals = false;
            ReadOnlySpan<AdvancedMaterialRecord> sourceMaterials = snapshot.Materials.PhysicalRecords;
            for (int dense = 0; dense < sourceMaterials.Length; dense++)
                if (snapshot.Materials.PhysicalOccupancy[dense] != 0 &&
                    sourceMaterials[dense].CoverageMode is EAdvancedMaterialCoverageMode.Opaque or EAdvancedMaterialCoverageMode.Masked &&
                    sourceMaterials[dense].SourceContract != EAdvancedMaterialSourceContract.UberBaseSurface)
                { needsNativeGlobals = true; break; }
            if (needsNativeGlobals && request.EnableLightProbesAndIbl)
                foreach (ref readonly AdvancedProbeRecord probe in snapshot.GlobalResources.Probes.PhysicalRecords)
                {
                    if ((probe.Flags & 3u) != 3u || !ViewMatches(probe.ViewMaskLo, probe.ViewMaskHi, view)) continue;
                    AddGlobal(snapshot, probe.Irradiance);
                    AddGlobal(snapshot, probe.PrefilteredRadiance);
                }
            ReadOnlySpan<AdvancedShadowRecord> shadows = snapshot.GlobalResources.Shadows.PhysicalRecords;
            for (int index = 0; needsNativeGlobals && index < shadows.Length; index++)
            {
                ref readonly AdvancedShadowRecord shadow = ref shadows[index];
                if (snapshot.GlobalResources.Shadows.TryGetDenseIndex(new(shadow.StableShadowId, shadow.Generation), out uint dense) &&
                    dense == (uint)index && ((uint)shadow.Flags & 1u) != 0 && ViewMatches(shadow.ViewMaskLo, shadow.ViewMaskHi, view))
                    AddGlobal(snapshot, shadow.Texture);
            }
            foreach (ref readonly AdvancedDecalRecord decal in snapshot.GlobalResources.Decals.PhysicalRecords)
            {
                if (!needsNativeGlobals) break;
                if ((decal.Flags & (AdvancedDecalRecord.AuthoredAlbedoFlag | AdvancedDecalRecord.UnsupportedAuthoredFlag)) != 0) continue;
                if (!snapshot.GlobalResources.Decals.TryGetDenseIndex(decal.Identity, out _)) continue;
                if ((decal.Flags & AdvancedDecalRecord.EnabledFlag) == 0 || !ViewMatches(decal.ViewMaskLo, decal.ViewMaskHi, view)) continue;
                AddGlobal(snapshot, decal.MaskTexture);
                if (!snapshot.Materials.TryGet(decal.Material, out AdvancedMaterialRecord material))
                    throw Invalid("DecalMaterialMissing", "an enabled decal lacks its exact published material");
                AddMaterial(snapshot, in material, _globals, ref _globalCount);
            }
            frame.DepthComparisonBank = false;
            for (int index = 0; index < _globalCount; index++)
                frame.DepthComparisonBank |= _globals[index].DepthComparison;
            ReadOnlySpan<AdvancedMaterialRecord> materials = snapshot.Materials.PhysicalRecords;
            if (frame.MaterialRows.Length < Math.Max(materials.Length, 1)) Array.Resize(ref frame.MaterialRows, Math.Max(materials.Length, 1));
            frame.MaterialRows.AsSpan().Fill(uint.MaxValue);
            frame.CohortCount = 0;
            frame.HasUberRaster = false;
            for (int dense = 0; dense < materials.Length; dense++)
            {
                if (snapshot.Materials.PhysicalOccupancy[dense] == 0) continue;
                ref readonly AdvancedMaterialRecord material = ref materials[dense];
                if (material.CoverageMode is not (EAdvancedMaterialCoverageMode.Opaque or EAdvancedMaterialCoverageMode.Masked)) continue;
                if (WebGpuAdvancedMaterialContract.GetSourceRejection(material.SourceContract) is { } sourceReason)
                    throw Invalid("MaterialSourceUnsupported", sourceReason);
                AdvancedGpuHandle handle = new(material.ShadingKernelId, material.ShadingKernelGeneration);
                if (!snapshot.Kernels.TryGetDenseIndex(handle, out uint kernelDense) ||
                    !snapshot.Kernels.TryGet(handle, out AdvancedShadingKernelRecord kernel) || !WebGpuAdvancedMaterialContract.IsCanonicalKernel(in material, in kernel))
                    throw Invalid("KernelUnsupported", "the material requires a kernel other than its exact canonical native companion");
                bool uberRaster = material.SourceContract == EAdvancedMaterialSourceContract.UberBaseSurface;
                if (!uberRaster) _globals.AsSpan(0, _globalCount).CopyTo(_working);
                int count = uberRaster ? 0 : _globalCount;
                AddMaterial(snapshot, in material, _working, ref count);
                frame.HasUberRaster |= uberRaster;
                int selected = -1;
                for (int index = 0; index < frame.CohortCount; index++)
                    if (frame.Cohorts[index]!.Matches(kernelDense, _working.AsSpan(0, count), uberRaster)) { selected = index; break; }
                if (selected < 0)
                {
                    if (frame.CohortCount >= WebGpuAdvancedShadingFrame.MaximumCohorts - 1)
                        throw Invalid("CohortCapacity", "the complete publication exceeds 127 native cohorts plus the diagnostic cohort");
                    selected = frame.CohortCount++;
                    WebGpuAdvancedShadingCohort cohort = frame.Cohorts[selected] ??= new(_renderer);
                    cohort.UberRaster = uberRaster;
                    PrepareCohort(snapshot, cohort, kernelDense, _working.AsSpan(0, count), frame: frame);
                }
                frame.MaterialRows[dense] = (uint)selected;
            }
            WebGpuAdvancedShadingCohort diagnostic = frame.Cohorts[frame.CohortCount] ??= new(_renderer);
            diagnostic.UberRaster = false;
            PrepareCohort(snapshot, diagnostic, uint.MaxValue, [], frame: frame);
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
            frame.NativeModifiersAbsent = AreNativeModifiersAbsent(snapshot, in request, frame);
            frame.DatabaseEpoch = snapshot.DatabaseEpoch; frame.MaterialGenerations = snapshot.MaterialPayloads.Generations;
            frame.ResourceGenerations = snapshot.ResourceGenerations; frame.ViewIndex = request.NativeViewIndex; frame.IblEnabled = request.EnableLightProbesAndIbl;
            frame.AuthoredDecalsEnabled = request.EnableAuthoredDecals;
            frame.AuthoredDecalCommandSignature = AuthoredDecalCommandSignature(in request);
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
            frame.ViewIndex != request.NativeViewIndex || frame.IblEnabled != request.EnableLightProbesAndIbl ||
            frame.AuthoredDecalsEnabled != request.EnableAuthoredDecals ||
            !HasCurrentAuthoredDecalCount(in request, frame) ||
            frame.AuthoredDecalCommandSignature != AuthoredDecalCommandSignature(in request)) return false;
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
        AddPair(snapshot, reference.Handle, record.DefaultSampler, _globals, ref _globalCount, allowDepthComparison: true);
    }

    private static void AddMaterial(AdvancedGpuScenePublicationSnapshot snapshot, in AdvancedMaterialRecord material,
        WebGpuAdvancedTexturePair[] pairs, ref int count)
    {
        if (WebGpuAdvancedMaterialContract.GetSourceRejection(material.SourceContract) is { } sourceReason)
            throw Invalid("MaterialSourceUnsupported", sourceReason);
        if (WebGpuAdvancedEngineSurfaceContract.GetRejection(in material, snapshot.MaterialPayloads) is { } companionReason)
            throw Invalid("EngineSurfaceUnsupported", companionReason);
        if (WebGpuAdvancedUberBaseContract.GetRejection(in material, snapshot.MaterialPayloads) is { } uberReason)
            throw Invalid("UberBaseUnsupported", uberReason);
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
        if (material.SourceContract == EAdvancedMaterialSourceContract.UberBaseSurface &&
            snapshot.MaterialPayloads.TryGetUberBaseSurface(in material, out AdvancedUberBaseSurfaceRecord uber))
        {
            // The exact seven material samples are produced by the shared fragment raster stage.
            // Native lighting binds only its own globals plus the full-float surface image.
            return;
        }
        if (snapshot.MaterialPayloads.TryGetEngineSurface(in material, out AdvancedEngineSurfaceRecord engineSurface) &&
            engineSurface.SchemaVersion != 0)
        {
            for (int index = 0; index < AdvancedEngineSurfaceRecord.RoleCount; ++index)
            {
                if ((engineSurface.RoleFlags & (1u << index)) == 0) continue;
                AdvancedMaterialTextureBinding binding = engineSurface.GetRole(index).Binding;
                AddPair(snapshot, binding.Texture.Handle, binding.Sampler.Handle, pairs, ref count,
                    samplingKey: engineSurface.GetSamplingKey(index));
            }
            return;
        }
        for (uint index = 0; index < material.TextureReferenceCount; index++)
        {
            AdvancedMaterialTextureBinding binding = bindings[(int)(material.TextureReferenceOffset + index)];
            if (binding.Texture.Handle.IsValid) AddPair(snapshot, binding.Texture.Handle, binding.Sampler.Handle, pairs, ref count);
        }
    }

    private static void AddPair(AdvancedGpuScenePublicationSnapshot snapshot, AdvancedGpuHandle texture, AdvancedGpuHandle sampler,
        WebGpuAdvancedTexturePair[] pairs, ref int count, bool allowDepthComparison = false, uint samplingKey = 0)
    {
        if (!snapshot.Textures.TryGet(texture, out AdvancedTextureRecord record) ||
            !snapshot.Samplers.TryGet(sampler, out AdvancedSamplerRecord samplerRecord) ||
            !snapshot.ResourcePayloads.TryGetTextureSource(texture, out XRTexture source, out ulong generation))
            throw Invalid("TexturePairMissing", "a valid texture/sampler pair lacks its exact retained records and source generation");
        if (samplingKey != 0) samplerRecord = new AdvancedEngineSurfaceSamplingKey(samplingKey).ApplyTo(in samplerRecord);
        if (WebGpuAdvancedMaterialContract.GetTexturePairRejection(in record, in samplerRecord,
            allowDepthComparison, out bool depth) is { } pairReason)
            throw Invalid("TextureSampleType", pairReason);
        for (int index = 0; index < count; index++) if (pairs[index].Texture == texture && pairs[index].Sampler == sampler && pairs[index].SamplingKey == samplingKey) return;
        int color2D = record.Dimension == EAdvancedTextureDimension.Texture2D && !depth ? 1 : 0;
        int depth2D = depth ? 1 : 0;
        int cube = record.Dimension == EAdvancedTextureDimension.Cube ? 1 : 0;
        int array = record.Dimension == EAdvancedTextureDimension.Texture2DArray ? 1 : 0;
        for (int index = 0; index < count; index++)
        {
            WebGpuAdvancedTexturePair pair = pairs[index];
            if (pair.DepthComparison) depth2D++;
            else if (pair.Dimension == EAdvancedTextureDimension.Texture2D) color2D++;
            else if (pair.Dimension == EAdvancedTextureDimension.Cube) cube++;
            else if (pair.Dimension == EAdvancedTextureDimension.Texture2DArray) array++;
        }
        if (WebGpuAdvancedMaterialContract.GetTextureBankRejection(color2D, depth2D, cube, array) is { } bankReason)
            throw Invalid("TextureBankCapacity", bankReason);
        pairs[count++] = new(texture, sampler, record.Dimension, source, generation, depth, samplingKey);
    }

    private void PrepareCohort(AdvancedGpuScenePublicationSnapshot snapshot, WebGpuAdvancedShadingCohort cohort,
        uint kernel, ReadOnlySpan<WebGpuAdvancedTexturePair> pairs, bool uploadMap = true, WebGpuAdvancedShadingFrame? frame = null)
    {
        cohort.Kernel = kernel; cohort.PairCount = pairs.Length;
        pairs.CopyTo(cohort.Pairs);
        int bindingWordCount = checked(WebGpuAdvancedShadingCohort.SlotCount * 8 + (frame?.AuthoredDecalCount ?? 0));
        if (uploadMap)
        {
            if (cohort.BindingWords.Length < bindingWordCount) Array.Resize(ref cohort.BindingWords, bindingWordCount);
            cohort.BindingWords.AsSpan(0, bindingWordCount).Clear();
            cohort.BindingWordCount = bindingWordCount;
        }
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
                pair.SamplingKey == 0 && current.TextureRecord.MipCount != record.MipCount || current.TextureRecord.FormatClass != record.FormatClass ||
                current.TextureRecord.Flags != record.Flags)
                throw Invalid("TextureGenerationChanged", "the source no longer matches its frozen texture metadata and content generation");
            WebGpuTextureResource resource = WebGpuTextureResource.Resolve(_renderer, pair.Source);
            if (resource.Width != record.Width || resource.Height != record.Height || resource.Layers != record.DepthOrLayers || resource.Mips != (pair.SamplingKey == 0 ? record.MipCount : new AdvancedEngineSurfaceSamplingKey(pair.SamplingKey).StorageMipCount) || WebGpuAdvancedMaterialContract.GetSamplingRejection(resource.Format, resource.Samples,
                    _renderer.DeviceCapabilities?.Features.Contains("float32-filterable") == true, pair.DepthComparison) is not null)
                throw Invalid("TextureSampleType", "native bank sampling requires exact single-sample color or typed depth-comparison resources");
            if (pair.SamplingKey != 0) samplerRecord = new AdvancedEngineSurfaceSamplingKey(pair.SamplingKey).ApplyTo(in samplerRecord);
            var key = (snapshot.DatabaseEpoch, pair.Sampler, pair.SamplingKey);
            if (cohort.UberRaster && pair.Dimension == EAdvancedTextureDimension.Texture2D && !pair.DepthComparison && next2D >= 8)
                throw Invalid("UberLightingTextureCapacity", "the dedicated Uber lighting bank reserves slot eight for its full-float raster surface");
            int slot = pair.DepthComparison ? 9 : pair.Dimension == EAdvancedTextureDimension.Texture2D ? next2D++ : pair.Dimension == EAdvancedTextureDimension.Cube ? 10 : 11;
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
                _samplers.Add(key, sampler);
            }
            sampler.Generate();
            cohort.SetSampler(slot, sampler);
            cohort.SetView(slot, in resource, pair.SamplingKey); occupied[slot] = true;
            Span<uint> words = cohort.BindingWords.AsSpan(index * 8, 8);
            words[0] = pair.Texture.Index; words[1] = pair.Texture.Generation;
            words[2] = pair.Sampler.Index; words[3] = pair.Sampler.Generation;
            words[4] = (uint)pair.Dimension; words[5] = (uint)slot;
            words[6] = pair.DepthComparison ? 1u : 0u;
            words[7] = pair.SamplingKey;
        }
        for (int slot = 0; slot < occupied.Length; slot++) if (!occupied[slot]) cohort.ReleaseView(slot);
        if (uploadMap)
        {
            if (frame is not null) frame.AuthoredDecalIndices.AsSpan(0, frame.AuthoredDecalCount).CopyTo(
                cohort.BindingWords.AsSpan(WebGpuAdvancedShadingCohort.SlotCount * 8));
            cohort.Bindings.EnsureCapacity(checked(bindingWordCount * sizeof(uint)));
            cohort.Bindings.UploadPreparation(MemoryMarshal.AsBytes(cohort.BindingWords.AsSpan(0, bindingWordCount)));
        }
    }

    private void ReleaseSampler(WebGpuAdvancedSampler sampler)
    {
        if (_samplers.TryGetValue(sampler.Key, out WebGpuAdvancedSampler? current) && ReferenceEquals(current, sampler))
            _samplers.Remove(sampler.Key);
    }

    private static bool ViewMatches(uint low, uint high, uint view)
        => (low | high) == 0 || (view < 32 ? (low & (1u << (int)view)) != 0 : view < 64 && (high & (1u << (int)(view - 32))) != 0);
    private static NotSupportedException Invalid(string code, string reason) => new($"WebGPU.Advanced.{code}: {reason}.");
}
