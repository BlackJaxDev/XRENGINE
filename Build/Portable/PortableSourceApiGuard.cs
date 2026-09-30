using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace XREngine.Build
{
    /// <summary>Checks evaluated portable sources against the shared lexical API policy.</summary>
    public sealed class PortableSourceApiGuard : Task
    {
        [Required]
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string RepositoryRoot { get; set; } = string.Empty;

        [Required]
        public string PolicyFile { get; set; } = string.Empty;

        [Required]
        public string ProjectName { get; set; } = string.Empty;

        [Required]
        public string ProjectFile { get; set; } = string.Empty;

        [Required]
        public string ProjectsFile { get; set; } = string.Empty;

        [Required]
        public string PackagesFile { get; set; } = string.Empty;

        public string GeneratedRenderCommandRegistrations { get; set; } = string.Empty;

        public ITaskItem[] ProjectReferences { get; set; } = Array.Empty<ITaskItem>();

        public ITaskItem[] Packages { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            try
            {
                if (Sources.Length == 0)
                    throw new ArgumentException("The portable Compile source list is empty.");

                HashSet<string> portableProjects = ReadProjects();
                if (!portableProjects.Contains(ProjectName))
                    throw new ArgumentException($"{ProjectName} was marked portable but is absent from the reviewed project set.");
                ValidateWholeProjectSourceSet();
                ValidateProjectReferences(portableProjects);
                ValidatePackages();

                // This is a lexical guard, not semantic analysis. In particular, aliases,
                // interpolated expressions and dynamically selected calls need source review.
                var mask = CreateRegex("//[^\\n]*|/\\*[\\s\\S]*?\\*/|\\$*\"\"\"[\\s\\S]*?\"\"\"|\\$?@?\"(?:\"\"|\\\\.|[^\"])*\"|'(?:\\\\.|[^'\\\\])*'");
                var forbidden = new List<KeyValuePair<string, Regex>>();
                var categories = new HashSet<string>(StringComparer.Ordinal);
                var reviewed = new HashSet<string>(StringComparer.Ordinal);
                Regex reflection = null;
                foreach (string line in File.ReadAllLines(PolicyFile))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    string[] fields = line.Split('\t');
                    if (fields[0] == "deny" && fields.Length == 3 && categories.Add(fields[1]))
                        forbidden.Add(new KeyValuePair<string, Regex>(fields[1], CreateRegex(fields[2])));
                    else if (fields[0] == "reflection" && fields.Length == 2 && reflection == null)
                        reflection = CreateRegex(fields[1]);
                    else if (fields[0] == "allow" && fields.Length == 4 &&
                        !string.IsNullOrWhiteSpace(fields[3]) && reviewed.Add(fields[1] + "|" + fields[2]))
                    {
                        // Reasons are retained in the policy for review; they are not suppressions
                        // for other files, symbols, or forbidden native APIs.
                    }
                    else
                        throw new ArgumentException("Invalid or duplicate portable API policy entry.");
                }
                if (forbidden.Count == 0 || reflection == null)
                    throw new ArgumentException("The portable API policy is incomplete.");

                string root = Path.GetFullPath(RepositoryRoot).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var visited = new HashSet<string>(Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (ITaskItem source in Sources)
                {
                    string fullPath = Path.GetFullPath(source.GetMetadata("FullPath"));
                    if (!visited.Add(fullPath))
                        continue;
                    if (!fullPath.StartsWith(root, comparison) || !File.Exists(fullPath))
                    {
                        Log.LogError("Portable Compile item is absent or outside the repository: {0}", source.ItemSpec);
                        continue;
                    }
                    string relative = fullPath.Substring(root.Length).Replace('\\', '/');
                    string code = mask.Replace(File.ReadAllText(fullPath), BlankLiteral);
                    foreach (KeyValuePair<string, Regex> rule in forbidden)
                        foreach (Match match in rule.Value.Matches(code))
                            Report(fullPath, code, match, rule.Key);
                    foreach (Match match in reflection.Matches(code))
                        if (!reviewed.Contains(relative + "|" + match.Value))
                            Report(fullPath, code, match, "unreviewed reflection");
                }
                return !Log.HasLoggedErrors;
            }
            catch (Exception exception) when (exception is IOException ||
                exception is UnauthorizedAccessException || exception is ArgumentException ||
                exception is RegexMatchTimeoutException)
            {
                Log.LogError("Portable source API guard failed: {0}", exception.Message);
                return false;
            }
        }

        private HashSet<string> ReadProjects()
        {
            var projects = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(ProjectsFile))
            {
                string name = line.Trim();
                if (name.Length == 0 || name.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (!Regex.IsMatch(name, "^XREngine[.][A-Za-z0-9.]+$") || !projects.Add(name))
                    throw new ArgumentException($"Invalid or duplicate portable project: {name}.");
            }
            if (projects.Count == 0)
                throw new ArgumentException("The reviewed portable project set is empty.");
            return projects;
        }

        private void ValidateProjectReferences(HashSet<string> portableProjects)
        {
            foreach (ITaskItem reference in ProjectReferences)
            {
                string name = Path.GetFileNameWithoutExtension(reference.ItemSpec);
                if (!portableProjects.Contains(name))
                    Log.LogError("Portable project {0} references nonportable project {1}.", ProjectName, reference.ItemSpec);
                string removed = reference.GetMetadata("GlobalPropertiesToRemove");
                if (removed.IndexOf("XREnginePortableProject", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log.LogError("Portable project {0} removes the portability property from {1}.", ProjectName, reference.ItemSpec);
            }
        }

        private void ValidateWholeProjectSourceSet()
        {
            string project = File.ReadAllText(ProjectFile);
            if (ProjectName == "XREngine.Runtime.Rendering")
            {
                const string generatedItem = "<Compile Include=\"$(GeneratedRenderCommandRegistrations)\" />";
                if (project.Contains(generatedItem, StringComparison.Ordinal))
                {
                    bool generatedSourcePresent = false;
                    if (string.IsNullOrWhiteSpace(GeneratedRenderCommandRegistrations))
                        Log.LogError("Portable project {0} has no evaluated render command registration path.", ProjectName);
                    string expectedPath = string.Empty;
                    if (!string.IsNullOrWhiteSpace(GeneratedRenderCommandRegistrations))
                    {
                        string candidate = Path.IsPathRooted(GeneratedRenderCommandRegistrations)
                            ? GeneratedRenderCommandRegistrations
                            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ProjectFile)) ?? string.Empty,
                                GeneratedRenderCommandRegistrations);
                        expectedPath = Path.GetFullPath(candidate);
                    }
                    foreach (ITaskItem source in Sources)
                    {
                        string path = Path.GetFullPath(source.GetMetadata("FullPath"));
                        if (path.Equals(expectedPath,
                                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                            Path.GetFileName(path).Equals("RenderCommandRegistrations.g.cs", StringComparison.Ordinal))
                        {
                            generatedSourcePresent = true;
                            break;
                        }
                    }
                    if (!generatedSourcePresent)
                        Log.LogError("Portable project {0} omits its generated render command source from Compile.", ProjectName);
                    project = project.Replace(generatedItem, string.Empty, StringComparison.Ordinal);
                }
            }
            if (CreateRegex(@"<Compile\s+[^>]*(?:Include|Remove)\s*=").IsMatch(project) ||
                CreateRegex(@"<(?:DefaultItemExcludes|DefaultItemExcludesInProjectFolder|EnableDefaultCompileItems|OverrideDefaultCompileItems)\b").IsMatch(project))
            {
                Log.LogError("Portable project {0} filters or replaces its default Compile source set.", ProjectName);
            }
        }

        private void ValidatePackages()
        {
            var reviewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(PackagesFile))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string[] fields = line.Split('\t');
                if (fields.Length != 4 || string.IsNullOrWhiteSpace(fields[3]) ||
                    !reviewed.Add(fields[0] + "|" + fields[1] + "|" + fields[2]))
                    throw new ArgumentException("Invalid or duplicate portable package policy entry.");
            }
            foreach (ITaskItem package in Packages)
            {
                // The WebAssembly SDK injects its own build pack. It is not an engine
                // dependency; the resolved native-runtime check handles SDK assets.
                if (ProjectName == "XREngine.Browser" &&
                    package.ItemSpec.Equals("Microsoft.NET.Sdk.WebAssembly.Pack", StringComparison.OrdinalIgnoreCase))
                    continue;

                string version = package.GetMetadata("Version");
                if (string.IsNullOrEmpty(version))
                    version = package.GetMetadata("VersionOverride");
                string identity = ProjectName + "|" + package.ItemSpec + "|" + version;
                if (!reviewed.Contains(identity))
                    Log.LogError("Portable project {0} has unreviewed package {1} version {2}.",
                        ProjectName, package.ItemSpec, string.IsNullOrEmpty(version) ? "<unspecified>" : version);
            }
        }

        private static Regex CreateRegex(string pattern)
            => new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

        private static string BlankLiteral(Match match)
        {
            char[] chars = match.Value.ToCharArray();
            for (int index = 0; index < chars.Length; index++)
                if (chars[index] != '\n' && chars[index] != '\r')
                    chars[index] = ' ';
            return new string(chars);
        }

        private void Report(string file, string code, Match match, string category)
        {
            int line = 1;
            for (int index = 0; index < match.Index; index++)
                if (code[index] == '\n')
                    line++;
            Log.LogError(null, "XREPORTABLE001", null, file, line, 1, 0, 0,
                "Portable {0}: {1}.", category, match.Value);
        }
    }
}
