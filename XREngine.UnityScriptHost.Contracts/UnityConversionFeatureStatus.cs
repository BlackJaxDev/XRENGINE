using System.Text.Json.Serialization;

namespace XREngine.UnityScriptHost.Contracts;

/// <summary>Separates observed native conversion from missing mappings and blocked authoring work.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnityConversionFeatureStatus>))]
public enum UnityConversionFeatureStatus
{
    /// <summary>The available evidence does not establish conversion support or loss.</summary>
    Unknown,
    /// <summary>The converter produced data for an existing native capability.</summary>
    Converted,
    /// <summary>A native capability exists, but the required import mapping is not implemented.</summary>
    MissingConverter,
    /// <summary>The source behavior requires a capability that the native target does not provide.</summary>
    MissingNativeCapability,
    /// <summary>Compilation or an authoring operation blocked production of output to assess.</summary>
    AuthoringBlocked
}
