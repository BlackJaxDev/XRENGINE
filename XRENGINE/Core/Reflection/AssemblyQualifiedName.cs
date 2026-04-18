using XREngine.Extensions;
using System.Globalization;
using System.Reflection;

namespace XREngine.Core.Reflection
{
    [Serializable]
    public class AssemblyQualifiedName
    {
        private static readonly char[] Separators = ['.', '+', '\\'];

        private string? _classPath;
        public string? ClassPath
        {
            get => _classPath;
            set
            {
                _classPath = value;
                string[]? classPathParts = ClassPath?.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                ClassName = (classPathParts?.Length ?? 0) == 0 ? null : classPathParts![^1];
            }
        }
        public string? ClassName { get; private set; }
        public string AssemblyName { get; set; }
        public int VersionMajor { get; set; }
        public int VersionMinor { get; set; }
        public int VersionBuild { get; set; }
        public int VersionRevision { get; set; }
        public CultureInfo? CultureInfo { get; set; }
        public byte[]? PublicKeyToken { get; set; }

        public AssemblyName GetAssemblyName()
        {
            AssemblyName name = new(AssemblyName)
            {
                Version = new Version(VersionMajor, VersionMinor, VersionBuild, VersionRevision),
                CultureInfo = CultureInfo
            };
            name.SetPublicKeyToken(PublicKeyToken);
            return name;
        }

        public AssemblyQualifiedName(string value)
        {
            string[] parts = value.Split(',');

            ClassPath = "";
            for (int i = 0; i < parts.Length - 4; ++i)
            {
                if (i != 0)
                    ClassPath += ",";
                ClassPath += parts[i];
            }

            AssemblyName = parts[^4].Trim();

            string version = parts[^3][(parts[^3].IndexOf('=') + 1)..];
            string culture = parts[^2][(parts[^2].IndexOf('=') + 1)..];
            string publicKeyToken = parts[^1][(parts[^1].IndexOf('=') + 1)..];

            string[] nums = version.Split('.');
            VersionMajor = int.Parse(nums[0]);
            VersionMinor = int.Parse(nums[1]);
            VersionBuild = int.Parse(nums[2]);
            VersionRevision = int.Parse(nums[3]);

            CultureInfo = culture == "neutral" ? null : new CultureInfo(culture);
            PublicKeyToken = publicKeyToken == "null" ? null : publicKeyToken.SelectEvery(2, x => byte.Parse(x[0].ToString() + x[1].ToString())).ToArray();
        }

        public override string ToString()
        {
            return $"{ClassPath}, {AssemblyName}, Version={VersionMajor}.{VersionMinor}.{VersionBuild}.{VersionRevision}, Culture={CultureInfo?.ToString() ?? "neutral"}, PublicKeyToken={PublicKeyToken?.ToString() ?? "null"}";
        }
    }
}