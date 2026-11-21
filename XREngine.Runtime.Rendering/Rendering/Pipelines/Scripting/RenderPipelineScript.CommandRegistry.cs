using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Command name registry: maps script names to render command types.
public sealed partial class RenderPipelineScript
{
    private static ScriptCommandRegistry BuildCommandRegistry()
    {
        Dictionary<string, Type> lookup = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<Type, string> methodNames = [];
        Type baseType = typeof(ViewportRenderCommand);

        foreach (Type type in ViewportRenderCommandContainer.RegisteredCommandTypes)
        {
            if (type.IsAbstract || type.ContainsGenericParameters || !baseType.IsAssignableFrom(type))
                continue;

            bool hasStaticName;
            (string? MethodName, bool IncludeLegacyTypeNameAlias) staticName;
            lock (s_staticCommandNamesGate)
                hasStaticName = s_staticCommandNames.TryGetValue(type, out staticName);
            RenderPipelineScriptCommandAttribute? attribute = hasStaticName || XRRuntimeEnvironment.IsPublishedBuild
                ? null
                : type.GetCustomAttribute<RenderPipelineScriptCommandAttribute>();
            string scriptMethodName = staticName.MethodName ?? attribute?.MethodName ?? ResolveScriptCommandMethodName(type, inspectAttributes: false);
            TryAddLookupName(lookup, scriptMethodName, type);
            methodNames[type] = scriptMethodName;
            bool includeLegacyAlias = hasStaticName
                ? staticName.IncludeLegacyTypeNameAlias
                : attribute?.IncludeLegacyTypeNameAlias ?? false;
            if (includeLegacyAlias && !string.Equals(scriptMethodName, type.Name, StringComparison.Ordinal))
                TryAddLookupName(lookup, type.Name, type);
        }

        return new ScriptCommandRegistry(lookup, methodNames);
    }

    private static void TryAddLookupName(Dictionary<string, Type> lookup, string name, Type type)
    {
        if (!lookup.TryAdd(name, type))
            throw new InvalidOperationException($"Duplicate render command script name '{name}'. Use unique command type names or script method names.");
    }

    private static string ResolveScriptCommandMethodName(Type type, bool inspectAttributes = true)
    {
        if (inspectAttributes)
        {
            RenderPipelineScriptCommandAttribute? attribute = type.GetCustomAttribute<RenderPipelineScriptCommandAttribute>();
            if (!string.IsNullOrWhiteSpace(attribute?.MethodName))
                return attribute.MethodName;
        }

        return type.Name.StartsWith("VPRC_", StringComparison.Ordinal)
            ? JsonNamingPolicy.CamelCase.ConvertName(type.Name["VPRC_".Length..])
            : type.Name;
    }

    /// <summary>
    /// Gets the script method name of a command type, such as <c>renderQuadToFBO</c> for <c>VPRC_RenderQuadToFBO</c>.
    /// </summary>
    public static string GetCommandMethodName(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        return GetScriptCommandMethodName(commandType);
    }

    private static string GetScriptCommandMethodName(Type type)
        => s_commandRegistry.Value.MethodNamesByType.TryGetValue(type, out string? methodName)
            ? methodName
            : ResolveScriptCommandMethodName(type);

    private sealed class ScriptCommandRegistry(
        IReadOnlyDictionary<string, Type> lookup,
        IReadOnlyDictionary<Type, string> methodNamesByType)
    {
        public IReadOnlyDictionary<string, Type> Lookup { get; } = lookup;
        public IReadOnlyDictionary<Type, string> MethodNamesByType { get; } = methodNamesByType;
    }
}
