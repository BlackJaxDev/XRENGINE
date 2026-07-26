using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public enum EMaterialAnimationValueKind
{
    Scalar,
    Vector,
    Color,
    Texture,
    Referenced,
    Packed,
    RepeatedSlot,
}

public sealed record MaterialAnimationAuthoringRequest(
    XRMaterial Material,
    string SemanticId,
    string RuntimePath,
    EMaterialAnimationValueKind ValueKind,
    object? CurrentValue,
    bool RenamedIdentity,
    string? OriginalSourceName);

/// <summary>
/// Adapter between reusable material authoring and the editor's active
/// animation timeline/recording surface. The inspector never guesses a clip.
/// </summary>
public static class MaterialAnimationAuthoringService
{
    public static Func<bool>? IsRecordModeActive { get; set; }
    public static Func<MaterialAnimationAuthoringRequest, string?>? AddBinding { get; set; }
    public static Func<MaterialAnimationAuthoringRequest, string?>? InsertKeyframe { get; set; }

    public static bool CanAnimate(ShaderAuthoringNode node, out string? diagnostic)
    {
        diagnostic = null;
        if (node.Attributes.Any(static attribute => attribute.Name == "DoNotAnimate"))
        {
            diagnostic = "This source property is marked DoNotAnimate.";
            return false;
        }
        if (node.ManifestProperty is null)
        {
            diagnostic = "This property has no runtime semantic binding.";
            return false;
        }
        return true;
    }

    public static string? AutoMarkAnimated(
        XRMaterial material,
        ShaderAuthoringNode node,
        string runtimePath,
        object? currentValue)
    {
        if (IsRecordModeActive?.Invoke() != true)
            return null;
        if (!CanAnimate(node, out string? diagnostic))
            return diagnostic;
        ShaderUiProperty property = node.ManifestProperty!;
        material.SetUberPropertyMode(property.Name, EShaderUiPropertyMode.Animated);
        return AddBinding?.Invoke(CreateRequest(material, node, runtimePath, currentValue)) ??
               "No animation binding adapter is registered.";
    }

    public static string? RequestBinding(
        XRMaterial material,
        ShaderAuthoringNode node,
        string runtimePath,
        object? currentValue)
    {
        if (!CanAnimate(node, out string? diagnostic))
            return diagnostic;
        return AddBinding?.Invoke(CreateRequest(material, node, runtimePath, currentValue)) ??
               "No animation binding adapter is registered.";
    }

    public static string? RequestKeyframe(
        XRMaterial material,
        ShaderAuthoringNode node,
        string runtimePath,
        object? currentValue)
    {
        if (!CanAnimate(node, out string? diagnostic))
            return diagnostic;
        return InsertKeyframe?.Invoke(CreateRequest(material, node, runtimePath, currentValue)) ??
               "No animation keyframe adapter is registered.";
    }

    public static string? ValidateModeChange(
        XRMaterial material,
        ShaderAuthoringNode node,
        EShaderUiPropertyMode requestedMode,
        bool confirmedBindingRepair)
    {
        ShaderUiProperty? property = node.ManifestProperty;
        if (property is null)
            return "The property has no runtime semantic binding.";
        EShaderUiPropertyMode current = material.GetUberPropertyMode(
            property.Name,
            property.DefaultMode,
            property.IsSampler);
        if (current == EShaderUiPropertyMode.Animated &&
            requestedMode == EShaderUiPropertyMode.Static &&
            !confirmedBindingRepair)
            return "Changing an animated property to static requires confirmation and binding repair.";
        if (requestedMode == EShaderUiPropertyMode.Animated &&
            node.Attributes.Any(static attribute => attribute.Name == "DoNotAnimate"))
            return "The source property is marked DoNotAnimate.";
        return null;
    }

    private static MaterialAnimationAuthoringRequest CreateRequest(
        XRMaterial material,
        ShaderAuthoringNode node,
        string runtimePath,
        object? currentValue)
    {
        bool renamed = !string.Equals(
            node.SourcePropertyName,
            node.ManifestProperty?.Name,
            StringComparison.Ordinal);
        return new(
            material,
            node.SemanticId,
            runtimePath,
            ResolveValueKind(node),
            currentValue,
            renamed,
            node.SourcePropertyName);
    }

    private static EMaterialAnimationValueKind ResolveValueKind(ShaderAuthoringNode node)
    {
        if (node.ManifestProperty?.IsSampler == true)
            return EMaterialAnimationValueKind.Texture;
        if (node.ReferencedProperties.Count > 0)
            return EMaterialAnimationValueKind.Referenced;
        if (node.WidgetId is "ThryRGBAPacker" or "ThryMask")
            return EMaterialAnimationValueKind.Packed;
        if (node.SourcePropertyName?.Any(char.IsDigit) == true)
            return EMaterialAnimationValueKind.RepeatedSlot;
        return node.ManifestProperty?.GlslType switch
        {
            "vec2" or "vec3" => EMaterialAnimationValueKind.Vector,
            "vec4" => EMaterialAnimationValueKind.Color,
            _ => EMaterialAnimationValueKind.Scalar,
        };
    }
}
