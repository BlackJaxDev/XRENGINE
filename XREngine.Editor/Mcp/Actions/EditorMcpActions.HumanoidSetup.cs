using System.ComponentModel;
using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Editor.Importers.SerializedAssets;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Adds and confirms a humanoid on an imported avatar, so humanoid animation clips can drive it.</summary>
    [XRMcp(Name = "setup_humanoid_avatar", Permission = McpPermissionLevel.Mutate, PermissionReason = "Adds a HumanoidComponent and saves the bind pose of the avatar bones.")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description(
        "Add a HumanoidComponent to an imported avatar, map its bones, and confirm its avatar definition. The Unity import creates the bones but no humanoid, " +
        "and a humanoid animation clip or an animation_clip cutscene track needs one on its target node. The tool puts the humanoid on the model root of the avatar " +
        "(the node with the authored T-pose under node_id) and returns that node as humanoid_node_id. Use that node as the target of animation tracks and as the " +
        "node of get_node_bounds with source 'humanoid'. The bone names come from the Unity humanoid settings of the source FBX file (.fbx.meta) when the file is known. " +
        "By default, the tool reads the FBX file from the import record of the prefab.")]
    public static Task<McpToolResponse> SetupHumanoidAvatarAsync(
        McpToolContext context,
        [McpName("node_id"), Description("ID of the imported avatar root node.")] string nodeId,
        [McpName("source_model_path"), Description("Optional FBX file of the avatar. Default: the first FBX file in the import record of the prefab.")] string? sourceModelPath = null)
    {
        if (!TryGetNodeById(context.World, nodeId, out SceneNode? node, out string? error) || node is null)
            return Task.FromResult(new McpToolResponse(error ?? $"The node '{nodeId}' was not found.", isError: true));

        string? modelPath;
        string? modelSha256 = null;
        if (!string.IsNullOrWhiteSpace(sourceModelPath))
            modelPath = Path.GetFullPath(sourceModelPath);
        else
        {
            (modelPath, modelSha256) = FindImportedSourceModel(node);
            modelPath ??= SerializedHumanoidAvatarSetup.FindSourceModelPath(node);
        }

        if (modelPath is null)
            return Task.FromResult(new McpToolResponse(
                "No source FBX file was found for the avatar. The humanoid needs the source model to identify the skeleton. Pass source_model_path.",
                isError: true));
        if (!File.Exists(modelPath))
            return Task.FromResult(new McpToolResponse($"The source model '{modelPath}' does not exist.", isError: true));

        SceneNode avatarRoot = SerializedHumanoidAvatarSetup.FindHumanoidTargetNode(node);
        HumanoidComponent humanoid;
        int mappedRoleCount;
        try
        {
            humanoid = SerializedHumanoidAvatarSetup.Configure(avatarRoot, modelPath, modelSha256, out mappedRoleCount);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new McpToolResponse(ex.Message, isError: true));
        }

        return Task.FromResult(new McpToolResponse($"Configured the humanoid on '{avatarRoot.Name}'.", new
        {
            humanoid_node_id = avatarRoot.ID.ToString(),
            humanoid_node_name = avatarRoot.Name,
            humanoid_id = humanoid.ID.ToString(),
            hips_node_name = humanoid.Hips.Node?.Name,
            source_model_path = modelPath,
            mapped_role_count = mappedRoleCount,
        }));
    }

    /// <summary>
    /// Finds the prefab asset of the prefab instance that holds <paramref name="node"/>, reads its Unity import manifest
    /// (kept in memory after the import, or in the <c>.unity-import.yaml</c> file next to the asset), and returns the
    /// first FBX file that the import read.
    /// </summary>
    private static (string? FbxPath, string Sha256) FindImportedSourceModel(SceneNode node)
    {
        Guid prefabAssetId = FindPrefabInstanceRoot(node).Prefab?.PrefabAssetId ?? Guid.Empty;
        if (prefabAssetId == Guid.Empty || Engine.Assets?.GetAssetByID(prefabAssetId) is not XRPrefabSource prefab)
            return (null, string.Empty);

        if (!SerializedPrefabImportManifestStore.TryGet(prefab, out SerializedPrefabImportManifest? manifest)
            && !string.IsNullOrWhiteSpace(prefab.FilePath)
            && SerializedPrefabImportManifestStore.TryLoadAfterRoot(prefab, prefab.FilePath))
        {
            SerializedPrefabImportManifestStore.TryGet(prefab, out manifest);
        }

        return SerializedHumanoidAvatarSetup.FindPrimaryModelDependency(manifest);
    }
}
