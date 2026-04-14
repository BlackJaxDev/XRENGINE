using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using XREngine;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            private static ConcurrentDictionary<ulong, BinaryProgram>? BinaryCache = null;

            private static string GetShaderCacheDirectoryPath()
                => Path.Combine(Environment.CurrentDirectory, "ShaderCache");

            private static string GetBinaryShaderCacheMetaPath(string binaryFilePath)
                => $"{binaryFilePath}.meta";

            private static UniformMetadataEntry[]? ReadUniformMetadataCache(string metaPath)
            {
                if (!File.Exists(metaPath))
                    return null;

                try
                {
                    var entries = new List<UniformMetadataEntry>();
                    foreach (string line in File.ReadLines(metaPath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        string[] parts = line.Split('\t');
                        if (parts.Length != 3)
                            continue;

                        string name = parts[0];
                        if (string.IsNullOrEmpty(name))
                            continue;

                        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeValue))
                            continue;
                        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int size))
                            continue;

                        entries.Add(new UniformMetadataEntry(name, (GLEnum)typeValue, size));
                    }

                    return entries.Count == 0 ? null : [.. entries];
                }
                catch
                {
                    return null;
                }
            }

            private static void WriteUniformMetadataCache(string metaPath, UniformMetadataEntry[] metadata)
            {
                if (metadata.Length == 0)
                    return;

                var sb = new StringBuilder(metadata.Length * 32);
                foreach (var entry in metadata)
                    sb.Append(entry.Name)
                        .Append('\t')
                        .Append(((int)entry.Type).ToString(CultureInfo.InvariantCulture))
                        .Append('\t')
                        .Append(entry.Size.ToString(CultureInfo.InvariantCulture))
                        .AppendLine();

                File.WriteAllText(metaPath, sb.ToString());
            }

            internal static void ReadBinaryShaderCache(string currentVer)
            {
                if (BinaryCache is not null)
                    return;

                BinaryCache = new();

                string path = GetShaderCacheDirectoryPath();
                if (!Directory.Exists(path))
                    return;

                foreach (string filePath in Directory.EnumerateFiles(path, "*.bin"))
                {
                    string name = Path.GetFileNameWithoutExtension(filePath);

                    // Parse "hash-format-version" without allocating a string[] via Split.
                    int firstDash = name.IndexOf('-');
                    if (firstDash < 0)
                    {
                        Debug.OpenGLWarning($"Invalid binary shader cache file name, deleting: {name}");
                        File.Delete(filePath);
                        continue;
                    }
                    int secondDash = name.IndexOf('-', firstDash + 1);
                    if (secondDash < 0)
                    {
                        Debug.OpenGLWarning($"Invalid binary shader cache file name, deleting: {name}");
                        File.Delete(filePath);
                        continue;
                    }

                    ReadOnlySpan<char> fileVer = name.AsSpan(secondDash + 1);
                    if (!fileVer.Equals(currentVer.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.OpenGLWarning($"Binary shader cache file version mismatch, deleting: {name}");
                        File.Delete(filePath);
                        continue;
                    }

                    try
                    {
                        ReadOnlySpan<char> hashSpan = name.AsSpan(0, firstDash);
                        if (ulong.TryParse(hashSpan, out ulong hash))
                        {
                            byte[] binary = File.ReadAllBytes(filePath);
                            ReadOnlySpan<char> formatSpan = name.AsSpan(firstDash + 1, secondDash - firstDash - 1);
                            GLEnum format = Enum.Parse<GLEnum>(formatSpan);
                            UniformMetadataEntry[]? metadata = ReadUniformMetadataCache(GetBinaryShaderCacheMetaPath(filePath));
                            BinaryProgram binaryProgram = new(binary, format, (uint)binary.Length, metadata);
                            BinaryCache.TryAdd(hash, binaryProgram);
                        }
                    }
                    catch
                    {

                    }
                }
            }
            private void WriteToBinaryShaderCache(BinaryProgram binary)
            {
                string ver;
                unsafe
                {
                    ver = new((sbyte*)Api.GetString(StringName.Version));
                }

                string path = GetShaderCacheDirectoryPath();
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                path = Path.Combine(path, $"{Hash}-{binary.Format}-{ver}.bin");
                File.WriteAllBytes(path, binary.Binary);
                if (binary.Uniforms is { Length: > 0 })
                    WriteUniformMetadataCache(GetBinaryShaderCacheMetaPath(path), binary.Uniforms);
            }
            
            public static void DeleteFromBinaryShaderCache(ulong hash, GLEnum format)
            {
                BinaryCache?.TryRemove(hash, out _);
                //Delete any matching file (hash-format-*.bin pattern)
                string path = GetShaderCacheDirectoryPath();
                if (!Directory.Exists(path))
                    return;
                    
                // Search for files matching the hash-format pattern with any version
                string pattern = $"{hash}-{format}-*.bin";
                foreach (string filePath in Directory.EnumerateFiles(path, pattern))
                {
                    try
                    {
                        File.Delete(filePath);
                        string metaPath = GetBinaryShaderCacheMetaPath(filePath);
                        if (File.Exists(metaPath))
                            File.Delete(metaPath);
                    }
                    catch
                    {
                        // Ignore deletion failures (file may be in use)
                    }
                }
            }

            private void CacheBinary(uint bindingId)
            {
                if (!Engine.Rendering.Settings.AllowBinaryProgramCaching)
                    return;

                Api.GetProgram(bindingId, GLEnum.ProgramBinaryLength, out int len);
                if (len <= 0)
                    return;

                byte[] binary = new byte[len];
                GLEnum format;
                uint binaryLength;
                fixed (byte* ptr = binary)
                {
                    Api.GetProgramBinary(bindingId, (uint)len, &binaryLength, &format, ptr);
                }
                UniformMetadataEntry[] metadata = SnapshotUniformMetadata();
                BinaryProgram bin = new(binary, format, binaryLength, metadata.Length == 0 ? null : metadata);
                var binaryCache = BinaryCache;
                if (binaryCache is null)
                    return;

                binaryCache[Hash] = bin;
                WriteToBinaryShaderCache(bin);
            }

            /// <summary>
            /// Resolves #include directives for hashing so that included-file changes invalidate the cache.
            /// Falls back to raw source text if resolution fails (e.g., missing include file).
            /// </summary>
            private string ResolveSourceForCompilation(XRShader shader)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Link.ResolveSourceForCompilation");
                if (shader is null)
                    return string.Empty;

                try
                {
                    string resolved = shader.GetResolvedSource();
                    return GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(resolved, shader.Type, Data.Separable);
                }
                catch (Exception ex)
                {
                    Debug.OpenGLWarning($"[ShaderCache] Include resolution failed for compilation (filePath={shader.Source?.FilePath ?? "null"}): {ex.Message}. Using raw source.");
                    string rawSource = shader.Source?.Text ?? string.Empty;
                    return GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(rawSource, shader.Type, Data.Separable);
                }
            }

            private string ResolveSourceForHash(GLShader shader)
            {
                using var sample = Engine.Profiler.Start("GLRenderProgram.Link.ResolveSourceForHash");
                if (shader is null)
                    return string.Empty;

                try
                {
                    string resolved = ResolveSourceForCompilation(shader.Data);
                    if (resolved.Contains("#include"))
                        Debug.OpenGLWarning($"[ShaderCache] Include resolution left unresolved #include in source (filePath={shader.Data.Source?.FilePath ?? "null"})");
                    return resolved;
                }
                catch (Exception ex)
                {
                    Debug.OpenGLWarning($"[ShaderCache] Include resolution failed for hash (filePath={shader.Data.Source?.FilePath ?? "null"}): {ex.Message}. Using raw source.");
                    string rawSource = shader.Data.Source?.Text ?? string.Empty;
                    return GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(rawSource, shader.Data.Type, Data.Separable);
                }
            }

            private static ulong CalcHash(IEnumerable<string> enumerable)
            {
                ulong hash = 17ul;
                foreach (string? item in enumerable)
                    hash = hash * 31ul + GetDeterministicHashCode(item ?? string.Empty);
                return hash;
            }

            static ulong GetDeterministicHashCode(string str)
            {
                unchecked
                {
                    ulong hash1 = (5381 << 16) + 5381;
                    ulong hash2 = hash1;

                    for (int i = 0; i < str.Length; i += 2)
                    {
                        hash1 = ((hash1 << 5) + hash1) ^ str[i];
                        if (i == str.Length - 1)
                            break;
                        hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                    }

                    ulong value = hash1 + (hash2 * 1566083941ul);
                    //Debug.Out(value.ToString());
                    return value;
                }
            }
        }
    }

    internal record struct BinaryProgram(byte[] Binary, GLEnum Format, uint Length, OpenGLRenderer.GLRenderProgram.UniformMetadataEntry[]? Uniforms = null)
    {
        public static implicit operator (byte[] bin, GLEnum fmt, uint len)(BinaryProgram value)
            => (value.Binary, value.Format, value.Length);

        public static implicit operator BinaryProgram((byte[] bin, GLEnum fmt, uint len) value)
            => new(value.bin, value.fmt, value.len);
    }
}
