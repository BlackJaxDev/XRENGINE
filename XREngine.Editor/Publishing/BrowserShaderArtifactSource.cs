using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor.Publishing;

/// <summary>Resolves authored descriptor identities from an explicitly selected offline cooker manifest.</summary>
internal sealed class BrowserShaderArtifactSource : IShaderProgramArtifactResolver
{
    private readonly string _directory;
    private readonly Dictionary<string, string> _descriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShaderProgramArtifact> _loaded = new(StringComparer.Ordinal);
    private long _loadedBytes;

    internal BrowserShaderArtifactSource(string projectDirectory, string manifestPath)
    {
        if (Path.IsPathRooted(manifestPath) || Uri.TryCreate(manifestPath, UriKind.Absolute, out _))
            throw new InvalidDataException("Browser shader manifest must be project-relative.");
        string manifest = Path.GetFullPath(Path.Combine(projectDirectory, manifestPath));
        string relative = Path.GetRelativePath(projectDirectory, manifest);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Browser shader manifest escapes the authored project.");
        _directory = Path.GetDirectoryName(manifest)!;
        using JsonDocument document = JsonDocument.Parse(ReadBounded(manifest, 1024 * 1024));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 3 || root.GetProperty("backend").GetString() != "WebGPU")
            throw new InvalidDataException("Browser shader publish requires the engine schema-three WebGPU cooker manifest.");
        JsonElement artifacts = root.GetProperty("artifacts");
        if (artifacts.GetArrayLength() > 256)
            throw new InvalidDataException("Browser shader catalog exceeds 256 artifacts.");
        foreach (JsonElement entry in artifacts.EnumerateArray())
        {
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(entry.GetProperty("sha256").GetString())
                ?? throw new InvalidDataException("Browser shader artifact has no identity.");
            string descriptor = entry.GetProperty("descriptor").GetString()!;
            if (descriptor != identity + ".shader.json" || !_descriptors.TryAdd(identity, descriptor))
                throw new InvalidDataException("Browser shader manifest has an invalid or duplicate descriptor identity.");
        }
    }

    public bool TryResolve(string identity, ShaderCompileTarget target, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        artifact = null;
        if (target != ShaderCompileTarget.WebGPUWgsl || !_descriptors.TryGetValue(identity, out string? descriptorName))
            return false;
        if (_loaded.TryGetValue(identity, out artifact)) return true;
        byte[] descriptor = ReadBounded(Path.Combine(_directory, descriptorName), 1024 * 1024);
        using JsonDocument document = JsonDocument.Parse(descriptor);
        string sourceName = document.RootElement.GetProperty("source").GetProperty("url").GetString()!;
        string sourceHash = Path.GetFileNameWithoutExtension(sourceName);
        ShaderProgramArtifactCatalog.ValidateIdentity(sourceHash);
        if (sourceName != sourceHash + ".wgsl")
            throw new InvalidDataException("Browser shader source URL must be a local content-addressed WGSL filename.");
        byte[] source = ReadBounded(Path.Combine(_directory, sourceName), 4 * 1024 * 1024);
        _loadedBytes += descriptor.Length + source.Length;
        if (_loadedBytes > 64L * 1024 * 1024)
            throw new InvalidDataException("Browser shader catalog exceeds 64 MiB.");
        artifact = ShaderProgramArtifactReader.Read(descriptor, source);
        if (artifact.Identity != identity)
            throw new InvalidDataException($"Browser shader descriptor hash mismatch for '{identity}'.");
        _loaded.Add(identity, artifact);
        return true;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        for (string? parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            FileInfo info = new(parent);
            if (info.LinkTarget is not null)
                throw new InvalidDataException("Browser shader artifact paths cannot contain symbolic links.");
        }
        using FileStream stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > maximum)
            throw new InvalidDataException($"Browser shader payload '{Path.GetFileName(path)}' exceeds its size bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Browser shader payload changed during reading.");
        return bytes;
    }
}
