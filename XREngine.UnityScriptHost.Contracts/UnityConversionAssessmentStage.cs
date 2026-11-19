using System.Text.Json.Serialization;

namespace XREngine.UnityScriptHost.Contracts;

/// <summary>Identifies the operation that supplied conversion evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnityConversionAssessmentStage>))]
public enum UnityConversionAssessmentStage
{
    /// <summary>No conversion assessment has run.</summary>
    NotAssessed,
    /// <summary>Only source compilation and reference checks ran.</summary>
    Compilation,
    /// <summary>Authoring operations ran without native conversion.</summary>
    Authoring,
    /// <summary>The native importer assessed selected source features.</summary>
    NativeConversion
}
