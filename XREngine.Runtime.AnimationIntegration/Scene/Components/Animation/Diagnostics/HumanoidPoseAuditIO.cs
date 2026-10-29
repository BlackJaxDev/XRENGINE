using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data;

namespace XREngine.Components.Animation
{
    public static class HumanoidPoseAuditIO
    {
        public static HumanoidPoseAuditReport LoadReport(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = Path.GetFullPath(path);
            using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
            read.EnsureHostFileAccess("Humanoid pose audit read");
            IRuntimeFileDiscovery? discovery = RuntimeFileDiscoveryServices.Capture(out long generation);
            return LoadReport(fullPath, read, discovery, generation);
        }

        /// <summary>Loads an existing host report or returns false when it is missing.</summary>
        public static bool TryLoadReport(string path, [NotNullWhen(true)] out HumanoidPoseAuditReport? report)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string fullPath = Path.GetFullPath(path);
            using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
            read.EnsureHostFileAccess("Humanoid pose audit read");
            IRuntimeFileDiscovery? discovery = RuntimeFileDiscoveryServices.Capture(out long generation);
            if (!RuntimeFileDiscoveryServices.HostFileExists(read, fullPath, "Humanoid pose audit read", discovery, generation))
            {
                report = null;
                return false;
            }
            report = LoadReport(fullPath, read, discovery, generation);
            return true;
        }

        private static HumanoidPoseAuditReport LoadReport(string fullPath, RuntimeAssetReadLease read,
            IRuntimeFileDiscovery? discovery, long generation)
        {
            read.EnsureHostFileAccess("Humanoid pose audit read");
            if (!RuntimeFileDiscoveryServices.IsCurrent(discovery, generation))
                throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the audit file provider changed during the read.");
            IRuntimeHostFileReadBackend files = discovery as IRuntimeHostFileReadBackend
                ?? throw new NotSupportedException("AssetSource.HostFileReadUnavailable: the installed file system does not provide direct host-file reads.");
            using Stream input = files.OpenRead(fullPath, FileShare.Read);
            using Stream content = IsGZipPath(fullPath)
                ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: false)
                : input;
            using StreamReader reader = new(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            using IDisposable publication = read.BeginPublication();
            if (!RuntimeFileDiscoveryServices.IsCurrent(discovery, generation))
                throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the audit file provider changed during the read.");
            HumanoidPoseAuditReport report = JsonConvert.DeserializeObject<HumanoidPoseAuditReport>(json)
                ?? throw new InvalidOperationException($"Failed to deserialize humanoid pose audit report '{fullPath}'.");
            if (!RuntimeFileDiscoveryServices.IsCurrent(discovery, generation))
                throw new OperationCanceledException("AssetSource.StaleFileDiscovery: the audit file provider changed during the read.");
            return report;
        }

        public static void SaveReport(string path, HumanoidPoseAuditReport report)
            => SaveJson(path, report);

        public static void SaveComparison(string path, HumanoidPoseAuditComparisonReport report)
            => SaveJson(path, report);

        private static void SaveJson<T>(string path, T value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(value);

            string fullPath = Path.GetFullPath(path);
            RuntimeAssetReadServices.EnsureHostFileAccess("Humanoid pose audit output");
            IHostAssetFileSharedOutput output = HostAssetFileOutputServices.Required as IHostAssetFileSharedOutput
                ?? throw new NotSupportedException("HostAssetFileOutput.SharedCreateUnavailable: the installed output service does not support shared file creation.");
            string json = JsonConvert.SerializeObject(value, Formatting.Indented);
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            using Stream fs = output.CreateFileWithParentDirectory(fullPath, FileShare.Read);
            if (!IsGZipPath(fullPath))
            {
                fs.Write(bytes);
                return;
            }

            using var compressed = new GZipStream(fs, CompressionLevel.SmallestSize, leaveOpen: false);
            compressed.Write(bytes);
        }

        private static bool IsGZipPath(string path)
            => string.Equals(Path.GetExtension(path), ".gz", StringComparison.OrdinalIgnoreCase);
    }
}
