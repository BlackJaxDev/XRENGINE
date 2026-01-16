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
                        if (method.GetCustomAttribute<XRMCPAttribute>() is not null)
                            yield return method;
                    }
                }
            }
        }

        private static McpToolDefinition? BuildToolDefinition(MethodInfo method)
        {
            if (!IsSupportedReturnType(method.ReturnType))
                return null;

            string toolName = method.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? method.Name;
            string toolDescription = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? method.Name;

            var parameters = method.GetParameters();
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var parameter in parameters)
            {
                if (IsInjectedParameter(parameter))
                    continue;

                string propertyName = parameter.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? parameter.Name ?? "param";
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
                handler: (context, args, token) => InvokeToolAsync(method, context, args, token));
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
        {
            return parameter.ParameterType == typeof(McpToolContext) || parameter.ParameterType == typeof(CancellationToken);
        }

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
            string? displayName = parameter.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
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
    }
}
