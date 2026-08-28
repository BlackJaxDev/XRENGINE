using XREngine.Core.Files;
using XREngine.Data;
using YamlDotNet.Serialization;

namespace XREngine.Serialization;

/// <summary>Installs Data-owned YAML converters and published asset serializers.</summary>
public static class DataAssetSerializationRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(DataPublishedCookedAssetRegistration.Install());
            leases.Add(YamlSerializationContributions.Install(new DataYamlContribution()));
        });

    private sealed class DataYamlContribution : IYamlSerializationContribution
    {
        public string OwnerName => "XREngine.Data";

        public IEnumerable<IYamlTypeConverter> CreateTypeConverters()
            =>
            [
                new Vector2YamlTypeConverter(),
                new Vector3YamlTypeConverter(),
                new Vector4YamlTypeConverter(),
                new QuaternionYamlTypeConverter(),
                new Matrix4x4YamlTypeConverter(),
                new ColorF3YamlTypeConverter(),
                new ColorF4YamlTypeConverter(),
                new DataSourceYamlTypeConverter(),
                new OverrideableSettingYamlTypeConverter(),
                new TextFileYamlTypeConverter(),
            ];
    }
}
