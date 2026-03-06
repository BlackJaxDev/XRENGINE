using System.Text;
using Newtonsoft.Json;

namespace XREngine.Components.Animation
{
    public static class HumanoidPoseAuditIO
    {
        public static HumanoidPoseAuditReport LoadReport(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonConvert.DeserializeObject<HumanoidPoseAuditReport>(json)
                ?? throw new InvalidOperationException($"Failed to deserialize humanoid pose audit report '{path}'.");
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

            File.WriteAllText(fullPath, JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
