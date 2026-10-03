using XREngine;

internal partial class CodeManager
{
    /// <summary>Publishes the browser application with the existing child-MSBuild logging path.</summary>
    internal string PublishBrowserApplication(string configuration, string publishDirectory, bool includePdbFiles,
        CancellationToken cancellationToken = default)
    {
        string project = ResolveBrowserProject();
        Directory.CreateDirectory(publishDirectory);
        Dictionary<string, string?> properties = new()
        {
            ["PublishDir"] = EnsureTrailingSlash(publishDirectory),
            ["RuntimeIdentifier"] = "browser-wasm",
            ["XREnginePortableRuntime"] = "true",
            ["PublishTrimmed"] = "false",
            ["RunAOTCompilation"] = "false"
        };
        if (!BuildProjectFile(project, configuration, Platform_AnyCPU, ["Publish"], properties, out string? log,
            cancellationToken))
        {
            Debug.Out(log ?? "Browser publish produced no diagnostics.");
            throw new InvalidOperationException($"Browser application publish failed. {log}");
        }
        if (!string.IsNullOrWhiteSpace(log)) Debug.Out(log);
        string site = Path.Combine(publishDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(site, "index.html")))
            throw new FileNotFoundException("Browser publish did not produce wwwroot/index.html.", site);
        if (!includePdbFiles)
            foreach (string symbols in Directory.EnumerateFiles(site, "*.pdb", SearchOption.AllDirectories))
                File.Delete(symbols);
        return site;
    }

    private static string ResolveBrowserProject()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(start));
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "XREngine.Browser", "XREngine.Browser.csproj");
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }
        throw new FileNotFoundException("The XREngine.Browser source project is required to publish a browser target.");
    }
}
