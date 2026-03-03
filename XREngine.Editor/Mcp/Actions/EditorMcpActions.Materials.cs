using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Scene;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        // ═══════════════════════════════════════════════════════════════════
        // Material Uniform Tools
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lists all shader uniforms on a material, including their names, types, and current values.
        /// The material can be targeted by its asset GUID directly, or by specifying a component
        /// on a scene node that exposes a <c>Material</c> property.
        /// </summary>
        [XRMcp(Name = "get_material_uniforms", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List all shader uniforms (Parameters) on a material, including names, types, and current values. Target the material by asset ID or via a component's Material property.")]
        public static Task<McpToolResponse> GetMaterialUniformsAsync(
            McpToolContext context,
            [McpName("material_id"), Description("GUID of the material asset. If omitted, the material is resolved from the component.")]
            string? materialId = null,
            [McpName("node_id"), Description("Scene node ID (required when resolving material from a component).")]
            string? nodeId = null,
            [McpName("component_id"), Description("Optional component ID on the node.")]
            string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name on the node.")]
            string? componentName = null,
            [McpName("component_type"), Description("Optional component type name on the node.")]
            string? componentType = null)
        {
            if (!TryResolveMaterial(context, materialId, nodeId, componentId, componentName, componentType, out var material, out var error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            var uniforms = material!.Parameters.Select((p, i) => new
            {
                index = i,
                name = p.Name,
                uniformType = p.TypeName.ToString(),
                valueType = p.GenericValue?.GetType().Name ?? "null",
                value = FormatUniformValue(p)
            }).ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Material '{material.Name}' ({material.ID}) has {uniforms.Length} uniform(s).",
                new
                {
                    materialId = material.ID,
                    materialName = material.Name,
                    materialType = material.GetType().FullName ?? material.GetType().Name,
                    uniforms
                }));
        }

        /// <summary>
        /// Sets a shader uniform value on a material by uniform name.
        /// Supports float, int, uint, vec2, vec3, vec4, and mat4 uniforms.
        /// The material can be targeted by asset GUID or via a component's Material property.
        /// </summary>
        [XRMcp(Name = "set_material_uniform", Permission = McpPermissionLevel.Mutate, PermissionReason = "Modifies a material's shader uniform value.")]
        [Description("Set a shader uniform value on a material by uniform name. Supports float, int, uint, vec2 ({X,Y}), vec3 ({X,Y,Z}), vec4 ({X,Y,Z,W}). Target by material asset ID or via a component's Material property.")]
        public static Task<McpToolResponse> SetMaterialUniformAsync(
            McpToolContext context,
            [McpName("uniform_name"), Description("Name of the shader uniform to set (e.g. 'BaseColor', 'Roughness', 'Metallic').")]
            string uniformName,
            [McpName("value"), Description("Value to assign. For float/int: number literal. For vec2: {\"X\":0,\"Y\":1}. For vec3: {\"X\":1,\"Y\":0,\"Z\":0}. For vec4: {\"X\":1,\"Y\":0,\"Z\":0,\"W\":1}. Also accepts arrays like [1,0,0] for vectors.")]
            object value,
            [McpName("material_id"), Description("GUID of the material asset. If omitted, resolved from the component.")]
            string? materialId = null,
            [McpName("node_id"), Description("Scene node ID (required when resolving material from a component).")]
            string? nodeId = null,
            [McpName("component_id"), Description("Optional component ID on the node.")]
            string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name on the node.")]
            string? componentName = null,
            [McpName("component_type"), Description("Optional component type name on the node.")]
            string? componentType = null)
        {
            if (!TryResolveMaterial(context, materialId, nodeId, componentId, componentName, componentType, out var material, out var error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            // Find the uniform by name
            ShaderVar? shaderVar = null;
            for (int i = 0; i < material!.Parameters.Length; i++)
            {
                if (string.Equals(material.Parameters[i].Name, uniformName, StringComparison.OrdinalIgnoreCase))
                {
                    shaderVar = material.Parameters[i];
                    break;
                }
            }

            if (shaderVar is null)
            {
                var available = string.Join(", ", material.Parameters.Select(p => $"'{p.Name}' ({p.TypeName})"));
                return Task.FromResult(new McpToolResponse(
                    $"Uniform '{uniformName}' not found on material '{material.Name}'. Available uniforms: {available}",
                    isError: true));
            }

            // Set the value based on the concrete ShaderVar type
            if (!TrySetShaderVarValue(shaderVar, value, out var setError))
                return Task.FromResult(new McpToolResponse(setError!, isError: true));

            return Task.FromResult(new McpToolResponse(
                $"Set uniform '{shaderVar.Name}' on material '{material.Name}'.",
                new
                {
                    materialId = material.ID,
                    materialName = material.Name,
                    uniformName = shaderVar.Name,
                    uniformType = shaderVar.TypeName.ToString(),
                    newValue = FormatUniformValue(shaderVar)
                }));
        }

        /// <summary>
        /// Sets multiple shader uniform values on a material in a single call.
        /// </summary>
        [XRMcp(Name = "set_material_uniforms", Permission = McpPermissionLevel.Mutate, PermissionReason = "Modifies material shader uniform values.")]
        [Description("Set multiple shader uniforms on a material in one call. Pass a map of uniform_name -> value.")]
        public static Task<McpToolResponse> SetMaterialUniformsAsync(
            McpToolContext context,
            [McpName("uniforms"), Description("Object map of uniform names to values, e.g. {\"BaseColor\":{\"X\":1,\"Y\":0,\"Z\":0}, \"Roughness\":0.5, \"Metallic\":1.0}.")]
            Dictionary<string, object> uniforms,
            [McpName("material_id"), Description("GUID of the material asset. If omitted, resolved from the component.")]
            string? materialId = null,
            [McpName("node_id"), Description("Scene node ID (required when resolving material from a component).")]
            string? nodeId = null,
            [McpName("component_id"), Description("Optional component ID on the node.")]
            string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name on the node.")]
            string? componentName = null,
            [McpName("component_type"), Description("Optional component type name on the node.")]
            string? componentType = null)
        {
            if (!TryResolveMaterial(context, materialId, nodeId, componentId, componentName, componentType, out var material, out var error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            var results = new List<object>();
            int successCount = 0;
            int failCount = 0;

            foreach (var kvp in uniforms)
            {
                ShaderVar? shaderVar = null;
                for (int i = 0; i < material!.Parameters.Length; i++)
                {
                    if (string.Equals(material.Parameters[i].Name, kvp.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        shaderVar = material.Parameters[i];
                        break;
                    }
                }

                if (shaderVar is null)
                {
                    results.Add(new { uniform = kvp.Key, success = false, error = $"Uniform '{kvp.Key}' not found." });
                    failCount++;
                    continue;
                }

                if (TrySetShaderVarValue(shaderVar, kvp.Value, out var setError))
                {
                    results.Add(new { uniform = shaderVar.Name, success = true, newValue = FormatUniformValue(shaderVar) });
                    successCount++;
                }
                else
                {
                    results.Add(new { uniform = shaderVar.Name, success = false, error = setError ?? "Unknown error" });
                    failCount++;
                }
            }

            bool anyFailed = failCount > 0;
            return Task.FromResult(new McpToolResponse(
                $"Set {successCount}/{successCount + failCount} uniform(s) on material '{material!.Name}'.",
                new { materialId = material.ID, materialName = material.Name, results },
                isError: anyFailed && successCount == 0));
        }

        // ─────────────────────────────────────────────────────────────────
        // Material resolution helpers
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves an <see cref="XRMaterialBase"/> from either a direct asset ID or from a component's
        /// Material property on a scene node.
        /// </summary>
        private static bool TryResolveMaterial(
            McpToolContext context,
            string? materialId,
            string? nodeId,
            string? componentId,
            string? componentName,
            string? componentType,
            out XRMaterialBase? material,
            out string? error)
        {
            material = null;
            error = null;

            // Path 1: Direct material asset ID
            if (!string.IsNullOrWhiteSpace(materialId))
            {
                if (!Guid.TryParse(materialId, out var guid))
                {
                    error = $"Invalid material_id '{materialId}'.";
                    return false;
                }

                if (!XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj))
                {
                    error = $"Material '{materialId}' not found in runtime cache.";
                    return false;
                }

                if (obj is XRMaterialBase mat)
                {
                    material = mat;
                    return true;
                }

                error = $"Object '{materialId}' is {obj.GetType().Name}, not a material.";
                return false;
            }

            // Path 2: Resolve from component on a scene node
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                error = "Provide material_id or node_id (with optional component identifiers) to target a material.";
                return false;
            }

            if (!TryGetNodeById(context.WorldInstance, nodeId!, out var node, out var nodeError))
            {
                error = nodeError ?? "Scene node not found.";
                return false;
            }

            // If a component is specified, use it; otherwise search all components for one with a Material property
            IEnumerable<XRComponent> candidates;
            if (!string.IsNullOrWhiteSpace(componentId) || !string.IsNullOrWhiteSpace(componentName) || !string.IsNullOrWhiteSpace(componentType))
            {
                var comp = FindComponent(node!, componentId, componentName, componentType, out var compError);
                if (comp is null)
                {
                    error = compError ?? "Component not found.";
                    return false;
                }
                candidates = [comp];
            }
            else
            {
                candidates = node!.Components;
            }

            // Search for a Material property via reflection on each candidate component
            foreach (var comp in candidates)
            {
                var matProp = comp.GetType().GetProperty("Material",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

                if (matProp is not null && matProp.CanRead && typeof(XRMaterialBase).IsAssignableFrom(matProp.PropertyType))
                {
                    var matValue = matProp.GetValue(comp) as XRMaterialBase;
                    if (matValue is not null)
                    {
                        material = matValue;
                        return true;
                    }
                }
            }

            error = $"No material found on node '{node!.Name}'. Ensure the node has a component with a 'Material' property, or provide a direct material_id.";
            return false;
        }

        /// <summary>
        /// Attempts to set the value on a <see cref="ShaderVar"/> by inspecting its concrete generic type
        /// and converting the incoming JSON value accordingly.
        /// </summary>
        private static bool TrySetShaderVarValue(ShaderVar shaderVar, object value, out string? error)
        {
            error = null;

            try
            {
                // Unwrap JsonElement if present
                if (value is JsonElement element)
                {
                    return TrySetShaderVarFromJson(shaderVar, element, out error);
                }

                // Try direct numeric conversion for common atomic types
                if (shaderVar is ShaderFloat sf)
                {
                    if (TryConvertToFloat(value, out float fVal))
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        sf.Value = fVal;
                        return true;
                    }
                    error = $"Cannot convert value to float for uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderInt si)
                {
                    if (TryConvertToInt(value, out int iVal))
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        si.Value = iVal;
                        return true;
                    }
                    error = $"Cannot convert value to int for uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderUInt su)
                {
                    if (TryConvertToUInt(value, out uint uVal))
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        su.Value = uVal;
                        return true;
                    }
                    error = $"Cannot convert value to uint for uniform '{shaderVar.Name}'.";
                    return false;
                }

                // For vector/matrix types, serialize to JSON and go through the JSON path
                string json = JsonSerializer.Serialize(value);
                using var doc = JsonDocument.Parse(json);
                return TrySetShaderVarFromJson(shaderVar, doc.RootElement, out error);
            }
            catch (Exception ex)
            {
                error = $"Error setting uniform '{shaderVar.Name}': {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Sets a <see cref="ShaderVar"/> value from a parsed <see cref="JsonElement"/>.
        /// Supports objects {X,Y,Z,...}, arrays [r,g,b,...], and scalar numbers.
        /// </summary>
        private static bool TrySetShaderVarFromJson(ShaderVar shaderVar, JsonElement element, out string? error)
        {
            error = null;

            try
            {
                if (shaderVar is ShaderFloat sf)
                {
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        sf.Value = element.GetSingle();
                        return true;
                    }
                    error = $"Expected a number for float uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderInt si)
                {
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        si.Value = element.GetInt32();
                        return true;
                    }
                    error = $"Expected a number for int uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderUInt su)
                {
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        su.Value = element.GetUInt32();
                        return true;
                    }
                    error = $"Expected a number for uint uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderVector2 sv2)
                {
                    if (TryParseVector2(element, out var v2))
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        sv2.Value = v2;
                        return true;
                    }
                    error = $"Expected {{\"X\":n,\"Y\":n}} or [x,y] for vec2 uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderVector3 sv3)
                {
                    if (TryParseVector3(element, out var v3))
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        sv3.Value = v3;
                        return true;
                    }
                    error = $"Expected {{\"X\":n,\"Y\":n,\"Z\":n}} or [x,y,z] for vec3 uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderVector4 sv4)
                {
                    if (TryParseVector4(element, out var v4))
                    {
                        using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                        sv4.Value = v4;
                        return true;
                    }
                    error = $"Expected {{\"X\":n,\"Y\":n,\"Z\":n,\"W\":n}} or [x,y,z,w] for vec4 uniform '{shaderVar.Name}'.";
                    return false;
                }

                if (shaderVar is ShaderMat4 sm4)
                {
                    // Matrix4x4 expects a flat 16-element array or an object with M11..M44 properties
                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        var values = new float[16];
                        int idx = 0;
                        foreach (var item in element.EnumerateArray())
                        {
                            if (idx >= 16) break;
                            values[idx++] = item.GetSingle();
                        }
                        if (idx == 16)
                        {
                            using var _ = Undo.TrackChange($"MCP Set {shaderVar.Name}", shaderVar);
                            sm4.Value = new Matrix4x4(
                                values[0], values[1], values[2], values[3],
                                values[4], values[5], values[6], values[7],
                                values[8], values[9], values[10], values[11],
                                values[12], values[13], values[14], values[15]);
                            return true;
                        }
                    }
                    error = $"Expected a 16-element array for mat4 uniform '{shaderVar.Name}'.";
                    return false;
                }

                error = $"Unsupported uniform type '{shaderVar.GetType().Name}' for uniform '{shaderVar.Name}'.";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Error parsing value for uniform '{shaderVar.Name}': {ex.Message}";
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Vector parsing helpers — accept both {X,Y,Z} objects and [x,y,z] arrays.
        // Also accept R,G,B / r,g,b aliases for color convenience.
        // ─────────────────────────────────────────────────────────────────

        private static bool TryParseVector2(JsonElement el, out Vector2 result)
        {
            result = default;

            if (el.ValueKind == JsonValueKind.Array)
            {
                var items = el.EnumerateArray().ToArray();
                if (items.Length >= 2)
                {
                    result = new Vector2(items[0].GetSingle(), items[1].GetSingle());
                    return true;
                }
                return false;
            }

            if (el.ValueKind == JsonValueKind.Object)
            {
                float x = GetFloatProp(el, "X", "R");
                float y = GetFloatProp(el, "Y", "G");
                result = new Vector2(x, y);
                return true;
            }

            return false;
        }

        private static bool TryParseVector3(JsonElement el, out Vector3 result)
        {
            result = default;

            if (el.ValueKind == JsonValueKind.Array)
            {
                var items = el.EnumerateArray().ToArray();
                if (items.Length >= 3)
                {
                    result = new Vector3(items[0].GetSingle(), items[1].GetSingle(), items[2].GetSingle());
                    return true;
                }
                return false;
            }

            if (el.ValueKind == JsonValueKind.Object)
            {
                float x = GetFloatProp(el, "X", "R");
                float y = GetFloatProp(el, "Y", "G");
                float z = GetFloatProp(el, "Z", "B");
                result = new Vector3(x, y, z);
                return true;
            }

            return false;
        }

        private static bool TryParseVector4(JsonElement el, out Vector4 result)
        {
            result = default;

            if (el.ValueKind == JsonValueKind.Array)
            {
                var items = el.EnumerateArray().ToArray();
                if (items.Length >= 4)
                {
                    result = new Vector4(items[0].GetSingle(), items[1].GetSingle(), items[2].GetSingle(), items[3].GetSingle());
                    return true;
                }
                // Allow 3-element array for vec4 with W=1 (common for RGBA with implied alpha)
                if (items.Length == 3)
                {
                    result = new Vector4(items[0].GetSingle(), items[1].GetSingle(), items[2].GetSingle(), 1.0f);
                    return true;
                }
                return false;
            }

            if (el.ValueKind == JsonValueKind.Object)
            {
                float x = GetFloatProp(el, "X", "R");
                float y = GetFloatProp(el, "Y", "G");
                float z = GetFloatProp(el, "Z", "B");
                float w = GetFloatPropOrDefault(el, 1.0f, "W", "A");
                result = new Vector4(x, y, z, w);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets a float property from a JSON object, trying multiple case-insensitive names.
        /// Returns 0 if none found.
        /// </summary>
        private static float GetFloatProp(JsonElement el, params string[] names)
            => GetFloatPropOrDefault(el, 0f, names);

        private static float GetFloatPropOrDefault(JsonElement el, float defaultValue, params string[] names)
        {
            foreach (var name in names)
            {
                // Try exact case first, then case-insensitive
                if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
                    return prop.GetSingle();

                // Case-insensitive fallback
                foreach (var objProp in el.EnumerateObject())
                {
                    if (string.Equals(objProp.Name, name, StringComparison.OrdinalIgnoreCase)
                        && objProp.Value.ValueKind == JsonValueKind.Number)
                        return objProp.Value.GetSingle();
                }
            }
            return defaultValue;
        }

        // ─────────────────────────────────────────────────────────────────
        // Numeric conversion helpers
        // ─────────────────────────────────────────────────────────────────

        private static bool TryConvertToFloat(object value, out float result)
        {
            result = 0f;
            if (value is float f) { result = f; return true; }
            if (value is double d) { result = (float)d; return true; }
            if (value is int i) { result = i; return true; }
            if (value is long l) { result = l; return true; }
            if (value is decimal m) { result = (float)m; return true; }
            if (value is string s && float.TryParse(s, out result)) return true;
            return false;
        }

        private static bool TryConvertToInt(object value, out int result)
        {
            result = 0;
            if (value is int i) { result = i; return true; }
            if (value is long l) { result = (int)l; return true; }
            if (value is float f) { result = (int)f; return true; }
            if (value is double d) { result = (int)d; return true; }
            if (value is string s && int.TryParse(s, out result)) return true;
            return false;
        }

        private static bool TryConvertToUInt(object value, out uint result)
        {
            result = 0;
            if (value is uint u) { result = u; return true; }
            if (value is int i && i >= 0) { result = (uint)i; return true; }
            if (value is long l && l >= 0) { result = (uint)l; return true; }
            if (value is float f && f >= 0) { result = (uint)f; return true; }
            if (value is double d && d >= 0) { result = (uint)d; return true; }
            if (value is string s && uint.TryParse(s, out result)) return true;
            return false;
        }

        /// <summary>
        /// Formats a <see cref="ShaderVar"/> value for MCP response display.
        /// </summary>
        private static object? FormatUniformValue(ShaderVar shaderVar)
        {
            if (shaderVar is ShaderFloat sf)
                return sf.Value;
            if (shaderVar is ShaderInt si)
                return si.Value;
            if (shaderVar is ShaderUInt su)
                return su.Value;
            if (shaderVar is ShaderVector2 sv2)
                return new { sv2.Value.X, sv2.Value.Y };
            if (shaderVar is ShaderVector3 sv3)
                return new { sv3.Value.X, sv3.Value.Y, sv3.Value.Z };
            if (shaderVar is ShaderVector4 sv4)
                return new { sv4.Value.X, sv4.Value.Y, sv4.Value.Z, sv4.Value.W };
            if (shaderVar is ShaderMat4 sm4)
                return sm4.Value.ToString();

            return shaderVar.GenericValue?.ToString();
        }
    }
}
