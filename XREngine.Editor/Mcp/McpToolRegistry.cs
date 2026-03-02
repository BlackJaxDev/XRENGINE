using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public static class McpToolRegistry
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly Lazy<IReadOnlyDictionary<string, Type>> s_componentTypeCache
            = new(BuildComponentTypeCache);

        private static readonly IReadOnlyList<McpToolDefinition> s_tools = BuildTools();

        public static IReadOnlyList<McpToolDefinition> Tools => s_tools;

        public static bool TryGetTool(string name, out McpToolDefinition? tool)
        {
            tool = s_tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            return tool is not null;
        }

        private static IReadOnlyList<McpToolDefinition> BuildTools()
        {
            var tools = new List<McpToolDefinition>();
            foreach (var method in GetMcpMethods())
            {
                var definition = BuildToolDefinition(method);
                if (definition is not null)
                    tools.Add(definition);
            }

            return tools;
        }

        internal static bool TryResolveComponentType(string name, out Type type)
        {
            type = null!;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var cache = s_componentTypeCache.Value;
            if (cache.TryGetValue(name, out var resolved))
            {
                type = resolved;
                return true;
            }

            return false;
        }

        private static IReadOnlyDictionary<string, Type> BuildComponentTypeCache()
        {
            var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            var baseType = typeof(XRComponent);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

                foreach (var type in assemblyTypes)
                {
                    if (!baseType.IsAssignableFrom(type) || type.IsAbstract)
                        continue;

                    types.TryAdd(type.Name, type);
                    if (type.FullName is not null)
                        types.TryAdd(type.FullName, type);
                }
            }

            return types;
        }

        internal static bool TryConvertValue(object? value, Type targetType, out object? converted, out string? error)
        {
            try
            {
                if (value is null)
                {
                    if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                    {
                        converted = null;
                        error = $"Cannot assign null to {targetType.Name}.";
                        return false;
                    }

                    converted = null;
                    error = null;
                    return true;
                }

                if (targetType.IsInstanceOfType(value))
                {
                    converted = value;
                    error = null;
                    return true;
                }

                // Skip JSON deserialization for types that System.Text.Json cannot handle:
                // abstract types, interface types, pointer types, and types with no public
                // parameterless constructor (unless they're value types or have JSON converters).
                if (IsUnserializableType(targetType))
                {
                    converted = null;
                    error = $"Type '{targetType.Name}' cannot be deserialized from JSON.";
                    return false;
                }

                if (value is JsonElement element)
                {
                    converted = JsonSerializer.Deserialize(element.GetRawText(), targetType, s_jsonOptions);
                    error = null;
                    return true;
                }

                string json = JsonSerializer.Serialize(value, s_jsonOptions);
                converted = JsonSerializer.Deserialize(json, targetType, s_jsonOptions);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                converted = null;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Returns true if a type is known to be unserializable by System.Text.Json,
        /// avoiding expensive exception-based fallback paths.
        /// </summary>
        private static bool IsUnserializableType(Type type)
        {
            // Unwrap arrays/collections to check the element type.
            Type? elementType = type.IsArray ? type.GetElementType() : null;
            Type checkType = elementType ?? type;

            // Abstract or interface types can't be directly deserialized.
            if (checkType.IsAbstract || checkType.IsInterface)
                return true;

            // Pointer-like types.
            if (checkType == typeof(IntPtr) || checkType == typeof(UIntPtr)
                || checkType.IsPointer || checkType.IsByRef || checkType.IsByRefLike)
                return true;

            // Skip complex engine types that don't have JSON serialization support.
            // Heuristic: if the type is in an XREngine namespace and is not a simple
            // value type / enum / string, it's very unlikely to roundtrip through JSON.
            string? ns = checkType.Namespace;
            if (ns is not null && ns.StartsWith("XREngine", StringComparison.Ordinal)
                && !checkType.IsEnum && !checkType.IsValueType
                && checkType != typeof(string))
                return true;

            return false;
        }

        private static IEnumerable<MethodInfo> GetMcpMethods()
        {
            var baseType = typeof(XRBase);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? types = null;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type is null || !baseType.IsAssignableFrom(type))
                        continue;

                    foreach (var method in type.GetMethods(flags))
                    {
                        if (method.GetCustomAttribute<XRMcpAttribute>() is not null)
                            yield return method;
                    }
                }
            }
        }

        private static McpToolDefinition? BuildToolDefinition(MethodInfo method)
        {
            if (!IsSupportedReturnType(method.ReturnType))
                return null;

            var mcpAttribute = method.GetCustomAttribute<XRMcpAttribute>();
            string toolName = mcpAttribute?.Name ?? method.Name;
            string toolDescription = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? method.Name;

            var parameters = method.GetParameters();
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var parameter in parameters)
            {
                if (IsInjectedParameter(parameter))
                    continue;

                string propertyName = parameter.GetCustomAttribute<McpNameAttribute>()?.Name ?? parameter.Name ?? "param";
                string? description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                properties[propertyName] = BuildParameterSchema(parameter.ParameterType, propertyName, description);

                if (!parameter.HasDefaultValue && !parameter.IsOptional)
                    required.Add(propertyName);
            }

            var inputSchema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            };

            return new McpToolDefinition(
                name: toolName,
                description: toolDescription,
                inputSchema: inputSchema,
                handler: (context, args, token) => InvokeToolAsync(method, context, args, token),
                permissionLevel: ResolvePermissionLevel(method, toolName),
                permissionReason: mcpAttribute?.PermissionReason,
                threadAffinity: method.GetCustomAttribute<McpThreadAffinityAttribute>()?.Affinity);
        }

        private static object BuildParameterSchema(Type parameterType, string title, string? description)
        {
            var schema = new Dictionary<string, object?> { ["type"] = MapJsonType(parameterType) };
            if (!string.IsNullOrWhiteSpace(description))
                schema["description"] = description;
            if (!string.IsNullOrWhiteSpace(title))
                schema["title"] = title;
            if (parameterType.IsEnum)
                schema["enum"] = Enum.GetNames(parameterType);

            // JSON Schema requires "items" for array types.
            Type underlying = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            if (underlying.IsArray)
            {
                Type elementType = underlying.GetElementType()!;
                schema["items"] = new Dictionary<string, object?> { ["type"] = MapJsonType(elementType) };
            }
            else if (underlying != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying))
            {
                // For generic collections like List<T>, extract T.
                Type[] genericArgs = underlying.GetGenericArguments();
                string itemType = genericArgs.Length > 0 ? MapJsonType(genericArgs[0]) : "string";
                schema["items"] = new Dictionary<string, object?> { ["type"] = itemType };
            }

            return schema;
        }

        private static string MapJsonType(Type parameterType)
        {
            var type = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            if (type == typeof(string) || type.IsEnum)
                return "string";
            if (type == typeof(bool))
                return "boolean";
            if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long))
                return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "number";
            if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
                return "array";
            return "object";
        }

        private static bool IsInjectedParameter(ParameterInfo parameter)
            => parameter.ParameterType == typeof(McpToolContext) || parameter.ParameterType == typeof(CancellationToken);

        private static async Task<McpToolResponse> InvokeToolAsync(MethodInfo method, McpToolContext context, JsonElement arguments, CancellationToken token)
        {
            var parameters = method.GetParameters();
            var invokeArgs = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.ParameterType == typeof(McpToolContext))
                {
                    invokeArgs[i] = context;
                    continue;
                }

                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    invokeArgs[i] = token;
                    continue;
                }

                if (!TryGetArgument(arguments, parameter, out var element))
                {
                    if (parameter.HasDefaultValue)
                    {
                        invokeArgs[i] = parameter.DefaultValue;
                        continue;
                    }

                    return new McpToolResponse($"Missing required argument '{parameter.Name}'.", isError: true);
                }

                if (!TryDeserialize(element, parameter.ParameterType, out var value, out var error))
                    return new McpToolResponse(error ?? $"Unable to deserialize argument '{parameter.Name}'.", isError: true);

                invokeArgs[i] = value;
            }

            object? target = null;
            if (!method.IsStatic)
            {
                if (method.DeclaringType is null)
                    return new McpToolResponse($"Tool '{method.Name}' has no declaring type.", isError: true);

                try
                {
                    target = Activator.CreateInstance(method.DeclaringType, nonPublic: true);
                }
                catch (Exception ex)
                {
                    return new McpToolResponse($"Failed to construct tool target '{method.DeclaringType.Name}': {ex.Message}", isError: true);
                }
            }

            object? result = method.Invoke(target, invokeArgs);
            if (result is Task<McpToolResponse> task)
                return await task;
            if (result is McpToolResponse response)
                return response;

            return new McpToolResponse($"Tool '{method.Name}' did not return a response.", isError: true);
        }

        private static bool TryGetArgument(JsonElement arguments, ParameterInfo parameter, out JsonElement value)
        {
            string? displayName = parameter.GetCustomAttribute<McpNameAttribute>()?.Name;
            if (!string.IsNullOrWhiteSpace(displayName) && arguments.TryGetProperty(displayName, out value))
                return true;

            string paramName = parameter.Name ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(paramName) && arguments.TryGetProperty(paramName, out value))
                return true;

            value = default;
            return false;
        }

        private static bool TryDeserialize(JsonElement element, Type targetType, out object? value, out string? error)
        {
            try
            {
                value = JsonSerializer.Deserialize(element.GetRawText(), targetType, s_jsonOptions);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                value = null;
                error = ex.Message;
                return false;
            }
        }

        private static bool IsSupportedReturnType(Type returnType)
        {
            if (returnType == typeof(McpToolResponse))
                return true;

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                return returnType.GetGenericArguments()[0] == typeof(McpToolResponse);

            return false;
        }

        /// <summary>
        /// Resolves the permission level for a tool method.
        /// Uses the explicit <see cref="XRMcpAttribute.Permission"/> if present,
        /// otherwise infers from the tool name prefix.
        /// </summary>
        private static McpPermissionLevel ResolvePermissionLevel(MethodInfo method, string toolName)
        {
            var attr = method.GetCustomAttribute<XRMcpAttribute>();
            if (attr is not null && attr.Permission != McpPermissionLevel.Unspecified)
                return attr.Permission;

            // Heuristic: read-prefixed tools are ReadOnly, delete-prefixed are Destructive, everything else is Mutate.
            if (toolName.StartsWith("get_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("list_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("find_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("validate_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("search_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("capture_", StringComparison.OrdinalIgnoreCase))
                return McpPermissionLevel.ReadOnly;

            if (toolName.StartsWith("delete_", StringComparison.OrdinalIgnoreCase))
                return McpPermissionLevel.Destructive;

            return McpPermissionLevel.Mutate;
        }
    }
}
