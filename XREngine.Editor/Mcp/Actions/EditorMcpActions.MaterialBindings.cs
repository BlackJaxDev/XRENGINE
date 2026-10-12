using System.ComponentModel;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Scene;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Reads model slots and texture sampling metadata without decoding pixels.</summary>
    [XRMcp(Name = "get_model_material_bindings", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Read model source slots, assigned materials, texture source paths, and sampling metadata. Does not decode textures or read GPU pixels.")]
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
                            if (texture is null || !seenTextures.Add(texture.ID))
                                continue;
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
                        bool? nativeUberPreflight = null;
                        string? nativeUberReason = null;
                        if (material.EngineSemantic == EngineMaterialSemanticIdentity.NativeUberV1)
                        {
                            nativeUberPreflight = NativeUberMaterialSourceEncoder.TryEncode(material,
                                nativeWords, nativeResources, out _, out string reason);
                            nativeUberReason = nativeUberPreflight == true ? null : reason;
                        }
                        materials.Add(new
                        {
                            id = material.ID, name = material.Name, sourcePath = material.OriginalPath,
                            engineSemantic = material.EngineSemantic.ToString(), nativeUberPreflight, nativeUberReason,
                            samplers,
                        });
                    }
                }
            }
        }
    Complete:
        return Task.FromResult(new McpToolResponse($"Read {slots.Count} model material slots.",
            new { slots, materials, textures, truncated }));
    }
}
