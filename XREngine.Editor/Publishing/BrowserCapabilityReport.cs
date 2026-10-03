using System.Text.Json;
using XREngine.Diagnostics;

namespace XREngine.Editor.Publishing;

/// <summary>Records bounded, authored-world browser capability findings before output activation.</summary>
internal sealed class BrowserCapabilityReport(string worldAssetPath, string intermediateDirectory,
    CancellationToken cancellationToken) : IDisposable
{
    private const int MaximumDiagnostics = 4096;
    private readonly List<BrowserCapabilityDiagnostic> _diagnostics = [];
    private readonly HashSet<BrowserCapabilityDiagnostic> _unique = [];
    private bool _savedComplete;

    internal bool HasRequiredFailures => _diagnostics.Any(static diagnostic => diagnostic.Severity == "required");
    internal IReadOnlyList<BrowserCapabilityDiagnostic> Diagnostics => _diagnostics;

    internal void Inspect(Action action, string scenePath, string nodePath, string? component = null,
        string? material = null, string? pass = null)
    {
        try { action(); }
        catch (NotSupportedException error) when (TryGetCapabilityCode(error, out _))
        {
            TryGetCapabilityCode(error, out string code);
            Add(new BrowserCapabilityDiagnostic("required", code, scenePath, nodePath, component, material,
                pass, error.Message[(code.Length + 1)..].Trim()));
        }
    }

    internal void AddOptional(string code, string scenePath, string nodePath, string? component,
        string? material, string? pass, string reason)
        => Add(new BrowserCapabilityDiagnostic("optional", code, scenePath, nodePath, component, material,
            pass, reason));

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

    private static bool TryGetCapabilityCode(NotSupportedException error, out string code)
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
    string NodePath, string? Component, string? Material, string? Pass, string Reason);
