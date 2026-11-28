using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Keeps the finalized editor picking source of a fragment shader, in memory and on disk, so that a shader is
/// not preprocessed by shaderc again.
/// </summary>
/// <remarks>
/// <see cref="VulkanShaderCompiler"/> runs the shaderc preprocessor on each editor picking fragment shader before
/// it looks for the compiled SPIR-V artifact, because the finalized source is part of the artifact key. One
/// preprocess takes about 100 ms, and the first one in a process takes about one second, on the render thread.
/// The first selection after launch creates the picking shaders of the selection tools, so without this cache
/// that selection held the frame for one to two seconds. The key holds the input source, the shader stage, the
/// native shaderc identity, and the identity of the assemblies that rewrite and finalize the source, so a
/// change to any of them makes a new entry.
/// </remarks>
internal static class VulkanEditorPickingSourceCache
{
    private const string DirectoryName = "EditorPicking";
    private const string FileExtension = ".glsl";
    private const int MaxMemoryEntries = 256;

    private static readonly ConcurrentDictionary<string, string> Memory = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> PendingWrites = new(StringComparer.Ordinal);
    private static readonly string CodeIdentity =
        $"{typeof(VulkanShaderCompiler).Assembly.ManifestModule.ModuleVersionId:N}:" +
        $"{typeof(EditorHighlightFragmentSourceAdapter).Assembly.ManifestModule.ModuleVersionId:N}";

    /// <summary>Makes the cache key for one input source of one shader stage.</summary>
    internal static string ComputeKey(EShaderType type, string source)
    {
        string identity = $"{type}\n{VulkanShaderArtifactCache.ShadercNativeIdentity}\n{CodeIdentity}\n{source}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    /// <summary>Gets the finalized source for a key from memory, or from disk when this process has not read it.</summary>
    internal static bool TryGet(string key, out string finalized)
    {
        if (Memory.TryGetValue(key, out finalized!))
            return true;

        string path = GetPath(key);
        try
        {
            if (!File.Exists(path))
                return false;
            finalized = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[VulkanShaderCache] Could not read editor picking source '{Path.GetFileName(path)}': {ex.Message}.");
            finalized = string.Empty;
            return false;
        }

        if (string.IsNullOrEmpty(finalized))
            return false;
        Remember(key, finalized);
        return true;
    }

    /// <summary>Keeps a finalized source in memory and queues a disk write on the thread pool.</summary>
    internal static void Store(string key, string finalized)
    {
        Remember(key, finalized);
        if (!PendingWrites.TryAdd(key, 0))
            return;

        ThreadPool.QueueUserWorkItem(static state =>
        {
            (string entryKey, string text) = ((string, string))state!;
            try
            {
                string path = GetPath(entryKey);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Write to a temporary file first, so that a reader never sees part of an entry.
                string temporary = $"{path}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temporary, text, Encoding.UTF8);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[VulkanShaderCache] Could not write editor picking source '{entryKey}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                PendingWrites.TryRemove(entryKey, out _);
            }
        }, (key, finalized));
    }

    private static void Remember(string key, string finalized)
    {
        if (Memory.Count < MaxMemoryEntries)
            Memory.TryAdd(key, finalized);
    }

    private static string GetPath(string key)
        => Path.Combine(VulkanShaderArtifactCache.GetShaderCacheDirectoryPath(), DirectoryName, key + FileExtension);
}
