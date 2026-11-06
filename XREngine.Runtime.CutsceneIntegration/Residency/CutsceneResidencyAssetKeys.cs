using System.Security.Cryptography;
using System.Text;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Makes the stable asset keys that a residency manifest stores. A key stays the same when the editor imports
/// the same model again, so a baked manifest still matches the scene after a restart.
/// </summary>
/// <remarks>
/// <para>
/// A model import makes new runtime IDs for its textures and sub-meshes at each import, and the model itself has no
/// stored ID. A runtime ID is therefore not a stable reference. The keys use data that the import does not change:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A texture with a source file (<see cref="XRAsset.OriginalPath"/>) uses a hash of the full source path. A model import
/// sets this path for each texture. Two textures of one source file get one key, and the backend sends the requests of
/// that key to one of them.
/// </description></item>
/// <item><description>
/// A texture with no source path that is embedded in a stored asset uses its runtime ID. The stored asset saves that ID.
/// </description></item>
/// <item><description>
/// Any other texture uses a hash of its own file path (<see cref="XRAsset.FilePath"/>).
/// </description></item>
/// <item><description>
/// A sub-mesh uses a hash of its place in the scene: the node path from the root (each node name with its index among
/// the siblings of the same name), the component index on the node, and the sub-mesh index in the model.
/// </description></item>
/// </list>
/// <para>
/// A path key depends on the location of the project folder, so a manifest that is baked in one folder does not match
/// in another. Playback then finds that the keys do not resolve and uses the approximate manifest.
/// The calculation allocates, so call it at a bake or at an open, and not in each frame.
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
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (!string.IsNullOrWhiteSpace(texture.OriginalPath))
            return ForTexturePath(texture.OriginalPath);

        // An embedded texture shares the file path of its stored source asset, so the path does not identify it.
        // The source asset stores the ID of the embedded texture.
        if (!ReferenceEquals(texture.SourceAsset, texture))
            return string.IsNullOrWhiteSpace(texture.FilePath) ? Guid.Empty : texture.ID;

        return string.IsNullOrWhiteSpace(texture.FilePath) ? Guid.Empty : ForTexturePath(texture.FilePath);
    }

    /// <summary>Gets the stable key of a texture source file.</summary>
    /// <param name="path">The path of the file. A relative path is relative to the current directory.</param>
    public static Guid ForTexturePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Hash(TexturePathPrefix + NormalizePath(path));
    }

    /// <summary>Gets the stable key of a sub-mesh from its place in the scene.</summary>
    /// <param name="node">The node that has the model component.</param>
    /// <param name="componentIndex">The index of the model component in the components of the node.</param>
    /// <param name="subMeshIndex">The index of the sub-mesh in the model.</param>
    public static Guid ForSubMesh(SceneNode node, int componentIndex, int subMeshIndex)
    {
        ArgumentNullException.ThrowIfNull(node);

        StringBuilder builder = new(SubMeshPrefix);
        AppendNodePath(builder, node);
        builder.Append("/c").Append(componentIndex).Append("/m").Append(subMeshIndex);
        return Hash(builder.ToString());
    }

    private static void AppendNodePath(StringBuilder builder, SceneNode node)
    {
        if (node.Parent is { } parent)
            AppendNodePath(builder, parent);

        string name = node.Name ?? string.Empty;
        builder.Append('/').Append(name.Length).Append(':').Append(name).Append('#').Append(SameNameIndex(node, name));
    }

    /// <summary>
    /// The index of a node among the siblings with the same name. A new sibling with another name does not change it.
    /// </summary>
    private static int SameNameIndex(SceneNode node, string name)
    {
        if (node.Parent is not { } parent)
            return 0;

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

    private static string NormalizePath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        full = full.Replace('\\', '/');
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
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
