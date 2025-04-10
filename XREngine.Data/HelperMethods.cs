
namespace XREngine.Core
{
    public static class Utility
    {
        /// <summary>
        /// Multiply this constant by a byte value to convert to normalized float.
        /// </summary>
        public static readonly float ByteToFloat = 1.0f / 255.0f;

        public static void EnsureDirPathExists(string path)
        {
            if (path.Contains(Path.AltDirectorySeparatorChar))
                path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (Path.HasExtension(path))
                path = Path.GetDirectoryName(path) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return;

            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
                return;
            Directory.CreateDirectory(fullPath);
        }

        /// <summary>
        /// Swaps the two values with each other.
        /// </summary>
        public static void Swap<T>(ref T value1, ref T value2)
        {
            (value2, value1) = (value1, value2);
        }
    }
}
