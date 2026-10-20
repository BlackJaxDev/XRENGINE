using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace XREngine.Build
{
    /// <summary>Admits only reviewed native files for one project and runtime identifier.</summary>
    public sealed class PortableNativeAssetGuard : Task
    {
        public ITaskItem[] Candidates { get; set; } = Array.Empty<ITaskItem>();
        [Required] public string ProjectName { get; set; } = string.Empty;
        public string RuntimeIdentifier { get; set; } = string.Empty;
        [Required] public string PolicyFile { get; set; } = string.Empty;
        public string JoltBrowserLinkArchive { get; set; } = string.Empty;
        public string JoltBrowserArchiveDirectory { get; set; } = string.Empty;
        public override bool Execute()
        {
            try
            {
                var reviewed = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(PolicyFile))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    string[] fields = line.Split('\t');
                    if (fields.Length != 4 || string.IsNullOrWhiteSpace(fields[3]))
                        throw new InvalidDataException("Portable native policy requires project, runtime, filename and reason.");
                    reviewed.Add(fields[0] + "|" + fields[1] + "|" + fields[2]);
                }
                StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                foreach (ITaskItem candidate in Candidates)
                {
                    string path = Path.GetFullPath(candidate.ItemSpec);
                    string name = Path.GetFileName(path);
                    bool admitted = reviewed.Contains(ProjectName + "|" + RuntimeIdentifier + "|" + name);
                    string expected = name == "joltc.a" ? JoltBrowserLinkArchive
                        : name == "libJolt.a" && !string.IsNullOrWhiteSpace(JoltBrowserArchiveDirectory)
                            ? Path.Combine(JoltBrowserArchiveDirectory, "libJolt.a") : string.Empty;
                    if (!admitted || string.IsNullOrWhiteSpace(expected) || !path.Equals(Path.GetFullPath(expected), comparison))
                        Log.LogError("Portable project {0} rejects unreviewed native asset for {1}: {2}", ProjectName, RuntimeIdentifier, candidate.ItemSpec);
                }
                return !Log.HasLoggedErrors;
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException)
            {
                Log.LogError("Portable native asset admission failed: {0}", error.Message);
                return false;
            }
        }
    }
}
