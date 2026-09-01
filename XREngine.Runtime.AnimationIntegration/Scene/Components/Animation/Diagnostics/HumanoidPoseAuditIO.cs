using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace XREngine.Components.Animation
{
    public static class HumanoidPoseAuditIO
    {
        public static HumanoidPoseAuditReport LoadReport(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = Path.GetFullPath(path);
            using FileStream input = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Stream content = IsGZipPath(fullPath)
                ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: false)
                : input;
            using StreamReader reader = new(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            return JsonConvert.DeserializeObject<HumanoidPoseAuditReport>(json)
                ?? throw new InvalidOperationException($"Failed to deserialize humanoid pose audit report '{fullPath}'.");
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
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string json = JsonConvert.SerializeObject(value, Formatting.Indented);
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
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
