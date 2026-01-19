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
        /// <summary>
        /// Adds a component to a scene node by specifying the component type name.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to attach the component to.</param>
        /// <param name="componentTypeName">The component type name (short name like "MeshComponent" or full name like "XREngine.Components.MeshComponent").</param>
        /// <param name="componentName">Optional instance name for the component.</param>
        /// <returns>
        /// A response containing:
        /// <list type="bullet">
        /// <item><description><c>componentId</c> - The GUID of the newly added component</description></item>
        /// <item><description><c>componentName</c> - The instance name of the component</description></item>
        /// <item><description><c>componentType</c> - The full type name of the component</description></item>
        /// </list>
        /// </returns>
        [XRMcp]
        [McpName("add_component_to_node")]
        [Description("Add a component to a scene node by type name.")]
        public static Task<McpToolResponse> AddComponentToNodeAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to attach the component to.")] string nodeId,
            [McpName("component_type"), Description("Component type name (short name or full name).")]
            string componentTypeName,
            [McpName("component_name"), Description("Optional component instance name.")]
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

        /// <summary>
        /// Lists all components attached to a scene node.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to inspect.</param>
        /// <returns>
        /// A response containing an array of components, each with:
        /// <list type="bullet">
        /// <item><description><c>id</c> - The component's unique GUID</description></item>
        /// <item><description><c>name</c> - The component's instance name</description></item>
        /// <item><description><c>type</c> - The full type name of the component</description></item>
        /// </list>
        /// </returns>
        [XRMcp]
        [McpName("list_components")]
        [Description("List components on a scene node.")]
        public static Task<McpToolResponse> ListComponentsAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to inspect.")] string nodeId)
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

        /// <summary>
        /// Sets a property or field value on a component by name.
        /// The component can be identified by ID, instance name, or type name.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node that owns the component.</param>
        /// <param name="propertyName">The name of the property or field to set (case-insensitive).</param>
        /// <param name="value">The JSON value to assign. Will be deserialized to the property's type.</param>
        /// <param name="componentId">Optional: target component by its GUID.</param>
        /// <param name="componentName">Optional: target component by its instance name.</param>
        /// <param name="componentTypeName">Optional: target component by its type name.</param>
        /// <returns>A confirmation message indicating which property was set.</returns>
        /// <remarks>
        /// At least one of <paramref name="componentId"/>, <paramref name="componentName"/>, 
        /// or <paramref name="componentTypeName"/> must be provided to identify the target component.
        /// </remarks>
        [XRMcp]
        [McpName("set_component_property")]
        [Description("Set a component property or field value by name.")]
        public static Task<McpToolResponse> SetComponentPropertyAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID that owns the component.")] string nodeId,
            [McpName("property_name"), Description("Property or field name to set.")] string propertyName,
            [McpName("value"), Description("JSON value to assign to the property.")] object value,
            [McpName("component_id"), Description("Optional component ID to target.")] string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name to target.")] string? componentName = null,
            [McpName("component_type"), Description("Optional component type name to target.")] string? componentTypeName = null)
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

        /// <summary>
        /// Retrieves a property or field value from a component by name.
        /// The component can be identified by ID, instance name, or type name.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node that owns the component.</param>
        /// <param name="propertyName">The name of the property or field to retrieve (case-insensitive).</param>
        /// <param name="componentId">Optional: target component by its GUID.</param>
        /// <param name="componentName">Optional: target component by its instance name.</param>
        /// <param name="componentTypeName">Optional: target component by its type name.</param>
        /// <returns>The property or field value for the requested component.</returns>
        /// <remarks>
        /// At least one of <paramref name="componentId"/>, <paramref name="componentName"/>,
        /// or <paramref name="componentTypeName"/> must be provided to identify the target component.
        /// </remarks>
        [XRMcp]
        [McpName("get_component_property")]
        [Description("Get a component property or field value by name.")]
        public static Task<McpToolResponse> GetComponentPropertyAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID that owns the component.")] string nodeId,
            [McpName("property_name"), Description("Property or field name to read.")] string propertyName,
            [McpName("component_id"), Description("Optional component ID to target.")] string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name to target.")] string? componentName = null,
            [McpName("component_type"), Description("Optional component type name to target.")] string? componentTypeName = null)
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
            if (property is not null && property.CanRead)
            {
                var value = property.GetValue(component);
                return Task.FromResult(new McpToolResponse($"Retrieved property '{property.Name}' on '{componentType.Name}'.", new { value }));
            }

            var field = componentType.GetField(propertyName, bindingFlags);
            if (field is not null)
            {
                var value = field.GetValue(component);
                return Task.FromResult(new McpToolResponse($"Retrieved field '{field.Name}' on '{componentType.Name}'.", new { value }));
            }

            return Task.FromResult(new McpToolResponse($"Property or field '{propertyName}' not found on '{componentType.Name}'.", isError: true));
        }

        /// <summary>
        /// Removes a component from a scene node by id, name, or type.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node that owns the component.</param>
        /// <param name="componentId">Optional: target component by its GUID.</param>
        /// <param name="componentName">Optional: target component by its instance name.</param>
        /// <param name="componentTypeName">Optional: target component by its type name.</param>
        /// <returns>A confirmation message indicating the component was removed.</returns>
        [XRMcp]
        [McpName("remove_component")]
        [Description("Remove a component from a scene node.")]
        public static Task<McpToolResponse> RemoveComponentAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID that owns the component.")] string nodeId,
            [McpName("component_id"), Description("Optional component ID to target.")] string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name to target.")] string? componentName = null,
            [McpName("component_type"), Description("Optional component type name to target.")] string? componentTypeName = null)
        {
            if (string.IsNullOrWhiteSpace(componentId) && string.IsNullOrWhiteSpace(componentName) && string.IsNullOrWhiteSpace(componentTypeName))
                return Task.FromResult(new McpToolResponse("Provide component_id, component_name, or component_type to target a component.", isError: true));

            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var nodeError))
                return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));

            XRComponent? component = FindComponent(node!, componentId, componentName, componentTypeName, out var compError);
            if (component is null)
                return Task.FromResult(new McpToolResponse(compError ?? "Component not found on the specified node.", isError: true));

            component.Destroy();
            return Task.FromResult(new McpToolResponse($"Removed component '{component.ID}' from '{nodeId}'."));
        }
    }
}
