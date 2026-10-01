using XREngine;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

internal partial class CodeManager
{
    /// <summary>Publishes the browser application with the existing child-MSBuild logging path.</summary>
    internal string PublishBrowserApplication(string configuration, string publishDirectory, bool includePdbFiles,
        CancellationToken cancellationToken = default)
    {
        string project = ResolveBrowserProject();
        Directory.CreateDirectory(publishDirectory);
        string gameProject = GetManagedGameProjectPath();
        string gameAssembly = GetBinaryPath(configuration, Platform_AnyCPU);
        if (!File.Exists(gameProject) || !File.Exists(gameAssembly))
            throw new FileNotFoundException("Build the portable game assembly before browser publishing.", gameAssembly);
        BrowserGameAssemblyAudit.Validate(gameAssembly);
        string? bootstrapTypeName = ProjectBuilder.ResolveGameLaunchBootstrapTypeName(configuration, Platform_AnyCPU);
        string registrationSource = WriteBrowserGameRegistration(gameAssembly, publishDirectory, bootstrapTypeName);
        Dictionary<string, string?> properties = new()
        {
            ["PublishDir"] = EnsureTrailingSlash(publishDirectory),
            ["RuntimeIdentifier"] = "browser-wasm",
            ["XREngineJoltBrowser"] = "true",
            ["XREngineBrowserGameProject"] = gameProject,
            ["XREngineBrowserGameRegistrationSource"] = registrationSource,
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

    private static string WriteBrowserGameRegistration(string assemblyPath, string directory, string? bootstrapTypeName)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader image = new(stream);
        MetadataReader reader = image.GetMetadataReader();
        string? anchor = null;
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public
                || type.GetGenericParameters().Count != 0)
                continue;
            string name = reader.GetString(type.Name);
            if (name.StartsWith('<')) continue;
            string ns = reader.GetString(type.Namespace);
            string candidate = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            if (anchor is null || string.CompareOrdinal(candidate, anchor) < 0) anchor = candidate;
        }
        // Use the desktop publisher's validated concrete type so inherited and public
        // nested implementations have identical registration semantics in both hosts.
        if (bootstrapTypeName is not null)
            anchor = bootstrapTypeName.Replace('+', '.');
        string invocation = anchor is null ? string.Empty
            : $"        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(global::{string.Join(".", anchor.Split('.').Select(segment => "@" + segment))}).Module.ModuleHandle);\n";
        if (bootstrapTypeName is not null)
            invocation += $"        BootstrapFactory = static () => new global::{string.Join(".", bootstrapTypeName.Replace('+', '.').Split('.').Select(segment => "@" + segment))}();\n";
        string source = "namespace XREngine.Browser;\ninternal static partial class BrowserGameComposition\n{\n"
            + "    static partial void RegisterProvidedGame()\n    {\n"
            + invocation
            + "    }\n}\n";
        string path = Path.Combine(directory, "BrowserGameComposition.g.cs");
        File.WriteAllText(path, source, new UTF8Encoding(false));
        return path;
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
