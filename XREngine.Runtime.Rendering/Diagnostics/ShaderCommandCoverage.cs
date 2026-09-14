using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Bounded, opt-in accounting of engine-issued shader commands. Counters describe
/// OpenGL commands issued by the CPU and Vulkan commands recorded into a command
/// buffer; they never claim Vulkan GPU completion.
/// </summary>
public static class ShaderCommandCoverage
{
    private const int MaximumEntries = 4096;
    private static readonly object s_gate = new();
    private static readonly List<ShaderCommandCoverageEntry> s_entries = new(MaximumEntries);
    private static readonly Dictionary<string, int> s_entriesByIdentity = new(StringComparer.Ordinal);
    private static ShaderCommandCoverageEntry[] s_publishedEntries = [];
    private static int s_enabled;
    private static long s_droppedRegistrations;

    /// <summary>Gets whether command accounting is enabled. It is disabled by default.</summary>
    public static bool Enabled => Volatile.Read(ref s_enabled) != 0;

    /// <summary>Enables or disables future command accounting.</summary>
    public static void Configure(bool enabled) => Volatile.Write(ref s_enabled, enabled ? 1 : 0);

    /// <summary>Clears counters while retaining the bounded metadata registry and its tokens.</summary>
    public static void Reset()
    {
        lock (s_gate)
        {
            for (int index = 0; index < s_entries.Count; index++)
                s_entries[index].Reset();
        }
    }

    /// <summary>Creates cached stage tokens when a backend program successfully links.</summary>
    public static ShaderCommandCoverageProgramToken[] CreateProgramTokens(string backend, IReadOnlyList<XRShader> shaders)
    {
        if (shaders.Count == 0)
            return [];

        ShaderCommandCoverageProgramToken[] tokens = new ShaderCommandCoverageProgramToken[shaders.Count];
        lock (s_gate)
        {
            for (int index = 0; index < shaders.Count; index++)
            {
                XRShader shader = shaders[index];
                string sourceText = shader.Source?.Text ?? string.Empty;
                string sourceContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText)));
                string sourceIdentity = shader.Source?.FilePath ?? shader.FilePath ?? shader.Name ?? "<memory>";
                string identity = string.Concat(
                    backend, "\n", sourceIdentity, "\n", sourceContentHash, "\n", shader.SourceLanguage, "\n", shader.Type, "\n", shader.EntryPoint, "\n", shader.SourceRevision);
                if (s_entriesByIdentity.TryGetValue(identity, out int existingEntryIndex))
                {
                    tokens[index] = new ShaderCommandCoverageProgramToken(existingEntryIndex, shader.Type);
                    continue;
                }
                if (s_entries.Count >= MaximumEntries)
                {
                    Interlocked.Increment(ref s_droppedRegistrations);
                    continue;
                }
                ShaderCommandCoverageEntry entry = new(
                    backend,
                    shader.SourceLanguage.ToString(),
                    shader.Type.ToString(),
                    shader.EntryPoint,
                    sourceIdentity,
                    sourceContentHash,
                    shader.SourceRevision);
                s_entries.Add(entry);
                int entryIndex = s_entries.Count;
                s_entriesByIdentity.Add(identity, entryIndex);
                tokens[index] = new ShaderCommandCoverageProgramToken(entryIndex, shader.Type);
            }
            Volatile.Write(ref s_publishedEntries, s_entries.ToArray());
        }
        return tokens;
    }

    /// <summary>Records one command against the cached program tokens without metadata lookup.</summary>
    public static void Record(ShaderCommandCoverageProgramToken[]? tokens, ShaderCommandKind kind, bool indirect, bool instancingKnown, bool instanced, bool rasterizerDiscard = false)
    {
        if (Volatile.Read(ref s_enabled) == 0 || tokens is null)
            return;

        ShaderCommandCoverageEntry[] entries = Volatile.Read(ref s_publishedEntries);
        for (int index = 0; index < tokens.Length; index++)
        {
            if ((tokens[index].Stage == EShaderType.Compute) != (kind == ShaderCommandKind.Compute))
                continue;
            if (rasterizerDiscard && tokens[index].Stage == EShaderType.Fragment)
                continue;
            int entryIndex = tokens[index].EntryIndex - 1;
            if ((uint)entryIndex >= (uint)entries.Length)
                continue;
            entries[entryIndex].Record(kind, indirect, instancingKnown, instanced);
        }
    }

    /// <summary>Returns a copy of independently atomic counters; values may change during assembly.</summary>
    public static ShaderCommandCoverageSnapshot CaptureSnapshot()
    {
        lock (s_gate)
        {
            ShaderCommandCoverageEntrySnapshot[] entries = new ShaderCommandCoverageEntrySnapshot[s_entries.Count];
            for (int index = 0; index < entries.Length; index++)
                entries[index] = s_entries[index].Snapshot();
            return new ShaderCommandCoverageSnapshot(Enabled, MaximumEntries, Interlocked.Read(ref s_droppedRegistrations), entries);
        }
    }

}
