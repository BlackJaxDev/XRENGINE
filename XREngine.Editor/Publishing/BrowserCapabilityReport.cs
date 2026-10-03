using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Diagnostics;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor.Publishing;

/// <summary>Records bounded, authored-world browser capability findings before output activation.</summary>
internal sealed class BrowserCapabilityReport(string worldAssetPath, string intermediateDirectory,
    CancellationToken cancellationToken, string? gameAssetRoot = null, string? engineAssetRoot = null) : IDisposable
{
    private const int MaximumDiagnostics = 4096;
    private readonly List<BrowserCapabilityDiagnostic> _diagnostics = [];
    private readonly HashSet<BrowserCapabilityDiagnostic> _unique = [];
    private bool _savedComplete;

    internal bool HasRequiredFailures => _diagnostics.Any(static diagnostic => diagnostic.Severity == "required");
    internal IReadOnlyList<BrowserCapabilityDiagnostic> Diagnostics => _diagnostics;
    internal string WorldAssetPath => worldAssetPath;

    internal void Inspect(Action action, string scenePath, string nodePath, string? component = null,
        string? material = null, string? pass = null, string? sourcePath = null)
    {
        try { action(); }
        catch (NotSupportedException error) when (TryGetCapabilityCode(error, out _))
        {
            Record(error, scenePath, nodePath, component, material, pass, sourcePath);
        }
        catch (ShaderCompilationException error)
        {
            Record(error, scenePath, nodePath, component, material, pass, sourcePath);
        }
    }

    /// <summary>Retains a diagnostic from a failing cook step without suppressing its exception.</summary>
    internal void Record(Exception error, string scenePath, string nodePath, string? component = null,
        string? material = null, string? pass = null, string? sourcePath = null)
    {
        material = error.Data["BrowserCook.Material"] as string ?? material;
        pass = error.Data["BrowserCook.Pass"] as string ?? pass;
        string? reportedSourcePath = error.Data["BrowserCook.SourcePath"] as string;
        string? normalizedSourcePath = NormalizeSourcePath(reportedSourcePath) ?? NormalizeSourcePath(sourcePath);
        if (error is ShaderCompilationException compilation)
        {
            foreach (ShaderCompileDiagnostic diagnostic in compilation.Diagnostics)
            {
                string? diagnosticPath = NormalizeSourcePath(diagnostic.OriginalPath ?? reportedSourcePath);
                Add(new BrowserCapabilityDiagnostic("required", "BrowserCook.ShaderCompilationFailed", scenePath,
                    nodePath, component, material, pass, SanitizeReason(diagnostic.Message, diagnostic.OriginalPath),
                    diagnosticPath ?? (diagnostic.OriginalPath is null ? normalizedSourcePath : null),
                    diagnosticPath is null ? null : diagnostic.Line,
                    diagnosticPath is null ? null : diagnostic.Column));
            }
            if (compilation.Diagnostics.Count == 0)
                Add(new BrowserCapabilityDiagnostic("required", "BrowserCook.ShaderCompilationFailed", scenePath,
                    nodePath, component, material, pass, SanitizeReason(error.Message, reportedSourcePath ?? sourcePath),
                    normalizedSourcePath));
            return;
        }
        bool hasCode = TryGetCapabilityCode(error, out string code);
        if (!hasCode)
        {
            if (error is not InvalidDataException || material is null)
                return;
            code = "BrowserCook.MaterialArtifactInvalid";
        }
        string reason = hasCode ? error.Message[(code.Length + 1)..].Trim() : error.Message;
        Add(new BrowserCapabilityDiagnostic("required", code, scenePath, nodePath, component, material,
            pass, SanitizeReason(reason, reportedSourcePath ?? sourcePath),
            normalizedSourcePath));
    }

    internal void AddOptional(string code, string scenePath, string nodePath, string? component,
        string? material, string? pass, string reason, string? sourcePath = null)
        => Add(new BrowserCapabilityDiagnostic("optional", code, scenePath, nodePath, component, material,
            pass, SanitizeReason(reason, sourcePath), NormalizeSourcePath(sourcePath)));

    internal string Save(bool complete = true)
    {
        string directory = Path.Combine(intermediateDirectory, "Build");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "BrowserCapabilityReport.json");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                worldAsset = worldAssetPath,
                status = !complete ? "incomplete" : HasRequiredFailures ? "blocked" : "no-known-required-findings",
                scope = "Bounded authored-world preflight. Runtime-created game behavior, undiscovered native services, later quality selections, and device behavior are not classified.",
                diagnostics = _diagnostics
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
            _savedComplete = complete;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return destination;
    }

    public void Dispose()
    {
        if (_savedComplete || _diagnostics.Count == 0 || cancellationToken.IsCancellationRequested)
            return;
        try
        {
            string partialPath = Save(complete: false);
            Debug.LogWarning($"Browser capability preflight was interrupted; partial findings remain at '{partialPath}'.");
        }
        catch (Exception error)
        {
            // Preserve the original cook or corrupt-data error.
            Debug.LogWarning($"Browser capability findings could not be saved: {error.Message}");
        }
    }

    internal void ThrowIfBlocked(string reportPath)
    {
        if (!HasRequiredFailures)
            return;
        string examples = string.Join("; ", _diagnostics.Where(static diagnostic => diagnostic.Severity == "required")
            .Take(8).Select(static diagnostic => $"{diagnostic.Code} at {diagnostic.ScenePath}/{diagnostic.NodePath}: {diagnostic.Reason.TrimEnd('.')}"));
        throw new NotSupportedException($"BrowserCook.CapabilityReportBlocked: {_diagnostics.Count(static diagnostic => diagnostic.Severity == "required")} required browser capability failure(s). {examples}. Full report: '{reportPath}'.");
    }

    private void Add(BrowserCapabilityDiagnostic diagnostic)
    {
        if (!_unique.Add(diagnostic))
            return;
        if (_diagnostics.Count >= MaximumDiagnostics)
            throw new InvalidDataException("BrowserCook.CapabilityReportBudgetExceeded: the world exceeds 4096 distinct capability findings.");
        _diagnostics.Add(diagnostic);
    }

    private string? NormalizeSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return null;
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/game/", StringComparison.Ordinal) ||
            normalized.StartsWith("/engine/", StringComparison.Ordinal))
            return SafeRelative(normalized[1..]) ? normalized : null;
        if (!Path.IsPathRooted(path))
            return SafeRelative(normalized) ? normalized : null;
        string? relative = RelativeUnderRoot(path, gameAssetRoot);
        if (relative is not null)
            return "/game/" + relative;
        relative = RelativeUnderRoot(path, engineAssetRoot);
        return relative is null ? null : "/engine/" + relative;
    }

    private static string? RelativeUnderRoot(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        try
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)).Replace('\\', '/');
            return SafeRelative(relative) ? relative : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SafeRelative(string path)
        => path.Length > 0 && !path.Contains(':') && !path.StartsWith('/') &&
            path.Split('/').All(static part => part.Length > 0 && part is not ("." or ".."));

    private string SanitizeReason(string reason, string? rawSourcePath)
    {
        if (rawSourcePath is not null && (Path.IsPathRooted(rawSourcePath) ||
            rawSourcePath.Length >= 3 && char.IsLetter(rawSourcePath[0]) && rawSourcePath[1] == ':' &&
            rawSourcePath[2] is '/' or '\\') &&
            !rawSourcePath.StartsWith("/game/", StringComparison.Ordinal) &&
            !rawSourcePath.StartsWith("/engine/", StringComparison.Ordinal))
            reason = reason.Replace(rawSourcePath, NormalizeSourcePath(rawSourcePath) ?? "<source>", StringComparison.Ordinal);
        foreach ((string? root, string logical) in new[] { (gameAssetRoot, "/game"), (engineAssetRoot, "/engine") })
            if (!string.IsNullOrWhiteSpace(root))
            {
                string full = Path.GetFullPath(root);
                reason = reason.Replace(full.Replace('\\', '/'), logical, StringComparison.Ordinal)
                    .Replace(full.Replace('/', '\\'), logical, StringComparison.Ordinal);
            }
        return reason;
    }

    private static bool TryGetCapabilityCode(Exception error, out string code)
    {
        const string prefix = "BrowserCook.";
        string message = error.Message;
        int colon = message.IndexOf(':');
        if (!message.StartsWith(prefix, StringComparison.Ordinal) || colon <= prefix.Length || colon > 100)
        {
            code = string.Empty;
            return false;
        }
        code = message[..colon];
        return true;
    }
}

internal sealed record BrowserCapabilityDiagnostic(string Severity, string Code, string ScenePath,
    string NodePath, string? Component, string? Material, string? Pass, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourcePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SourceLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SourceColumn = null);
