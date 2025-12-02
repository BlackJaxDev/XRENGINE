using System;

namespace XREngine.Core.Files;

/// <summary>
/// Declares a custom Dear ImGui inspector implementation for an <see cref="XRAsset"/> type.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class XRAssetInspectorAttribute : Attribute
{
    public XRAssetInspectorAttribute(Type inspectorType)
        : this(GetTypeName(inspectorType))
    {
    }

    public XRAssetInspectorAttribute(string inspectorTypeName)
    {
        if (string.IsNullOrWhiteSpace(inspectorTypeName))
            throw new ArgumentException("Inspector type name must be provided.", nameof(inspectorTypeName));

        InspectorTypeName = inspectorTypeName;
    }

    /// <summary>
    /// Fully-qualified type name for the inspector implementation.
    /// </summary>
    public string InspectorTypeName { get; }

    private static string GetTypeName(Type inspectorType)
    {
        ArgumentNullException.ThrowIfNull(inspectorType);
        return inspectorType.AssemblyQualifiedName
               ?? inspectorType.FullName
               ?? inspectorType.Name
               ?? throw new ArgumentException("Inspector type did not provide a name.", nameof(inspectorType));
    }
}

/// <summary>
/// Declares an asset-specific context menu item for the asset browser.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class XRAssetContextMenuAttribute : Attribute
{
    public XRAssetContextMenuAttribute(string label, Type handlerType, string handlerMethodName)
        : this(label, GetTypeName(handlerType), handlerMethodName)
    {
    }

    public XRAssetContextMenuAttribute(string label, string handlerTypeName, string handlerMethodName)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Menu label must be provided.", nameof(label));
        if (string.IsNullOrWhiteSpace(handlerTypeName))
            throw new ArgumentException("Handler type name must be provided.", nameof(handlerTypeName));
        if (string.IsNullOrWhiteSpace(handlerMethodName))
            throw new ArgumentException("Handler method name must be provided.", nameof(handlerMethodName));

        Label = label;
        HandlerTypeName = handlerTypeName;
        HandlerMethodName = handlerMethodName;
    }

    /// <summary>
    /// Display text for the menu item.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Fully-qualified name of the handler type that implements the action.
    /// </summary>
    public string HandlerTypeName { get; }

    /// <summary>
    /// Name of the static method on the handler type to invoke.
    /// The method must accept a single <see cref="XRAssetContextMenuContext"/> parameter.
    /// </summary>
    public string HandlerMethodName { get; }

    private static string GetTypeName(Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        return handlerType.AssemblyQualifiedName
               ?? handlerType.FullName
               ?? handlerType.Name
               ?? throw new ArgumentException("Handler type did not provide a name.", nameof(handlerType));
    }
}

/// <summary>
/// Provides context information for asset-specific menu handlers.
/// </summary>
public readonly struct XRAssetContextMenuContext
{
    public XRAssetContextMenuContext(string assetPath, XRAsset? asset)
    {
        AssetPath = assetPath ?? string.Empty;
        Asset = asset;
    }

    /// <summary>
    /// Full path to the asset file that triggered the menu.
    /// </summary>
    public string AssetPath { get; }

    /// <summary>
    /// Loaded asset instance when available; otherwise <c>null</c>.
    /// </summary>
    public XRAsset? Asset { get; }
}
