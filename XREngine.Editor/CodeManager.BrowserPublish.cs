using XREngine;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using XREngine.Components.Scripting;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Security.Cryptography;

internal partial class CodeManager
{
    internal string GetBrowserGameAssemblyPath(string configuration)
    {
        string project = GetManagedGameProjectPath();
        return Path.Combine(Path.GetDirectoryName(project)!, "Build", Platform_AnyCPU, configuration,
            GameTargetFramework, GetProjectName() + ".dll");
    }

    /// <summary>Builds and loads the exact game target consumed by the browser project reference.</summary>
    internal void BuildBrowserGameAssemblyForPublishing(string configuration, CancellationToken cancellationToken)
    {
        string gameProject = GetManagedGameProjectPath();
        bool singleNode = UseSingleNodeBrowserPublish();
        if (!BuildProjectFile(gameProject, configuration, Platform_AnyCPU, ["Build"], extraProperties: null,
            out string? log, cancellationToken, singleNode))
            throw new InvalidOperationException($"Browser game assembly build failed. {log}");
        string gameAssembly = GetBrowserGameAssemblyPath(configuration);
        if (!File.Exists(gameAssembly))
            throw new FileNotFoundException("Browser game build did not produce its exact managed target.", gameAssembly);
        GameCSProjLoader.Unload("GAME");
        GameCSProjLoader.LoadFromPath("GAME", gameAssembly);
        Assembly loaded = GameCSProjLoader.GetLoadedAssembly("GAME")
            ?? throw new InvalidOperationException("Browser game build did not load its compiled assembly.");
        ValidateLoadedBrowserGameAssembly(gameAssembly, loaded);
    }

    private static void ValidateLoadedBrowserGameAssembly(string gameAssembly, Assembly loaded)
    {
        using FileStream stream = File.OpenRead(gameAssembly);
        using PEReader image = new(stream);
        MetadataReader reader = image.GetMetadataReader();
        if (loaded.ManifestModule.ModuleVersionId != reader.GetGuid(reader.GetModuleDefinition().Mvid))
            throw new InvalidOperationException("Browser game build loaded an assembly different from its compiled target.");
        BrowserGameAssemblyAudit.Validate(gameAssembly);
    }

    private static bool UseSingleNodeBrowserPublish()
        => string.Equals(Environment.GetEnvironmentVariable("XRE_BROWSER_PUBLISH_SINGLE_MSBUILD_NODE"),
            "1", StringComparison.Ordinal);

    /// <summary>Publishes the browser application with the existing child-MSBuild logging path.</summary>
    internal string PublishBrowserApplication(string configuration, string publishDirectory, bool includePdbFiles,
        string intermediateDirectory,
        CancellationToken cancellationToken = default)
    {
        string browserProject = ResolveBrowserProject();
        Directory.CreateDirectory(publishDirectory);
        string gameProject = GetManagedGameProjectPath();
        string gameAssembly = GetBrowserGameAssemblyPath(configuration);
        if (!File.Exists(gameProject) || !File.Exists(gameAssembly))
            throw new FileNotFoundException("Build the portable game assembly before browser publishing.", gameAssembly);
        Assembly activeGame = GameCSProjLoader.GetLoadedAssembly("GAME")
            ?? throw new InvalidOperationException("The browser game assembly is not loaded for bootstrap inspection.");
        ValidateLoadedBrowserGameAssembly(gameAssembly, activeGame);
        string? bootstrapTypeName = ProjectBuilder.ResolveGameLaunchBootstrapTypeName(activeGame);
        string launcherDirectory = GetBrowserLauncherDirectory(intermediateDirectory);
        Directory.CreateDirectory(launcherDirectory);
        WriteBrowserGameRegistration(gameAssembly, launcherDirectory, bootstrapTypeName);
        string project = WriteBrowserLauncherProject(browserProject, gameProject, gameAssembly, launcherDirectory);
        string outputRecord = Path.Combine(launcherDirectory, "target-directory.txt");
        File.Delete(outputRecord);
        Dictionary<string, string?> properties = new()
        {
            ["PublishDir"] = EnsureTrailingSlash(publishDirectory),
            ["RuntimeIdentifier"] = "browser-wasm",
            ["XREngineJoltBrowser"] = "true",
            ["PublishTrimmed"] = "false",
            ["RunAOTCompilation"] = "false"
        };
        // Restricted local task hosts can request a single MSBuild node without
        // changing the ordinary publisher's build scheduling.
        bool singleNode = UseSingleNodeBrowserPublish();
        if (!BuildProjectFile(project, configuration, Platform_AnyCPU, ["Publish"], properties, out string? log,
            cancellationToken, singleNode))
        {
            Debug.Out(log ?? "Browser publish produced no diagnostics.");
            throw new InvalidOperationException($"Browser application publish failed. {log}");
        }
        if (!string.IsNullOrWhiteSpace(log)) Debug.Out(log);
        string binaryDirectory = ResolveBrowserAssemblyDirectory(configuration, intermediateDirectory);
        string publisherRoot = Path.GetDirectoryName(Path.GetDirectoryName(browserProject)!)!;
        if (File.Exists(Path.Combine(publisherRoot, "browser-host.json")))
        {
            foreach (string library in Directory.EnumerateFiles(Path.Combine(publisherRoot, "lib"), "*.dll"))
            {
                string linked = Path.Combine(binaryDirectory, Path.GetFileName(library));
                if (!File.Exists(linked) || !SHA256.HashData(File.ReadAllBytes(library)).AsSpan()
                    .SequenceEqual(SHA256.HashData(File.ReadAllBytes(linked))))
                    throw new InvalidDataException($"BrowserPublisher.EngineBinaryChanged: '{Path.GetFileName(library)}' differs from the verified engine package.");
            }
        }
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
        string registration = anchor is null ? "null"
            : $"static () => System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(global::{string.Join(".", anchor.Split('.').Select(segment => "@" + segment))}).Module.ModuleHandle)";
        string factory = bootstrapTypeName is null ? "null"
            : $"static () => new global::{string.Join(".", bootstrapTypeName.Replace('+', '.').Split('.').Select(segment => "@" + segment))}()";
        string source = "internal static class Program\n{\n    private static void Main()\n    {\n"
            + $"        XREngine.Browser.BrowserRuntime.Initialize(bootstrapFactory: {factory}, registerGameModule: {registration});\n"
            + "    }\n}\n";
        string path = Path.Combine(directory, "Program.g.cs");
        File.WriteAllText(path, source, new UTF8Encoding(false));
        return path;
    }

    private static string WriteBrowserLauncherProject(string browserProject, string gameProject,
        string gameAssembly, string directory)
    {
        const string launcherName = "XREngine.BrowserSite";
        if (string.Equals(AssemblyName.GetAssemblyName(gameAssembly).Name, launcherName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BrowserPublisher.AssemblyNameCollision: the game cannot use the browser launcher assembly name.");
        string root = Path.GetDirectoryName(Path.GetDirectoryName(browserProject)!)!;
        XElement references = new("ItemGroup");
        if (File.Exists(Path.Combine(root, "browser-host.json")))
        {
            foreach (string library in Directory.EnumerateFiles(Path.Combine(root, "lib"), "*.dll").Order(StringComparer.Ordinal))
                references.Add(new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(library)),
                    new XElement("HintPath", library), new XElement("Private", "true")));
        }
        else
            references.Add(new XElement("ProjectReference", new XAttribute("Include", browserProject)));
        references.Add(new XElement("ProjectReference", new XAttribute("Include", gameProject)));
        XElement project = new("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk.WebAssembly"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("RuntimeIdentifier", "browser-wasm"),
                new XElement("OutputType", "Exe"),
                new XElement("AssemblyName", launcherName),
                new XElement("ImplicitUsings", "enable"),
                new XElement("Nullable", "enable"),
                new XElement("AllowUnsafeBlocks", "true"),
                new XElement("IsPackable", "false"),
                new XElement("EnableDefaultCompileItems", "false"),
                new XElement("XREngineBrowserLauncherSource", "$(MSBuildProjectDirectory)/Program.g.cs"),
                new XElement("XREngineBrowserGameProject", gameProject)),
            new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", "Program.g.cs"))),
            references,
            new XElement("Import", new XAttribute("Project", Path.Combine(root, "XREngine.Browser", "XREngine.Browser.Host.targets"))),
            new XElement("Target", new XAttribute("Name", "RecordBrowserLauncherOutput"), new XAttribute("AfterTargets", "Build"),
                new XElement("WriteLinesToFile", new XAttribute("File", "$(MSBuildProjectDirectory)/target-directory.txt"),
                    new XAttribute("Lines", "$(TargetDir)"), new XAttribute("Overwrite", "true"))));
        string path = Path.Combine(directory, launcherName + ".csproj");
        File.WriteAllText(path, project.ToString(), new UTF8Encoding(false));
        return path;
    }

    internal static string ResolveBrowserProject()
    {
        string packagedRoot = Path.Combine(AppContext.BaseDirectory, "BrowserPublishing");
        if (Directory.Exists(packagedRoot))
        {
            BrowserPublisherPayloadManifest.Validate(packagedRoot);
            string binaryDescriptor = Path.Combine(packagedRoot, "browser-host.json");
            if (File.Exists(binaryDescriptor))
            {
                using JsonDocument descriptor = JsonDocument.Parse(File.ReadAllBytes(binaryDescriptor));
                if (descriptor.RootElement.GetProperty("schema").GetInt32() != 1
                    || descriptor.RootElement.GetProperty("kind").GetString() != "binary-library")
                    throw new InvalidDataException("BrowserPublisher.HostContractInvalid: unsupported binary browser host contract.");
                string host = Path.Combine(packagedRoot, "XREngine.Browser", "XREngine.Browser.Host.targets");
                foreach (string required in new[] { host, Path.Combine(packagedRoot, "lib", "XREngine.Browser.dll"),
                    Path.Combine(packagedRoot, "Build", "Portable", "PortableProjects.tsv") })
                    if (!File.Exists(required))
                        throw new FileNotFoundException("BrowserPublisher.PayloadIncomplete: a binary browser host input is missing.", required);
                return host;
            }
            string packagedProject = Path.Combine(packagedRoot, "XREngine.Browser", "XREngine.Browser.csproj");
            if (!File.Exists(packagedProject) ||
                !File.Exists(Path.Combine(packagedRoot, "Directory.Build.props")) ||
                !File.Exists(Path.Combine(packagedRoot, "Build", "Portable", "PortableProjects.tsv")) ||
                !File.Exists(Path.Combine(packagedRoot, "Tools", "Generate-AotFactoryRegistrations.ps1")) ||
                !File.Exists(Path.Combine(packagedRoot, "Build", "Registration", "RuntimeContracts.props")) ||
                !File.Exists(Path.Combine(packagedRoot, "Build", "Registration", "RuntimeContractSchemas.txt")) ||
                !File.Exists(Path.Combine(packagedRoot, "XREngine.SourceGenerators", "XREngine.SourceGenerators.csproj")) ||
                !File.Exists(Path.Combine(packagedRoot, "XREngine.SourceGenerators", "AnalyzerReleases.Unshipped.md")))
                throw new FileNotFoundException("BrowserPublisher.PayloadIncomplete: the packaged Editor browser publishing payload is incomplete.", packagedProject);
            return packagedProject;
        }

        DirectoryInfo? directory = new(Path.GetFullPath(AppContext.BaseDirectory));
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "XREngine.Browser", "XREngine.Browser.csproj");
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "XREngine.Editor", "XREngine.Editor.csproj")))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("BrowserPublisher.PayloadMissing: publish the Editor with its BrowserPublishing payload before building a browser target.");
    }

    internal static string ResolveBrowserAssemblyDirectory(string configuration, string intermediateDirectory)
    {
        string launcherDirectory = GetBrowserLauncherDirectory(intermediateDirectory);
        string record = Path.Combine(launcherDirectory, "target-directory.txt");
        if (!File.Exists(record))
            throw new FileNotFoundException("BrowserPublisher.OutputRecordMissing: build the generated browser launcher before cooking metadata.", record);
        string output = Path.GetFullPath(File.ReadAllText(record).Trim());
        string prefix = EnsureTrailingSlash(launcherDirectory);
        if (!output.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(Path.Combine(output, "XREngine.BrowserSite.dll")))
            throw new InvalidDataException("BrowserPublisher.OutputRecordInvalid: the launcher output must remain inside the project intermediate directory.");
        return output;
    }

    private static string GetBrowserLauncherDirectory(string intermediateDirectory)
    {
        if (string.IsNullOrWhiteSpace(intermediateDirectory))
            throw new InvalidOperationException("BrowserPublisher.ProjectIntermediateMissing: the active project needs a writable Intermediate directory.");
        return Path.GetFullPath(Path.Combine(intermediateDirectory, "BrowserPublishing", "Launcher"));
    }
}
