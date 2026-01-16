using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMCP]
        [DisplayName("add_component_to_node")]
        [Description("Add a component to a scene node by type name.")]
        public static Task<McpToolResponse> AddComponentToNodeAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to attach the component to.")] string nodeId,
            [DisplayName("component_type"), Description("Component type name (short name or full name).")]
            string componentTypeName,
            [DisplayName("component_name"), Description("Optional component instance name.")]
            string? componentName = null)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            if (!McpToolRegistry.TryResolveComponentType(componentTypeName, out var componentType))
                return Task.FromResult(new McpToolResponse($"Component type '{componentTypeName}' not found.", isError: true));

            var component = node!.AddComponent(componentType, componentName);
            if (component is null)
                return Task.FromResult(new McpToolResponse($"Failed to add component '{componentTypeName}' to '{nodeId}'.", isError: true));

            return Task.FromResult(new McpToolResponse($"Added component '{componentType.Name}' to '{nodeId}'.", new
            {
                componentId = component.ID,
                componentName = component.Name,
                componentType = component.GetType().FullName
            }));
        }

        [XRMCP]
        [DisplayName("list_components")]
        [Description("List components on a scene node.")]
        public static Task<McpToolResponse> ListComponentsAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to inspect.")] string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var components = node!.Components.Select(comp => new
            {
                id = comp.ID,
                name = comp.Name,
                type = comp.GetType().FullName ?? comp.GetType().Name
            }).ToArray();

            return Task.FromResult(new McpToolResponse($"Listed components on '{nodeId}'.", new { components }));
        }

        [XRMCP]
        [DisplayName("set_component_property")]
        [Description("Set a component property or field value by name.")]
        public static Task<McpToolResponse> SetComponentPropertyAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID that owns the component.")] string nodeId,
            [DisplayName("property_name"), Description("Property or field name to set.")] string propertyName,
            [DisplayName("value"), Description("JSON value to assign to the property.")] object value,
            [DisplayName("component_id"), Description("Optional component ID to target.")] string? componentId = null,
            [DisplayName("component_name"), Description("Optional component instance name to target.")] string? componentName = null,
            [DisplayName("component_type"), Description("Optional component type name to target.")] string? componentTypeName = null)
        {
            if (string.IsNullOrWhiteSpace(componentId) && string.IsNullOrWhiteSpace(componentName) && string.IsNullOrWhiteSpace(componentTypeName))
                return Task.FromResult(new McpToolResponse("Provide component_id, component_name, or component_type to target a component.", isError: true));

            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var nodeError))
                return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));

            XRComponent? component = FindComponent(node!, componentId, componentName, componentTypeName, out var compError);
            if (component is null)
                return Task.FromResult(new McpToolResponse(compError ?? "Component not found on the specified node.", isError: true));

            var componentType = component.GetType();
            var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var property = componentType.GetProperty(propertyName, bindingFlags);
            if (property is not null && property.CanWrite)
            {
                if (!McpToolRegistry.TryConvertValue(value, property.PropertyType, out var converted, out var error))
                    return Task.FromResult(new McpToolResponse(error ?? "Unable to deserialize value.", isError: true));

                property.SetValue(component, converted);
                return Task.FromResult(new McpToolResponse($"Set property '{property.Name}' on '{componentType.Name}'."));
            }

            var field = componentType.GetField(propertyName, bindingFlags);
            if (field is not null && !field.IsInitOnly)
            {
                if (!McpToolRegistry.TryConvertValue(value, field.FieldType, out var converted, out var error))
                    return Task.FromResult(new McpToolResponse(error ?? "Unable to deserialize value.", isError: true));

                field.SetValue(component, converted);
                return Task.FromResult(new McpToolResponse($"Set field '{field.Name}' on '{componentType.Name}'."));
            }

            return Task.FromResult(new McpToolResponse($"Property or field '{propertyName}' not found on '{componentType.Name}'.", isError: true));
        }
    }
}
