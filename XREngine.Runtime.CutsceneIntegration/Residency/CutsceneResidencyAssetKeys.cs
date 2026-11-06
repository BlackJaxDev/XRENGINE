using System.Security.Cryptography;
using System.Text;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Makes the stable asset keys that a residency manifest stores. A key stays the same when the editor imports
/// the same model again, on another machine, in another project folder, and in a cooked build.
/// </summary>
/// <remarks>
/// <para>
/// A model import makes new runtime IDs for its textures and sub-meshes at each import, and the model itself has no
/// stored ID. A runtime ID is therefore not a stable reference. The keys use data that the import does not change:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A texture that is its own stored asset (its file has the asset extension) uses its persisted ID.
/// </description></item>
/// <item><description>
/// A texture with a source file (<see cref="XRAsset.OriginalPath"/>) uses a hash of the portable source reference
/// (<c>game://</c> or <c>engine://</c>, from <see cref="IAssetSerializationServices.TryCreatePortableAssetReference"/>)
/// and the sampler name. A model import keeps one texture for each source file and sampler name. A path that is already
/// portable, for example in a cooked build, is used as it is.
/// </description></item>
/// <item><description>
/// A texture with no source path that is embedded in a stored asset uses its runtime ID. The stored asset saves that ID.
/// </description></item>
/// <item><description>
/// Any other texture uses a hash of its own file reference (<see cref="XRAsset.FilePath"/>) and the sampler name.
/// </description></item>
/// <item><description>
/// A sub-mesh uses a hash of its place in the scene: the persisted ID of the scene that owns the root node, the node path
/// from the root (each node name with its index among the siblings or scene roots of the same name), the component index
/// on the node, and the sub-mesh index in the model.
/// </description></item>
/// </list>
/// <para>
/// A reference uses one fixed case and <c>/</c> separators on every OS. A file outside the game and engine asset folders
/// has no portable reference, so its key uses the full path and is machine dependent. The calculation allocates, so call it
/// at a bake or at an open, and not in each frame.
/// </para>
/// </remarks>
public static class CutsceneResidencyAssetKeys
{
    private const string TexturePathPrefix = "xrengine.cutscene.residency/texture-path:";
    private const string SubMeshPrefix = "xrengine.cutscene.residency/sub-mesh:";

    /// <summary>Gets the stable key of a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>The key, or <see cref="Guid.Empty"/> if the texture has no file and no stored ID.</returns>
    public static Guid ForTexture(XRTexture2D texture)
        => ForTexture(texture, out _);

    /// <summary>Gets the stable key of a texture, and tells if the key depends on the machine.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="isMachineDependent">True if the key uses a full path, because the file is outside the asset folders.</param>
    /// <returns>The key, or <see cref="Guid.Empty"/> if the texture has no file and no stored ID.</returns>
    public static Guid ForTexture(XRTexture2D texture, out bool isMachineDependent)
    {
        ArgumentNullException.ThrowIfNull(texture);
        isMachineDependent = false;

        bool standalone = ReferenceEquals(texture.SourceAsset, texture);
        if (standalone && IsStoredAssetFile(texture.FilePath))
            return texture.ID;

        if (!string.IsNullOrWhiteSpace(texture.OriginalPath))
            return ForTexturePath(texture.OriginalPath, texture.SamplerName, out isMachineDependent);

        // An embedded texture shares the file path of its stored source asset, so the path does not identify it.
        // The source asset stores the ID of the embedded texture.
        if (!standalone)
            return string.IsNullOrWhiteSpace(texture.FilePath) ? Guid.Empty : texture.ID;

        return string.IsNullOrWhiteSpace(texture.FilePath)
            ? Guid.Empty
            : ForTexturePath(texture.FilePath, texture.SamplerName, out isMachineDependent);
    }

    /// <summary>Gets the stable key of a texture source file and sampler name.</summary>
    /// <param name="path">
    /// The path of the file: a portable reference, or a file path. A relative file path is relative to the current directory.
    /// </param>
    /// <param name="samplerName">The sampler name of the texture, or null.</param>
    public static Guid ForTexturePath(string path, string? samplerName)
        => ForTexturePath(path, samplerName, out _);

    /// <summary>Gets the stable key of a texture source file and sampler name, and tells if the key depends on the machine.</summary>
    /// <param name="path">The path of the file: a portable reference, or a file path.</param>
    /// <param name="samplerName">The sampler name of the texture, or null.</param>
    /// <param name="isMachineDependent">True if the key uses a full path, because the file is outside the asset folders.</param>
    public static Guid ForTexturePath(string path, string? samplerName, out bool isMachineDependent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string reference = ToPortableReference(path, out isMachineDependent);
        return Hash(string.Concat(TexturePathPrefix, reference, "|", samplerName ?? string.Empty));
    }

    /// <summary>Gets the stable key of a sub-mesh from its place in the scene.</summary>
    /// <param name="node">The node that has the model component.</param>
    /// <param name="componentIndex">The index of the model component in the components of the node.</param>
    /// <param name="subMeshIndex">The index of the sub-mesh in the model.</param>
    public static Guid ForSubMesh(SceneNode node, int componentIndex, int subMeshIndex)
    {
        ArgumentNullException.ThrowIfNull(node);
        return ForSubMesh(node, componentIndex, subMeshIndex, FindOwningScene(GetRoot(node)));
    }

    /// <summary>Gets the stable key of a sub-mesh with a known scene that owns the root node of the path.</summary>
    internal static Guid ForSubMesh(SceneNode node, int componentIndex, int subMeshIndex, XRScene? scene)
    {
        ArgumentNullException.ThrowIfNull(node);

        StringBuilder builder = new(SubMeshPrefix);
        if (scene is not null)
            builder.Append("scene:").Append(scene.ID.ToString("N"));
        AppendNodePath(builder, node, scene);
        builder.Append("/c").Append(componentIndex).Append("/m").Append(subMeshIndex);
        return Hash(builder.ToString());
    }

    /// <summary>
    /// Converts a path to the form that a key hashes: a portable reference if the path is in an asset folder, or else the
    /// full path. The result has <c>/</c> separators and upper case on every OS.
    /// </summary>
    internal static string ToPortableReference(string path, out bool isMachineDependent)
    {
        string reference;
        isMachineDependent = false;
        if (AssetReferencePath.IsPortable(path))
            reference = path;
        else if (AssetSerializationServices.Current.TryCreatePortableAssetReference(path, out string? portable))
            reference = portable;
        else
        {
            isMachineDependent = true;
            try
            {
                reference = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                reference = path;
            }
        }

        return reference.Replace('\\', '/').ToUpperInvariant();
    }

    private static bool IsStoredAssetFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        string extension = AssetSerializationServices.Current.AssetExtension;
        return filePath.Length > extension.Length + 1
            && filePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && filePath[filePath.Length - extension.Length - 1] == '.';
    }

    private static SceneNode GetRoot(SceneNode node)
    {
        SceneNode current = node;
        while (current.Parent is { } parent)
            current = parent;
        return current;
    }

    /// <summary>Finds the scene of the world of a root node whose root list holds the node.</summary>
    private static XRScene? FindOwningScene(SceneNode root)
    {
        if (root.World is not RuntimeWorld { TargetWorld: { } world })
            return null;

        var scenes = world.Scenes;
        for (int s = 0; s < scenes.Count; s++)
        {
            if (scenes[s] is not { } scene)
                continue;

            var roots = scene.RootNodes;
            for (int r = 0; r < roots.Count; r++)
                if (ReferenceEquals(roots[r], root))
                    return scene;
        }
        return null;
    }

    private static void AppendNodePath(StringBuilder builder, SceneNode node, XRScene? scene)
    {
        if (node.Parent is { } parent)
            AppendNodePath(builder, parent, scene);

        string name = node.Name ?? string.Empty;
        builder.Append('/').Append(name.Length).Append(':').Append(name).Append('#').Append(SameNameIndex(node, name, scene));
    }

    /// <summary>
    /// The index of a node among the siblings with the same name. For a root node, the siblings are the roots of its scene.
    /// A new sibling with another name does not change it.
    /// </summary>
    private static int SameNameIndex(SceneNode node, string name, XRScene? scene)
    {
        if (node.Parent is { } parent)
        {
            int index = 0;
            var children = parent.Transform.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i]?.SceneNode is not { } sibling)
                    continue;
                if (ReferenceEquals(sibling, node))
                    return index;
                if (string.Equals(sibling.Name ?? string.Empty, name, StringComparison.Ordinal))
                    index++;
            }
            return index;
        }

        if (scene is null)
            return 0;

        int rootIndex = 0;
        var roots = scene.RootNodes;
        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i] is not { } root)
                continue;
            if (ReferenceEquals(root, node))
                return rootIndex;
            if (string.Equals(root.Name ?? string.Empty, name, StringComparison.Ordinal))
                rootIndex++;
        }
        return rootIndex;
    }

    private static Guid Hash(string text)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), digest);

        // Mark the value as a name-based GUID (version 8, RFC 9562 variant), so that it cannot equal a random runtime ID.
        digest[6] = (byte)((digest[6] & 0x0F) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest[..16], bigEndian: true);
    }
}
