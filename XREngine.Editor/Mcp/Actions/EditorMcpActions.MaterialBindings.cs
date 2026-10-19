using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    private static readonly ConditionalWeakTable<RuntimeWorld, EditorMcpLiveTextureReceiptStore> s_liveTextureReceipts = new();

    /// <summary>Reads model slots and texture sampling metadata without decoding pixels.</summary>
    [XRMcp(Name = "get_model_material_bindings", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Read model source slots, assigned materials, legacy and surface texture bindings, source paths, and sampling metadata. Native Uber preflight does not prove GPU execution. Does not decode textures or read GPU pixels.")]
    public static Task<McpToolResponse> GetModelMaterialBindingsAsync(
        McpToolContext context,
        [McpName("node_id"), Description("Optional subtree root. Omit to inspect the current world.")] string? nodeId = null,
        [McpName("include_inactive"), Description("Include inactive nodes and model components.")] bool includeInactive = false)
    {
        List<SceneNode> roots = [];
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            if (!TryGetNodeById(context.World, nodeId, out SceneNode? root, out string? error) || root is null)
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));
            roots.Add(root);
        }
        else
        {
            if (context.World.TargetWorld is { } world)
                foreach (XRScene scene in world.Scenes)
                    foreach (SceneNode root in scene.RootNodes)
                        if (root is not null)
                            roots.Add(root);
            foreach (SceneNode root in context.World.RootNodes)
                if (root is not null)
                    roots.Add(root);
        }

        List<object> slots = [];
        List<object> materials = [];
        List<object> textures = [];
        HashSet<Guid> seenMaterials = [];
        HashSet<Guid> seenTextures = [];
        HashSet<Guid> seenNodes = [];
        bool truncated = false;
        uint[] nativeWords = new uint[NativeUberMaterialSchema.Opaque.RowWordCount];
        AdvancedGpuResourceBindingSource[] nativeResources = new AdvancedGpuResourceBindingSource[NativeUberMaterialSchema.TextureRoleCount];
        void AddTextureSummary(XRTexture? texture)
        {
            if (texture is null || !seenTextures.Add(texture.ID))
                return;
            XRTexture2D? texture2D = texture as XRTexture2D;
            var mipmaps = texture2D?.Mipmaps;
            textures.Add(new
            {
                id = texture.ID, name = texture.Name, sourcePath = texture.OriginalPath,
                sampler = texture.SamplerName,
                colorSpace = texture.ImportedColorSpace.ToString(), usage = texture.ImportedUsage.ToString(),
                flipGreen = texture.ImportedNormalMapFlipGreen,
                minFilter = texture2D?.MinFilter.ToString(), magFilter = texture2D?.MagFilter.ToString(),
                wrapU = texture2D?.UWrap.ToString(), wrapV = texture2D?.VWrap.ToString(),
                lodBias = texture2D?.LodBias, anisotropy = texture2D?.MaxAnisotropy,
                autoGenerateMipmaps = texture.AutoGenerateMipmaps,
                format = texture2D?.SizedInternalFormat.ToString(),
                mipCount = mipmaps?.Length ?? 0,
                width = mipmaps is { Length: > 0 } ? mipmaps[0].Width : 0,
                height = mipmaps is { Length: > 0 } ? mipmaps[0].Height : 0,
            });
        }
        foreach (SceneNode root in roots)
        foreach (SceneNode node in EnumerateHierarchy(root))
        {
            if (!seenNodes.Add(node.ID))
                continue;
            foreach (ModelComponent component in node.GetComponents<ModelComponent>())
            {
                if (component.Model is not { } model ||
                    (!includeInactive && (!node.IsActiveInHierarchy || !component.IsActive)))
                    continue;
                for (int index = 0; index < model.Meshes.Count; index++)
                {
                    var subMesh = model.Meshes[index];
                    int lodIndex = 0;
                    foreach (var lod in subMesh.LODs)
                    {
                        if (slots.Count >= 4096)
                        {
                            truncated = true;
                            goto Complete;
                        }
                        slots.Add(new
                        {
                            nodeId = node.ID, nodeName = node.Name, path = BuildNodePath(node),
                            active = node.IsActiveInHierarchy && component.IsActive,
                            componentId = component.ID, submeshIndex = index, submeshName = subMesh.Name,
                            sourceSlot = subMesh.ImportedSourceMaterialSlot,
                            rendererSlot = subMesh.ImportedRendererMaterialSlot,
                            sourceMaterialName = subMesh.ImportedSourceMaterialName,
                            lodIndex = lodIndex++, materialId = lod.Material?.ID,
                        });
                        if (lod.Material is not { } material || !seenMaterials.Add(material.ID))
                            continue;
                        List<object> samplers = [];
                        for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
                        {
                            XRTexture? texture = material.Textures[textureIndex];
                            samplers.Add(new { index = textureIndex, sampler = texture?.SamplerName, textureId = texture?.ID });
                            AddTextureSummary(texture);
                        }
                        List<object> surfaceBindings = [];
                        for (int bindingIndex = 0; bindingIndex < material.SurfaceTextureBindings.Length; ++bindingIndex)
                        {
                            MaterialSurfaceTextureBinding? binding = material.SurfaceTextureBindings[bindingIndex];
                            if (binding is null)
                                continue;
                            surfaceBindings.Add(new
                            {
                                index = bindingIndex, semantic = binding.Semantic.ToString(),
                                textureId = binding.Texture?.ID,
                                sampler = binding.Texture?.SamplerName,
                            });
                            AddTextureSummary(binding.Texture);
                        }
                        bool? nativeUberPreflight = null;
                        string? nativeUberReason = null;
                        if (material.EngineSemantic == EngineMaterialSemanticIdentity.NativeUberV1)
                        {
                            nativeUberPreflight = NativeUberMaterialSourceEncoder.TryEncode(material,
                                nativeWords, nativeResources, out _, out string reason);
                            nativeUberReason = nativeUberPreflight == true ? null : reason;
                        }
                        MaterialConversionReportRegistry.Instance.TryGet(material, out MaterialConversionReport report);
                        materials.Add(new
                        {
                            id = material.ID, name = material.Name, sourcePath = material.OriginalPath,
                            engineSemantic = material.EngineSemantic.ToString(), nativeUberPreflight, nativeUberReason,
                            conversion = report is null ? null : new
                            {
                                outcome = report.Outcome.ToString(),
                                sourceShaderVersion = report.SourceShaderVersion,
                                converterVersion = report.ConverterVersion,
                                failures = report.Failures,
                                warnings = report.Warnings,
                            },
                            samplers, surfaceBindings,
                        });
                    }
                }
            }
        }
    Complete:
        return Task.FromResult(new McpToolResponse($"Read {slots.Count} model material slots.",
            new { slots, materials, textures, truncated }));
    }

    /// <summary>Replaces one texture reference on an exact live model material.</summary>
    [XRMcp(Name = "set_model_material_texture_binding", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Changes a live model material texture reference.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Replace one legacy or surface texture binding on an exact live model material. The replacement texture must already be used by a model material in the active world. StandardLitTextureV1 updates its paired legacy and surface aliases and rejects null. Other legacy slots can be cleared with null texture_id when the list allows null.")]
    public static Task<McpToolResponse> SetModelMaterialTextureBindingAsync(
        McpToolContext context,
        [McpName("node_id"), Description("Node ID of the live ModelComponent.")] string nodeId,
        [McpName("component_id"), Description("Exact ModelComponent ID.")] string componentId,
        [McpName("submesh_index"), Description("Zero-based source submesh index.")] int submeshIndex,
        [McpName("lod_index"), Description("Zero-based LOD index.")] int lodIndex,
        [McpName("expected_material_id"), Description("Material ID returned by get_model_material_bindings. The call fails if this slot changed.")] string expectedMaterialId,
        [McpName("binding_kind"), Description("Exact slot kind: legacy or surface.")] string bindingKind,
        [McpName("binding_index"), Description("Zero-based index in the selected binding kind.")] int bindingIndex,
        [McpName("texture_id"), Description("ID of a texture used by a live model material, or null to clear a legacy slot.")] string? textureId,
        [McpName("expected_sampler_name"), Description("Required to fill a null legacy slot. It must match the replacement sampler name.")] string? expectedSamplerName = null)
    {
        if (!TryGetUniqueLiveModelNode(context.World, nodeId, out SceneNode? node, out string? nodeError) || node is null)
            return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));
        if (!Guid.TryParse(componentId, out Guid componentGuid) ||
            !Guid.TryParse(expectedMaterialId, out Guid materialGuid))
            return Task.FromResult(new McpToolResponse("component_id and expected_material_id must be GUIDs.", isError: true));
        ModelComponent? component = null;
        foreach (ModelComponent candidate in node.GetComponents<ModelComponent>())
            if (candidate.ID == componentGuid)
            {
                if (component is not null && !ReferenceEquals(component, candidate))
                    return Task.FromResult(new McpToolResponse(
                        "More than one ModelComponent on this node has component_id.", isError: true));
                component = candidate;
            }
        if (component is null || component.Model is not { } model)
            return Task.FromResult(new McpToolResponse("The exact ModelComponent has no live model.", isError: true));
        if ((uint)submeshIndex >= (uint)model.Meshes.Count)
            return Task.FromResult(new McpToolResponse("submesh_index is out of range.", isError: true));
        SubMesh submesh = model.Meshes[submeshIndex];
        if ((uint)lodIndex >= (uint)submesh.LODs.Count)
            return Task.FromResult(new McpToolResponse("lod_index is out of range.", isError: true));
        XRMaterial? material = submesh.LODs.ElementAt(lodIndex).Material;
        if (material is null || material.ID != materialGuid || material.IsDestroyed)
            return Task.FromResult(new McpToolResponse("The live source slot no longer has expected_material_id.", isError: true));
        if (!TryCheckEffectiveModelMaterial(component, submesh, lodIndex, material,
                out string? effectiveError))
            return Task.FromResult(new McpToolResponse(effectiveError!, isError: true));

        bool legacy = string.Equals(bindingKind, "legacy", StringComparison.Ordinal);
        bool surface = string.Equals(bindingKind, "surface", StringComparison.Ordinal);
        if (!legacy && !surface)
            return Task.FromResult(new McpToolResponse("binding_kind must be legacy or surface.", isError: true));
        if (bindingIndex < 0 || legacy && bindingIndex >= material.Textures.Count ||
            surface && bindingIndex >= material.SurfaceTextureBindings.Length)
            return Task.FromResult(new McpToolResponse("binding_index is out of range for the selected kind.", isError: true));

        MaterialSurfaceTextureBinding? surfaceBinding = surface
            ? material.SurfaceTextureBindings[bindingIndex]
            : null;
        if (surface && surfaceBinding is null)
            return Task.FromResult(new McpToolResponse("The surface binding is missing.", isError: true));
        if (surface && textureId is null)
            return Task.FromResult(new McpToolResponse("A surface binding requires a non-null texture_id.", isError: true));
        XRTexture? previous = legacy ? material.Textures[bindingIndex] : surfaceBinding?.Texture;
        XRTexture? replacement = null;
        if (textureId is not null)
        {
            if (!Guid.TryParse(textureId, out Guid textureGuid))
                return Task.FromResult(new McpToolResponse("texture_id must be a GUID.", isError: true));
            if (!TryFindLiveModelTexture(context.World, textureGuid, out replacement, out string? textureError))
                return Task.FromResult(new McpToolResponse(textureError ?? "Live texture not found.", isError: true));
        }
        bool pairedStandardLit = material.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitTextureV1;
        string? expectedLegacySampler = previous?.ResolveSamplerName(bindingIndex) ?? expectedSamplerName;
        if (!pairedStandardLit && legacy && replacement is not null &&
            (string.IsNullOrWhiteSpace(expectedLegacySampler) ||
             !string.Equals(expectedLegacySampler,
                 replacement.ResolveSamplerName(bindingIndex), StringComparison.Ordinal)))
            return Task.FromResult(new McpToolResponse(
                "The replacement sampler name does not match this legacy slot. No texture was renamed.", isError: true));
        if (legacy && replacement is null && !material.Textures.AllowNull)
            return Task.FromResult(new McpToolResponse("This legacy texture list does not allow null.", isError: true));

        Guid? previousId = previous?.ID;
        string? previousSampler = previous?.SamplerName;
        Guid? receiptId = null;
        StandardLitTextureBindingEdit? pairedEdit = null;
        if (!ReferenceEquals(previous, replacement))
        {
            if (pairedStandardLit)
            {
                if (!StandardLitTextureBindingEditor.TryReplace(material, surface, bindingIndex,
                        replacement, out StandardLitTextureBindingEdit edit, out string? pairedReason))
                    return Task.FromResult(new McpToolResponse(pairedReason ??
                        "The standard lit texture edit was rejected.", isError: true));
                pairedEdit = edit;
            }
            else if (legacy)
                material.Textures[bindingIndex] = replacement;
            else
                SetSurfaceTextureSlot(material, bindingIndex, replacement!);
            XRTexture? accepted = legacy
                ? material.Textures[bindingIndex]
                : material.SurfaceTextureBindings[bindingIndex]?.Texture;
            if (!ReferenceEquals(accepted, replacement))
                return Task.FromResult(new McpToolResponse("The material did not accept the texture replacement.",
                    new { materialId = material.ID, bindingKind, bindingIndex, previousTextureId = previousId,
                        currentTextureId = accepted?.ID }, isError: true));
            MaterialSurfaceTextureBinding? replacementSurface = surface
                ? material.SurfaceTextureBindings[bindingIndex]
                : null;
            StandardLitTextureBindingEdit retainedPair = pairedEdit.GetValueOrDefault();
            bool hasPair = pairedEdit.HasValue;
            using var interaction = Undo.BeginUserInteraction();
            using var change = Undo.BeginChange("MCP Set Live Material Texture");
            Undo.RecordStructuralChange("Set Live Material Texture",
                undoAction: () => RestoreMaterialTextureBinding(material, legacy, bindingIndex,
                    previous, surfaceBinding, hasPair ? retainedPair : null, restorePair: true),
                redoAction: () => RestoreMaterialTextureBinding(material, legacy, bindingIndex,
                    replacement, replacementSurface, hasPair ? retainedPair : null, restorePair: false));
            receiptId = s_liveTextureReceipts.GetValue(context.World, static _ => new()).Add(
                new EditorMcpLiveTextureReceipt(node, component, submesh, submesh.LODs.ElementAt(lodIndex),
                    submeshIndex, lodIndex, material, legacy, bindingIndex, previous, replacement,
                    surfaceBinding, replacementSurface, pairedEdit));
        }
        else if (pairedStandardLit)
        {
            if (!StandardLitTextureBindingEditor.TryReplace(material, surface, bindingIndex,
                    replacement, out StandardLitTextureBindingEdit unchangedEdit, out string? pairedReason))
                return Task.FromResult(new McpToolResponse(pairedReason ??
                    "The standard lit texture slot is not valid.", isError: true));
            pairedEdit = unchangedEdit;
        }
        XRTexture? current = legacy
            ? material.Textures[bindingIndex]
            : material.SurfaceTextureBindings[bindingIndex]?.Texture;
        return Task.FromResult(new McpToolResponse("The live material texture binding is current.",
            new
            {
                nodeId = node.ID, componentId = component.ID, submeshIndex, lodIndex,
                materialId = material.ID, bindingKind, bindingIndex,
                surfaceSemantic = (pairedEdit?.PreviousSurface ?? surfaceBinding)?.Semantic.ToString(),
                previousTextureId = previousId, currentTextureId = current?.ID,
                previousSampler, currentSampler = current?.SamplerName,
                changed = !ReferenceEquals(previous, current), receiptId,
                pairedLegacyIndex = pairedEdit?.LegacyIndex,
                pairedSurfaceIndex = pairedEdit?.SurfaceIndex,
            }));
    }

    /// <summary>Restores an exact texture reference retained by a prior live edit.</summary>
    [XRMcp(Name = "restore_model_material_texture_binding", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Restores a prior live model material texture reference.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Restore a live texture edit by its receipt ID. The current slot must still hold the replacement. A receipt is consumed after a successful restore.")]
    public static Task<McpToolResponse> RestoreModelMaterialTextureBindingAsync(
        McpToolContext context,
        [McpName("receipt_id"), Description("Receipt ID returned by set_model_material_texture_binding.")] string receiptId)
    {
        if (!Guid.TryParse(receiptId, out Guid id) ||
            !s_liveTextureReceipts.TryGetValue(context.World, out EditorMcpLiveTextureReceiptStore? store) ||
            !store.TryGet(id, out EditorMcpLiveTextureReceipt? receipt) || receipt is null)
            return Task.FromResult(new McpToolResponse(
                "The texture receipt is invalid, expired, or already consumed.", isError: true));

        if (!TryGetUniqueLiveModelNode(context.World, receipt.Node.ID.ToString(),
                out SceneNode? node, out string? nodeError) ||
            node is null ||
            !ReferenceEquals(node, receipt.Node))
            return Task.FromResult(new McpToolResponse(
                nodeError ?? "The receipt node is no longer the same live instance.", isError: true));
        int componentMatches = 0;
        foreach (ModelComponent candidate in node.GetComponents<ModelComponent>())
            if (candidate.ID == receipt.Component.ID)
            {
                ++componentMatches;
                if (!ReferenceEquals(candidate, receipt.Component))
                    return Task.FromResult(new McpToolResponse(
                        "The receipt component is no longer the same live instance.", isError: true));
            }
        if (componentMatches != 1 || receipt.Component.Model is not { } model ||
            (uint)receipt.SubmeshIndex >= (uint)model.Meshes.Count ||
            !ReferenceEquals(model.Meshes[receipt.SubmeshIndex], receipt.Submesh) ||
            (uint)receipt.LodIndex >= (uint)receipt.Submesh.LODs.Count ||
            !ReferenceEquals(receipt.Submesh.LODs.ElementAt(receipt.LodIndex), receipt.Lod) ||
            !ReferenceEquals(receipt.Lod.Material, receipt.Material) ||
            receipt.Material.IsDestroyed ||
            !TryCheckEffectiveModelMaterial(receipt.Component, receipt.Submesh,
                receipt.LodIndex, receipt.Material, out _))
            return Task.FromResult(new McpToolResponse(
                "The receipt target or effective material changed. The receipt remains available.", isError: true));

        XRMaterial material = receipt.Material;
        if (receipt.Previous is { IsDestroyed: true })
            return Task.FromResult(new McpToolResponse(
                "The previous texture was destroyed. The receipt cannot restore it.", isError: true));
        int index = receipt.BindingIndex;
        bool legacy = receipt.Legacy;
        if (legacy ? (uint)index >= (uint)material.Textures.Count :
            (uint)index >= (uint)material.SurfaceTextureBindings.Length)
            return Task.FromResult(new McpToolResponse("The receipt slot no longer exists.", isError: true));
        XRTexture? current = legacy ? material.Textures[index] :
            material.SurfaceTextureBindings[index]?.Texture;
        if (!ReferenceEquals(current, receipt.Replacement) ||
            (receipt.PairedEdit is { } pairedCurrent
                ? !StandardLitTextureBindingEditor.Matches(material, in pairedCurrent, previous: false)
                : !legacy && material.SurfaceTextureBindings[index] != receipt.ReplacementSurface))
            return Task.FromResult(new McpToolResponse(
                "The receipt slot changed after the edit. The receipt remains available.", isError: true));

        if (receipt.PairedEdit is { } pairedRestore)
        {
            if (!StandardLitTextureBindingEditor.Apply(material, in pairedRestore,
                    restore: true, out string? restoreReason))
                return Task.FromResult(new McpToolResponse(
                    restoreReason ?? "The material did not accept the receipt restore. The receipt remains available.",
                    isError: true));
        }
        else
            RestoreMaterialTextureBinding(material, legacy, index,
                receipt.Previous, receipt.PreviousSurface);
        XRTexture? restored = legacy ? material.Textures[index] :
            material.SurfaceTextureBindings[index]?.Texture;
        if (!ReferenceEquals(restored, receipt.Previous) ||
            (receipt.PairedEdit is { } pairedRestored
                ? !StandardLitTextureBindingEditor.Matches(material, in pairedRestored, previous: true)
                : !legacy && material.SurfaceTextureBindings[index] != receipt.PreviousSurface))
            return Task.FromResult(new McpToolResponse(
                "The material did not accept the receipt restore. The receipt remains available.", isError: true));
        using (var interaction = Undo.BeginUserInteraction())
        using (var change = Undo.BeginChange("MCP Restore Live Material Texture"))
            Undo.RecordStructuralChange("Restore Live Material Texture",
                undoAction: () => RestoreMaterialTextureBinding(material, legacy, index,
                    receipt.Replacement, receipt.ReplacementSurface, receipt.PairedEdit, restorePair: false),
                redoAction: () => RestoreMaterialTextureBinding(material, legacy, index,
                    receipt.Previous, receipt.PreviousSurface, receipt.PairedEdit, restorePair: true));
        store.Remove(id);
        return Task.FromResult(new McpToolResponse("The live texture binding was restored.",
            new
            {
                receiptId = id, consumed = true, materialId = material.ID,
                bindingKind = legacy ? "legacy" : "surface", bindingIndex = index,
                previousTextureId = receipt.Replacement?.ID,
                currentTextureId = receipt.Previous?.ID,
                currentSampler = receipt.Previous?.SamplerName,
                pairedLegacyIndex = receipt.PairedEdit?.LegacyIndex,
                pairedSurfaceIndex = receipt.PairedEdit?.SurfaceIndex,
            }));
    }

    /// <summary>Replaces one immutable surface binding through the material setter.</summary>
    private static void SetSurfaceTextureSlot(XRMaterial material, int index, XRTexture texture)
    {
        MaterialSurfaceTextureBinding[] updated = [.. material.SurfaceTextureBindings];
        updated[index] = updated[index] with { Texture = texture };
        material.SurfaceTextureBindings = updated;
    }

    /// <summary>Restores one binding without changing a shared texture object.</summary>
    private static void RestoreMaterialTextureBinding(
        XRMaterial material, bool legacy, int index, XRTexture? texture,
        MaterialSurfaceTextureBinding? surfaceBinding,
        StandardLitTextureBindingEdit? pairedEdit = null,
        bool restorePair = false)
    {
        if (material.IsDestroyed)
            return;
        if (pairedEdit is { } edit)
        {
            if (!StandardLitTextureBindingEditor.Apply(material, in edit, restorePair, out string? reason))
                Debug.LogWarning(reason ?? "The standard lit texture Undo edit was rejected.");
            return;
        }
        if (legacy)
        {
            if ((uint)index < (uint)material.Textures.Count)
                material.Textures[index] = texture;
        }
        else if (surfaceBinding is not null && (uint)index < (uint)material.SurfaceTextureBindings.Length)
        {
            MaterialSurfaceTextureBinding[] updated = [.. material.SurfaceTextureBindings];
            updated[index] = surfaceBinding;
            material.SurfaceTextureBindings = updated;
        }
    }

    /// <summary>Checks the renderer bound to the selected source LOD.</summary>
    private static bool TryCheckEffectiveModelMaterial(
        ModelComponent component, SubMesh submesh, int lodIndex, XRMaterial material,
        out string? error)
    {
        error = null;
        foreach (RenderableMesh renderable in component.Meshes)
        {
            if (!component.TryGetSourceSubMesh(renderable, out SubMesh? source) ||
                !ReferenceEquals(source, submesh))
                continue;
            XRMaterial? activeOverride = renderable.MaterialOverride;
            if (activeOverride is not null && !ReferenceEquals(activeOverride, material))
            {
                error = "The runtime renderable has a different material override.";
                return false;
            }
            LinkedListNode<RenderableMesh.RenderableLOD>? runtimeLod = renderable.LODs.First;
            for (int index = 0; index < lodIndex && runtimeLod is not null; ++index)
                runtimeLod = runtimeLod.Next;
            if (runtimeLod is null)
            {
                error = "The runtime renderable no longer has the selected LOD.";
                return false;
            }
            XRMeshRenderer renderer = runtimeLod.Value.Renderer;
            int primitiveCount = renderer.Submeshes.Count == 0 ? 1 : renderer.Submeshes.Count;
            bool foundMaterialPrimitive = false;
            for (int primitiveIndex = 0; primitiveIndex < primitiveCount; ++primitiveIndex)
            {
                if (!renderer.TryGetMesh(primitiveIndex, out _, out XRMaterial? primitiveMaterial))
                    continue;
                if (ReferenceEquals(activeOverride ?? primitiveMaterial, material))
                    foundMaterialPrimitive = true;
            }
            if (!foundMaterialPrimitive)
            {
                error = "No primitive in the selected runtime LOD uses the expected material.";
                return false;
            }
        }
        return true;
    }

    /// <summary>Resolves one node instance and rejects duplicate persistent IDs.</summary>
    private static bool TryGetUniqueLiveModelNode(
        RuntimeWorld world, string nodeId, out SceneNode? node, out string? error)
    {
        node = null;
        error = null;
        if (!Guid.TryParse(nodeId, out Guid guid))
        {
            error = "node_id must be a GUID.";
            return false;
        }
        HashSet<SceneNode> seen = new(ReferenceEqualityComparer.Instance);
        IEnumerable<SceneNode> roots = world.TargetWorld is { } targetWorld
            ? targetWorld.Scenes.SelectMany(scene => scene.RootNodes).Concat(world.RootNodes)
            : world.RootNodes;
        foreach (SceneNode root in roots)
        {
            if (root is null)
                continue;
            foreach (SceneNode candidate in EnumerateHierarchy(root))
            {
                if (!seen.Add(candidate) || candidate.ID != guid)
                    continue;
                if (node is not null)
                {
                    node = null;
                    error = "More than one live node instance has node_id.";
                    return false;
                }
                node = candidate;
            }
        }
        if (node is null)
            error = "node_id is not in the active world.";
        return node is not null;
    }

    /// <summary>Finds one texture owned by a material in the active world.</summary>
    private static bool TryFindLiveModelTexture(
        RuntimeWorld world, Guid textureId, out XRTexture? texture, out string? error)
    {
        texture = null;
        error = null;
        XRTexture? found = null;
        string? failure = null;
        HashSet<SceneNode> seenNodes = new(ReferenceEqualityComparer.Instance);
        IEnumerable<SceneNode> roots = world.TargetWorld is { } targetWorld
            ? targetWorld.Scenes.SelectMany(scene => scene.RootNodes).Concat(world.RootNodes)
            : world.RootNodes;
        foreach (SceneNode root in roots)
        {
            if (root is null)
                continue;
            foreach (SceneNode node in EnumerateHierarchy(root))
            {
                if (!seenNodes.Add(node))
                    continue;
                foreach (ModelComponent component in node.GetComponents<ModelComponent>())
                {
                    if (component.Model is not { } model)
                        continue;
                    foreach (SubMesh submesh in model.Meshes)
                    foreach (SubMeshLOD lod in submesh.LODs)
                    {
                        XRMaterial? material = lod.Material;
                        if (material is null)
                            continue;
                        foreach (XRTexture? candidate in material.Textures)
                            if (!AcceptCandidate(candidate))
                            {
                                error = failure;
                                return false;
                            }
                        foreach (MaterialSurfaceTextureBinding binding in material.SurfaceTextureBindings)
                            if (binding is not null && !AcceptCandidate(binding.Texture))
                            {
                                error = failure;
                                return false;
                            }
                    }
                }
            }
        }
        if (found is null)
            error = "The texture ID is not bound to a model material in the active world.";
        texture = found;
        return found is not null;

        bool AcceptCandidate(XRTexture? candidate)
        {
            if (candidate?.ID != textureId)
                return true;
            if (candidate.IsDestroyed)
            {
                failure = "The selected live texture was destroyed.";
                return false;
            }
            if (found is not null && !ReferenceEquals(found, candidate))
            {
                failure = "More than one live texture instance has this ID. Select an unambiguous texture.";
                return false;
            }
            found = candidate;
            return true;
        }
    }
}
