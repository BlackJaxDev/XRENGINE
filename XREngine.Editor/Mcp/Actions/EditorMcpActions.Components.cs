using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;

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

            // Record structural undo
            var nodeCapture = node!;
            using var _ = Undo.TrackChange($"MCP Add {componentType.Name}", component);
            Undo.RecordStructuralChange($"Add {componentType.Name}",
                undoAction: () =>
                {
                    nodeCapture.DetachComponent(component);
                },
                redoAction: () =>
                {
                    nodeCapture.ReattachComponent(component);
                });

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

            if (!TrySetComponentMember(component, propertyName, value, out string? setMessage))
                return Task.FromResult(new McpToolResponse(setMessage ?? "Unable to set component member.", isError: true));

            return Task.FromResult(new McpToolResponse(setMessage ?? $"Set '{propertyName}'."));
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
        /// Returns a snapshot of a component's readable properties and fields.
        /// Useful for LLM planning before mutating component state.
        /// </summary>
        [XRMcp]
        [McpName("get_component_snapshot")]
        [Description("Get a component snapshot including readable properties and fields.")]
        public static Task<McpToolResponse> GetComponentSnapshotAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID that owns the component.")] string nodeId,
            [McpName("component_id"), Description("Optional component ID to target.")] string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name to target.")] string? componentName = null,
            [McpName("component_type"), Description("Optional component type name to target.")] string? componentTypeName = null,
            [McpName("include_non_public"), Description("Include non-public instance members.")] bool includeNonPublic = false,
            [McpName("include_properties"), Description("Include properties in the snapshot.")] bool includeProperties = true,
            [McpName("include_fields"), Description("Include fields in the snapshot.")] bool includeFields = true)
        {
            if (string.IsNullOrWhiteSpace(componentId) && string.IsNullOrWhiteSpace(componentName) && string.IsNullOrWhiteSpace(componentTypeName))
                return Task.FromResult(new McpToolResponse("Provide component_id, component_name, or component_type to target a component.", isError: true));

            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var nodeError))
                return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));

            XRComponent? component = FindComponent(node!, componentId, componentName, componentTypeName, out var compError);
            if (component is null)
                return Task.FromResult(new McpToolResponse(compError ?? "Component not found on the specified node.", isError: true));

            var componentType = component.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
            if (includeNonPublic)
                flags |= BindingFlags.NonPublic;

            object[] properties = [];
            if (includeProperties)
            {
                properties = [.. componentType
                    .GetProperties(flags)
                    .Where(prop => prop.GetIndexParameters().Length == 0)
                    .Select(prop =>
                    {
                        object? rawValue = null;
                        string? readError = null;
                        if (prop.CanRead)
                        {
                            try { rawValue = prop.GetValue(component); }
                            catch (Exception ex) { readError = ex.Message; }
                        }

                        return new
                        {
                            name = prop.Name,
                            type = prop.PropertyType.FullName ?? prop.PropertyType.Name,
                            kind = "property",
                            canRead = prop.CanRead,
                            canWrite = prop.CanWrite,
                            value = prop.CanRead ? ToMcpValue(rawValue) : null,
                            valuePreview = prop.CanRead ? rawValue?.ToString() : null,
                            readError
                        };
                    })];
            }

            object[] fields = [];
            if (includeFields)
            {
                fields = [.. componentType
                    .GetFields(flags)
                    .Where(field => !field.IsStatic)
                    .Select(field =>
                    {
                        object? rawValue = null;
                        string? readError = null;
                        try { rawValue = field.GetValue(component); }
                        catch (Exception ex) { readError = ex.Message; }

                        return new
                        {
                            name = field.Name,
                            type = field.FieldType.FullName ?? field.FieldType.Name,
                            kind = "field",
                            isReadOnly = field.IsInitOnly,
                            value = ToMcpValue(rawValue),
                            valuePreview = rawValue?.ToString(),
                            readError
                        };
                    })];
            }

            var data = new
            {
                component = new
                {
                    id = component.ID,
                    name = component.Name,
                    type = componentType.FullName ?? componentType.Name,
                    nodeId = node!.ID,
                    nodeName = node.Name,
                    nodePath = BuildNodePath(node)
                },
                properties,
                fields
            };

            return Task.FromResult(new McpToolResponse($"Retrieved component snapshot for '{componentType.Name}'.", data));
        }

        /// <summary>
        /// Sets multiple component properties/fields in a single call.
        /// </summary>
        [XRMcp]
        [McpName("set_component_properties")]
        [Description("Set multiple component properties/fields in one call.")]
        public static Task<McpToolResponse> SetComponentPropertiesAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID that owns the component.")] string nodeId,
            [McpName("properties"), Description("Object map of member name -> JSON value.")] Dictionary<string, object>? properties,
            [McpName("component_id"), Description("Optional component ID to target.")] string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name to target.")] string? componentName = null,
            [McpName("component_type"), Description("Optional component type name to target.")] string? componentTypeName = null,
            [McpName("stop_on_error"), Description("Stop on first failed member set.")] bool stopOnError = false)
        {
            if (string.IsNullOrWhiteSpace(componentId) && string.IsNullOrWhiteSpace(componentName) && string.IsNullOrWhiteSpace(componentTypeName))
                return Task.FromResult(new McpToolResponse("Provide component_id, component_name, or component_type to target a component.", isError: true));

            if (properties is null || properties.Count == 0)
                return Task.FromResult(new McpToolResponse("Provide at least one member in properties.", isError: true));

            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var nodeError))
                return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));

            XRComponent? component = FindComponent(node!, componentId, componentName, componentTypeName, out var compError);
            if (component is null)
                return Task.FromResult(new McpToolResponse(compError ?? "Component not found on the specified node.", isError: true));

            var successes = new List<object>(properties.Count);
            var failures = new List<object>();

            foreach ((string key, object memberValue) in properties)
            {
                if (TrySetComponentMember(component, key, memberValue, out string? message))
                {
                    successes.Add(new { member = key, message });
                }
                else
                {
                    failures.Add(new { member = key, error = message ?? "Unknown error." });
                    if (stopOnError)
                        break;
                }
            }

            bool anySuccess = successes.Count > 0;
            bool hasFailures = failures.Count > 0;
            bool isError = hasFailures && !anySuccess;

            string summary = hasFailures
                ? $"Set {successes.Count} member(s); {failures.Count} failed on '{component.GetType().Name}'."
                : $"Set {successes.Count} member(s) on '{component.GetType().Name}'.";

            return Task.FromResult(new McpToolResponse(summary, new
            {
                componentId = component.ID,
                componentType = component.GetType().FullName ?? component.GetType().Name,
                successfulCount = successes.Count,
                failedCount = failures.Count,
                successes,
                failures
            }, isError));
        }

        /// <summary>
        /// Creates a new XRMaterial asset and saves it into the project assets directory.
        /// </summary>
        [XRMcp]
        [McpName("create_material_asset")]
        [McpPermission(McpPermissionLevel.Destructive, Reason = "Creates and writes an asset file to disk.")]
        [Description("Create and save a new XRMaterial asset.")]
        public static Task<McpToolResponse> CreateMaterialAssetAsync(
            McpToolContext context,
            [McpName("asset_name"), Description("Optional material asset name.")] string? assetName = null,
            [McpName("output_dir"), Description("Output directory. Relative paths are resolved under GameAssetsPath.")] string? outputDir = "Materials",
            [McpName("material_kind"), Description("Material preset kind: unlit_color_forward, lit_color, deferred_color.")] string materialKind = "unlit_color_forward",
            [McpName("color"), Description("Optional color object for the preset, e.g. {r,g,b,a}.")] object? color = null,
            [McpName("deferred"), Description("When material_kind=lit_color, choose deferred vs forward render pass.")] bool deferred = true)
        {
            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            string normalizedKind = (materialKind ?? string.Empty).Trim().ToLowerInvariant();
            ColorF4? parsedColor = null;
            if (color is not null)
            {
                if (!McpToolRegistry.TryConvertValue(color, typeof(ColorF4), out var convertedColor, out var colorError) || convertedColor is not ColorF4 c)
                    return Task.FromResult(new McpToolResponse(colorError ?? "Unable to parse color value.", isError: true));
                parsedColor = c;
            }

            XRMaterial material;
            switch (normalizedKind)
            {
                case "unlit":
                case "unlit_color":
                case "unlit_color_forward":
                    material = parsedColor.HasValue
                        ? XRMaterial.CreateUnlitColorMaterialForward(parsedColor.Value)
                        : XRMaterial.CreateUnlitColorMaterialForward();
                    break;

                case "lit":
                case "lit_color":
                    material = parsedColor.HasValue
                        ? XRMaterial.CreateLitColorMaterial(parsedColor.Value, deferred)
                        : XRMaterial.CreateLitColorMaterial(deferred);
                    break;

                case "deferred_color":
                case "color_deferred":
                    material = parsedColor.HasValue
                        ? XRMaterial.CreateColorMaterialDeferred(parsedColor.Value)
                        : XRMaterial.CreateColorMaterialDeferred();
                    break;

                default:
                    return Task.FromResult(new McpToolResponse($"Unsupported material_kind '{materialKind}'. Supported: unlit_color_forward, lit_color, deferred_color.", isError: true));
            }

            if (!string.IsNullOrWhiteSpace(assetName))
                material.Name = assetName;

            string targetDirectory = ResolveAssetDirectory(assets, outputDir, "Materials");
            Directory.CreateDirectory(targetDirectory);
            assets.SaveTo(material, targetDirectory);

            return Task.FromResult(new McpToolResponse("Created material asset.", new
            {
                id = material.ID,
                name = material.Name,
                type = material.GetType().FullName ?? material.GetType().Name,
                filePath = material.FilePath,
                materialKind = normalizedKind,
                outputDirectory = targetDirectory
            }));
        }

        /// <summary>
        /// Finds a loaded asset by id/path/name, optionally loading from disk when a path is provided.
        /// </summary>
        [XRMcp]
        [McpName("find_asset")]
        [Description("Find a project asset by ID, path, or name.")]
        public static Task<McpToolResponse> FindAssetAsync(
            McpToolContext context,
            [McpName("asset_id"), Description("Optional asset GUID.")] string? assetId = null,
            [McpName("asset_path"), Description("Optional asset path (absolute or relative to GameAssetsPath).")]
            string? assetPath = null,
            [McpName("asset_name"), Description("Optional loaded-asset name query.")] string? assetName = null,
            [McpName("asset_type"), Description("Optional type filter, e.g. XRMaterial.")] string? assetTypeName = null,
            [McpName("exact"), Description("If false, name matching uses contains().")] bool exact = true,
            [McpName("load_if_needed"), Description("When asset_path is provided, attempt load from disk if not already loaded.")] bool loadIfNeeded = true)
        {
            var assets = Engine.Assets;
            if (assets is null)
                return Task.FromResult(new McpToolResponse("Asset manager is unavailable.", isError: true));

            if (string.IsNullOrWhiteSpace(assetId) && string.IsNullOrWhiteSpace(assetPath) && string.IsNullOrWhiteSpace(assetName))
                return Task.FromResult(new McpToolResponse("Provide asset_id, asset_path, or asset_name.", isError: true));

            Type? typeFilter = null;
            if (!string.IsNullOrWhiteSpace(assetTypeName) && !TryResolveAssetType(assetTypeName!, out typeFilter))
                return Task.FromResult(new McpToolResponse($"Asset type '{assetTypeName}' not found.", isError: true));

            var matches = new List<XRAsset>();

            if (!string.IsNullOrWhiteSpace(assetId))
            {
                if (!Guid.TryParse(assetId, out var guid))
                    return Task.FromResult(new McpToolResponse($"Invalid asset_id '{assetId}'.", isError: true));

                XRAsset? byId = assets.GetAssetByID(guid);
                if (byId is null)
                    return Task.FromResult(new McpToolResponse("Asset not found by ID.", isError: true));

                if (typeFilter is null || typeFilter.IsInstanceOfType(byId))
                    matches.Add(byId);
            }

            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                string normalizedPath = NormalizeAssetPath(assets, assetPath!);

                XRAsset? byPath = assets.GetAssetByPath(normalizedPath)
                    ?? assets.GetAssetByOriginalPath(normalizedPath);

                if (byPath is null && loadIfNeeded && File.Exists(normalizedPath))
                {
                    if (typeFilter is not null)
                        byPath = assets.Load(normalizedPath, typeFilter);
                    else
                        byPath = assets.Load<XRMaterial>(normalizedPath);
                }

                if (byPath is not null && (typeFilter is null || typeFilter.IsInstanceOfType(byPath)))
                    matches.Add(byPath);
            }

            if (!string.IsNullOrWhiteSpace(assetName))
            {
                IEnumerable<XRAsset> query = assets.LoadedAssetsByIDInternal.Values;
                if (typeFilter is not null)
                    query = query.Where(typeFilter.IsInstanceOfType);

                query = exact
                    ? query.Where(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
                    : query.Where(a => a.Name?.Contains(assetName, StringComparison.OrdinalIgnoreCase) == true);

                matches.AddRange(query);
            }

            XRAsset[] result = matches.Distinct().ToArray();
            if (result.Length == 0)
                return Task.FromResult(new McpToolResponse("No matching assets found.", isError: true));

            return Task.FromResult(new McpToolResponse($"Found {result.Length} asset(s).", new
            {
                assets = result.Select(asset => new
                {
                    id = asset.ID,
                    name = asset.Name,
                    type = asset.GetType().FullName ?? asset.GetType().Name,
                    filePath = asset.FilePath,
                    originalPath = asset.OriginalPath,
                    isDirty = asset.IsDirty
                }).ToArray()
            }));
        }

        /// <summary>
        /// Assigns an asset reference (for example XRMaterial) to a component property/field.
        /// </summary>
        [XRMcp]
        [McpName("assign_component_asset_property")]
        [Description("Assign an asset reference to a component property or field (e.g., Material).")]
        public static Task<McpToolResponse> AssignComponentAssetPropertyAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID that owns the component.")] string nodeId,
            [McpName("property_name"), Description("Target property or field name to assign.")] string propertyName,
            [McpName("asset_id"), Description("Optional asset GUID.")] string? assetId = null,
            [McpName("asset_path"), Description("Optional asset path.")] string? assetPath = null,
            [McpName("asset_name"), Description("Optional loaded-asset name.")] string? assetName = null,
            [McpName("asset_type"), Description("Optional asset type hint, e.g. XRMaterial.")] string? assetTypeName = null,
            [McpName("component_id"), Description("Optional component ID to target.")] string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name to target.")] string? componentName = null,
            [McpName("component_type"), Description("Optional component type name to target.")] string? componentTypeName = null,
            [McpName("exact_asset_name"), Description("When resolving by asset_name, require exact match.")] bool exactAssetName = true)
        {
            if (string.IsNullOrWhiteSpace(componentId) && string.IsNullOrWhiteSpace(componentName) && string.IsNullOrWhiteSpace(componentTypeName))
                return Task.FromResult(new McpToolResponse("Provide component_id, component_name, or component_type to target a component.", isError: true));

            if (string.IsNullOrWhiteSpace(assetId) && string.IsNullOrWhiteSpace(assetPath) && string.IsNullOrWhiteSpace(assetName))
                return Task.FromResult(new McpToolResponse("Provide asset_id, asset_path, or asset_name.", isError: true));

            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var nodeError))
                return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));

            XRComponent? component = FindComponent(node!, componentId, componentName, componentTypeName, out var compError);
            if (component is null)
                return Task.FromResult(new McpToolResponse(compError ?? "Component not found on the specified node.", isError: true));

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var componentType = component.GetType();

            Type? targetMemberType = null;
            PropertyInfo? property = componentType.GetProperty(propertyName, flags);
            FieldInfo? field = null;

            if (property is not null && property.CanWrite)
                targetMemberType = property.PropertyType;
            else
            {
                field = componentType.GetField(propertyName, flags);
                if (field is not null && !field.IsInitOnly)
                    targetMemberType = field.FieldType;
            }

            if (targetMemberType is null)
                return Task.FromResult(new McpToolResponse($"Writable property/field '{propertyName}' not found on '{componentType.Name}'.", isError: true));

            if (!typeof(XRAsset).IsAssignableFrom(targetMemberType))
                return Task.FromResult(new McpToolResponse($"'{propertyName}' on '{componentType.Name}' is not an XRAsset reference type.", isError: true));

            Type resolvedAssetType = targetMemberType;
            if (!string.IsNullOrWhiteSpace(assetTypeName))
            {
                if (!TryResolveAssetType(assetTypeName!, out Type? hintedType) || hintedType is null)
                    return Task.FromResult(new McpToolResponse($"Asset type '{assetTypeName}' not found.", isError: true));

                if (!targetMemberType.IsAssignableFrom(hintedType))
                    return Task.FromResult(new McpToolResponse($"Asset type '{hintedType.Name}' is not assignable to '{targetMemberType.Name}'.", isError: true));

                resolvedAssetType = hintedType;
            }

            if (!TryResolveAssetReference(resolvedAssetType, assetId, assetPath, assetName, exactAssetName, out XRAsset? asset, out string? assetError))
                return Task.FromResult(new McpToolResponse(assetError ?? "Asset could not be resolved.", isError: true));

            if (!targetMemberType.IsInstanceOfType(asset))
                return Task.FromResult(new McpToolResponse($"Resolved asset '{asset!.ID}' ({asset.GetType().Name}) is not assignable to '{targetMemberType.Name}'.", isError: true));

            using var _ = Undo.TrackChange($"MCP Assign {propertyName}", component);
            if (property is not null)
                property.SetValue(component, asset);
            else
                field!.SetValue(component, asset);

            return Task.FromResult(new McpToolResponse($"Assigned '{propertyName}' on '{componentType.Name}'.", new
            {
                componentId = component.ID,
                componentType = componentType.FullName ?? componentType.Name,
                property = property?.Name ?? field!.Name,
                asset = new
                {
                    id = asset!.ID,
                    name = asset.Name,
                    type = asset.GetType().FullName ?? asset.GetType().Name,
                    filePath = asset.FilePath
                }
            }));
        }

        private static bool TrySetComponentMember(XRComponent component, string memberName, object value, out string? message)
        {
            message = null;

            var componentType = component.GetType();
            var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var property = componentType.GetProperty(memberName, bindingFlags);
            if (property is not null && property.CanWrite)
            {
                if (!McpToolRegistry.TryConvertValue(value, property.PropertyType, out var converted, out var error))
                {
                    message = error ?? $"Unable to deserialize value for property '{property.Name}'.";
                    return false;
                }

                using var _ = Undo.TrackChange($"MCP Set {property.Name}", component);
                property.SetValue(component, converted);
                message = $"Set property '{property.Name}' on '{componentType.Name}'.";
                return true;
            }

            var field = componentType.GetField(memberName, bindingFlags);
            if (field is not null && !field.IsInitOnly)
            {
                if (!McpToolRegistry.TryConvertValue(value, field.FieldType, out var converted, out var error))
                {
                    message = error ?? $"Unable to deserialize value for field '{field.Name}'.";
                    return false;
                }

                using var _f = Undo.TrackChange($"MCP Set {field.Name}", component);
                field.SetValue(component, converted);
                message = $"Set field '{field.Name}' on '{componentType.Name}'.";
                return true;
            }

            message = $"Property or field '{memberName}' not found on '{componentType.Name}'.";
            return false;
        }

        private static object? ToMcpValue(object? value)
        {
            if (value is null)
                return null;

            Type type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal || value is Guid || value is DateTime || type.IsEnum)
                return value;

            if (value is XRAsset asset)
            {
                return new
                {
                    assetId = asset.ID,
                    assetName = asset.Name,
                    assetType = asset.GetType().FullName ?? asset.GetType().Name,
                    assetFilePath = asset.FilePath
                };
            }

            if (value is XRObjectBase obj)
            {
                return new
                {
                    objectId = obj.ID,
                    objectName = obj.Name,
                    objectType = obj.GetType().FullName ?? obj.GetType().Name
                };
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                var previewItems = new List<string>();
                int count = 0;
                foreach (object? item in enumerable)
                {
                    count++;
                    if (previewItems.Count < 8)
                        previewItems.Add(item?.ToString() ?? "null");
                }

                return new
                {
                    type = type.FullName ?? type.Name,
                    count,
                    preview = previewItems,
                    truncated = count > previewItems.Count
                };
            }

            return value.ToString();
        }

        private static string ResolveAssetDirectory(AssetManager assets, string? outputDir, string fallbackRelativeFolder)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
                return Path.GetFullPath(Path.Combine(assets.GameAssetsPath, fallbackRelativeFolder));

            return Path.IsPathRooted(outputDir)
                ? Path.GetFullPath(outputDir)
                : Path.GetFullPath(Path.Combine(assets.GameAssetsPath, outputDir));
        }

        private static string NormalizeAssetPath(AssetManager assets, string assetPath)
        {
            if (Path.IsPathRooted(assetPath))
                return Path.GetFullPath(assetPath);

            return Path.GetFullPath(Path.Combine(assets.GameAssetsPath, assetPath));
        }

        private static bool TryResolveAssetType(string assetTypeName, out Type? type)
        {
            type = null;
            if (string.IsNullOrWhiteSpace(assetTypeName))
                return false;

            Type baseType = typeof(XRAsset);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? assemblyTypes = null;
                try
                {
                    assemblyTypes = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    assemblyTypes = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (Type candidate in assemblyTypes)
                {
                    if (!baseType.IsAssignableFrom(candidate) || candidate.IsAbstract)
                        continue;

                    if (string.Equals(candidate.Name, assetTypeName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate.FullName, assetTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        type = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryResolveAssetReference(
            Type targetAssetType,
            string? assetId,
            string? assetPath,
            string? assetName,
            bool exactAssetName,
            out XRAsset? asset,
            out string? error)
        {
            asset = null;
            error = null;

            var assets = Engine.Assets;
            if (assets is null)
            {
                error = "Asset manager is unavailable.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(assetId))
            {
                if (!Guid.TryParse(assetId, out Guid guid))
                {
                    error = $"Invalid asset_id '{assetId}'.";
                    return false;
                }

                asset = assets.GetAssetByID(guid);
                if (asset is null)
                {
                    error = $"Asset '{assetId}' not found.";
                    return false;
                }

                if (!targetAssetType.IsInstanceOfType(asset))
                {
                    error = $"Asset '{assetId}' is '{asset.GetType().Name}', expected '{targetAssetType.Name}'.";
                    return false;
                }

                return true;
            }

            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                string normalizedPath = NormalizeAssetPath(assets, assetPath!);
                asset = assets.GetAssetByPath(normalizedPath) ?? assets.GetAssetByOriginalPath(normalizedPath);
                if (asset is null)
                {
                    if (!File.Exists(normalizedPath))
                    {
                        error = $"Asset path '{normalizedPath}' does not exist.";
                        return false;
                    }

                    asset = assets.Load(normalizedPath, targetAssetType);
                }

                if (asset is null)
                {
                    error = $"Failed to load asset from '{normalizedPath}'.";
                    return false;
                }

                if (!targetAssetType.IsInstanceOfType(asset))
                {
                    error = $"Asset '{normalizedPath}' is '{asset.GetType().Name}', expected '{targetAssetType.Name}'.";
                    return false;
                }

                return true;
            }

            if (!string.IsNullOrWhiteSpace(assetName))
            {
                XRAsset[] matches = (exactAssetName
                        ? assets.LoadedAssetsByIDInternal.Values.Where(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
                        : assets.LoadedAssetsByIDInternal.Values.Where(a => a.Name?.Contains(assetName, StringComparison.OrdinalIgnoreCase) == true))
                    .Where(targetAssetType.IsInstanceOfType)
                    .Distinct()
                    .ToArray();

                if (matches.Length == 0)
                {
                    error = $"No loaded asset named '{assetName}' matched '{targetAssetType.Name}'.";
                    return false;
                }

                if (matches.Length > 1)
                {
                    error = $"Multiple assets matched name '{assetName}'. Use asset_id or asset_path.";
                    return false;
                }

                asset = matches[0];
                return true;
            }

            error = "Provide asset_id, asset_path, or asset_name.";
            return false;
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

            var nodeCapture = node!;
            string compTypeName = component.GetType().Name;
            nodeCapture.DetachComponent(component);

            // Record structural undo
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange($"MCP Remove {compTypeName}");
            Undo.RecordStructuralChange($"Remove {compTypeName}",
                undoAction: () =>
                {
                    nodeCapture.ReattachComponent(component);
                },
                redoAction: () =>
                {
                    nodeCapture.DetachComponent(component);
                });

            return Task.FromResult(new McpToolResponse($"Removed component '{component.ID}' from '{nodeId}'."));
        }
    }
}
