using System.ComponentModel;
using System.Reflection;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Assigns an existing live object through a reference property's setter.</summary>
    [XRMcp(Name = "set_object_reference", Permission = McpPermissionLevel.Mutate, PermissionReason = "Changes a live object reference.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Set a reference property to an existing engine object, including compatible interface properties. Omit reference_object_id to clear the reference.")]
    public static Task<McpToolResponse> SetObjectReferenceAsync(
        McpToolContext context,
        [McpName("object_id"), Description("GUID of the target object.")] string objectId,
        [McpName("property_name"), Description("Reference property to set.")] string propertyName,
        [McpName("reference_object_id"), Description("GUID of the existing reference object. Omit to clear.")] string? referenceObjectId = null,
        [McpName("object_path"), Description("Optional member path to the target object.")] string? objectPath = null,
        [McpName("reference_object_path"), Description("Optional member path to the reference object.")] string? referenceObjectPath = null)
    {
        if (!TryResolveInspectionTarget(context.WorldOrNull, objectId, objectPath, out var target, out string? error))
            return Task.FromResult(new McpToolResponse(error!, isError: true));

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        PropertyInfo? property = target!.GetType().GetProperty(propertyName, flags);
        if (property is null || !property.CanWrite || property.GetIndexParameters().Length != 0 ||
            property.PropertyType.IsValueType || property.PropertyType == typeof(string))
            return Task.FromResult(new McpToolResponse("The target requires a writable object reference property.", isError: true));

        object? reference = null;
        if (!string.IsNullOrWhiteSpace(referenceObjectId))
        {
            if (!TryResolveInspectionTarget(context.WorldOrNull, referenceObjectId, null, out XRBase? referenceRoot, out error)
                || !TryResolveOwnedReference(referenceRoot!, referenceObjectPath, out reference, out error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));
            if (!property.PropertyType.IsInstanceOfType(reference))
                return Task.FromResult(new McpToolResponse($"The reference must be assignable to {property.PropertyType.Name}.", isError: true));
        }
        else if (!string.IsNullOrWhiteSpace(referenceObjectPath))
            return Task.FromResult(new McpToolResponse("reference_object_path requires reference_object_id.", isError: true));

        using var change = Undo.TrackChange($"MCP Set {property.Name}", target);
        property.SetValue(target, reference);
        return Task.FromResult(new McpToolResponse($"Set reference '{property.Name}'.", new
        {
            rootObjectId = objectId,
            objectPath,
            property = property.Name,
            referenceObjectId,
            referenceObjectPath,
        }));
    }
}
