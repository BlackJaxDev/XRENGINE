using XREngine.UnityScriptHost.Contracts;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    /// <summary>Records one observed import branch for a source component without asserting runtime parity.</summary>
    private static void RecordAvatarFeature(
        ImportState state,
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        string featureId,
        UnityConversionFeatureStatus status,
        string functionality,
        string reason,
        string? nativeTarget = null,
        string? member = null,
        string? lostFunctionality = null,
        string? requiredNativeCapability = null)
    {
        string? sourceObjectId = string.IsNullOrWhiteSpace(hierarchy.SourceGuid)
            ? null
            : $"{hierarchy.SourceGuid}:{behaviour.FileId}";
        state.Context.RecordConversionFeature(new UnityConversionFeature
        {
            FeatureId = featureId,
            Status = status,
            SourcePath = hierarchy.SourcePath,
            SourceObjectId = sourceObjectId,
            Member = member,
            NativeTarget = nativeTarget,
            Functionality = functionality,
            Reason = reason,
            LostFunctionality = lostFunctionality,
            RequiredNativeCapability = requiredNativeCapability,
        });
    }
}
